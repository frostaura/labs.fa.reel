using System.Reflection;
using System.Text.Json;
using FrostAura.Reel.Application.Auth;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Domain.Ports.Trakt;
using FrostAura.Reel.Domain.Tenancy;
using FrostAura.Reel.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace FrostAura.Reel.Tests.IntegrationTests;

/// <summary>
/// WS-8's P2 account lines, end to end through the real pipeline: refresh-token family revocation on
/// reuse (J*4), the founder allow-list at the OAuth callback (K-10, FD-11), the account time zone
/// (ADD-22, M7), and sign-in, refresh and logout under <c>Tenancy__UnscopedReads=Throw</c> (J-11).
/// </summary>
[Collection("postgres")]
public class AccountLifecycleTests(PostgresFixture fixture, ReelApiFactory api) : IClassFixture<ReelApiFactory>
{
    [Fact]
    public async Task A_rotated_token_presented_after_the_grace_window_revokes_its_whole_family()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await api.SeedAccountAsync($"family-{Guid.NewGuid():N}");
        var first = await IssueRefreshTokenAsync(accountId);
        var otherDevice = await IssueRefreshTokenAsync(accountId);

        // first → second → third: one family, rotated twice; the browser now holds only `third`.
        var second = RefreshCookie(await EdgeSecurityTests.SendAsync(api, "POST", "/api/auth/refresh", cookie: $"reel_rt={first}"));
        var third = RefreshCookie(await EdgeSecurityTests.SendAsync(api, "POST", "/api/auth/refresh", cookie: $"reel_rt={second}"));
        Assert.NotEqual(second, third);

