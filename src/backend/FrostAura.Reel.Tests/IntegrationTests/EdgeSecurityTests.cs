using System.Net;
using System.Text;
using System.Text.Json;
using FrostAura.Reel.Api.Security;
using FrostAura.Reel.Application.Auth;
using FrostAura.Reel.Domain.Tenancy;
using FrostAura.Reel.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace FrostAura.Reel.Tests.IntegrationTests;

/// <summary>
/// WS-8's P1 acceptance lines, end to end through the real pipeline: Secure cookies and forwarded
/// HTTPS (J-7), refresh rotation under concurrency (J-10), the inbound Ask and PIN limits (J-8,
/// K-4), the push allow-list (J-9), settings validation (H-4, H-11) and the /health commit (A-17).
/// </summary>
[Collection("postgres")]
public class EdgeSecurityTests(PostgresFixture fixture, ReelApiFactory api) : IClassFixture<ReelApiFactory>
{
    internal sealed record Answer(int Status, string Body, IReadOnlyList<string> SetCookies, string? RetryAfter);

    [Fact]
    public async Task Production_session_cookies_are_secure_even_over_the_gateways_plain_http()
    {
        if (!fixture.Available)
        {
            return;
        }

        await using var production = api.WithWebHostBuilder(b => b.UseEnvironment(Environments.Production));
        var accountId = await api.SeedAccountAsync($"edge-prod-{Guid.NewGuid():N}");
        var refresh = await IssueRefreshTokenAsync(accountId);

        var answer = await SendAsync(production, "POST", "/api/auth/refresh", cookie: $"reel_rt={refresh}");

        Assert.Equal(StatusCodes.Status200OK, answer.Status);
        Assert.Equal(2, answer.SetCookies.Count);
        Assert.All(answer.SetCookies, c => Assert.Contains("secure", c, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Development_cookies_follow_the_forwarded_scheme_of_a_trusted_proxy()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await api.SeedAccountAsync($"edge-dev-{Guid.NewGuid():N}");

        var plain = await SendAsync(api, "POST", "/api/auth/refresh", cookie: $"reel_rt={await IssueRefreshTokenAsync(accountId)}");
        Assert.DoesNotContain(plain.SetCookies, c => c.Contains("secure", StringComparison.OrdinalIgnoreCase));

        // Loopback is a trusted proxy: its X-Forwarded-Proto is believed.
        var forwarded = await SendAsync(
            api, "POST", "/api/auth/refresh", cookie: $"reel_rt={await IssueRefreshTokenAsync(accountId)}",
            headers: new() { ["X-Forwarded-Proto"] = "https", ["X-Forwarded-For"] = "203.0.113.9" });
        Assert.Equal(StatusCodes.Status200OK, forwarded.Status);
        Assert.All(forwarded.SetCookies, c => Assert.Contains("secure", c, StringComparison.OrdinalIgnoreCase));

        // A LAN peer is not: the same header from it changes nothing.
        var spoofed = await SendAsync(
            api, "POST", "/api/auth/refresh", cookie: $"reel_rt={await IssueRefreshTokenAsync(accountId)}",
            headers: new() { ["X-Forwarded-Proto"] = "https" }, peer: IPAddress.Parse("192.168.0.148"));
        Assert.DoesNotContain(spoofed.SetCookies, c => c.Contains("secure", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Three_concurrent_refreshes_of_one_token_make_exactly_one_session()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await api.SeedAccountAsync($"edge-race-{Guid.NewGuid():N}");
        var refresh = await IssueRefreshTokenAsync(accountId);

        var answers = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ =>
            SendAsync(api, "POST", "/api/auth/refresh", cookie: $"reel_rt={refresh}")));

        // Every caller stays signed in…
        Assert.All(answers, a => Assert.Equal(StatusCodes.Status200OK, a.Status));
        // …exactly one of them was handed a new refresh token…
        Assert.Equal(1, answers.Count(a => a.SetCookies.Any(c => c.StartsWith("reel_rt=", StringComparison.Ordinal))));
        Assert.All(answers, a => Assert.Contains(a.SetCookies, c => c.StartsWith("reel_at=", StringComparison.Ordinal)));

        // …and the database holds the original (revoked, pointing at its successor) plus ONE successor.
        await using var db = Db();
        var sessions = await db.RefreshSessions.IgnoreQueryFilters().Where(s => s.AccountId == accountId).ToListAsync();
        Assert.Equal(2, sessions.Count);
        var original = Assert.Single(sessions, s => s.RevokedAt is not null);
        var successor = Assert.Single(sessions, s => s.RevokedAt is null);
        Assert.Equal(successor.Id, original.ReplacedBySessionId);
    }

    [Fact]
    public async Task A_rotated_token_presented_after_the_grace_window_is_refused()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await api.SeedAccountAsync($"edge-grace-{Guid.NewGuid():N}");
        var refresh = await IssueRefreshTokenAsync(accountId);
        Assert.Equal(StatusCodes.Status200OK, (await SendAsync(api, "POST", "/api/auth/refresh", cookie: $"reel_rt={refresh}")).Status);

        await using (var db = Db())
        {
            await db.RefreshSessions.IgnoreQueryFilters()
                .Where(s => s.AccountId == accountId && s.RevokedAt != null)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, DateTime.UtcNow - SessionService.RefreshGrace - TimeSpan.FromSeconds(1)));
        }

