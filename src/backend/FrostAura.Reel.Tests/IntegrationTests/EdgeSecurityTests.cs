using System.Net;
using System.Text;
using System.Text.Json;
using FrostAura.Reel.Api.Security;
using FrostAura.Reel.Application.Auth;
using FrostAura.Reel.Domain.Tenancy;
using FrostAura.Reel.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
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

        // Six answered turns — the stub models answer them, so nothing is spent.
        for (var i = 0; i < 6; i++)
        {
            Assert.Equal(StatusCodes.Status200OK, (await SendAsync(api, "POST", "/api/search/ask", cookie, body: ValidAsk())).Status);
        }

        var seventh = await SendAsync(api, "POST", "/api/search/ask", cookie, body: ValidAsk());

        Assert.Equal(StatusCodes.Status429TooManyRequests, seventh.Status);
        Assert.Equal("ask_rate_minute", Code(seventh.Body));
        Assert.True(int.TryParse(seventh.RetryAfter, out var seconds) && seconds is > 0 and <= 60, $"Retry-After was '{seventh.RetryAfter}'.");

        // Another account is its own partition.
        var other = await CookieAsync(await api.SeedAccountAsync($"edge-ask-2-{Guid.NewGuid():N}"));
        Assert.Equal(StatusCodes.Status200OK, (await SendAsync(api, "POST", "/api/search/ask", other, body: ValidAsk())).Status);
    }

    /// <summary>
    /// HREV1-PLAT-2: a request the Ask handler refuses is not a turn. Six refusals used to spend the
    /// minute's six turns (and six of the hour's twenty), so the next real question was a 429 although
    /// nothing had been answered. Each refusal shape the handler makes before it writes a turn record
    /// is tried: a malformed body, a missing turn id, an empty query and an unknown conversation.
    /// </summary>
    [Theory]
    [InlineData("{\"bogus\":1}", StatusCodes.Status400BadRequest)]
    [InlineData("{\"query\":\"a tense heist movie\"}", StatusCodes.Status400BadRequest)]
    [InlineData("{\"query\":\"   \",\"clientTurnId\":\"6f1c1f9e-3c4b-4d43-9a43-1f2e3d4c5b6a\"}", StatusCodes.Status400BadRequest)]
    [InlineData("{\"query\":\"a tense heist movie\",\"clientTurnId\":\"6f1c1f9e-3c4b-4d43-9a43-1f2e3d4c5b6a\",\"conversationId\":\"0b7f3a52-5d2e-4f43-8a51-2c3d4e5f6a7b\"}", StatusCodes.Status404NotFound)]
    public async Task Refused_asks_do_not_spend_the_accounts_turns(string refusedBody, int refusedStatus)
    {
        if (!fixture.Available)
        {
            return;
        }

        var cookie = await CookieAsync(await api.SeedAccountAsync($"edge-ask-refused-{Guid.NewGuid():N}"));
        for (var i = 0; i < 8; i++)
        {
            Assert.Equal(refusedStatus, (await SendAsync(api, "POST", "/api/search/ask", cookie, body: refusedBody)).Status);
        }

        var answered = await SendAsync(api, "POST", "/api/search/ask", cookie, body: ValidAsk());
        Assert.Equal(StatusCodes.Status200OK, answered.Status);
        Assert.Contains("event: ", answered.Body, StringComparison.Ordinal);
    }

    private static string ValidAsk() =>
        JsonSerializer.Serialize(new { query = "a tense heist movie", clientTurnId = Guid.NewGuid() });

    /// <summary>
    /// R1-15: the day limit is counted in the zone the account saved, so its 429 says the day's
    /// turns come back at THAT midnight — the one the ledger's Ask allowance resets at. Kiritimati's
    /// midnight is always ten hours from UTC's, so the two accounts' waits can never coincide.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("Pacific/Kiritimati")]
    [InlineData("America/Los_Angeles")]
    public async Task The_day_limit_waits_for_the_accounts_own_midnight(string? zone)
    {
        if (!fixture.Available)
        {
            return;
        }

        await using var host = api.WithWebHostBuilder(b => b.ConfigureAppConfiguration(c => c.AddInMemoryCollection(
            new Dictionary<string, string?> { [AskTurnLimitOptions.PerDayFreeKey] = "1" })));
        var cookie = await CookieAsync(await api.SeedAccountAsync($"edge-day-{Guid.NewGuid():N}"));
        if (zone is not null)
        {
            var saved = await SendAsync(host, "PUT", "/api/settings", cookie, body: JsonSerializer.Serialize(new { timeZone = zone }));
            Assert.Equal(StatusCodes.Status200OK, saved.Status);
        }

        Assert.Equal(StatusCodes.Status200OK, (await SendAsync(host, "POST", "/api/search/ask", cookie, body: ValidAsk())).Status);
        var second = await SendAsync(host, "POST", "/api/search/ask", cookie, body: ValidAsk());
        var now = DateTime.UtcNow;

        Assert.Equal(StatusCodes.Status429TooManyRequests, second.Status);
        Assert.Equal("ask_rate_day", Code(second.Body));
        var expected = (AccountTimeZone.NextDayStartUtc(zone, now) - now).TotalSeconds;
        Assert.True(
            int.TryParse(second.RetryAfter, out var seconds) && Math.Abs(seconds - expected) <= 5,
            $"Retry-After was '{second.RetryAfter}'; midnight in {zone ?? "UTC"} is {expected:F0} s away.");
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

    /// <summary>
    /// HREV1-PLAT-1: the lockout counts attempts, not finished failures. Forty wrong PINs sent at once
    /// used to clear the lock check together — fourteen of them were each a full PBKDF2 comparison of
    /// a guess — because a failure was recorded only after its 100k-iteration hash. At most the
    /// account's remaining allowance may be compared, on every route that checks a PIN, and the lock
    /// that follows is real: the right PIN waits it out.
    /// </summary>
    [Theory]
    [InlineData("verify", 0)]
    [InlineData("verify", 3)]
    [InlineData("change", 0)]
    [InlineData("remove", 0)]
    [InlineData("remove", 4)]
    public async Task Forty_concurrent_wrong_pins_compare_no_more_than_the_remaining_allowance(string route, int earlierFailures)
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await api.SeedAccountAsync($"edge-pin-race-{Guid.NewGuid():N}");
        var cookie = await CookieAsync(accountId);
        Assert.Equal(StatusCodes.Status200OK, (await SendAsync(api, "POST", "/api/settings/pin", cookie, body: "{\"pin\":\"4831\"}")).Status);
        for (var i = 0; i < earlierFailures; i++)
        {
            Assert.Equal(StatusCodes.Status423Locked, (await SendAsync(api, "POST", "/api/settings/pin/verify", cookie, body: "{\"pin\":\"0000\"}")).Status);
        }

        var answers = await Task.WhenAll(Enumerable.Range(1000, 40).Select(guess => route switch
        {
            "verify" => SendAsync(api, "POST", "/api/settings/pin/verify", cookie, body: $"{{\"pin\":\"{guess}\"}}"),
            "change" => SendAsync(api, "POST", "/api/settings/pin", cookie, body: $"{{\"pin\":\"1234\",\"currentPin\":\"{guess}\"}}"),
            _ => SendAsync(api, "DELETE", "/api/settings/pin", cookie, headers: new() { ["X-Settings-Pin"] = $"{guess}" }),
        }));

        var compared = answers.Count(a => a.Status == StatusCodes.Status423Locked);
        Assert.Equal(PinAttemptLimiter.MaxFailures - earlierFailures, compared);
        Assert.All(
            answers.Where(a => a.Status != StatusCodes.Status423Locked),
            a =>
            {
                Assert.Equal(StatusCodes.Status429TooManyRequests, a.Status);
                Assert.Equal("pin_locked", Code(a.Body));
                Assert.True(int.TryParse(a.RetryAfter, out var seconds) && seconds is > 0 and <= 900, $"Retry-After was '{a.RetryAfter}'.");
            });

        var right = await SendAsync(api, "POST", "/api/settings/pin/verify", cookie, body: "{\"pin\":\"4831\"}");
        Assert.Equal(StatusCodes.Status429TooManyRequests, right.Status);
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

    /// <summary>
    /// HREV1-PLAT-7: a redeploy needs no key-ring mount. The Data Protection keys live in the
    /// <c>DataProtectionKeys</c> table, so a brand-new process on the same database — what a redeploy
    /// is — reads every secret the old one sealed. The control proves the check can fail: a host whose
    /// ring is NOT the database cannot read the same token, which is the R1-27 "Revoked" path a lost
    /// ring (or, once <c>DATAPROTECTION_CERT_PATH</c> is set, a missing certificate) leads to.
    /// </summary>
    [Fact]
    public async Task A_redeployed_process_reads_the_old_ones_secrets_from_the_database_alone()
    {
        if (!fixture.Available)
        {
            return;
        }

        var secret = $"trakt-token-{Guid.NewGuid():N}";
        var sealedByOld = api.Services.GetRequiredService<FrostAura.Reel.Domain.Ports.ISecretProtector>().Protect(secret);

        await using (var redeployed = api.WithWebHostBuilder(_ => { }))
        {
            Assert.Equal(secret, redeployed.Services.GetRequiredService<FrostAura.Reel.Domain.Ports.ISecretProtector>().Unprotect(sealedByOld));
        }

        await using (var elsewhere = api.WithWebHostBuilder(b => b.ConfigureServices(s =>
            s.AddDataProtection().SetApplicationName("FrostAura.Reel").UseEphemeralDataProtectionProvider())))
        {
            var reader = elsewhere.Services.GetRequiredService<FrostAura.Reel.Domain.Ports.ISecretProtector>();
            Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(() => reader.Unprotect(sealedByOld));
        }

        await using var db = Db();
        Assert.True(await db.DataProtectionKeys.AnyAsync(), "the key ring is expected in the DataProtectionKeys table");
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
        // A git-archive build outside any checkout is stamped with nothing and says so (HREV1-PLAT-6).
        Assert.Equal(FrostAura.Reel.Tests.Harness.BuildProvenance.StampedCommit, json.RootElement.GetProperty("commit").GetString());
        if (FrostAura.Reel.Tests.Harness.BuildProvenance.MustBeStamped)
        {
            Assert.Matches(FrostAura.Reel.Tests.Harness.BuildProvenance.FullSha, json.RootElement.GetProperty("commit").GetString());
        }
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
