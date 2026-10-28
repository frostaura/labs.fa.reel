using System.Net;
using System.Text;
using System.Text.Json;
using FrostAura.Reel.Api.Endpoints;
using FrostAura.Reel.Api.Security;
using FrostAura.Reel.Application.Auth;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Domain.Ports.Trakt;
using FrostAura.Reel.Domain.Sync;
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
using Microsoft.Extensions.DependencyInjection.Extensions;

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
    public async Task Dev_link_through_a_proxy_that_recorded_a_lan_hop_is_refused()
    {
        if (!fixture.Available)
        {
            return;
        }

        // The review's bypass once Vite records its client: the attacker writes 127.0.0.1 itself and
        // Vite (xfwd) appends the LAN address it really saw. Kestrel's peer is still loopback.
        var slug = $"dev-lock-vite-lan-{Guid.NewGuid():N}";
        var accountId = await api.SeedAccountAsync(slug);
        await using var host = WithDevProfile(slug);

        var answer = await SendAsync(host, "POST", DevEndpoints.DevLinkRoute, IPAddress.Loopback, body: "{}",
            headers: new Dictionary<string, string>
            {
                ["X-Forwarded-For"] = "127.0.0.1,::ffff:192.168.0.148",
                ["X-Forwarded-Host"] = "192.168.0.149:5173",
            });

        Assert.Equal(StatusCodes.Status403Forbidden, answer.Status);
        Assert.Equal(LoopbackGuard.RefusalCode, ErrorCode(answer.Body));
        Assert.False(answer.SetCookie);
        await using var db = Db();
        Assert.Equal(0, await db.RefreshSessions.IgnoreQueryFilters().CountAsync(s => s.AccountId == accountId));
    }

    [Fact]
    public async Task Dev_link_through_vite_on_this_machine_is_granted()
    {
        if (!fixture.Available)
        {
            return;
        }

        // A browser on this machine through Vite with xfwd: every recorded hop is loopback. This is
        // the headed-QA path ("re-POST dev/link from the page origin"), and it must keep working.
        var slug = $"dev-lock-vite-local-{Guid.NewGuid():N}";
        await api.SeedAccountAsync(slug);
        await using var host = WithDevProfile(slug);

        var answer = await SendAsync(host, "POST", DevEndpoints.DevLinkRoute, IPAddress.Loopback, body: """{"noCascade":true}""",
            headers: new Dictionary<string, string>
            {
                ["X-Forwarded-For"] = "::ffff:127.0.0.1",
                ["X-Forwarded-Host"] = "localhost:5208",
            });

        Assert.Equal(StatusCodes.Status200OK, answer.Status);
        Assert.True(answer.SetCookie);
    }

    [Fact]
    public async Task Dev_link_with_no_cascade_on_a_linked_account_writes_only_the_marked_session()
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
        // The one write the default call makes to a linked account (tier to Founder) did not happen;
        // Dev_link_from_this_machine_keeps_its_old_behaviour shows the same fixture DOES change tier.
        Assert.Equal(AccountTier.Free, account.Tier);
        var session = await db.RefreshSessions.IgnoreQueryFilters().SingleAsync(s => s.AccountId == accountId);
        Assert.Equal("reel-dev-link curl/8.7.1", session.UserAgent);
    }

    /// <summary>
    /// HREV4-PLAT-01 (FD-18): the frontend refreshes on every access-token expiry, so a QA session
    /// is a CHAIN of sessions, and the identity has to ride the whole chain. Before the fix the
    /// successor took the raw request agent — <c>curl/8.7.1</c> — so the chain was unmarked from
    /// the first rotation and the retention purge deleted it once it expired. Each refresh here
    /// sends a different agent (another client, then none at all), so the marker cannot be passing
    /// by coincidence of the request; and a plain session on the SAME account, refreshed and
    /// expired the same way, is purged — so the purge did run over this account.
    /// </summary>
    [Fact]
    public async Task A_dev_link_session_keeps_its_marker_across_every_refresh_and_survives_the_purge()
    {
        if (!fixture.Available)
        {
            return;
        }

        var slug = $"dev-lock-chain-{Guid.NewGuid():N}";
        var accountId = await api.SeedAccountAsync(slug);
        await using var host = WithDevProfile(slug);

        var link = await EdgeSecurityTests.SendAsync(host, "POST", DevEndpoints.DevLinkRoute, cookie: null,
            body: """{"noCascade":true}""", headers: new Dictionary<string, string> { ["User-Agent"] = "curl/8.7.1" });
        Assert.Equal(StatusCodes.Status200OK, link.Status);

        var first = await EdgeSecurityTests.SendAsync(host, "POST", "/api/auth/refresh", RefreshCookie(link.SetCookies),
            headers: new Dictionary<string, string> { ["User-Agent"] = "Mozilla/5.0 (Macintosh) Playwright" });
        Assert.Equal(StatusCodes.Status200OK, first.Status);
        var second = await EdgeSecurityTests.SendAsync(host, "POST", "/api/auth/refresh", RefreshCookie(first.SetCookies));
        Assert.Equal(StatusCodes.Status200OK, second.Status);

        // A plain session on the same account: the contrast that proves the purge reached it.
        string plainToken;
        using (var scope = api.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ReelDbContext>();
            var account = await db.Accounts.IgnoreQueryFilters().SingleAsync(a => a.Id == accountId);
            var sessions = scope.ServiceProvider.GetRequiredService<SessionService>();
            var issued = await sessions.IssueAsync(account, "curl/8.7.1");
            plainToken = (await sessions.RefreshAsync(issued.RefreshToken!, "curl/8.7.1"))!.RefreshToken!;
        }

        await using (var db = Db())
        {
            var chain = await db.RefreshSessions.IgnoreQueryFilters()
                .Where(s => s.AccountId == accountId && s.UserAgent != null && s.UserAgent.StartsWith(DevEndpoints.DevLinkSessionMarker))
                .OrderBy(s => s.CreatedAt)
                .Select(s => s.UserAgent)
                .ToListAsync();
            Assert.Equal(
                ["reel-dev-link curl/8.7.1", "reel-dev-link Mozilla/5.0 (Macintosh) Playwright", "reel-dev-link"],
                chain);
            var all = await db.RefreshSessions.IgnoreQueryFilters().CountAsync(s => s.AccountId == accountId);
            Assert.Equal(5, all);
            Assert.NotNull(plainToken);

            // Expire this account's sessions only (the purge is global; other tests' rows stay live).
            var now = DateTime.UtcNow;
            await db.Database.ExecuteSqlAsync(
                $"""UPDATE "RefreshSessions" SET "ExpiresAt" = {now.AddDays(-1)} WHERE "AccountId" = {accountId}""");
            await RetentionPurge.RunAsync(db, now, CancellationToken.None);

            var kept = await db.RefreshSessions.IgnoreQueryFilters()
                .Where(s => s.AccountId == accountId)
                .Select(s => s.UserAgent)
                .ToListAsync();
            Assert.Equal(3, kept.Count);
            Assert.All(kept, ua => Assert.StartsWith(DevEndpoints.DevLinkSessionMarker, ua, StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// The marker is assigned by the server, never claimed by a request: a client that sends an
    /// agent spelled like the marker on an ordinary sign-in or refresh is stored without it, so the
    /// chain rule above cannot be entered from outside dev/link and FD-18's exclusion stays exact.
    /// </summary>
    [Theory]
    [InlineData("reel-dev-link curl/8.7.1", "curl/8.7.1")]
    [InlineData("reel-dev-link", null)]
    [InlineData("reel-dev-linkish/2.0", "ish/2.0")]
    public async Task An_ordinary_session_cannot_claim_the_dev_link_marker(string claimed, string? stored)
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await api.SeedAccountAsync($"dev-lock-forge-{Guid.NewGuid():N}");
        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ReelDbContext>();
        var account = await db.Accounts.IgnoreQueryFilters().SingleAsync(a => a.Id == accountId);
        var sessions = scope.ServiceProvider.GetRequiredService<SessionService>();

        var issued = await sessions.IssueAsync(account, claimed);
        var rotated = await sessions.RefreshAsync(issued.RefreshToken!, claimed);
        Assert.NotNull(rotated);

        await using var read = Db();
        var agents = await read.RefreshSessions.IgnoreQueryFilters()
            .Where(s => s.AccountId == accountId)
            .Select(s => s.UserAgent)
            .ToListAsync();
        Assert.Equal(2, agents.Count);
        Assert.All(agents, ua => Assert.Equal(stored, ua));
    }

    [Fact]
    public async Task Dev_link_with_no_cascade_and_no_linked_account_stops_before_trakt_and_starts_nothing()
    {
        if (!fixture.Available)
        {
            return;
        }

        // The cascade lives on this path: no linked dev account, so the default call goes on to the
        // pre-provisioned-token branch, Trakt, LinkAccountAsync and a FullIngest job. This harness
        // refuses to run with any Trakt token configured (ReelApiFactory's live-credential guard), so
        // the link itself cannot be driven here; what is pinned is that noCascade answers BEFORE that
        // branch. The contrast test below sends the same fixture without the flag and reaches it.
        var slug = $"dev-lock-unlinked-{Guid.NewGuid():N}";
        var trakt = new RecordingTraktClient(slug);
        await using var host = WithUnlinkedDevProfile(slug, trakt);
        var jobsBefore = await CountSyncJobsAsync();

        var answer = await SendAsync(host, "POST", DevEndpoints.DevLinkRoute, IPAddress.Loopback, body: """{"noCascade":true}""");

        Assert.Equal(StatusCodes.Status409Conflict, answer.Status);
        Assert.Equal("dev_account_not_linked", ErrorCode(answer.Body));
        Assert.False(answer.SetCookie);
        Assert.Equal(0, trakt.Calls);
        Assert.Equal(jobsBefore, await CountSyncJobsAsync());

        await using var db = Db();
        Assert.False(await db.Accounts.IgnoreQueryFilters().AnyAsync(a => a.TraktUserSlug == slug));
    }

    [Fact]
    public async Task Dev_link_without_no_cascade_on_the_same_unlinked_fixture_goes_on_to_the_linking_branch()
    {
        if (!fixture.Available)
        {
            return;
        }

        var slug = $"dev-lock-cascade-{Guid.NewGuid():N}";
        var trakt = new RecordingTraktClient(slug);
        await using var host = WithUnlinkedDevProfile(slug, trakt);

        var answer = await SendAsync(host, "POST", DevEndpoints.DevLinkRoute, IPAddress.Loopback, body: "{}");

        // Past the point where noCascade answers: the token branch, which here has no tokens.
        Assert.Equal(StatusCodes.Status400BadRequest, answer.Status);
        Assert.Contains("TRAKT_ACCESS_TOKEN", answer.Body, StringComparison.Ordinal);
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

    /// <summary>
    /// R1-02: dev/link, under <c>Tenancy__UnscopedReads=Throw</c>, prints the stage the status poll
    /// prints for the account it signed in — a stage a newer image wrote reads as the shared Degraded
    /// fallback, a cold-start Degraded as NotEnoughRatings — and the stale cookie of another account
    /// sent with it changes nothing.
    /// </summary>
    [Theory]
    [InlineData("Hibernating", "Degraded")]
    [InlineData("Degraded", "NotEnoughRatings")]
    public async Task Dev_link_prints_the_status_polls_stage_under_Throw(string storedStage, string expected)
    {
        if (!fixture.Available)
        {
            return;
        }

        var slug = $"dev-lock-stage-{Guid.NewGuid():N}";
        var accountId = await api.SeedAccountAsync(slug);
        await using (var db = Db())
        {
            db.TraktConnections.Add(new TraktConnection
            {
                Id = Guid.NewGuid(),
                AccountId = accountId,
                ExpiresAt = DateTime.UtcNow.AddDays(30),
                Status = ConnectionStatus.Active,
            });
            await db.SaveChangesAsync();
            await db.Database.ExecuteSqlAsync($"""UPDATE "Accounts" SET "PipelineStage" = {storedStage} WHERE "Id" = {accountId}""");
        }

        var staleCookie = await AccessCookieAsync(await SeedFounderAsync());
        await using var host = api.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration(c => c.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["TRAKT_DEV_PROFILE"] = slug,
                [UnscopedReadPolicy.ConfigurationKey] = nameof(UnscopedReadMode.Throw),
            })));

        var link = await EdgeSecurityTests.SendAsync(host, "POST", DevEndpoints.DevLinkRoute, staleCookie, body: """{"noCascade":true}""");
        Assert.Equal(StatusCodes.Status200OK, link.Status);
        using (var json = JsonDocument.Parse(link.Body))
        {
            Assert.Equal(accountId.ToString(), json.RootElement.GetProperty("accountId").GetString());
            Assert.Equal(expected, json.RootElement.GetProperty("pipelineStage").GetString());
        }

        var access = Assert.Single(link.SetCookies, c => c.StartsWith("reel_at=", StringComparison.Ordinal));
        var status = await EdgeSecurityTests.SendAsync(host, "GET", "/api/sync/status", access[..access.IndexOf(';', StringComparison.Ordinal)]);
        Assert.Equal(StatusCodes.Status200OK, status.Status);
        using var statusJson = JsonDocument.Parse(status.Body);
        Assert.Equal(expected, statusJson.RootElement.GetProperty("pipelineStage").GetString());
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────

    private sealed record Answer(int Status, string Body, bool SetCookie);

    private static string RefreshCookie(IReadOnlyList<string> setCookies)
    {
        var cookie = Assert.Single(setCookies, c => c.StartsWith(SessionCookies.RefreshTokenCookie + "=", StringComparison.Ordinal));
        return cookie[..cookie.IndexOf(';', StringComparison.Ordinal)];
    }

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

    private WebApplicationFactory<Program> WithUnlinkedDevProfile(string slug, ITraktClient trakt) =>
        api.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration(c => c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TRAKT_DEV_PROFILE"] = slug,
            }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ITraktClient>();
                services.AddSingleton(trakt);
            });
        });

    private async Task<int> CountSyncJobsAsync()
    {
        await using var db = Db();
        return await db.SyncJobs.IgnoreQueryFilters().CountAsync();
    }

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

    /// <summary>
    /// Answers the profile read a dev/link link makes and counts every call; any other call throws
    /// (and is counted), so an unexpected Trakt call fails the test loudly.
    /// </summary>
    private sealed class RecordingTraktClient(string slug) : ITraktClient
    {
        private int calls;

        public int Calls => calls;

        public Task<TraktUserSettings> GetUserSettingsAsync(string accessToken, CancellationToken ct = default)
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(new TraktUserSettings(slug, slug, null, false, null));
        }

        public Task<TraktTokenResponse> RefreshTokenAsync(string refreshToken, CancellationToken ct = default) => Unexpected<TraktTokenResponse>();

        public Task<TraktTokenResponse> ExchangeCodeAsync(string code, CancellationToken ct = default) => Unexpected<TraktTokenResponse>();

        public Task<IReadOnlyList<TraktWatchedMovie>> GetWatchedMoviesAsync(string accessToken, RatePriority priority, CancellationToken ct = default) => Unexpected<IReadOnlyList<TraktWatchedMovie>>();

        public Task<IReadOnlyList<TraktWatchedShow>> GetWatchedShowsAsync(string accessToken, RatePriority priority, CancellationToken ct = default) => Unexpected<IReadOnlyList<TraktWatchedShow>>();

        public Task<IReadOnlyList<TraktRatingItem>> GetRatingsAsync(string accessToken, RatePriority priority, CancellationToken ct = default) => Unexpected<IReadOnlyList<TraktRatingItem>>();

        public Task<TraktShowProgress?> GetShowProgressAsync(string accessToken, long showTraktId, RatePriority priority, CancellationToken ct = default) => Unexpected<TraktShowProgress?>();

        public Task<string> GetLastActivitiesRawAsync(string accessToken, RatePriority priority, CancellationToken ct = default) => Unexpected<string>();

        public Task<IReadOnlyList<TraktHistoryItem>> GetHistoryAsync(string accessToken, string type, long traktId, DateTime? startAt, DateTime? endAt, RatePriority priority, CancellationToken ct = default) => Unexpected<IReadOnlyList<TraktHistoryItem>>();

        public Task<long?> ResolveTraktIdByTmdbAsync(string accessToken, long tmdbId, bool movie, RatePriority priority, CancellationToken ct = default) => Unexpected<long?>();

        public Task PostSyncBatchAsync(string accessToken, string endpoint, object payload, RatePriority priority, CancellationToken ct = default) => Unexpected<bool>();

        public Task<long> EnsureListAsync(string accessToken, string name, string description, RatePriority priority, CancellationToken ct = default) => Unexpected<long>();

        public Task PostListItemsAsync(string accessToken, long listId, object payload, bool remove, RatePriority priority, CancellationToken ct = default) => Unexpected<bool>();

        private Task<T> Unexpected<T>()
        {
            Interlocked.Increment(ref calls);
            throw new NotSupportedException("dev/link made a Trakt call it has no reason to make.");
        }
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
