using FrostAura.Reel.Api.Endpoints;
using FrostAura.Reel.Application.Ml;
using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Application.Ranking;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Library;
using FrostAura.Reel.Domain.Ml;
using FrostAura.Reel.Domain.Sync;
using FrostAura.Reel.Domain.Tenancy;
using FrostAura.Reel.Infrastructure.Ml;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrostAura.Reel.Tests.IntegrationTests;

/// <summary>
/// Every reaction mutation survives losing the rebuild-enqueue race, on real Postgres.
///
/// <para>
/// The race is structural, not exotic. <see cref="FeedRefreshCoordinator.RequestRebuildAsync"/>
/// coalesces with a check-then-insert against the PARTIAL unique index
/// <c>IX_SyncJobs_AccountId_Kind WHERE Status IN ('Pending','Running')</c>. Two rapid saves — or a
/// save landing while a delta sync enqueues its own rebuild — both read "nothing in flight" and
/// both insert; Postgres rejects the loser with 23505. Before the absorber, that
/// <see cref="DbUpdateException"/> escaped unhandled and rolled back the ENTIRE reaction
/// transaction: the reaction row, the managed-list row and both outbox rows, answered as a 500.
/// The user's save silently did not happen, during exactly the nine-minute queueing burst the
/// founder described.
/// </para>
///
/// <para>
/// Each test reproduces the losing side deterministically rather than by timing: context A asks for
/// a rebuild (tracking an unsaved SyncJob insert), context B then commits a Pending BuildFeed row,
/// and the endpoint is finally invoked on context A — whose own coalescing check now sees the
/// winner, adds nothing more, and flushes the doomed insert alongside the user's change.
/// </para>
/// </summary>
[Collection("postgres")]
public class ReactionEnqueueRaceTests(PostgresFixture fixture)
{
    [Fact]
    public async Task A_rating_survives_a_lost_rebuild_enqueue_race()
    {
        if (!fixture.Available)
        {
            return; // no Postgres available — covered in CI's backend-integration job
        }

        var (accountId, tmdbId) = await SeedAsync(MediaType.Movie);

        await using var db = fixture.CreateContext(new AccountContext());
        await LoseTheRaceAsync(db, accountId);
        var result = await ReactionEndpoints.RateAsync(
            "movie", tmdbId, new ReactionEndpoints.RateRequest(9),
            db, accountId, FeedRefresh(db), Residual(db), CancellationToken.None);

        Assert.NotNull(result);
        await using var verify = fixture.CreateContext(new AccountContext());
        var rating = await verify.UserRatings.SingleAsync(r => r.AccountId == accountId);
        Assert.Equal((short)9, rating.Rating);
        Assert.True(await verify.TraktOutbox.AnyAsync(o => o.AccountId == accountId && o.Kind == OutboxKind.AddRating));
        // Exactly one in-flight BuildFeed: the winner's. The loser's insert was dropped, not retried.
        Assert.Equal(1, await InFlightBuildsAsync(verify, accountId));
    }

    [Fact]
    public async Task Removing_a_rating_survives_a_lost_rebuild_enqueue_race()
    {
        if (!fixture.Available)
        {
            return;
        }

        var (accountId, tmdbId) = await SeedAsync(MediaType.Movie);
        await using (var seed = fixture.CreateContext(new AccountContext()))
        {
            await ReactionEndpoints.RateAsync(
                "movie", tmdbId, new ReactionEndpoints.RateRequest(8),
                seed, accountId, FeedRefresh(seed), Residual(seed), CancellationToken.None);
        }

        await ClearInFlightBuildsAsync(accountId);

        await using var db = fixture.CreateContext(new AccountContext());
        await LoseTheRaceAsync(db, accountId);
        await ReactionEndpoints.RemoveRatingAsync(
            "movie", tmdbId, db, accountId, FeedRefresh(db), CancellationToken.None);

        await using var verify = fixture.CreateContext(new AccountContext());
        Assert.False(await verify.UserRatings.AnyAsync(r => r.AccountId == accountId)); // the retraction landed
        Assert.True(await verify.TraktOutbox.AnyAsync(o => o.AccountId == accountId && o.Kind == OutboxKind.RemoveRating));
    }

