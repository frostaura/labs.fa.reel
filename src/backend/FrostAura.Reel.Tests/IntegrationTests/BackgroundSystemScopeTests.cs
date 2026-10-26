using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Feed;
using FrostAura.Reel.Domain.Sync;
using FrostAura.Reel.Domain.Tenancy;
using FrostAura.Reel.Infrastructure.Background;
using FrostAura.Reel.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrostAura.Reel.Tests.IntegrationTests;

/// <summary>
/// WS-4 task 10 (J-11): WS-4's cross-account background passes declare system scope, so they keep
/// working when <c>Tenancy:UnscopedReads</c> becomes Throw. Each pass runs here against a container
/// whose <see cref="IAccountContext"/> refuses every unpinned, unscoped read — exactly the production
/// container after the flip — and must still see every account.
/// </summary>
[Collection("postgres")]
public class BackgroundSystemScopeTests(PostgresFixture fixture)
{
    [Fact]
    public async Task The_delta_poll_and_the_digest_scheduler_sweep_every_account_under_Throw()
    {
        if (!fixture.Available)
        {
            return;
        }

        var now = DateTime.UtcNow;
        var accounts = new[] { await SeedConnectedAccountAsync(now), await SeedConnectedAccountAsync(now) };
        await using var services = StrictServices();
        var scopes = services.GetRequiredService<IServiceScopeFactory>();
        var configuration = services.GetRequiredService<IConfiguration>();

        var poll = new TraktDeltaPollService(scopes, configuration, NullLogger<TraktDeltaPollService>.Instance);
        var digest = new WeeklyDigestService(scopes, configuration, NullLogger<WeeklyDigestService>.Instance);
        Assert.True(await poll.ScheduleAsync(now, 5, 15, CancellationToken.None) >= accounts.Length);
        Assert.True(await digest.ScheduleAsync(now, CancellationToken.None) >= accounts.Length);

        await using var db = fixture.CreateContext(new AccountContext());
        foreach (var account in accounts)
        {
            Assert.Equal(1, await db.SyncJobs.CountAsync(j => j.AccountId == account && j.Kind == JobKind.DeltaSync));
            Assert.Equal(1, await db.SyncJobs.CountAsync(j => j.AccountId == account && j.Kind == JobKind.BuildDigest));
        }
    }

    [Fact]
    public async Task The_boot_repairs_run_under_Throw()
    {
        if (!fixture.Available)
        {
            return;
        }

        await using var services = StrictServices();
        var repairs = new DataRepairStartupService(
            services.GetRequiredService<IServiceScopeFactory>(),
            services.GetRequiredService<IConfiguration>(),
            NullLogger<DataRepairStartupService>.Instance);

        // The service logs and swallows a failed boot; an empty result list is how a refused read shows.
        await repairs.StartingAsync(CancellationToken.None);
        Assert.NotEmpty(repairs.LastResults);
    }

    /// <summary>
    /// The structural half: no WS-4 background service opens a DI scope that is not a system scope,
    /// so a pass added later to one of them inherits the declaration instead of re-opening the hole.
    /// </summary>
    [Theory]
    [InlineData("CatalogFollowThroughService")]
    [InlineData("CatalogGrowthService")]
    [InlineData("DataRepairStartupService")]
    [InlineData("TitleLanguageBackfillService")]
    [InlineData("TokenRefreshService")]
    [InlineData("TraktDeltaPollService")]
    [InlineData("TraktOutboxDispatcherService")]
    [InlineData("TraktRatingsBackfillService")]
    [InlineData("WeeklyDigestService")]
    public void Every_WS4_background_service_opens_only_system_scopes(string service)
    {
        var path = Path.Combine(RepoBackend(), "FrostAura.Reel.Infrastructure", "Background", service + ".cs");
        var source = File.ReadAllText(path);
        Assert.DoesNotContain(".CreateScope()", source, StringComparison.Ordinal);
        Assert.DoesNotContain(".CreateAsyncScope()", source, StringComparison.Ordinal);
        Assert.Contains(".CreateSystemScope()", source, StringComparison.Ordinal);
    }

    private ServiceProvider StrictServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddScoped<IAccountContext>(_ => new AccountContext(new UnscopedReadPolicy(UnscopedReadMode.Throw)));
        services.AddDbContext<ReelDbContext>(o => o.UseNpgsql(fixture.ConnectionString, npgsql => npgsql.UseVector()));
        services.AddScoped<IReelDbContext>(sp => sp.GetRequiredService<ReelDbContext>());
        return services.BuildServiceProvider(validateScopes: true);
    }

    private async Task<Guid> SeedConnectedAccountAsync(DateTime now)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var account = new Account
        {
            Id = Guid.NewGuid(),
            TraktUserSlug = $"system-scope-{Guid.NewGuid():N}",
            DisplayName = "System Scope Fixture",
            CreatedAt = now,
            LastSeenAt = now,
            PipelineStageChangedAt = now,
        };
        db.Add(account);
        db.TraktConnections.Add(new TraktConnection
        {
            Id = Guid.NewGuid(),
            AccountId = account.Id,
            ExpiresAt = now.AddDays(60),
            Status = ConnectionStatus.Active,
        });
        await db.SaveChangesAsync();

        // Neither pass may skip it for an existing digest or job.
        var weekStart = DigestWeek.MondayOf(now);
        Assert.False(await db.AccountDigests.AnyAsync(d => d.AccountId == account.Id && d.WeekStart == weekStart));
        return account.Id;
    }

    private static string RepoBackend()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "FrostAura.Reel.Infrastructure")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new DirectoryNotFoundException("src/backend not found above the test binaries.");
    }
}
