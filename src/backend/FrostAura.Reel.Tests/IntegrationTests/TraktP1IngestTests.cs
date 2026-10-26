using System.Runtime.CompilerServices;
using System.Text.Json;
using FrostAura.Reel.Application.Ingestion;
using FrostAura.Reel.Application.Ml;
using FrostAura.Reel.Application.Pipeline;
using FrostAura.Reel.Application.Sync;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Library;
using FrostAura.Reel.Domain.Ml;
using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Domain.Ports.Trakt;
using FrostAura.Reel.Domain.Sync;
using FrostAura.Reel.Domain.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrostAura.Reel.Tests.IntegrationTests;

/// <summary>
/// WS-4 task 8 against real Postgres: the Trakt watchlist into Saved (excluding Reel's own writes),
/// hidden items recorded but not applied, the next-episode air-date back-fill, and the catalogue
/// Trakt-rating back-fill. Trakt is a scripted fake; nothing leaves the process.
/// </summary>
[Collection("postgres")]
public class TraktP1IngestTests(PostgresFixture fixture)
{
    [Fact]
    public async Task The_watchlist_becomes_saves_except_reels_own_echoes_consumed_and_declined_titles()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        var now = DateTime.UtcNow;
        var listed = now.AddDays(-10);

        var fresh = await SeedTitleAsync();                                  // nothing known → save
        var live = await SeedTitleAsync();                                   // live save already
        var echo = await SeedTitleAsync();                                   // Reel revoked after it was listed
        var putBack = await SeedTitleAsync();                                // revoked before it was listed → save
        var inFlight = await SeedTitleAsync();                               // RemoveFromWatchlist still pending
        var rated = await SeedTitleAsync();                                  // rated after listing → consumed
        var declined = await SeedTitleAsync();                               // not interested in Reel

        await using (var db = fixture.CreateContext(new AccountContext()))
        {
            db.UserTitleReactions.AddRange(
                Reaction(accountId, live.Id, ReactionKind.SaveForLater, now.AddDays(-20), null),
                Reaction(accountId, echo.Id, ReactionKind.SaveForLater, now.AddDays(-20), listed.AddDays(2)),
                Reaction(accountId, putBack.Id, ReactionKind.SaveForLater, now.AddDays(-30), listed.AddDays(-2)),
                Reaction(accountId, declined.Id, ReactionKind.NotInterested, now.AddDays(-5), null));
            db.TraktOutbox.Add(new TraktOutboxEntry
            {
                Id = Guid.NewGuid(),
                AccountId = accountId,
                Kind = OutboxKind.RemoveFromWatchlist,
                PayloadJson = JsonSerializer.Serialize(new OutboxDispatcher.OutboxPayload(inFlight.Id, MediaType.Movie, inFlight.TmdbId, inFlight.TraktId, null, null)),
                EnqueuedAt = now,
                NextAttemptAt = now,
            });
            db.UserRatings.Add(new UserRating
            {
                Id = Guid.NewGuid(),
                AccountId = accountId,
                TitleId = rated.Id,
                SubjectType = RatingSubjectType.Movie,
                Rating = 8,
                RatedAt = listed.AddDays(1),
                FirstRatedAt = listed.AddDays(1),
                SyncedAt = now,
            });
            await db.SaveChangesAsync();
        }

        var trakt = new ScriptedTrakt
        {
            Watchlist = new[] { fresh, live, echo, putBack, inFlight, rated, declined }
                .Select(t => new TraktWatchlistItem(Random.Shared.NextInt64(), listed, "movie", MovieOf(t), null))
                .ToList(),
        };

        TraktLibraryIngestor.WatchlistIngestResult result;
        await using (var db = Scoped(accountId))
        {
            result = await Ingestor(db, trakt).IngestWatchlistAsync(accountId, "token", RatePriority.DeltaPoll, CancellationToken.None);
            await db.SaveChangesAsync();
        }

