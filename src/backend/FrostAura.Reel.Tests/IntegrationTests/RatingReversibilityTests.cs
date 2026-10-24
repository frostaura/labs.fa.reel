using System.Text.Json;
using FrostAura.Reel.Api.Endpoints;
using FrostAura.Reel.Application.Ml;
using FrostAura.Reel.Application.Ml.ClosedLoop;
using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Application.Ranking;
using FrostAura.Reel.Application.Saved;
using FrostAura.Reel.Application.Sync;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Library;
using FrostAura.Reel.Domain.Sync;
using FrostAura.Reel.Domain.Tenancy;
using FrostAura.Reel.Infrastructure.Ml;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrostAura.Reel.Tests.IntegrationTests;

/// <summary>
/// The rating endpoint's history contract, on real Postgres against the extracted handlers:
/// a duplicate same-value rating changes nothing (no outbox rows, no play increments); a show
/// rating syncs the rating alone and never touches watched state; un-rating a movie reverses
/// exactly the play Reel logged (RemoveFromHistory + local row gone); a movie already watched
/// via ingest gets the rating only — its plays are not Reel's to log or reverse.
/// </summary>
[Collection("postgres")]
public class RatingReversibilityTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Duplicate_same_value_rating_changes_nothing()
    {
        if (!fixture.Available)
        {
            return; // no Postgres available — covered in CI's backend-integration job
        }

        var accountId = await SeedAccountAsync();
        var tmdbId = await SeedTitleAsync(MediaType.Movie);

        await RateAsync(accountId, "movie", tmdbId, 8);
        var rowsAfterFirst = await OutboxCountAsync(accountId);

        await RateAsync(accountId, "movie", tmdbId, 8);

        Assert.Equal(rowsAfterFirst, await OutboxCountAsync(accountId));
        await using var verify = fixture.CreateContext(new AccountContext());
        var watched = await verify.WatchedTitles.SingleAsync(w => w.AccountId == accountId);
        Assert.Equal(1, watched.Plays); // NOT incremented by the duplicate click
    }

    [Fact]
    public async Task Show_rating_enqueues_the_rating_alone_and_never_touches_watched_state()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        var tmdbId = await SeedTitleAsync(MediaType.Show);

        await RateAsync(accountId, "show", tmdbId, 7);

        await using var verify = fixture.CreateContext(new AccountContext());
        Assert.False(await verify.WatchedTitles.AnyAsync(w => w.AccountId == accountId));
        var entry = await verify.TraktOutbox.SingleAsync(o => o.AccountId == accountId);
        Assert.Equal(OutboxKind.AddRating, entry.Kind); // no AddToHistory, no ListRemove
    }

    [Fact]
    public async Task Unrating_a_movie_reverses_the_reel_logged_play()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        var tmdbId = await SeedTitleAsync(MediaType.Movie);

        await RateAsync(accountId, "movie", tmdbId, 9);
        await UnrateAsync(accountId, "movie", tmdbId);

        await using var verify = fixture.CreateContext(new AccountContext());
        var remove = await verify.TraktOutbox
            .SingleAsync(o => o.AccountId == accountId && o.Kind == OutboxKind.RemoveFromHistory);
        var payload = JsonSerializer.Deserialize<OutboxDispatcher.OutboxPayload>(remove.PayloadJson)!;
        Assert.NotNull(payload.WatchedAt); // anchored on the watched_at Reel logged
        Assert.False(await verify.WatchedTitles.AnyAsync(w => w.AccountId == accountId)); // the one play is gone
        Assert.False(await verify.UserRatings.AnyAsync(r => r.AccountId == accountId));
    }

    [Fact]
    public async Task Already_watched_movie_gets_the_rating_only()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        var tmdbId = await SeedTitleAsync(MediaType.Movie);
        await using (var db = fixture.CreateContext(new AccountContext()))
        {
            var title = await db.Titles.SingleAsync(t => t.TmdbId == tmdbId);
            db.WatchedTitles.Add(new WatchedTitle
            {
                Id = Guid.NewGuid(),
                AccountId = accountId,
                TitleId = title.Id,
                Plays = 3, // ingested from Trakt — these plays are not Reel's to log or reverse
                IsFullyWatched = true,
                FirstSyncedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        await RateAsync(accountId, "movie", tmdbId, 6);

        await using var verify = fixture.CreateContext(new AccountContext());
        var entry = await verify.TraktOutbox.SingleAsync(o => o.AccountId == accountId);
        Assert.Equal(OutboxKind.AddRating, entry.Kind); // no AddToHistory
        var watched = await verify.WatchedTitles.SingleAsync(w => w.AccountId == accountId);
        Assert.Equal(3, watched.Plays);
        Assert.Null(watched.ReelLoggedWatchedAt);
    }

    [Fact]
    public async Task Re_rating_moves_RatedAt_and_never_moves_FirstRatedAt()
    {
        if (!fixture.Available)
        {
            return;
        }

        // FirstRatedAt is the eval split axis and the nightly retrain gate's drift axis. If a
        // re-rate could move it, an old title would cross into the holdout carrying a label the
        // split model was fitted on — the leak the harness exists to prevent.
        var accountId = await SeedAccountAsync();
        var tmdbId = await SeedTitleAsync(MediaType.Movie);

        await RateAsync(accountId, "movie", tmdbId, 6);

        await using (var first = fixture.CreateContext(new AccountContext()))
        {
            var row = await first.UserRatings.SingleAsync(r => r.AccountId == accountId);
            Assert.Equal(row.RatedAt, row.FirstRatedAt);
            // Back-date both so the re-rate below has somewhere to move to.
            row.RatedAt = row.RatedAt.AddDays(-400);
            row.FirstRatedAt = row.FirstRatedAt.AddDays(-400);
            await first.SaveChangesAsync();
        }

        DateTime backdated;
        await using (var probe = fixture.CreateContext(new AccountContext()))
        {
            backdated = (await probe.UserRatings.SingleAsync(r => r.AccountId == accountId)).FirstRatedAt;
        }

        await RateAsync(accountId, "movie", tmdbId, 9);

        await using var verify = fixture.CreateContext(new AccountContext());
        var rerated = await verify.UserRatings.SingleAsync(r => r.AccountId == accountId);
        Assert.Equal(9, rerated.Rating);
        Assert.True(rerated.RatedAt > backdated, "a re-rate must move Trakt's own RatedAt forward");
        Assert.Equal(backdated, rerated.FirstRatedAt);
    }

    /// <summary>
    /// G-1, on the in-app path: rating a saved title consumes the save. It is revoked (never
    /// deleted) and its managed-list row closed with a ListRemove write-back in the same transaction
    /// as the rating — for a movie the gesture logged as watched, for one rated without logging a
    /// play, and for a show, whose rating never touches watched state at all.
    /// </summary>
    [Theory]
    [InlineData(MediaType.Movie, "movie", true)]
    [InlineData(MediaType.Movie, "movie", false)]
    [InlineData(MediaType.Show, "show", true)]
    public async Task Rating_a_saved_title_revokes_the_save_and_takes_it_off_the_list(
        MediaType media, string mediaType, bool markWatched)
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        var tmdbId = await SeedTitleAsync(media);
        await SaveAsync(accountId, mediaType, tmdbId);

        await RateAsync(accountId, mediaType, tmdbId, 8, markWatched);

        await using var verify = fixture.CreateContext(new AccountContext());
        var save = await verify.UserTitleReactions.SingleAsync(
            r => r.AccountId == accountId && r.Kind == ReactionKind.SaveForLater);
        Assert.NotNull(save.RevokedAt);
        var listItem = await verify.ManagedListItems.SingleAsync(m => m.AccountId == accountId);
        Assert.NotNull(listItem.RemovedAt);
        Assert.Equal(ListRemovalReason.Watched, listItem.RemovalReason);
        Assert.Equal(1, await verify.TraktOutbox.CountAsync(o => o.AccountId == accountId && o.Kind == OutboxKind.ListRemove));
        Assert.Empty(await SaveConsumption.LiveSaves(verify, accountId).ToListAsync());
    }

    /// <summary>
    /// Un-rating reverses the play Reel logged, and deliberately does NOT resurrect the save: the
    /// user has seen the title, and a seen title does not go back on the "watch next" shelf.
    /// </summary>
    [Fact]
    public async Task Unrating_does_not_put_a_consumed_save_back_on_the_shelf()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        var tmdbId = await SeedTitleAsync(MediaType.Movie);
        await SaveAsync(accountId, "movie", tmdbId);
        await RateAsync(accountId, "movie", tmdbId, 6);
        await UnrateAsync(accountId, "movie", tmdbId);

        await using var verify = fixture.CreateContext(new AccountContext());
        Assert.Empty(await SaveConsumption.LiveSaves(verify, accountId).ToListAsync());
        Assert.Equal(1, await verify.UserTitleReactions.CountAsync(r => r.AccountId == accountId));
    }

    private async Task SaveAsync(Guid accountId, string mediaType, long tmdbId)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        await ReactionEndpoints.SaveForLaterAsync(
            mediaType, tmdbId, db, accountId, FeedRefresh(db), Residual(db), CancellationToken.None);
    }

    private async Task RateAsync(Guid accountId, string mediaType, long tmdbId, short rating, bool markWatched)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        await ReactionEndpoints.RateAsync(
            mediaType, tmdbId, new ReactionEndpoints.RateRequest(rating, markWatched),
            db, accountId, FeedRefresh(db), Residual(db), Calibration(db), new RatingVerdictQuery(db), CancellationToken.None);
    }

    private async Task RateAsync(Guid accountId, string mediaType, long tmdbId, short rating)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        await ReactionEndpoints.RateAsync(
            mediaType, tmdbId, new ReactionEndpoints.RateRequest(rating),
            db, accountId, FeedRefresh(db), Residual(db), Calibration(db), new RatingVerdictQuery(db), CancellationToken.None);
    }

    private async Task UnrateAsync(Guid accountId, string mediaType, long tmdbId)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        await ReactionEndpoints.RemoveRatingAsync(mediaType, tmdbId, db, accountId, FeedRefresh(db), CancellationToken.None);
    }

    private async Task<int> OutboxCountAsync(Guid accountId)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        return await db.TraktOutbox.CountAsync(o => o.AccountId == accountId);
    }

    private static FeedRefreshCoordinator FeedRefresh(IReelDbContext db) =>
        new(db, NullLogger<FeedRefreshCoordinator>.Instance);

    private static RatingCalibrationService Calibration(IReelDbContext db) =>
        new(db, new MemoryCache(new MemoryCacheOptions()));

    private static OnlineResidualModel Residual(IReelDbContext db) =>
        new(db, new FeatureVectorBuilder(db), new FastTreeModelEngine(),
            new MemoryCache(new MemoryCacheOptions()), NullLogger<OnlineResidualModel>.Instance);

    private async Task<Guid> SeedAccountAsync()
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var account = new Account
        {
            Id = Guid.NewGuid(),
            TraktUserSlug = $"rating-{Guid.NewGuid():N}",
            CreatedAt = DateTime.UtcNow,
            LastSeenAt = DateTime.UtcNow,
            PipelineStageChangedAt = DateTime.UtcNow,
        };
        db.Add(account);
        await db.SaveChangesAsync();
        return account.Id;
    }

    private async Task<long> SeedTitleAsync(MediaType media)
    {
        await using var db = fixture.CreateContext(new AccountContext());
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
        await db.SaveChangesAsync();
        return title.TmdbId!.Value;
    }
}
