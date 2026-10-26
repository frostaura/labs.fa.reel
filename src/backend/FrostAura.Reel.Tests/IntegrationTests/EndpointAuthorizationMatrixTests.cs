using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using FrostAura.Reel.Application.Auth;
using FrostAura.Reel.Domain.Tenancy;
using FrostAura.Reel.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FrostAura.Reel.Tests.IntegrationTests;

/// <summary>
/// The endpoint authorisation matrix (J-11): every route the running host maps, asked by an
/// anonymous caller, and every tier-gated route asked by each tier.
///
/// <para><b>Why a matrix and not per-route tests.</b> The gates are filters and inline checks
/// scattered over twenty endpoint files, and the failure that matters — a route that forgot its
/// <c>RequireAccount</c>, or a founder surface reachable by a paying account — is an ABSENCE no
/// per-feature test looks for. The route list here is read from the host's endpoint table, so a
/// route added next month is held to the rule without anyone remembering to list it; the only
/// hand-written lists are the deliberate exceptions, each with its reason.</para>
///
/// <para>Anonymous probes are sent from loopback so the loopback guard never answers first: the
/// question is whether the route needs an account, not where the caller sits
/// (<see cref="DevSurfaceLockdownTests"/> owns that).</para>
/// </summary>
[Collection("postgres")]
public class EndpointAuthorizationMatrixTests(PostgresFixture fixture, ReelApiFactory api) : IClassFixture<ReelApiFactory>
{
    /// <summary>Routes that answer without an account, deliberately.</summary>
    private static readonly Dictionary<string, string> Public = new(StringComparer.Ordinal)
    {
        ["GET /health"] = "liveness and the running commit, for Docker and the deploy job",
        ["GET /health/ready"] = "readiness, for the compose healthchecks and an external monitor",
        ["POST /api/auth/trakt/start"] = "sign-in starts signed out",
        ["POST /api/auth/trakt/callback"] = "sign-in completes signed out",
        ["POST /api/auth/refresh"] = "carries only the refresh cookie; an expired access token is the point",
        ["POST /api/auth/logout"] = "signing out must work with an expired session",
        ["POST /api/auth/dev/link"] = "QA sign-in: Development + QA_HELPERS_ENABLED + loopback only (DevSurfaceLockdownTests)",
        ["GET /openapi/{documentName}.json"] = "the API description, mapped in Development only",
        ["POST /api/billing/webhook"] = "Stripe calls it; authenticated by the Stripe-Signature over the body, not by a session",
        ["POST /api/client-errors"] = "a sign-in page that crashes must be able to report it; anonymous reports share one small budget",
    };

    /// <summary>Routes a Free account may not use, and the tiers that may.</summary>
    private static readonly Dictionary<string, AccountTier[]> TierGated = new(StringComparer.Ordinal)
    {
        ["GET /api/metrics/interleave"] = [AccountTier.Founder],
        ["POST /api/metrics/model/train"] = [AccountTier.Founder],
        ["POST /api/sync/history-cleanup"] = [AccountTier.Founder],
    };

    [Fact]
    public async Task Every_route_but_the_public_ones_answers_401_to_an_anonymous_caller()
    {
        if (!fixture.Available)
        {
            return;
        }

        var routes = Routes();
        Assert.True(routes.Count >= 60, $"Expected the whole API in the endpoint table; found {routes.Count}.");

        var open = new List<string>();
        foreach (var route in routes.Where(r => !Public.ContainsKey(r.Key)))
        {
            // A JSON body, so a route whose handler binds one reaches its filters instead of failing
            // binding first; a binding failure answers before any filter runs and would hide the gate.
            // `?q=` for the same reason on the one GET that requires a query value (typeahead).
            var status = route.Method == "GET"
                ? await SendAsync(api, route.Method, route.Concrete + "?q=x", cookie: null)
                : await SendAsync(api, route.Method, route.Concrete, cookie: null, body: "{}");
            if (status != StatusCodes.Status401Unauthorized)
            {
                open.Add($"{route.Key} -> {status}");
            }
        }

        Assert.True(open.Count == 0, "Routes reachable without an account:" + Environment.NewLine + string.Join(Environment.NewLine, open));
    }

    [Fact]
    public void Every_listed_exception_is_a_route_that_still_exists()
    {
        if (!fixture.Available)
        {
            return;
        }

        var keys = Routes().Select(r => r.Key).ToHashSet(StringComparer.Ordinal);
        var stale = Public.Keys.Concat(TierGated.Keys).Where(k => !keys.Contains(k)).ToList();

        Assert.True(stale.Count == 0, "Matrix entries for routes the host no longer maps: " + string.Join(", ", stale));
    }