        // `first` was copied before it rotated, and comes back after the grace window.
        await using (var db = Db())
        {
            await db.RefreshSessions.IgnoreQueryFilters()
                .Where(s => s.AccountId == accountId && s.ReplacedBySessionId != null)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, DateTime.UtcNow - SessionService.RefreshGrace - TimeSpan.FromSeconds(1)));
        }

        Assert.Equal(StatusCodes.Status401Unauthorized, (await EdgeSecurityTests.SendAsync(api, "POST", "/api/auth/refresh", cookie: $"reel_rt={first}")).Status);

        // The family's live tip is gone with it: whoever holds `third` signs in again…
        Assert.Equal(StatusCodes.Status401Unauthorized, (await EdgeSecurityTests.SendAsync(api, "POST", "/api/auth/refresh", cookie: $"reel_rt={third}")).Status);
        // …while the account's session on another device, a different family, is untouched.
        Assert.Equal(StatusCodes.Status200OK, (await EdgeSecurityTests.SendAsync(api, "POST", "/api/auth/refresh", cookie: $"reel_rt={otherDevice}")).Status);

        await using var check = Db();
        var live = await check.RefreshSessions.IgnoreQueryFilters().Where(s => s.AccountId == accountId && s.RevokedAt == null).CountAsync();
        Assert.Equal(1, live);
    }

    [Fact]
    public async Task A_listed_founder_signs_in_as_Founder_in_Production_with_tenancy_failing_closed()
    {
        if (!fixture.Available)
        {
            return;
        }

        var founderSlug = $"founder-{Guid.NewGuid():N}"[..30];
        var trakt = SignInTrakt.Create(founderSlug);
        await using var host = ProductionHost(trakt, founderSlugs: $"someone-else, {founderSlug.ToUpperInvariant()}");

        // The browser still carries an access cookie for a different account: the callback must
        // answer for the account it signed in, not for the stale cookie.
        var staleAccount = await api.SeedAccountAsync($"stale-{Guid.NewGuid():N}");
        var staleCookie = await AccessCookieAsync(staleAccount);

        var callback = await CallbackAsync(host, staleCookie);

        Assert.Equal(StatusCodes.Status200OK, callback.Status);
        using var payload = JsonDocument.Parse(callback.Body);
        Assert.Equal("Founder", payload.RootElement.GetProperty("tier").GetString());
        Assert.Equal(founderSlug, payload.RootElement.GetProperty("traktSlug").GetString());
        await using (var db = Db())
        {
            var account = await db.Accounts.SingleAsync(a => a.TraktUserSlug == founderSlug);
            Assert.Equal(AccountTier.Founder, account.Tier);
            Assert.Equal(account.Id.ToString(), payload.RootElement.GetProperty("accountId").GetString());
        }

        // Refresh and logout hold under Throw as well.
        var refresh = RefreshCookie(callback);
        var refreshed = await EdgeSecurityTests.SendAsync(host, "POST", "/api/auth/refresh", cookie: $"reel_rt={refresh}");
        Assert.Equal(StatusCodes.Status200OK, refreshed.Status);
        var logout = await EdgeSecurityTests.SendAsync(host, "POST", "/api/auth/logout", cookie: $"reel_rt={RefreshCookie(refreshed)}");
        Assert.Equal(StatusCodes.Status200OK, logout.Status);
    }

    [Fact]
    public async Task An_unlisted_profile_signs_in_as_Free_and_the_list_never_demotes()
    {
        if (!fixture.Available)
        {
            return;
        }

        var slug = $"free-{Guid.NewGuid():N}"[..30];
        await using var host = ProductionHost(SignInTrakt.Create(slug), founderSlugs: "someone-else");

        var first = await CallbackAsync(host, cookie: null);
        Assert.Equal(StatusCodes.Status200OK, first.Status);
        using (var payload = JsonDocument.Parse(first.Body))
        {
            Assert.Equal("Free", payload.RootElement.GetProperty("tier").GetString());
        }

        // Founder granted by hand during the beta survives a sign-in the list does not cover.
        await using (var db = Db())
        {
            await db.Accounts.Where(a => a.TraktUserSlug == slug).ExecuteUpdateAsync(s => s.SetProperty(a => a.Tier, AccountTier.Founder));
        }

        var again = await CallbackAsync(host, cookie: null);
        using var second = JsonDocument.Parse(again.Body);
        Assert.Equal("Founder", second.RootElement.GetProperty("tier").GetString());
    }

    [Theory]
    [InlineData("{\"timeZone\":\"Mars/Olympus_Mons\"}")]
    [InlineData("{\"timeZone\":\"+02:00\"}")]
    [InlineData("{\"timeZone\":\"South Africa Standard Time\"}")]
    [InlineData("{\"timeZone\":\"../../etc/passwd\"}")]
    [InlineData("{\"region\":\"GB\",\"timeZone\":\"Nowhere\"}")]
    public async Task An_unknown_time_zone_is_400_and_changes_nothing(string body)
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await api.SeedAccountAsync($"tz-bad-{Guid.NewGuid():N}");

        var answer = await EdgeSecurityTests.SendAsync(api, "PUT", "/api/settings", await AccessCookieAsync(accountId), body: body);

        Assert.Equal(StatusCodes.Status400BadRequest, answer.Status);
        using var json = JsonDocument.Parse(answer.Body);
        Assert.Equal("time_zone_invalid", json.RootElement.GetProperty("code").GetString());
        await using var db = Db();
        var account = await db.Accounts.SingleAsync(a => a.Id == accountId);
        Assert.Null(account.Settings.TimeZone);
        Assert.Equal("ZA", account.Region);
    }

    [Fact]
    public async Task A_time_zone_is_saved_served_and_cleared()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await api.SeedAccountAsync($"tz-ok-{Guid.NewGuid():N}");
        var cookie = await AccessCookieAsync(accountId);

        var unset = await EdgeSecurityTests.SendAsync(api, "GET", "/api/settings", cookie);
        using (var json = JsonDocument.Parse(unset.Body))
        {
            Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("timeZone").ValueKind);
            Assert.Equal(AccountTimeZone.Default, json.RootElement.GetProperty("effectiveTimeZone").GetString());
        }

        var saved = await EdgeSecurityTests.SendAsync(api, "PUT", "/api/settings", cookie, body: "{\"timeZone\":\" Africa/Johannesburg \"}");
        Assert.Equal(StatusCodes.Status200OK, saved.Status);
        using (var json = JsonDocument.Parse(saved.Body))
        {
            Assert.Equal("Africa/Johannesburg", json.RootElement.GetProperty("timeZone").GetString());
            Assert.Equal("Africa/Johannesburg", json.RootElement.GetProperty("effectiveTimeZone").GetString());
        }

        // Another screen's PUT, which does not carry the field, leaves it alone.
        await EdgeSecurityTests.SendAsync(api, "PUT", "/api/settings", cookie, body: "{\"region\":\"GB\"}");
        await using (var db = Db())
        {
            Assert.Equal("Africa/Johannesburg", (await db.Accounts.SingleAsync(a => a.Id == accountId)).Settings.TimeZone);
        }

        var cleared = await EdgeSecurityTests.SendAsync(api, "PUT", "/api/settings", cookie, body: "{\"timeZone\":\"\"}");
        using (var json = JsonDocument.Parse(cleared.Body))
        {
            Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("timeZone").ValueKind);
        }
    }

    private WebApplicationFactory<Program> ProductionHost(ITraktClient trakt, string founderSlugs) =>
        api.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(Environments.Production);
            builder.ConfigureAppConfiguration(configuration => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                [FounderAccessOptions.SlugsKey] = founderSlugs,
                [UnscopedReadPolicy.ConfigurationKey] = nameof(UnscopedReadMode.Throw),
            }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ITraktClient>();
                services.AddSingleton(trakt);
            });
        });

    private static async Task<EdgeSecurityTests.Answer> CallbackAsync(WebApplicationFactory<Program> host, string? cookie)
    {
        var state = host.Services.GetRequiredService<OAuthStateCodec>().Issue();
        return await EdgeSecurityTests.SendAsync(
            host, "POST", "/api/auth/trakt/callback", cookie, body: JsonSerializer.Serialize(new { code = "test-code", state }));
    }

    private static string RefreshCookie(EdgeSecurityTests.Answer answer)
    {
        Assert.Equal(StatusCodes.Status200OK, answer.Status);
        var cookie = Assert.Single(answer.SetCookies, c => c.StartsWith("reel_rt=", StringComparison.Ordinal));
        return cookie["reel_rt=".Length..cookie.IndexOf(';', StringComparison.Ordinal)];
    }

    private ReelDbContext Db() => api.Services.CreateScope().ServiceProvider.GetRequiredService<ReelDbContext>();

    private async Task<string> IssueRefreshTokenAsync(Guid accountId)
    {
        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ReelDbContext>();
        var account = await db.Accounts.SingleAsync(a => a.Id == accountId);
        return (await scope.ServiceProvider.GetRequiredService<SessionService>().IssueAsync(account, "account-lifecycle-tests")).RefreshToken!;
    }

    private async Task<string> AccessCookieAsync(Guid accountId)
    {
        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ReelDbContext>();
        var account = await db.Accounts.SingleAsync(a => a.Id == accountId);
        return "reel_at=" + scope.ServiceProvider.GetRequiredService<SessionService>().MintAccessToken(account, DateTime.UtcNow);
    }

    /// <summary>
    /// A Trakt that answers the two sign-in calls — the code exchange and the profile — for one
    /// slug, and throws on everything else, so a sign-in that strays into sync fails loudly.
    /// </summary>
    public class SignInTrakt : DispatchProxy
    {
        private string slug = string.Empty;

        public static ITraktClient Create(string slug)
        {
            var proxy = Create<ITraktClient, SignInTrakt>();
            ((SignInTrakt)(object)proxy).slug = slug;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => targetMethod?.Name switch
        {
            nameof(ITraktClient.ExchangeCodeAsync) => Task.FromResult(new TraktTokenResponse(
                "test-access", "test-refresh", "public", DateTimeOffset.UtcNow.ToUnixTimeSeconds(), 7_776_000)),
            nameof(ITraktClient.GetUserSettingsAsync) => Task.FromResult(new TraktUserSettings(slug, slug, null, false, null)),
            _ => throw new NotSupportedException($"Sign-in made an unexpected Trakt call: {targetMethod?.Name}."),
        };
    }
}
