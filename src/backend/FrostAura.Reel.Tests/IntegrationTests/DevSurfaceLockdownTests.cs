using System.Net;
using System.Text;
using System.Text.Json;
using FrostAura.Reel.Api.Endpoints;
using FrostAura.Reel.Api.Security;
using FrostAura.Reel.Application.Auth;
using FrostAura.Reel.Domain.Tenancy;
using FrostAura.Reel.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FrostAura.Reel.Tests.IntegrationTests;

/// <summary>
/// The QA helpers answer only this machine, and every founder diagnostic answers only a signed-in
/// Founder on this machine (J-1, D-17).
///
/// <para><b>What happened.</b> On 2026-10-22 the live stack ran Development with the QA helpers on,
/// its gateway on 0.0.0.0 and the host firewall off. An unauthenticated request from another LAN
/// host got the founder's slug and 2,142 replay events from <c>/api/dev/residual-replay</c>, and
/// <c>POST /api/auth/dev/link</c> would have handed it a Founder session outright.</para>
///
/// <para><b>How the peer is chosen.</b> The in-process test server reports no remote address, so
/// every request here is sent through <c>TestServer.SendAsync</c> with the peer set explicitly:
/// <c>192.168.0.148</c> is the LAN host the probe came from, <c>127.0.0.1</c> is this machine.</para>
///
/// <para>The route list is read from the running host's endpoint table, not written out by hand,
/// so a diagnostic added later is covered without anyone remembering to add it here.</para>
/// </summary>
[Collection("postgres")]
public class DevSurfaceLockdownTests(PostgresFixture fixture, ReelApiFactory api) : IClassFixture<ReelApiFactory>
{
    private static readonly IPAddress LanHost = IPAddress.Parse("192.168.0.148");

    [Fact]
    public async Task Every_dev_route_answers_401_to_an_anonymous_caller_from_the_lan()
    {
        if (!fixture.Available)
        {
            return;
        }

        var routes = DevRoutes();

        // Non-vacuous: the seven diagnostics that exist today, at least.
        Assert.True(routes.Count >= 7, $"Expected the founder diagnostics in the endpoint table; found {routes.Count}.");

        var answers = new List<string>();
        foreach (var (method, path) in routes)
        {
            var status = await SendAsync(method, path, LanHost);
            if (status.Status != StatusCodes.Status401Unauthorized)
            {
                answers.Add($"{method} {path} -> {status.Status}");
            }
        }

        Assert.True(answers.Count == 0, "Anonymous LAN callers must get 401 from every dev route:" + Environment.NewLine + string.Join(Environment.NewLine, answers));
    }

    [Fact]
    public async Task Every_dev_route_answers_401_to_an_anonymous_caller_on_this_machine()
    {
        if (!fixture.Available)
        {
            return;
        }

        foreach (var (method, path) in DevRoutes())
        {
            Assert.Equal(StatusCodes.Status401Unauthorized, (await SendAsync(method, path, IPAddress.Loopback)).Status);
        }
    }

    [Fact]
    public async Task A_signed_in_non_founder_on_this_machine_is_403()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await api.SeedAccountAsync($"dev-lock-free-{Guid.NewGuid():N}");
        var cookie = await AccessCookieAsync(accountId);

