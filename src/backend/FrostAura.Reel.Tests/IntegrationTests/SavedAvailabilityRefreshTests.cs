using System.Collections.Concurrent;
using System.Reflection;
using FrostAura.Reel.Application.Jobs;
using FrostAura.Reel.Application.Saved;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Library;
using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Domain.Ports.Tmdb;
using FrostAura.Reel.Domain.Providers;
using FrostAura.Reel.Domain.Sync;
using FrostAura.Reel.Domain.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrostAura.Reel.Tests.IntegrationTests;

/// <summary>
/// WS-4 task 6: live saves get fresh availability. The availability job used to walk only the
/// active feed snapshot — which by design never contains a saved title — so a save was priced once,
/// aged past "streaming now" freshness and dropped off the Tonight order. Each test varies one fact.
/// </summary>
[Collection("postgres")]
public class SavedAvailabilityRefreshTests(PostgresFixture fixture)
{
    [Fact]
    public async Task The_job_refreshes_stale_and_unpriced_live_saves_even_without_a_feed()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        var neverPriced = await SeedSaveAsync(accountId);
        var stale = await SeedSaveAsync(accountId, snapshotAge: TimeSpan.FromDays(3));
        var fresh = await SeedSaveAsync(accountId, snapshotAge: TimeSpan.FromHours(2));
        var consumed = await SeedSaveAsync(accountId, ratedAfterSave: true);
        var revoked = await SeedSaveAsync(accountId, revoked: true);

        var tmdb = ProviderScript.Create(out var script);
        await RunJobAsync(accountId, tmdb);

        Assert.Contains(neverPriced.TmdbId!.Value, script.Calls);
        Assert.Contains(stale.TmdbId!.Value, script.Calls);
        Assert.DoesNotContain(fresh.TmdbId!.Value, script.Calls);
        Assert.DoesNotContain(consumed.TmdbId!.Value, script.Calls);
        Assert.DoesNotContain(revoked.TmdbId!.Value, script.Calls);

