using System.Reflection;
using System.Text.Json;
using FrostAura.Reel.Api.Endpoints;
using FrostAura.Reel.Application.Auth;
using FrostAura.Reel.Application.Llm;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Domain.Ports.Trakt;
using FrostAura.Reel.Domain.Sync;
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

    /// <summary>
    /// R1-02: the session payload is read as the account it answers for. Called on a request that
    /// is not pinned (sign-in before the pin) it must not read unscoped — under Throw that is a 500
    /// — and on one pinned to a different account (a stale cookie) it must not read through that
    /// account's filter, which sees no connection and prints Degraded for an account the status poll
    /// calls NotEnoughRatings.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_session_payload_is_read_as_the_account_it_returns_under_Throw(bool pinnedToAnotherAccount)
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedLinkedAccountAsync($"payload-{Guid.NewGuid():N}", "Degraded");
        var stale = await api.SeedAccountAsync($"payload-stale-{Guid.NewGuid():N}");
        await using var host = ProductionHost(SignInTrakt.Create("unused"), founderSlugs: "someone-else");

        using var scope = host.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<IAccountContext>();
        if (pinnedToAnotherAccount)
        {
            context.SetAccount(stale);
        }

        var db = scope.ServiceProvider.GetRequiredService<ReelDbContext>();
        var account = await db.Accounts.IgnoreQueryFilters().AsNoTracking().SingleAsync(a => a.Id == accountId);

        var payload = await account.ToSessionPayloadAsync(new DefaultHttpContext { RequestServices = scope.ServiceProvider }, CancellationToken.None);

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(payload));
        Assert.Equal("NotEnoughRatings", json.RootElement.GetProperty("pipelineStage").GetString());
        Assert.Equal(accountId, context.AccountId);
    }

    /// <summary>
    /// R1-02: sign-in, refresh-then-me, all under Throw, print the stage the status poll prints —
    /// including a stage written by a newer image, which reads as the shared Degraded fallback, not
    /// as the cold-start park a copied ratings-floor rule turned it into.
    /// </summary>
    [Theory]
    [InlineData("Hibernating", "Degraded")]
    [InlineData("Degraded", "NotEnoughRatings")]
    [InlineData("FeedReady", "FeedReady")]
    public async Task Sign_in_and_me_print_the_status_polls_stage_under_Throw(string storedStage, string expected)
    {
        if (!fixture.Available)
        {
            return;
        }

        var slug = $"stage-{Guid.NewGuid():N}"[..30];
        await using var host = ProductionHost(SignInTrakt.Create(slug), founderSlugs: "someone-else");

        // The first sign-in creates the account and its Active connection; then a stage is stored.
        Assert.Equal(StatusCodes.Status200OK, (await CallbackAsync(host, cookie: null)).Status);
        await using (var db = Db())
        {
            var id = await db.Accounts.Where(a => a.TraktUserSlug == slug).Select(a => a.Id).SingleAsync();
            await db.Database.ExecuteSqlAsync($"""UPDATE "Accounts" SET "PipelineStage" = {storedStage} WHERE "Id" = {id}""");
        }

        var callback = await CallbackAsync(host, cookie: null);
        Assert.Equal(expected, Stage(callback));

        var refreshed = await EdgeSecurityTests.SendAsync(host, "POST", "/api/auth/refresh", cookie: $"reel_rt={RefreshCookie(callback)}");
        var access = AccessCookie(refreshed);
        var me = await EdgeSecurityTests.SendAsync(host, "GET", "/api/auth/me", access);
        Assert.Equal(StatusCodes.Status200OK, me.Status);
        Assert.Equal(expected, Stage(me));

        var status = await EdgeSecurityTests.SendAsync(host, "GET", "/api/sync/status", access);
        Assert.Equal(StatusCodes.Status200OK, status.Status);
        Assert.Equal(expected, Stage(status));
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

    /// <summary>
    /// R1-15: a changed zone reaches the budget calendar on the next call, not up to
    /// <see cref="BudgetCalendar.ZoneLifetime"/> later. The calendar here is the production one, fed by
    /// a zone source that reads <c>AccountSettings.TimeZone</c>, at a fixed noon UTC — when Kiritimati
    /// (+14:00) is already on the next date. Since WS-1's R1-14 residual the ceiling check reads the
    /// zone fresh on every call, so no settings PUT has to tell the calendar anything.
    /// </summary>
    [Fact]
    public async Task A_changed_time_zone_reaches_the_budget_calendar_at_once()
    {
        if (!fixture.Available)
        {
            return;
        }

        var noon = new DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);
        var calendar = new SettingsZoneCalendar(noon);
        await using var host = api.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IBudgetCalendar>();
            services.AddSingleton<IBudgetCalendar>(sp =>
            {
                calendar.Attach(sp.GetRequiredService<IServiceScopeFactory>());
                return calendar;
            });
        }));
        var accountId = await api.SeedAccountAsync($"tz-calendar-{Guid.NewGuid():N}");
        var cookie = await AccessCookieAsync(accountId);
        var budgets = host.Services.GetRequiredService<IBudgetCalendar>();

        // Cached as UTC by a ceiling check before the account had a zone.
        Assert.Equal(new DateTime(2026, 10, 27), await budgets.TodayAsync(accountId, CancellationToken.None));

        await EdgeSecurityTests.SendAsync(host, "PUT", "/api/settings", cookie, body: "{\"timeZone\":\"Pacific/Kiritimati\"}");
        Assert.Equal(new DateTime(2026, 10, 28), await budgets.TodayAsync(accountId, CancellationToken.None));

        // Another screen's PUT, and one re-sending the zone it already has, change no zone.
        await EdgeSecurityTests.SendAsync(host, "PUT", "/api/settings", cookie, body: "{\"region\":\"GB\"}");
        await EdgeSecurityTests.SendAsync(host, "PUT", "/api/settings", cookie, body: "{\"timeZone\":\"Pacific/Kiritimati\"}");
        Assert.Equal(new DateTime(2026, 10, 28), await budgets.TodayAsync(accountId, CancellationToken.None));

        // A refused zone changes nothing either; clearing it goes back to UTC at once.
        await EdgeSecurityTests.SendAsync(host, "PUT", "/api/settings", cookie, body: "{\"timeZone\":\"Mars/Olympus_Mons\"}");
        Assert.Equal(new DateTime(2026, 10, 28), await budgets.TodayAsync(accountId, CancellationToken.None));
        await EdgeSecurityTests.SendAsync(host, "PUT", "/api/settings", cookie, body: "{\"timeZone\":\"\"}");
        Assert.Equal(new DateTime(2026, 10, 27), await budgets.TodayAsync(accountId, CancellationToken.None));
    }

    /// <summary>The production <see cref="BudgetCalendar"/> at a fixed instant, reading the zone from settings.</summary>
    private sealed class SettingsZoneCalendar(DateTimeOffset now) : IBudgetCalendar
    {
        private BudgetCalendar inner = new(zones: null);

        public void Attach(IServiceScopeFactory scopes) => inner = new BudgetCalendar(new SettingsZones(scopes), new FixedClock(now));

        public DateTime DayFor(Guid? accountId, string? purpose) => inner.DayFor(accountId, purpose);

        public Task<DateTime> TodayAsync(Guid accountId, CancellationToken ct) => inner.TodayAsync(accountId, ct);
    }

    /// <summary>Reads the zone where WS-8's M7 stores it, <c>AccountSettings.TimeZone</c>.</summary>
    private sealed class SettingsZones(IServiceScopeFactory scopes) : IAccountZoneSource
    {
        public async Task<string?> ZoneIdAsync(Guid accountId, CancellationToken ct)
        {
            using var scope = scopes.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<ReelDbContext>().Accounts.IgnoreQueryFilters()
                .Where(a => a.Id == accountId).Select(a => a.Settings.TimeZone).FirstOrDefaultAsync(ct);
        }

        public string? ZoneId(Guid accountId) => ZoneIdAsync(accountId, CancellationToken.None).GetAwaiter().GetResult();
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
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

    private static string? Stage(EdgeSecurityTests.Answer answer)
    {
        Assert.Equal(StatusCodes.Status200OK, answer.Status);
        using var json = JsonDocument.Parse(answer.Body);
        return json.RootElement.GetProperty("pipelineStage").GetString();
    }

    private static string AccessCookie(EdgeSecurityTests.Answer answer)
    {
        Assert.Equal(StatusCodes.Status200OK, answer.Status);
        var cookie = Assert.Single(answer.SetCookies, c => c.StartsWith("reel_at=", StringComparison.Ordinal));
        return cookie[..cookie.IndexOf(';', StringComparison.Ordinal)];
    }

    /// <summary>An account with an Active Trakt connection, no ratings, and <paramref name="storedStage"/> as stored text.</summary>
    private async Task<Guid> SeedLinkedAccountAsync(string slug, string storedStage)
    {
        var accountId = await api.SeedAccountAsync(slug);
        await using var db = Db();
        db.TraktConnections.Add(new TraktConnection
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            ExpiresAt = DateTime.UtcNow.AddDays(30),
            Status = ConnectionStatus.Active,
        });
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlAsync($"""UPDATE "Accounts" SET "PipelineStage" = {storedStage} WHERE "Id" = {accountId}""");
        return accountId;
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