    [Theory]
    [InlineData(AccountTier.Free)]
    [InlineData(AccountTier.Paid)]
    [InlineData(AccountTier.Founder)]
    public async Task Tier_gated_routes_answer_403_to_every_tier_they_do_not_name(AccountTier tier)
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAsync(tier);
        var cookie = await CookieAsync(accountId);
        var wrong = new List<string>();
        foreach (var (key, allowed) in TierGated)
        {
            var route = Routes().Single(r => r.Key == key);
            var status = await SendAsync(api, route.Method, route.Concrete, cookie, body: "{}");
            var denied = status == StatusCodes.Status403Forbidden;
            if (denied == allowed.Contains(tier))
            {
                wrong.Add($"{key} as {tier} -> {status}");
            }
        }

        Assert.True(wrong.Count == 0, "Tier gates answered the wrong tier:" + Environment.NewLine + string.Join(Environment.NewLine, wrong));
    }

    [Fact]
    public async Task A_free_account_reaches_no_founder_diagnostic_anywhere_in_the_table()
    {
        if (!fixture.Available)
        {
            return;
        }

        // Every GET under /api/dev, /api/metrics and /api/sync is read-only, so a Free account can
        // ask every one of them; any that answers 2xx and is founder data is a hole. The list of
        // GETs a Free account MAY read is the product's, so this checks only the founder prefixes.
        var accountId = await SeedAsync(AccountTier.Free);
        var cookie = await CookieAsync(accountId);
        var reached = new List<string>();
        foreach (var route in Routes().Where(r => r.Method == "GET" && r.Template.StartsWith("/api/dev/", StringComparison.Ordinal)))
        {
            var status = await SendAsync(api, route.Method, route.Concrete, cookie);
            if (status is >= 200 and < 300)
            {
                reached.Add($"{route.Key} -> {status}");
            }
        }

        Assert.True(reached.Count == 0, "A Free account read founder diagnostics:" + Environment.NewLine + string.Join(Environment.NewLine, reached));
    }

    private sealed record Route(string Method, string Template, string Concrete)
    {
        public string Key => $"{Method} {Template}";
    }

    private List<Route> Routes() =>
        [.. api.Services.GetServices<EndpointDataSource>()
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText is not null)
            .SelectMany(e => (e.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? ["GET"])
                .Select(method => new Route(method, Normalise(e.RoutePattern.RawText!), Concrete(Normalise(e.RoutePattern.RawText!)))))
            .DistinctBy(r => r.Key)
            .OrderBy(r => r.Template, StringComparer.Ordinal)
            .ThenBy(r => r.Method, StringComparer.Ordinal)];

    private static string Normalise(string raw) => "/" + raw.TrimStart('/');

    /// <summary>A request path for a template: a fixed GUID, number or word for each parameter.</summary>
    private static string Concrete(string template) =>
        Regex.Replace(template, @"\{([^}:?]+)(:[^}?]+)?\??\}", m => m.Groups[2].Value switch
        {
            ":guid" => "00000000-0000-0000-0000-000000000001",
            ":int" or ":long" => "1",
            _ when m.Groups[1].Value.EndsWith("Id", StringComparison.OrdinalIgnoreCase) => "1",
            _ => "x",
        });

    private async Task<Guid> SeedAsync(AccountTier tier)
    {
        var accountId = await api.SeedAccountAsync($"authz-{tier}-{Guid.NewGuid():N}");
        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ReelDbContext>();
        var account = await db.Accounts.IgnoreQueryFilters().SingleAsync(a => a.Id == accountId);
        account.Tier = tier;
        await db.SaveChangesAsync();
        return accountId;
    }

    private async Task<string> CookieAsync(Guid accountId)
    {
        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ReelDbContext>();
        var account = await db.Accounts.IgnoreQueryFilters().SingleAsync(a => a.Id == accountId);
        return "reel_at=" + scope.ServiceProvider.GetRequiredService<SessionService>().MintAccessToken(account, DateTime.UtcNow);
    }

    private static async Task<int> SendAsync(WebApplicationFactory<Program> host, string method, string path, string? cookie, string? body = null)
    {
        var context = await host.Server.SendAsync(c =>
        {
            c.Request.Method = method;
            var query = path.IndexOf('?', StringComparison.Ordinal);
            c.Request.Path = query < 0 ? path : path[..query];
            c.Request.QueryString = query < 0 ? QueryString.Empty : new QueryString(path[query..]);
            c.Connection.RemoteIpAddress = IPAddress.Loopback;
            if (cookie is not null)
            {
                c.Request.Headers.Cookie = cookie;
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
        return context.Response.StatusCode;
    }

    private sealed class HasBody : IHttpRequestBodyDetectionFeature
    {
        public bool CanHaveBody => true;
    }
}