        await using var db = fixture.CreateContext(new AccountContext());
        var cutoff = DateTime.UtcNow.AddMinutes(-5);
        Assert.True(await db.TitleAvailabilities.AnyAsync(a => a.TitleId == neverPriced.Id && a.FetchedAt > cutoff));
        Assert.True(await db.TitleAvailabilities.AnyAsync(a => a.TitleId == stale.Id && a.FetchedAt > cutoff));
    }

    [Fact]
    public async Task One_run_refreshes_at_most_forty_saves_never_priced_first()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        var aged = await SeedSaveAsync(accountId, snapshotAge: TimeSpan.FromDays(10));
        var unpriced = new List<Title>();
        for (var i = 0; i < SavedAvailabilityRefresh.MaxSaves; i++)
        {
            unpriced.Add(await SeedSaveAsync(accountId));
        }

        var tmdb = ProviderScript.Create(out var script);
        await RunJobAsync(accountId, tmdb);

        Assert.Equal(SavedAvailabilityRefresh.MaxSaves, script.Calls.Count);
        Assert.All(unpriced, t => Assert.Contains(t.TmdbId!.Value, script.Calls));
        // The 41st — the one with an old snapshot — waits for the next run.
        Assert.DoesNotContain(aged.TmdbId!.Value, script.Calls);
    }

    [Fact]
    public async Task Opening_saved_queues_one_refresh_a_day_and_only_when_a_save_is_stale()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        var save = await SeedSaveAsync(accountId, snapshotAge: TimeSpan.FromHours(2));
        var now = DateTime.UtcNow;

        // Every save fresh: nothing to do, nothing queued.
        Assert.False(await RequestAsync(accountId, now));

        await AgeSnapshotAsync(save.Id, TimeSpan.FromDays(2));
        Assert.True(await RequestAsync(accountId, now));

        // In flight: the open after it does not queue a second.
        Assert.False(await RequestAsync(accountId, now));

        // Done, but inside the debounce window: still nothing.
        await SetJobsAsync(accountId, job => job.Status = JobStatus.Succeeded);
        Assert.False(await RequestAsync(accountId, now.AddHours(23)));

        // A day on, a still-stale save earns another refresh.
        Assert.True(await RequestAsync(accountId, now.AddHours(25)));

        await using var db = fixture.CreateContext(new AccountContext());
        var jobs = await db.SyncJobs.AsNoTracking()
            .Where(j => j.AccountId == accountId && j.Kind == JobKind.RefreshAvailability)
            .ToListAsync();
        Assert.Equal(2, jobs.Count);
        Assert.All(jobs, j => Assert.Equal(SavedAvailabilityRefresh.TriggerReason, j.TriggerReason));
    }

    private async Task<bool> RequestAsync(Guid accountId, DateTime now)
    {
        var context = new AccountContext();
        context.SetAccount(accountId);
        await using var db = fixture.CreateContext(context);
        var account = await db.Accounts.SingleAsync(a => a.Id == accountId);
        return await SavedAvailabilityRefresh.RequestAsync(db, account, now, CancellationToken.None);
    }

    private async Task SetJobsAsync(Guid accountId, Action<SyncJob> change)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        foreach (var job in await db.SyncJobs.Where(j => j.AccountId == accountId).ToListAsync())
        {
            change(job);
        }

        await db.SaveChangesAsync();
    }

    private async Task RunJobAsync(Guid accountId, ITmdbClient tmdb)
    {
        var context = new AccountContext();
        context.SetAccount(accountId);
        await using var db = fixture.CreateContext(context);
        await new RefreshAvailabilityJobHandler(db, tmdb, NullLogger<RefreshAvailabilityJobHandler>.Instance)
            .ExecuteAsync(new SyncJob { Id = Guid.NewGuid(), AccountId = accountId, Kind = JobKind.RefreshAvailability }, CancellationToken.None);
    }

    private async Task AgeSnapshotAsync(Guid titleId, TimeSpan by)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        foreach (var row in await db.TitleAvailabilities.Where(a => a.TitleId == titleId).ToListAsync())
        {
            row.FetchedAt -= by;
        }

        await db.SaveChangesAsync();
    }

    private async Task<Guid> SeedAccountAsync()
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var now = DateTime.UtcNow;
        var account = new Account
        {
            Id = Guid.NewGuid(),
            TraktUserSlug = $"saved-availability-{Guid.NewGuid():N}",
            DisplayName = "Saved Availability Fixture",
            Region = "US",
            CreatedAt = now,
            LastSeenAt = now,
            PipelineStageChangedAt = now,
        };
        db.Add(account);
        await db.SaveChangesAsync();
        return account.Id;
    }

    private async Task<Title> SeedSaveAsync(
        Guid accountId, TimeSpan? snapshotAge = null, bool ratedAfterSave = false, bool revoked = false)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var now = DateTime.UtcNow;
        var title = new Title
        {
            Id = Guid.NewGuid(),
            MediaType = MediaType.Movie,
            TmdbId = Random.Shared.NextInt64(1, long.MaxValue),
            TraktSlug = $"saved-availability-{Guid.NewGuid():N}",
            Name = $"Saved Availability {Guid.NewGuid():N}",
            CreatedAt = now,
        };
        db.Add(title);
        db.UserTitleReactions.Add(new UserTitleReaction
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            TitleId = title.Id,
            Kind = ReactionKind.SaveForLater,
            CreatedAt = now.AddDays(-30),
            RevokedAt = revoked ? now.AddDays(-1) : null,
        });

        if (ratedAfterSave)
        {
            db.UserRatings.Add(new UserRating
            {
                Id = Guid.NewGuid(),
                AccountId = accountId,
                TitleId = title.Id,
                SubjectType = RatingSubjectType.Movie,
                Rating = 7,
                RatedAt = now.AddDays(-2),
                FirstRatedAt = now.AddDays(-2),
                SyncedAt = now,
                Source = RatingSource.Trakt,
            });
        }

        if (snapshotAge is { } age)
        {
            var provider = new StreamingProvider
            {
                Id = Guid.NewGuid(),
                TmdbProviderId = Random.Shared.Next(200_000, int.MaxValue),
                Name = $"Provider {Guid.NewGuid():N}",
                DisplayPriority = 5,
            };
            db.StreamingProviders.Add(provider);
            db.TitleAvailabilities.Add(new TitleAvailability
            {
                Id = Guid.NewGuid(),
                TitleId = title.Id,
                Region = "US",
                ProviderId = provider.Id,
                Kind = AvailabilityKind.Flatrate,
                FetchedAt = now - age,
            });
        }

        // Unconsumed/consumed is decided against these; every other save has neither.
        await db.SaveChangesAsync();
        return title;
    }

    /// <summary>Answers watch-provider lookups with nothing and records which titles were asked about; refuses any other TMDB call.</summary>
    public class ProviderScript : DispatchProxy
    {
        public ConcurrentBag<long> Calls { get; } = [];

        public static ITmdbClient Create(out ProviderScript script)
        {
            var proxy = Create<ITmdbClient, ProviderScript>();
            script = (ProviderScript)(object)proxy;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name != nameof(ITmdbClient.GetWatchProvidersAsync))
            {
                throw new NotSupportedException($"RefreshAvailability called TMDB {targetMethod?.Name}.");
            }

            Calls.Add((long)args![0]!);
            return Task.FromResult<IReadOnlyList<TmdbWatchProvider>>([new TmdbWatchProvider(Random.Shared.Next(300_000, int.MaxValue), $"Provider {Guid.NewGuid():N}", "/p.png", 1, AvailabilityKind.Flatrate)]);
        }
    }
}