        foreach (var (method, path) in DevRoutes())
        {
            Assert.Equal(StatusCodes.Status403Forbidden, (await SendAsync(method, path, IPAddress.Loopback, cookie)).Status);
        }
    }

    [Fact]
    public async Task A_founder_session_used_from_the_lan_is_refused_as_loopback_only()
    {
        if (!fixture.Available)
        {
            return;
        }

        var cookie = await AccessCookieAsync(await SeedFounderAsync());

        foreach (var (method, path) in DevRoutes())
        {
            var answer = await SendAsync(method, path, LanHost, cookie);
            Assert.Equal(StatusCodes.Status403Forbidden, answer.Status);
            Assert.Equal(LoopbackGuard.RefusalCode, ErrorCode(answer.Body));
        }
    }

    [Fact]
    public async Task A_founder_on_this_machine_behind_a_proxy_is_refused()
    {
        if (!fixture.Available)
        {
            return;
        }

        var cookie = await AccessCookieAsync(await SeedFounderAsync());

        // What the compose gateway does: Kestrel's peer is the proxy, the real client is in the header.
        var answer = await SendAsync("GET", "/api/dev/cross-reference-eval", IPAddress.Loopback, cookie,
            headers: new Dictionary<string, string> { ["X-Forwarded-For"] = "192.168.0.148" });

        Assert.Equal(StatusCodes.Status403Forbidden, answer.Status);
        Assert.Equal(LoopbackGuard.RefusalCode, ErrorCode(answer.Body));
    }

    [Fact]
    public async Task A_founder_on_this_machine_reaches_the_diagnostics()
    {
        if (!fixture.Available)
        {
            return;
        }

        var cookie = await AccessCookieAsync(await SeedFounderAsync());

        // A free, read-only diagnostic: with no runs recorded it answers an empty list.
        var answer = await SendAsync("GET", "/api/dev/cross-reference-eval", IPAddress.Loopback, cookie);

        Assert.Equal(StatusCodes.Status200OK, answer.Status);
    }

    [Fact]
    public async Task Dev_link_from_the_lan_is_refused_and_writes_nothing()
    {
        if (!fixture.Available)
        {
            return;
        }

        var slug = $"dev-lock-lan-{Guid.NewGuid():N}";
        var accountId = await api.SeedAccountAsync(slug);
        await using var host = WithDevProfile(slug);

        var answer = await SendAsync(host, "POST", DevEndpoints.DevLinkRoute, LanHost, body: "{}");

        Assert.Equal(StatusCodes.Status403Forbidden, answer.Status);
        Assert.Equal(LoopbackGuard.RefusalCode, ErrorCode(answer.Body));
        Assert.False(answer.SetCookie, "A refused dev/link must not set a session cookie.");

        await using var db = Db();
        var account = await db.Accounts.IgnoreQueryFilters().SingleAsync(a => a.Id == accountId);
        Assert.Equal(AccountTier.Free, account.Tier);
        Assert.Equal(0, await db.RefreshSessions.IgnoreQueryFilters().CountAsync(s => s.AccountId == accountId));
    }

    [Fact]
    public async Task Dev_link_from_this_machine_through_a_proxy_is_refused()
    {
        if (!fixture.Available)
        {
            return;
        }

        var slug = $"dev-lock-proxy-{Guid.NewGuid():N}";
        await api.SeedAccountAsync(slug);
        await using var host = WithDevProfile(slug);

        var answer = await SendAsync(host, "POST", DevEndpoints.DevLinkRoute, IPAddress.Loopback, body: "{}",
            headers: new Dictionary<string, string> { ["X-Forwarded-For"] = "192.168.0.148" });

        Assert.Equal(StatusCodes.Status403Forbidden, answer.Status);
    }

    [Fact]
    public async Task Dev_link_with_no_cascade_issues_a_marked_session_and_starts_nothing()
    {
        if (!fixture.Available)
        {
            return;
        }

        var slug = $"dev-lock-nocascade-{Guid.NewGuid():N}";
        var accountId = await api.SeedAccountAsync(slug);
        await using var host = WithDevProfile(slug);

        var answer = await SendAsync(host, "POST", DevEndpoints.DevLinkRoute, IPAddress.Loopback,
            body: """{"noCascade":true}""", headers: new Dictionary<string, string> { ["User-Agent"] = "curl/8.7.1" });

        Assert.Equal(StatusCodes.Status200OK, answer.Status);
        Assert.True(answer.SetCookie, "A granted dev/link sets the session cookies.");

        await using var db = Db();
        var account = await db.Accounts.IgnoreQueryFilters().SingleAsync(a => a.Id == accountId);
        Assert.Equal(AccountTier.Free, account.Tier);
        Assert.Equal(0, await db.SyncJobs.IgnoreQueryFilters().CountAsync(j => j.AccountId == accountId));
        var session = await db.RefreshSessions.IgnoreQueryFilters().SingleAsync(s => s.AccountId == accountId);
        Assert.Equal("reel-dev-link curl/8.7.1", session.UserAgent);
    }

    [Fact]
    public async Task Dev_link_with_no_cascade_and_no_linked_account_is_409_and_links_nothing()
    {
        if (!fixture.Available)
        {
            return;
        }

        var slug = $"dev-lock-unlinked-{Guid.NewGuid():N}";
        await using var host = WithDevProfile(slug);

        var answer = await SendAsync(host, "POST", DevEndpoints.DevLinkRoute, IPAddress.Loopback, body: """{"noCascade":true}""");

        Assert.Equal(StatusCodes.Status409Conflict, answer.Status);
        Assert.Equal("dev_account_not_linked", ErrorCode(answer.Body));

        await using var db = Db();
        Assert.False(await db.Accounts.IgnoreQueryFilters().AnyAsync(a => a.TraktUserSlug == slug));
    }

    [Fact]
    public async Task Dev_link_from_this_machine_keeps_its_old_behaviour()
    {
        if (!fixture.Available)
        {
            return;
        }

        var slug = $"dev-lock-default-{Guid.NewGuid():N}";
        var accountId = await api.SeedAccountAsync(slug);
        await using var host = WithDevProfile(slug);

        // An empty body and no body at all are both the old call.
        var withEmptyObject = await SendAsync(host, "POST", DevEndpoints.DevLinkRoute, IPAddress.Loopback, body: "{}");
        var withNoBody = await SendAsync(host, "POST", DevEndpoints.DevLinkRoute, IPAddress.Loopback);

        Assert.Equal(StatusCodes.Status200OK, withEmptyObject.Status);
        Assert.Equal(StatusCodes.Status200OK, withNoBody.Status);

        await using var db = Db();
        var account = await db.Accounts.IgnoreQueryFilters().SingleAsync(a => a.Id == accountId);
        Assert.Equal(AccountTier.Founder, account.Tier);
        var agents = await db.RefreshSessions.IgnoreQueryFilters()
            .Where(s => s.AccountId == accountId)
            .Select(s => s.UserAgent)
            .ToListAsync();
        Assert.Equal(2, agents.Count);
        Assert.All(agents, a => Assert.Equal(DevEndpoints.DevLinkSessionMarker, a));
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────

    private sealed record Answer(int Status, string Body, bool SetCookie);

    private List<(string Method, string Path)> DevRoutes() =>
        [.. api.Services.GetServices<EndpointDataSource>()
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText is { } raw && raw.StartsWith(DevEndpoints.DevPrefix + "/", StringComparison.Ordinal))
            .SelectMany(e => (e.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? ["GET"])
                .Select(method => (method, e.RoutePattern.RawText!)))
            .Distinct()
            .OrderBy(r => r.Item2, StringComparer.Ordinal)
            .ThenBy(r => r.method, StringComparer.Ordinal)];

    private WebApplicationFactory<Program> WithDevProfile(string slug) =>
        api.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration(c => c.AddInMemoryCollection(
            new Dictionary<string, string?> { ["TRAKT_DEV_PROFILE"] = slug })));

    private ReelDbContext Db() =>
        api.Services.CreateScope().ServiceProvider.GetRequiredService<ReelDbContext>();

    private async Task<Guid> SeedFounderAsync()
    {
        var accountId = await api.SeedAccountAsync($"dev-lock-founder-{Guid.NewGuid():N}");
        await using var db = Db();
        var account = await db.Accounts.IgnoreQueryFilters().SingleAsync(a => a.Id == accountId);
        account.Tier = AccountTier.Founder;
        await db.SaveChangesAsync();
        return accountId;
    }

    private async Task<string> AccessCookieAsync(Guid accountId)
    {
        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ReelDbContext>();
        var account = await db.Accounts.IgnoreQueryFilters().SingleAsync(a => a.Id == accountId);
        var token = scope.ServiceProvider.GetRequiredService<SessionService>().MintAccessToken(account, DateTime.UtcNow);
        return "reel_at=" + token;
    }

    private Task<Answer> SendAsync(string method, string path, IPAddress peer, string? cookie = null) =>
        SendAsync(api, method, path, peer, cookie: cookie);

    private Task<Answer> SendAsync(string method, string path, IPAddress peer, string cookie, Dictionary<string, string> headers) =>
        SendAsync(api, method, path, peer, cookie: cookie, headers: headers);

    private static async Task<Answer> SendAsync(
        WebApplicationFactory<Program> host,
        string method,
        string path,
        IPAddress peer,
        string? body = null,
        string? cookie = null,
        Dictionary<string, string>? headers = null)
    {
        var context = await host.Server.SendAsync(c =>
        {
            c.Request.Method = method;
            c.Request.Path = path;
            c.Connection.RemoteIpAddress = peer;
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
                // SendAsync(Action<HttpContext>) builds a context whose body-detection feature says
                // "no body", and minimal APIs then bind null without reading the stream.
                c.Features.Set<IHttpRequestBodyDetectionFeature>(new HasBody());
            }
        });

        using var reader = new StreamReader(context.Response.Body);
        var text = await reader.ReadToEndAsync();
        return new Answer(context.Response.StatusCode, text, context.Response.Headers.SetCookie.Count > 0);
    }

    private sealed class HasBody : IHttpRequestBodyDetectionFeature
    {
        public bool CanHaveBody => true;
    }

    private static string? ErrorCode(string body)
    {
        using var document = JsonDocument.Parse(body);
        return document.RootElement.TryGetProperty("error", out var error) ? error.GetString() : null;
    }
}