    [Fact]
    public async Task Not_interested_survives_a_lost_rebuild_enqueue_race()
    {
        if (!fixture.Available)
        {
            return;
        }

        var (accountId, tmdbId) = await SeedAsync(MediaType.Movie);

        await using var db = fixture.CreateContext(new AccountContext());
        await LoseTheRaceAsync(db, accountId);
        await ReactionEndpoints.NotInterestedAsync(
            "movie", tmdbId, new ReactionEndpoints.ReactionRequest("tone"),
            db, accountId, FeedRefresh(db), Residual(db), Dislikes(db), CancellationToken.None);

        await using var verify = fixture.CreateContext(new AccountContext());
        var reaction = await verify.UserTitleReactions.SingleAsync(r => r.AccountId == accountId);
        Assert.Equal(ReactionKind.NotInterested, reaction.Kind);
        Assert.Equal(ReactionReason.Tone, reaction.Reason); // the reason survived the rollback too
    }

    [Fact]
    public async Task Dropping_survives_a_lost_rebuild_enqueue_race()
    {
        if (!fixture.Available)
        {
            return;
        }

        var (accountId, tmdbId) = await SeedAsync(MediaType.Show);

        await using var db = fixture.CreateContext(new AccountContext());
        await LoseTheRaceAsync(db, accountId);
        await ReactionEndpoints.DroppedAsync(
            "show", tmdbId, db, accountId, FeedRefresh(db), Residual(db), Dislikes(db), CancellationToken.None);

        await using var verify = fixture.CreateContext(new AccountContext());
        var reaction = await verify.UserTitleReactions.SingleAsync(r => r.AccountId == accountId);
        Assert.Equal(ReactionKind.Dropped, reaction.Kind);
    }

    [Fact]
    public async Task Save_for_later_survives_a_lost_rebuild_enqueue_race_with_its_list_and_outbox_rows()
    {
        if (!fixture.Available)
        {
            return;
        }

        var (accountId, tmdbId) = await SeedAsync(MediaType.Movie);

        await using var db = fixture.CreateContext(new AccountContext());
        await LoseTheRaceAsync(db, accountId);
        await ReactionEndpoints.SaveForLaterAsync(
            "movie", tmdbId, db, accountId, FeedRefresh(db), Residual(db), CancellationToken.None);

        await using var verify = fixture.CreateContext(new AccountContext());
        // The whole transaction, not just the reaction: this is what the 500 used to take with it.
        Assert.Equal(ReactionKind.SaveForLater, (await verify.UserTitleReactions.SingleAsync(r => r.AccountId == accountId)).Kind);
        Assert.True(await verify.ManagedListItems.AnyAsync(m => m.AccountId == accountId && m.RemovedAt == null));
        Assert.True(await verify.TraktOutbox.AnyAsync(o => o.AccountId == accountId && o.Kind == OutboxKind.AddToWatchlist));
        Assert.True(await verify.TraktOutbox.AnyAsync(o => o.AccountId == accountId && o.Kind == OutboxKind.ListAdd));
    }

    [Fact]
    public async Task Revoking_survives_a_lost_rebuild_enqueue_race()
    {
        if (!fixture.Available)
        {
            return;
        }

        var (accountId, tmdbId) = await SeedAsync(MediaType.Movie);
        await using (var seed = fixture.CreateContext(new AccountContext()))
        {
            await ReactionEndpoints.SaveForLaterAsync(
                "movie", tmdbId, seed, accountId, FeedRefresh(seed), Residual(seed), CancellationToken.None);
        }

        await ClearInFlightBuildsAsync(accountId);

        await using var db = fixture.CreateContext(new AccountContext());
        await LoseTheRaceAsync(db, accountId);
        await ReactionEndpoints.RevokeAsync(
            "movie", tmdbId, "save_for_later", db, accountId, FeedRefresh(db), Dislikes(db), CancellationToken.None);

        await using var verify = fixture.CreateContext(new AccountContext());
        Assert.NotNull((await verify.UserTitleReactions.SingleAsync(r => r.AccountId == accountId)).RevokedAt);
        Assert.True(await verify.ManagedListItems.AnyAsync(
            m => m.AccountId == accountId && m.RemovalReason == ListRemovalReason.UserRemoved));
    }