        Assert.Equal((7, 2, 1, 2, 1, 1), (result.Count, result.Saved, result.AlreadySaved, result.ReelsOwnWrites, result.Consumed, result.Declined));

        await using var check = fixture.CreateContext(new AccountContext());
        var liveSaves = await check.UserTitleReactions.AsNoTracking()
            .Where(r => r.AccountId == accountId && r.Kind == ReactionKind.SaveForLater && r.RevokedAt == null)
            .ToListAsync();
        Assert.Equal(
            new[] { fresh.Id, live.Id, putBack.Id }.Order(),
            liveSaves.Select(r => r.TitleId).Order());
        // Dated when it was listed, not when Reel read it.
        Assert.Equal(listed, liveSaves.Single(r => r.TitleId == fresh.Id).CreatedAt, TimeSpan.FromMilliseconds(1));
        // Read-only towards Trakt: no new write-backs, no list rows.
        Assert.Equal(1, await check.TraktOutbox.CountAsync(o => o.AccountId == accountId));
        Assert.Equal(0, await check.ManagedListItems.CountAsync(m => m.AccountId == accountId));
    }

    [Fact]
    public async Task Hidden_items_are_recorded_on_the_connection_and_a_failed_section_keeps_its_last_read()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync(withConnection: true);
        var known = await SeedTitleAsync(MediaType.Show);
        var trakt = new ScriptedTrakt
        {
            Hidden =
            {
                ["progress_watched"] = [new TraktHiddenItem(DateTime.UtcNow.AddDays(-3), "show", null, ShowOf(known))],
                ["recommendations"] = [],
                ["dropped"] = [new TraktHiddenItem(DateTime.UtcNow.AddDays(-1), "show", null, ShowOf(known))],
            },
        };

        await RecordHiddenAsync(accountId, trakt);
        var first = await HiddenAsync(accountId);
        Assert.Equal(2, first.Count);
        Assert.All(first, h => Assert.Equal(known.Id, h.TitleId));
        Assert.Contains(first, h => h.Section == "dropped");

        // "dropped" is unavailable on the next read: what it said last time stays.
        trakt.Hidden.Remove("dropped");
        trakt.Hidden["progress_watched"] = [];
        await RecordHiddenAsync(accountId, trakt);
        var second = await HiddenAsync(accountId);
        Assert.Equal("dropped", Assert.Single(second).Section);
    }

    [Fact]
    public async Task The_next_episode_back_fill_fills_air_dates_and_asks_about_an_undated_one_once_a_day()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        var dated = await SeedTitleAsync(MediaType.Show);
        var undated = await SeedTitleAsync(MediaType.Show);
        var aired = new DateTime(2026, 9, 1, 1, 0, 0, DateTimeKind.Utc);
        await using (var db = fixture.CreateContext(new AccountContext()))
        {
            foreach (var show in new[] { dated, undated })
            {
                db.ShowWatchProgresses.Add(new ShowWatchProgress
                {
                    Id = Guid.NewGuid(),
                    AccountId = accountId,
                    TitleId = show.Id,
                    NextEpisodeSeason = 2,
                    NextEpisodeNumber = 1,
                    UpdatedAt = DateTime.UtcNow.AddDays(-3),
                });
            }

            await db.SaveChangesAsync();
        }

        var trakt = new ScriptedTrakt
        {
            Progress =
            {
                [dated.TraktId!.Value] = new TraktShowProgress(10, 8, new TraktNextEpisode(2, 1, "One", aired)),
                [undated.TraktId!.Value] = new TraktShowProgress(10, 8, new TraktNextEpisode(2, 1, "One", null)),
            },
        };
        await BackfillNextEpisodesAsync(accountId, trakt);
        await BackfillNextEpisodesAsync(accountId, trakt);

        // One call each: the dated row left the list, the undated one waits a day.
        Assert.Equal(2, trakt.ProgressCalls);
        await using var check = fixture.CreateContext(new AccountContext());
        var rows = await check.ShowWatchProgresses.AsNoTracking().Where(p => p.AccountId == accountId).ToDictionaryAsync(p => p.TitleId);
        Assert.Equal(aired, rows[dated.Id].NextEpisodeAiredAt);
        Assert.Null(rows[undated.Id].NextEpisodeAiredAt);
    }

    [Fact]
    public async Task The_ratings_back_fill_rates_scored_titles_stamps_misses_and_leaves_failures_for_the_next_pass()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        // Vote counts above anything else in the shared test catalogue, and every title scored, so
        // these five are the head of the back-fill's order.
        var rated = await SeedTitleAsync(withTraktId: false, votes: int.MaxValue);
        var heldId = await SeedTitleAsync(withTraktId: false, votes: int.MaxValue - 1);
        var unknown = await SeedTitleAsync(withTraktId: false, votes: int.MaxValue - 2);
        var failing = await SeedTitleAsync(withTraktId: false, votes: int.MaxValue - 3);
        var holder = await SeedTitleAsync();
        await using (var db = fixture.CreateContext(new AccountContext()))
        {
            foreach (var title in new[] { rated, heldId, unknown, failing })
            {
                db.TitleScores.Add(new TitleScore
                {
                    Id = Guid.NewGuid(),
                    AccountId = accountId,
                    TitleId = title.Id,
                    ModelArtifactId = Guid.NewGuid(),
                    PredictedRating = 6.1m,
                    ScoredAt = DateTime.UtcNow,
                });
            }

            await db.SaveChangesAsync();
        }

        var freshTraktId = Random.Shared.NextInt64(1_000_000_000, long.MaxValue);
        var trakt = new ScriptedTrakt
        {
            CatalogRatings =
            {
                [rated.TmdbId!.Value] = new TraktCatalogRating(freshTraktId, "rated-slug", null, 7.4m, 1200),
                // Trakt says this TMDB title is a Trakt id another catalogue row already holds.
                [heldId.TmdbId!.Value] = new TraktCatalogRating(holder.TraktId!.Value, "held", null, 6.9m, 80),
                [unknown.TmdbId!.Value] = null,
            },
            FailCatalogFor = { failing.TmdbId!.Value },
        };

        await using (var db = fixture.CreateContext(new AccountContext()))
        {
            var result = await new TraktRatingsBackfill(db, trakt, NullLogger<TraktRatingsBackfill>.Instance)
                .RunBatchAsync(4, DateTime.UtcNow, CancellationToken.None);
            Assert.Equal((3, 2, 2, 1, false), (result.Asked, result.Found, result.Rated, result.Failed, result.Halted));
        }

        await using var check = fixture.CreateContext(new AccountContext());
        var rows = await check.Titles.AsNoTracking()
            .Where(t => t.Id == rated.Id || t.Id == heldId.Id || t.Id == unknown.Id || t.Id == failing.Id)
            .ToDictionaryAsync(t => t.Id);
        Assert.Equal((7.4m, 1200, freshTraktId), (rows[rated.Id].TraktRating!.Value, rows[rated.Id].TraktVotes, rows[rated.Id].TraktId!.Value));
        Assert.Equal(6.9m, rows[heldId.Id].TraktRating);
        Assert.Null(rows[heldId.Id].TraktId);
        Assert.NotNull(rows[unknown.Id].TraktRatingCheckedAt);
        Assert.Null(rows[unknown.Id].TraktRating);
        Assert.Null(rows[failing.Id].TraktRatingCheckedAt);

        // Asked titles leave the work list; the failure stays on it.
        var pending = await TraktRatingsBackfill.Pending(check, DateTime.UtcNow)
            .Where(t => t.Id == rated.Id || t.Id == heldId.Id || t.Id == unknown.Id || t.Id == failing.Id)
            .Select(t => t.Id)
            .ToListAsync();
        Assert.Equal([failing.Id], pending);

        // Leave the shared catalogue as the next run expects it: this run's fixtures out of the head.
        await check.Titles.Where(t => t.Id == failing.Id || t.Id == holder.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.TraktRatingCheckedAt, DateTime.UtcNow));
    }

    private async Task RecordHiddenAsync(Guid accountId, ScriptedTrakt trakt)
    {
        await using var db = Scoped(accountId);
        var connection = await db.TraktConnections.SingleAsync(c => c.AccountId == accountId);
        await Ingestor(db, trakt).IngestHiddenAsync(connection, "token", RatePriority.Backfill, CancellationToken.None);
        await db.SaveChangesAsync();
    }

    private async Task<List<TraktLibraryIngestor.RecordedHiddenItem>> HiddenAsync(Guid accountId)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var json = await db.TraktConnections.Where(c => c.AccountId == accountId).Select(c => c.HiddenItemsJson).SingleAsync();
        return JsonSerializer.Deserialize<List<TraktLibraryIngestor.RecordedHiddenItem>>(json!)!;
    }

    private async Task BackfillNextEpisodesAsync(Guid accountId, ScriptedTrakt trakt)
    {
        await using var db = Scoped(accountId);
        await Ingestor(db, trakt).EnrichNextEpisodesAsync(accountId, "token", budget: 5, CancellationToken.None, backfillOnly: true);
        await db.SaveChangesAsync();
    }

    private Infrastructure.Persistence.ReelDbContext Scoped(Guid accountId)
    {
        var context = new AccountContext();
        context.SetAccount(accountId);
        return fixture.CreateContext(context);
    }

    private static TraktLibraryIngestor Ingestor(Infrastructure.Persistence.ReelDbContext db, ITraktClient trakt) =>
        new(db, trakt, new NoopEventHub(), new RatingCalibrationService(db, new MemoryCache(new MemoryCacheOptions())),
            NullLogger<TraktLibraryIngestor>.Instance);

    private static UserTitleReaction Reaction(Guid accountId, Guid titleId, ReactionKind kind, DateTime createdAt, DateTime? revokedAt) => new()
    {
        Id = Guid.NewGuid(),
        AccountId = accountId,
        TitleId = titleId,
        Kind = kind,
        CreatedAt = createdAt,
        RevokedAt = revokedAt,
    };

    private static TraktMovie MovieOf(Title t) => new(
        t.Name, t.Year, new TraktIds(t.TraktId!.Value, t.TraktSlug, null, t.TmdbId), null, null, null, null, null, null, null, 0, null, null, null, null, null);

    private static TraktShow ShowOf(Title t) => new(
        t.Name, t.Year, new TraktIds(t.TraktId!.Value, t.TraktSlug, null, t.TmdbId), null, null, null, null, null, null, null, 0, null, null, null, null, null, null, null);

    private async Task<Guid> SeedAccountAsync(bool withConnection = false)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var now = DateTime.UtcNow;
        var account = new Account
        {
            Id = Guid.NewGuid(),
            TraktUserSlug = $"p1-ingest-{Guid.NewGuid():N}",
            DisplayName = "P1 Ingest Fixture",
            Region = "US",
            CreatedAt = now,
            LastSeenAt = now,
            PipelineStageChangedAt = now,
        };
        db.Add(account);
        if (withConnection)
        {
            db.TraktConnections.Add(new TraktConnection { Id = Guid.NewGuid(), AccountId = account.Id, ExpiresAt = now.AddDays(30) });
        }

        await db.SaveChangesAsync();
        return account.Id;
    }

    private async Task<Title> SeedTitleAsync(MediaType mediaType = MediaType.Movie, bool withTraktId = true, int votes = 10)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var title = new Title
        {
            Id = Guid.NewGuid(),
            MediaType = mediaType,
            TraktId = withTraktId ? Random.Shared.NextInt64(1_000_000_000, long.MaxValue) : null,
            TmdbId = Random.Shared.NextInt64(1_000_000_000, long.MaxValue),
            TraktSlug = $"p1-{Guid.NewGuid():N}",
            Name = $"P1 Fixture {Guid.NewGuid():N}",
            Year = 2020,
            TmdbVoteCount = votes,
            CreatedAt = DateTime.UtcNow,
        };
        db.Add(title);
        await db.SaveChangesAsync();
        return title;
    }

    private sealed class NoopEventHub : IPipelineEventHub
    {
        public void Publish(Guid accountId, string type, IReadOnlyDictionary<string, object?> data) { }

        public async IAsyncEnumerable<PipelineSseEvent> SubscribeAsync(
            Guid accountId, long? lastEventId, [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    /// <summary>The four Trakt reads task 8 uses, scripted; every other call fails loudly.</summary>
    private sealed class ScriptedTrakt : ITraktClient
    {
        public IReadOnlyList<TraktWatchlistItem> Watchlist { get; set; } = [];

        public Dictionary<string, IReadOnlyList<TraktHiddenItem>> Hidden { get; } = [];

        public Dictionary<long, TraktShowProgress> Progress { get; } = [];

        public Dictionary<long, TraktCatalogRating?> CatalogRatings { get; } = [];

        public HashSet<long> FailCatalogFor { get; } = [];

        public int ProgressCalls { get; private set; }

        public Task<IReadOnlyList<TraktWatchlistItem>> GetWatchlistAsync(string accessToken, RatePriority priority, CancellationToken ct = default) =>
            Task.FromResult(Watchlist);

        public Task<IReadOnlyList<TraktHiddenItem>> GetHiddenItemsAsync(string accessToken, string section, RatePriority priority, CancellationToken ct = default) =>
            Hidden.TryGetValue(section, out var items)
                ? Task.FromResult<IReadOnlyList<TraktHiddenItem>>(items.Select(i => i with { Section = section }).ToList())
                : Task.FromException<IReadOnlyList<TraktHiddenItem>>(new HttpRequestException($"section {section} unavailable"));

        public Task<TraktShowProgress?> GetShowProgressAsync(string accessToken, long showTraktId, RatePriority priority, CancellationToken ct = default)
        {
            ProgressCalls++;
            return Task.FromResult(Progress.GetValueOrDefault(showTraktId));
        }

        public Task<TraktCatalogRating?> GetCatalogRatingAsync(long tmdbId, bool movie, RatePriority priority, CancellationToken ct = default) =>
            FailCatalogFor.Contains(tmdbId)
                ? Task.FromException<TraktCatalogRating?>(new HttpRequestException("Trakt 503"))
                : Task.FromResult(CatalogRatings.TryGetValue(tmdbId, out var found) ? found : null);

        public Task<TraktTokenResponse> ExchangeCodeAsync(string code, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<TraktTokenResponse> RefreshTokenAsync(string refreshToken, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<TraktUserSettings> GetUserSettingsAsync(string accessToken, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<TraktWatchedMovie>> GetWatchedMoviesAsync(string accessToken, RatePriority priority, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<TraktWatchedShow>> GetWatchedShowsAsync(string accessToken, RatePriority priority, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<TraktRatingItem>> GetRatingsAsync(string accessToken, RatePriority priority, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<string> GetLastActivitiesRawAsync(string accessToken, RatePriority priority, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<TraktHistoryItem>> GetHistoryAsync(string accessToken, string type, long traktId, DateTime? startAt, DateTime? endAt, RatePriority priority, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<long?> ResolveTraktIdByTmdbAsync(string accessToken, long tmdbId, bool movie, RatePriority priority, CancellationToken ct = default) => throw new NotSupportedException();

        public Task PostSyncBatchAsync(string accessToken, string endpoint, object payload, RatePriority priority, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<long> EnsureListAsync(string accessToken, string name, string description, RatePriority priority, CancellationToken ct = default) => throw new NotSupportedException();

        public Task PostListItemsAsync(string accessToken, long listId, object payload, bool remove, RatePriority priority, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