        Assert.Equal(StatusCodes.Status401Unauthorized, (await SendAsync(api, "POST", "/api/auth/refresh", cookie: $"reel_rt={refresh}")).Status);
    }

    [Fact]
    public async Task The_seventh_ask_in_a_minute_is_429_with_a_code_and_retry_after()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await api.SeedAccountAsync($"edge-ask-{Guid.NewGuid():N}");
        var cookie = await CookieAsync(accountId);

        // Malformed on purpose: the limit counts turns STARTED at the edge, before the engine reads
        // the body, so six refusals by the handler are six turns — and no model is ever reached.
        for (var i = 0; i < 6; i++)
        {
            Assert.Equal(StatusCodes.Status400BadRequest, (await SendAsync(api, "POST", "/api/search/ask", cookie, body: "{\"query\": 7}")).Status);
        }

        var seventh = await SendAsync(api, "POST", "/api/search/ask", cookie, body: "{\"query\": 7}");

        Assert.Equal(StatusCodes.Status429TooManyRequests, seventh.Status);
        Assert.Equal("ask_rate_minute", Code(seventh.Body));
        Assert.True(int.TryParse(seventh.RetryAfter, out var seconds) && seconds is > 0 and <= 60, $"Retry-After was '{seventh.RetryAfter}'.");

        // Another account is its own partition.
        var other = await CookieAsync(await api.SeedAccountAsync($"edge-ask-2-{Guid.NewGuid():N}"));
        Assert.Equal(StatusCodes.Status400BadRequest, (await SendAsync(api, "POST", "/api/search/ask", other, body: "{\"query\": 7}")).Status);
    }

    [Fact]
    public async Task The_sixth_wrong_pin_is_429_and_even_the_right_pin_waits_out_the_lockout()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await api.SeedAccountAsync($"edge-pin-{Guid.NewGuid():N}");
        var cookie = await CookieAsync(accountId);
        Assert.Equal(StatusCodes.Status200OK, (await SendAsync(api, "POST", "/api/settings/pin", cookie, body: "{\"pin\":\"2468\"}")).Status);

        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(StatusCodes.Status423Locked, (await SendAsync(api, "POST", "/api/settings/pin/verify", cookie, body: "{\"pin\":\"0000\"}")).Status);
        }

        var sixth = await SendAsync(api, "POST", "/api/settings/pin/verify", cookie, body: "{\"pin\":\"0000\"}");
        Assert.Equal(StatusCodes.Status429TooManyRequests, sixth.Status);
        Assert.Equal("pin_locked", Code(sixth.Body));
        Assert.True(int.TryParse(sixth.RetryAfter, out var seconds) && seconds is > 0 and <= 900);

        var right = await SendAsync(api, "POST", "/api/settings/pin/verify", cookie, body: "{\"pin\":\"2468\"}");
        Assert.Equal(StatusCodes.Status429TooManyRequests, right.Status);

        // The removal path is the same lock, not a second oracle.
        var removal = await SendAsync(api, "DELETE", "/api/settings/pin", cookie, headers: new() { ["X-Settings-Pin"] = "2468" });
        Assert.Equal(StatusCodes.Status429TooManyRequests, removal.Status);
    }

    [Fact]
    public async Task The_filters_gate_no_longer_takes_a_raw_pin()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await api.SeedAccountAsync($"edge-legacy-{Guid.NewGuid():N}");
        var cookie = await CookieAsync(accountId);
        Assert.Equal(StatusCodes.Status200OK, (await SendAsync(api, "POST", "/api/settings/pin", cookie, body: "{\"pin\":\"2468\"}")).Status);

        var put = await SendAsync(
            api, "PUT", "/api/settings/filters", cookie,
            body: "{\"excludeGenres\":[],\"includeGenres\":[],\"excludeKeywords\":[],\"languages\":[]}",
            headers: new() { ["X-Settings-Pin"] = "2468" });

        Assert.Equal(StatusCodes.Status423Locked, put.Status);
    }

    [Theory]
    [InlineData("http://postgres:5432")]
    [InlineData("https://169.254.169.254/latest/meta-data")]
    [InlineData("https://fcm.googleapis.com.attacker.example/x")]
    public async Task Subscribing_an_internal_or_unknown_push_endpoint_is_400(string endpoint)
    {
        if (!fixture.Available)
        {
            return;
        }

        var cookie = await CookieAsync(await api.SeedAccountAsync($"edge-push-{Guid.NewGuid():N}"));
        var answer = await SendAsync(api, "POST", "/api/push/subscribe", cookie,
            body: JsonSerializer.Serialize(new { endpoint, keys = new { p256dh = "BNcRd", auth = "tBHI" } }));

        Assert.Equal(StatusCodes.Status400BadRequest, answer.Status);
        Assert.Equal("push_endpoint_not_allowed", Code(answer.Body));
    }

    [Fact]
    public async Task An_account_keeps_at_most_ten_push_subscriptions()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await api.SeedAccountAsync($"edge-push-cap-{Guid.NewGuid():N}");
        var cookie = await CookieAsync(accountId);
        for (var i = 0; i < 12; i++)
        {
            var body = JsonSerializer.Serialize(new { endpoint = $"https://fcm.googleapis.com/fcm/send/device-{i}", keys = new { p256dh = "BNcRd", auth = "tBHI" } });
            Assert.Equal(StatusCodes.Status200OK, (await SendAsync(api, "POST", "/api/push/subscribe", cookie, body: body)).Status);
        }

        await using var db = Db();
        var endpoints = await db.PushSubscriptions.IgnoreQueryFilters().Where(s => s.AccountId == accountId).Select(s => s.Endpoint).ToListAsync();
        Assert.Equal(10, endpoints.Count);
        Assert.Contains("https://fcm.googleapis.com/fcm/send/device-11", endpoints);
    }

    [Theory]
    [InlineData("{\"email\":\"not-an-email\"}", "email_invalid")]
    [InlineData("{\"region\":\"ZZ\"}", "region_invalid")]
    [InlineData("{\"region\":\"USA\"}", "region_invalid")]
    [InlineData("{\"region\":\"GB\",\"email\":\"bad@\"}", "email_invalid")]
    public async Task Invalid_settings_are_400_and_change_nothing(string body, string code)
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await api.SeedAccountAsync($"edge-settings-{Guid.NewGuid():N}");
        var cookie = await CookieAsync(accountId);

        var answer = await SendAsync(api, "PUT", "/api/settings", cookie, body: body);

        Assert.Equal(StatusCodes.Status400BadRequest, answer.Status);
        Assert.Equal(code, Code(answer.Body));
        await using var db = Db();
        var account = await db.Accounts.IgnoreQueryFilters().SingleAsync(a => a.Id == accountId);
        Assert.Equal("ZA", account.Region);
        Assert.Null(account.Settings.EmailForBilling);
    }

    [Fact]
    public async Task Valid_settings_save_normalised()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await api.SeedAccountAsync($"edge-settings-ok-{Guid.NewGuid():N}");
        var cookie = await CookieAsync(accountId);

        var answer = await SendAsync(api, "PUT", "/api/settings", cookie, body: "{\"region\":\"gb\",\"email\":\" dean@frostaura.net \"}");

        Assert.Equal(StatusCodes.Status200OK, answer.Status);
        await using var db = Db();
        var account = await db.Accounts.IgnoreQueryFilters().SingleAsync(a => a.Id == accountId);
        Assert.Equal("GB", account.Region);
        Assert.Equal("dean@frostaura.net", account.Settings.EmailForBilling);
    }

    [Fact]
    public async Task Health_reports_the_running_commit()
    {
        if (!fixture.Available)
        {
            return;
        }

        var answer = await SendAsync(api, "GET", "/health", cookie: null);

        Assert.Equal(StatusCodes.Status200OK, answer.Status);
        using var json = JsonDocument.Parse(answer.Body);
        Assert.Equal(FrostAura.Reel.Api.ReleaseInfo.Commit, json.RootElement.GetProperty("commit").GetString());
        Assert.Matches("^[0-9a-f]{40}$", json.RootElement.GetProperty("commit").GetString());
    }

    private ReelDbContext Db() => api.Services.CreateScope().ServiceProvider.GetRequiredService<ReelDbContext>();

    private async Task<string> IssueRefreshTokenAsync(Guid accountId)
    {
        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ReelDbContext>();
        var account = await db.Accounts.IgnoreQueryFilters().SingleAsync(a => a.Id == accountId);
        var tokens = await scope.ServiceProvider.GetRequiredService<SessionService>().IssueAsync(account, "edge-security-tests");
        return tokens.RefreshToken!;
    }

    private async Task<string> CookieAsync(Guid accountId)
    {
        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ReelDbContext>();
        var account = await db.Accounts.IgnoreQueryFilters().SingleAsync(a => a.Id == accountId);
        return "reel_at=" + scope.ServiceProvider.GetRequiredService<SessionService>().MintAccessToken(account, DateTime.UtcNow);
    }

    private static string? Code(string body)
    {
        try
        {
            using var json = JsonDocument.Parse(body);
            return json.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static async Task<Answer> SendAsync(
        WebApplicationFactory<Program> host,
        string method,
        string path,
        string? cookie,
        string? body = null,
        Dictionary<string, string>? headers = null,
        IPAddress? peer = null)
    {
        var context = await host.Server.SendAsync(c =>
        {
            c.Request.Method = method;
            c.Request.Path = path;
            c.Connection.RemoteIpAddress = peer ?? IPAddress.Loopback;
            if (cookie is not null)
            {
                c.Request.Headers.Cookie = cookie;
            }

            foreach (var (name, value) in headers ?? [])
            {
                c.Request.Headers[name] = value;
            }

            if (body is not null)
            {
                var bytes = Encoding.UTF8.GetBytes(body);
                c.Request.ContentType = "application/json";
                c.Request.ContentLength = bytes.Length;
                c.Request.Body = new MemoryStream(bytes);
                c.Features.Set<IHttpRequestBodyDetectionFeature>(new HasBody());
            }
        });

        using var reader = new StreamReader(context.Response.Body);
        var text = await reader.ReadToEndAsync();
        return new Answer(
            context.Response.StatusCode,
            text,
            [.. context.Response.Headers.SetCookie.Select(c => c ?? string.Empty)],
            context.Response.Headers.RetryAfter.FirstOrDefault());
    }

    private sealed class HasBody : IHttpRequestBodyDetectionFeature
    {
        public bool CanHaveBody => true;
    }
}