    [Fact]
    public async Task A_genuine_unique_violation_still_surfaces_rather_than_being_swallowed()
    {
        if (!fixture.Available)
        {
            return;
        }

        // The absorber must be narrow. A duplicate rating row — the (Account, Subject, Title,
        // Season, Episode) unique index, nothing to do with job coalescing — has to keep throwing,
        // otherwise "retry once and hope" becomes the codebase's silent default save.
        var (accountId, tmdbId) = await SeedAsync(MediaType.Movie);
        await using var db = fixture.CreateContext(new AccountContext());
        var title = await db.Titles.SingleAsync(t => t.TmdbId == tmdbId);
        for (var i = 0; i < 2; i++)
        {
            db.UserRatings.Add(new UserRating
            {
                Id = Guid.NewGuid(),
                AccountId = accountId,
                TitleId = title.Id,
                SubjectType = RatingSubjectType.Movie,
                Rating = 7,
                RatedAt = DateTime.UtcNow,
                SyncedAt = DateTime.UtcNow,
            });
        }

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveAbsorbingRebuildRaceAsync(CancellationToken.None));
    }

    // ── harness ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Puts <paramref name="db"/> on the losing side of the coalescing race: it tracks an unsaved
    /// BuildFeed insert, and a second connection then commits the winning row.
    /// </summary>
    private async Task LoseTheRaceAsync(IReelDbContext db, Guid accountId)
    {
        var enqueued = await FeedRefresh(db).RequestRebuildAsync(accountId, "the doomed enqueue", CancellationToken.None);
        Assert.True(enqueued, "the coordinator must have decided to enqueue for the race to exist");

        await using var winner = fixture.CreateContext(new AccountContext());
        winner.SyncJobs.Add(new SyncJob
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            Kind = JobKind.BuildFeed,
            Priority = 1,
            EnqueuedAt = DateTime.UtcNow,
            TriggerReason = "the concurrent winner",
        });
        await winner.SaveChangesAsync();
    }

    private async Task ClearInFlightBuildsAsync(Guid accountId)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var jobs = await db.SyncJobs.Where(j => j.AccountId == accountId).ToListAsync();
        foreach (var job in jobs)
        {
            job.Status = JobStatus.Succeeded;
        }

        await db.SaveChangesAsync();
    }

    private static Task<int> InFlightBuildsAsync(IReelDbContext db, Guid accountId) =>
        db.SyncJobs.CountAsync(j => j.AccountId == accountId && j.Kind == JobKind.BuildFeed
            && (j.Status == JobStatus.Pending || j.Status == JobStatus.Running));

    private static FeedRefreshCoordinator FeedRefresh(IReelDbContext db) =>
        new(db, NullLogger<FeedRefreshCoordinator>.Instance);

    private static OnlineResidualModel Residual(IReelDbContext db) =>
        new(db, new FeatureVectorBuilder(db), new FastTreeModelEngine(),
            new MemoryCache(new MemoryCacheOptions()), NullLogger<OnlineResidualModel>.Instance);

    private static DislikeSignal Dislikes(IReelDbContext db) =>
        new(db, new MemoryCache(new MemoryCacheOptions()));

    /// <summary>An account with an Active artifact (without one the coordinator never enqueues) and one title.</summary>
    private async Task<(Guid AccountId, long TmdbId)> SeedAsync(MediaType media)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var account = new Account
        {
            Id = Guid.NewGuid(),
            TraktUserSlug = $"race-{Guid.NewGuid():N}",
            CreatedAt = DateTime.UtcNow,
            LastSeenAt = DateTime.UtcNow,
            PipelineStageChangedAt = DateTime.UtcNow,
        };
        db.Add(account);

        var title = new Title
        {
            Id = Guid.NewGuid(),
            MediaType = media,
            TmdbId = Random.Shared.NextInt64(1, long.MaxValue),
            TraktId = Random.Shared.NextInt64(1, long.MaxValue),
            TraktSlug = string.Empty,
            Name = $"{media} {Guid.NewGuid():N}",
            CreatedAt = DateTime.UtcNow,
        };
        db.Add(title);

        db.ModelArtifacts.Add(new ModelArtifact
        {
            Id = Guid.NewGuid(),
            AccountId = account.Id,
            Version = 1,
            Algo = "FastTree",
            ArtifactBytes = [],
            // The schema version must be current or the global query filter hides the artifact and
            // the coordinator declines to enqueue at all — no race, and a vacuously green test.
            FeatureSchemaJson = System.Text.Json.JsonSerializer.Serialize(
                new { version = FeatureSchema.Version, names = FeatureSchema.AllNames() }),
            TrainingRunId = Guid.NewGuid(),
            Status = ArtifactStatus.Active,
            TrainedAt = DateTime.UtcNow,
        });

        await db.SaveChangesAsync();
        return (account.Id, title.TmdbId!.Value);
    }
}
