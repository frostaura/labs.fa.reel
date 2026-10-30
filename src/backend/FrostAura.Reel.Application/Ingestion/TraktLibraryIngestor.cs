using System.Text.Json;
using FrostAura.Reel.Application.Ml;
using FrostAura.Reel.Application.Ml.ClosedLoop;
using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Application.Pipeline;
using FrostAura.Reel.Application.Saved;
using FrostAura.Reel.Application.Sync;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Library;
using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Domain.Ports.Trakt;
using FrostAura.Reel.Domain.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FrostAura.Reel.Application.Ingestion;

/// <summary>
/// The shared, idempotent ingest core — FullIngest walks all phases with cursor checkpoints;
/// DeltaSync re-runs only the categories /sync/last_activities reports changed. All writes
/// upsert by natural keys, so any replay is harmless by construction.
/// </summary>
public class TraktLibraryIngestor(
    IReelDbContext db,
    ITraktClient trakt,
    IPipelineEventHub events,
    RatingCalibrationService calibrator,
    ILogger<TraktLibraryIngestor> logger)
{
    public record IngestCounts(int Movies, int Shows, int Episodes, int Ratings);

    /// <summary>One title-level rating this pass actually inserted or changed.</summary>
    public readonly record struct IngestedRating(Guid TitleId, short Rating);

    /// <summary>
    /// What a ratings ingest did. <paramref name="Count"/> is every row Trakt returned (the number the
    /// progress copy has always shown); <paramref name="Observed"/> is the far smaller set that was
    /// actually new or different — the only rows carrying information a learner has not already seen.
    /// Season and episode ratings are excluded: they are not title-level labels.
    /// <para><paramref name="Added"/> and <paramref name="Updated"/> partition <paramref name="Observed"/>:
    /// a rating Reel had never seen for that title vs one whose value moved. The post-sync moment says
    /// "pulled 12 NEW ratings", and a re-rate is genuinely a different sentence from a first rating —
    /// so the two are counted apart rather than summed at the call site.</para>
    /// </summary>
    public record RatingIngestResult(int Count, int Added, int Updated, IReadOnlyList<IngestedRating> Observed);

    /// <summary>
    /// What a watched-library ingest did. <paramref name="Count"/> is the size of Trakt's snapshot;
    /// <paramref name="Added"/> is how many of those rows Reel had never seen — the only number that
    /// answers "what arrived in this sync". A full paginated walk returns the whole library every
    /// time, so <paramref name="Count"/> is never the answer to that question.
    /// </summary>
    public record WatchIngestResult(int Count, int Added);

    /// <inheritdoc cref="WatchIngestResult"/>
    public record ShowIngestResult(int Shows, int Episodes, int Added);

    public async Task<WatchIngestResult> IngestMoviesAsync(Guid accountId, string token, RatePriority priority, CancellationToken ct)
    {
        var watchedMovies = await trakt.GetWatchedMoviesAsync(token, priority, ct);
        events.Publish(accountId, PipelineEventTypes.IngestProgress, Progress("movies", watchedMovies.Count));

        var titlesByTraktId = await LoadTitleMapAsync(MediaType.Movie, watchedMovies.Select(m => m.Movie.Ids.Trakt), ct);
        var titlesByTmdbId = await LoadTitleMapByTmdbAsync(MediaType.Movie, watchedMovies.Select(m => m.Movie.Ids.Tmdb), ct);
        var watchedByTitleId = await LoadWatchedMapAsync(accountId, ct);

        var now = DateTime.UtcNow;
        var added = 0;
        foreach (var item in watchedMovies)
        {
            var title = ResolveOrAddTitle(titlesByTraktId, titlesByTmdbId, MediaType.Movie, item.Movie.Ids.Trakt, item.Movie.Ids.Tmdb, now);
            IngestMapper.ApplyMovie(title, item.Movie);
            if (UpsertWatched(watchedByTitleId, accountId, title.Id, item.Plays, item.LastWatchedAt,
                    IngestMapper.IsFullyWatched(MediaType.Movie, item.Plays, 0, null), now))
            {
                added++;
            }
        }

        logger.LogInformation("Ingested {Count} watched movies for {AccountId} ({Added} new).", watchedMovies.Count, accountId, added);
        return new WatchIngestResult(watchedMovies.Count, added);
    }

    public async Task<ShowIngestResult> IngestShowsAsync(Guid accountId, string token, RatePriority priority, CancellationToken ct)
    {
        var watchedShows = await trakt.GetWatchedShowsAsync(token, priority, ct);
        var episodeCount = watchedShows.Sum(IngestMapper.CountDistinctWatchedEpisodes);
        events.Publish(accountId, PipelineEventTypes.IngestProgress, Progress("shows", watchedShows.Count));
        events.Publish(accountId, PipelineEventTypes.IngestProgress, Progress("episodes", episodeCount));

        var titlesByTraktId = await LoadTitleMapAsync(MediaType.Show, watchedShows.Select(s => s.Show.Ids.Trakt), ct);
        var titlesByTmdbId = await LoadTitleMapByTmdbAsync(MediaType.Show, watchedShows.Select(s => s.Show.Ids.Tmdb), ct);
        var watchedByTitleId = await LoadWatchedMapAsync(accountId, ct);
        var progressByTitleId = await db.ShowWatchProgresses
            .Where(p => p.AccountId == accountId)
            .ToDictionaryAsync(p => p.TitleId, ct);

        var now = DateTime.UtcNow;
        var added = 0;
        // /sync/watched/shows is a complete snapshot, so the set of title ids it returns is the
        // current truth. Any progress row whose show is absent here had its history removed on
        // Trakt — reconciled away below so it leaves Continue Watching.
        var returnedTitleIds = new HashSet<Guid>(watchedShows.Count);
        foreach (var item in watchedShows)
        {
            var title = ResolveOrAddTitle(titlesByTraktId, titlesByTmdbId, MediaType.Show, item.Show.Ids.Trakt, item.Show.Ids.Tmdb, now);
            IngestMapper.ApplyShow(title, item.Show);
            returnedTitleIds.Add(title.Id);

            var distinctEpisodes = IngestMapper.CountDistinctWatchedEpisodes(item);
            var fullyWatched = IngestMapper.IsFullyWatched(MediaType.Show, item.Plays, distinctEpisodes, title.AiredEpisodes);
            if (UpsertWatched(watchedByTitleId, accountId, title.Id, item.Plays, item.LastWatchedAt, fullyWatched, now))
            {
                added++;
            }

            var aired = title.AiredEpisodes ?? 0;
            var completion = aired > 0 ? Math.Clamp((decimal)distinctEpisodes / aired, 0m, 1m) : 0m;
            if (!fullyWatched)
            {
                if (!progressByTitleId.TryGetValue(title.Id, out var progress))
                {
                    progress = new ShowWatchProgress { Id = Guid.NewGuid(), AccountId = accountId, TitleId = title.Id };
                    progressByTitleId[title.Id] = progress;
                    db.ShowWatchProgresses.Add(progress);
                }

                progress.WatchedEpisodeCount = distinctEpisodes;
                progress.TotalAiredEpisodes = aired;
                progress.CompletionPct = completion;
                progress.LastWatchedAt = item.LastWatchedAt;
                progress.ResumeLikelihood = IngestMapper.ResumeLikelihood(item.LastWatchedAt, completion, now);
                progress.UpdatedAt = now;
            }
            else if (progressByTitleId.TryGetValue(title.Id, out var stale))
            {
                db.ShowWatchProgresses.Remove(stale);
                progressByTitleId.Remove(title.Id);
            }
        }

        // Reconcile removals: a show that no longer appears in the watched snapshot (history
        // cleared on Trakt) must not keep its in-progress row. ShowWatchProgress is purely
        // ingest-derived — never written by a local action — so deleting against the snapshot is
        // safe. Without this, "remove from history" never propagates and the show is stuck in
        // Continue Watching forever.
        var removedFromHistory = progressByTitleId.Values
            .Where(p => !returnedTitleIds.Contains(p.TitleId))
            .ToList();
        foreach (var orphan in removedFromHistory)
        {
            db.ShowWatchProgresses.Remove(orphan);
        }

        logger.LogInformation(
            "Ingested {Shows} watched shows ({Episodes} episodes, {Added} new) for {AccountId}; reconciled away {Removed} progress rows no longer in Trakt history.",
            watchedShows.Count, episodeCount, added, accountId, removedFromHistory.Count);
        return new ShowIngestResult(watchedShows.Count, episodeCount, added);
    }

    public async Task<RatingIngestResult> IngestRatingsAsync(Guid accountId, string token, RatePriority priority, CancellationToken ct)
    {
        var ratings = await trakt.GetRatingsAsync(token, priority, ct);
        events.Publish(accountId, PipelineEventTypes.IngestProgress, Progress("ratings", ratings.Count));

        var movieMap = await LoadTitleMapAsync(MediaType.Movie, ratings.Where(r => r.Movie is not null).Select(r => r.Movie!.Ids.Trakt), ct);
        var showMap = await LoadTitleMapAsync(MediaType.Show, ratings.Where(r => r.Show is not null).Select(r => r.Show!.Ids.Trakt), ct);
        var movieMapByTmdb = await LoadTitleMapByTmdbAsync(MediaType.Movie, ratings.Where(r => r.Movie is not null).Select(r => r.Movie!.Ids.Tmdb), ct);
        var showMapByTmdb = await LoadTitleMapByTmdbAsync(MediaType.Show, ratings.Where(r => r.Show is not null).Select(r => r.Show!.Ids.Tmdb), ct);
        var existing = await db.UserRatings
            .Where(r => r.AccountId == accountId)
            .ToDictionaryAsync(r => (r.SubjectType, r.TitleId, r.SeasonNumber, r.EpisodeNumber), ct);

        var now = DateTime.UtcNow;
        var changed = new List<IngestedRating>();
        var addedRatings = 0;
        var updatedRatings = 0;
        foreach (var item in ratings)
        {
            Title title;
            RatingSubjectType subjectType;
            int season = 0, episode = 0;

            if (item.Movie is not null)
            {
                title = ResolveOrAddTitle(movieMap, movieMapByTmdb, MediaType.Movie, item.Movie.Ids.Trakt, item.Movie.Ids.Tmdb, now);
                IngestMapper.ApplyMovie(title, item.Movie);
                subjectType = RatingSubjectType.Movie;
            }
            else if (item.Show is not null)
            {
                title = ResolveOrAddTitle(showMap, showMapByTmdb, MediaType.Show, item.Show.Ids.Trakt, item.Show.Ids.Tmdb, now);
                IngestMapper.ApplyShow(title, item.Show);
                (subjectType, season, episode) = item.Type switch
                {
                    "season" => (RatingSubjectType.Season, item.Season?.Number ?? 0, 0),
                    "episode" => (RatingSubjectType.Episode, item.Episode?.Season ?? 0, item.Episode?.Number ?? 0),
                    _ => (RatingSubjectType.Show, 0, 0),
                };
            }
            else
            {
                continue; // unknown subject shape — skip defensively
            }

            var key = (subjectType, title.Id, season, episode);
            var isNewRow = !existing.ContainsKey(key);
            if (!existing.TryGetValue(key, out var rating))
            {
                rating = new UserRating
                {
                    Id = Guid.NewGuid(),
                    AccountId = accountId,
                    TitleId = title.Id,
                    SubjectType = subjectType,
                    SeasonNumber = season,
                    EpisodeNumber = episode,
                    Source = RatingSource.Trakt,
                    // Set ONCE, here, on insert. The assignments below overwrite RatedAt on every
                    // walk with whatever Trakt currently reports; FirstRatedAt is the immutable
                    // copy the eval split and the retrain gate read, so it must never be touched
                    // again for this row.
                    FirstRatedAt = item.RatedAt,
                };
                existing[key] = rating;
                db.UserRatings.Add(rating);
            }

            // Captured BEFORE the assignment: a re-ingest replays Trakt's whole ratings snapshot, so
            // "did this row move" is the only thing separating 20 new ratings from 2,000 old ones.
            if (subjectType is RatingSubjectType.Movie or RatingSubjectType.Show
                && rating.Rating != item.Rating)
            {
                changed.Add(new IngestedRating(title.Id, item.Rating));

                // The append-only ledger, written only for rows that ACTUALLY moved. A walk replays
                // Trakt's whole snapshot — two thousand rows of which a handful changed — so
                // emitting per row walked would produce a history that is almost entirely no-ops.
                // ObservedAt is `now` because that is genuinely when Reel saw it; RatedAt is
                // Trakt's own claim. Neither is the eval split axis.
                RatingEventLog.Stage(
                    db, accountId, title.Id, subjectType, season, episode,
                    item.Rating, isNewRow ? null : rating.Rating, item.RatedAt, now, RatingSource.Trakt);

                // A brand-new row is "12 new ratings"; a row whose value moved is a re-rate. Both
                // teach the head, but only one of them is what the post-sync moment counts, so the
                // partition is made here — the one place that can still tell them apart.
                if (isNewRow)
                {
                    addedRatings++;
                }
                else
                {
                    updatedRatings++;
                }
            }

            rating.Rating = item.Rating;
            rating.RatedAt = item.RatedAt;
            rating.SyncedAt = now;
        }

        // A rating is a closed-loop outcome, so the account's fitted predicted→actual map is now out
        // of date. Same eviction the in-app rating endpoint performs, for the same reason.
        if (changed.Count > 0)
        {
            calibrator.Invalidate(accountId);
        }

        logger.LogInformation(
            "Ingested {Count} ratings for {AccountId} ({Added} new, {Updated} changed).",
            ratings.Count, accountId, addedRatings, updatedRatings);
        return new RatingIngestResult(ratings.Count, addedRatings, updatedRatings, changed);
    }

    /// <summary>
    /// How long a progress row that the back-fill already looked at waits before it is asked about
    /// again. A show whose next episode Trakt has no air date for would otherwise cost a call on
    /// every delta poll for ever.
    /// </summary>
    public static readonly TimeSpan NextEpisodeBackfillRetry = TimeSpan.FromHours(24);

    /// <summary>
    /// Progress rows that know their next episode but not when it aired — the back-fill's work list
    /// (N+5: 123 of 123 rows had no air date because the progress call never asked for one).
    /// </summary>
    public static IQueryable<ShowWatchProgress> MissingNextEpisodeAirDate(IReelDbContext db, Guid accountId) =>
        db.ShowWatchProgresses.Where(p => p.AccountId == accountId
            && p.NextEpisodeSeason != null
            && p.NextEpisodeAiredAt == null);

    /// <summary>
    /// Writes what Trakt's progress call said about the next episode onto <paramref name="row"/>.
    /// A response with no next episode means the user is caught up on everything aired, so the old
    /// next episode is cleared rather than left pointing at one they have since watched. A missing
    /// response (404) changes nothing. <c>first_aired</c> is stored as UTC.
    /// </summary>
    public static void ApplyNextEpisode(ShowWatchProgress row, TraktShowProgress? progress, DateTime now)
    {
        if (progress is null)
        {
            return;
        }

        var next = progress.NextEpisode;
        row.NextEpisodeSeason = next?.Season;
        row.NextEpisodeNumber = next?.Number;
        row.NextEpisodeAiredAt = next?.FirstAired is { } aired
            ? DateTime.SpecifyKind(aired.Kind == DateTimeKind.Local ? aired.ToUniversalTime() : aired, DateTimeKind.Utc)
            : null;
        row.UpdatedAt = now;
    }

    /// <summary>
    /// Next-episode enrichment for the most-resumable shows (budget-capped, best-effort). Rows that
    /// know their next episode but not its air date go first, so the back-fill rides every run.
    /// With <paramref name="backfillOnly"/> it touches ONLY those rows, and only ones not looked at
    /// inside <see cref="NextEpisodeBackfillRetry"/> — the cheap path a delta poll takes when no show
    /// moved, which costs no call at all once the back-fill has drained.
    /// <paramref name="onProgress"/> receives (done, total) after each show so a caller can surface
    /// a live x / y — this is the one ingest phase with genuine per-item granularity.
    /// </summary>
    public async Task EnrichNextEpisodesAsync(
        Guid accountId, string token, int budget, CancellationToken ct, Func<int, int, Task>? onProgress = null,
        bool backfillOnly = false)
    {
        var now = DateTime.UtcNow;
        var retryBefore = now - NextEpisodeBackfillRetry;
        var rows = backfillOnly
            ? MissingNextEpisodeAirDate(db, accountId).Where(p => p.UpdatedAt < retryBefore)
            : db.ShowWatchProgresses.Where(p => p.AccountId == accountId);

        var candidates = await rows
            .OrderByDescending(p => p.NextEpisodeSeason != null && p.NextEpisodeAiredAt == null)
            .ThenByDescending(p => p.ResumeLikelihood)
            .ThenBy(p => p.Id)
            .Take(budget)
            .Join(db.Titles.Where(t => t.TraktId != null), p => p.TitleId, t => t.Id, (p, t) => new { Progress = p, TraktId = t.TraktId!.Value })
            .ToListAsync(ct);

        var done = 0;
        foreach (var candidate in candidates)
        {
            try
            {
                var progress = await trakt.GetShowProgressAsync(token, candidate.TraktId, RatePriority.Backfill, ct);
                ApplyNextEpisode(candidate.Progress, progress, now);
            }
            catch (HttpRequestException ex)
            {
                logger.LogDebug(ex, "Next-episode enrichment failed for trakt show {TraktId}; continuing.", candidate.TraktId);
            }

            done++;
            if (onProgress is not null)
            {
                await onProgress(done, candidates.Count);
            }
        }
    }

    /// <summary>What a watchlist ingest did: entries read, and how many became saves.</summary>
    public record WatchlistIngestResult(int Count, int Saved, int AlreadySaved, int ReelsOwnWrites, int Consumed, int Declined);

    /// <summary>
    /// Puts the Trakt watchlist into Saved (N-5), by the rules in <see cref="WatchlistIntake"/>: a new
    /// save dated when the entry was listed, for every movie or show that is not already saved, not
    /// Reel's own echo, not consumed since, and not turned down in Reel. Writes nothing to Trakt.
    /// The caller saves.
    /// </summary>
    public async Task<WatchlistIngestResult> IngestWatchlistAsync(Guid accountId, string token, RatePriority priority, CancellationToken ct)
    {
        var items = (await trakt.GetWatchlistAsync(token, priority, ct))
            .Where(i => i.Movie is not null || i.Show is not null)
            .ToList();

        var movieMap = await LoadTitleMapAsync(MediaType.Movie, items.Where(i => i.Movie is not null).Select(i => i.Movie!.Ids.Trakt), ct);
        var showMap = await LoadTitleMapAsync(MediaType.Show, items.Where(i => i.Show is not null).Select(i => i.Show!.Ids.Trakt), ct);
        var movieMapByTmdb = await LoadTitleMapByTmdbAsync(MediaType.Movie, items.Where(i => i.Movie is not null).Select(i => i.Movie!.Ids.Tmdb), ct);
        var showMapByTmdb = await LoadTitleMapByTmdbAsync(MediaType.Show, items.Where(i => i.Show is not null).Select(i => i.Show!.Ids.Tmdb), ct);

        var now = DateTime.UtcNow;
        var listed = new List<(Title Title, DateTime ListedAt)>(items.Count);
        foreach (var item in items)
        {
            Title title;
            if (item.Movie is not null)
            {
                title = ResolveOrAddTitle(movieMap, movieMapByTmdb, MediaType.Movie, item.Movie.Ids.Trakt, item.Movie.Ids.Tmdb, now);
                IngestMapper.ApplyMovie(title, item.Movie);
            }
            else
            {
                title = ResolveOrAddTitle(showMap, showMapByTmdb, MediaType.Show, item.Show!.Ids.Trakt, item.Show.Ids.Tmdb, now);
                IngestMapper.ApplyShow(title, item.Show);
            }

            listed.Add((title, ToUtc(item.ListedAt)));
        }

        // One read per fact for the whole watchlist, not per entry.
        var titleIds = listed.Select(l => l.Title.Id).Distinct().ToList();
        var reactions = await db.UserTitleReactions
            .Where(r => r.AccountId == accountId && titleIds.Contains(r.TitleId))
            .Select(r => new { r.TitleId, r.Kind, r.RevokedAt })
            .ToListAsync(ct);
        var watchedAt = (await db.WatchedTitles
                .Where(w => w.AccountId == accountId && w.IsFullyWatched && titleIds.Contains(w.TitleId))
                .Select(w => new { w.TitleId, At = w.LastWatchedAt ?? w.UpdatedAt })
                .ToListAsync(ct))
            .GroupBy(w => w.TitleId)
            .ToDictionary(g => g.Key, g => g.Max(w => w.At));
        var ratedAt = (await db.UserRatings
                .Where(r => r.AccountId == accountId && titleIds.Contains(r.TitleId))
                .Select(r => new { r.TitleId, r.RatedAt })
                .ToListAsync(ct))
            .GroupBy(r => r.TitleId)
            .ToDictionary(g => g.Key, g => g.Max(r => r.RatedAt));
        var inFlight = (await db.TraktOutbox
                .Where(o => o.AccountId == accountId
                    && (o.Kind == OutboxKind.AddToWatchlist || o.Kind == OutboxKind.RemoveFromWatchlist)
                    && (o.Status == OutboxStatus.Pending || o.Status == OutboxStatus.Failed))
                .Select(o => o.PayloadJson)
                .ToListAsync(ct))
            .Select(OutboxTitleOf)
            .Where(id => id is not null)
            .Select(id => id!.Value)
            .ToHashSet();

        int saved = 0, alreadySaved = 0, reelsOwn = 0, consumed = 0, declined = 0;
        foreach (var (title, listedAt) in listed.DistinctBy(l => l.Title.Id))
        {
            var mine = reactions.Where(r => r.TitleId == title.Id).ToList();
            DateTime? lastConsumed = (watchedAt.TryGetValue(title.Id, out var w), ratedAt.TryGetValue(title.Id, out var r)) switch
            {
                (true, true) => w > r ? w : r,
                (true, false) => w,
                (false, true) => r,
                _ => null,
            };
            var facts = new WatchlistFacts(
                HasLiveSave: mine.Any(x => x.Kind == ReactionKind.SaveForLater && x.RevokedAt == null),
                HasLiveDecline: mine.Any(x => x.Kind is ReactionKind.NotInterested or ReactionKind.Dropped && x.RevokedAt == null),
                LatestSaveRevokedAt: mine.Where(x => x.Kind == ReactionKind.SaveForLater && x.RevokedAt != null).Max(x => x.RevokedAt),
                WatchlistWriteInFlight: inFlight.Contains(title.Id),
                LastConsumedAt: lastConsumed);

            switch (WatchlistIntake.Decide(listedAt, facts))
            {
                case WatchlistDecision.Save:
                    db.UserTitleReactions.Add(new UserTitleReaction
                    {
                        Id = Guid.NewGuid(),
                        AccountId = accountId,
                        TitleId = title.Id,
                        Kind = ReactionKind.SaveForLater,
                        // Dated when the user listed it, so "saved 41 days ago" is true and the
                        // time-aware consumption rule measures a later watch against the real intent.
                        CreatedAt = listedAt,
                    });
                    saved++;
                    break;
                case WatchlistDecision.AlreadySaved:
                    alreadySaved++;
                    break;
                case WatchlistDecision.ReelsOwnWrite:
                    reelsOwn++;
                    break;
                case WatchlistDecision.Consumed:
                    consumed++;
                    break;
                default:
                    declined++;
                    break;
            }
        }

        logger.LogInformation(
            "Ingested {Count} watchlist entries for {AccountId}: {Saved} saved, {AlreadySaved} already saved, "
            + "{ReelsOwn} Reel's own writes, {Consumed} consumed, {Declined} turned down in Reel.",
            items.Count, accountId, saved, alreadySaved, reelsOwn, consumed, declined);
        return new WatchlistIngestResult(items.Count, saved, alreadySaved, reelsOwn, consumed, declined);
    }

    /// <summary>
    /// The catalogue rows for Trakt's recommendations, in the order given, matched by Trakt id and
    /// then TMDB id exactly as every other ingest path matches. A subject Reel has never seen is
    /// added from Trakt's own metadata (the caller saves, then hydrates from TMDB within budget). A
    /// title Reel ALREADY holds is left exactly as it is: this runs on a read path, and rewriting a
    /// shared, TMDB-hydrated row from a recommendation payload every few hours would let Trakt's
    /// spelling and genres overwrite TMDB's for every tenant. Ingest owns refreshing known titles.
    /// Duplicates resolve to one entry, at the position of their first appearance.
    /// </summary>
    public async Task<IReadOnlyList<Title>> ResolveRecommendedTitlesAsync(
        IReadOnlyList<TraktRecommendation> recommendations, CancellationToken ct)
    {
        var items = recommendations.Where(r => r.Movie is not null || r.Show is not null).ToList();
        var movieMap = await LoadTitleMapAsync(MediaType.Movie, items.Where(i => i.Movie is not null).Select(i => i.Movie!.Ids.Trakt), ct);
        var showMap = await LoadTitleMapAsync(MediaType.Show, items.Where(i => i.Show is not null).Select(i => i.Show!.Ids.Trakt), ct);
        var movieMapByTmdb = await LoadTitleMapByTmdbAsync(MediaType.Movie, items.Where(i => i.Movie is not null).Select(i => i.Movie!.Ids.Tmdb), ct);
        var showMapByTmdb = await LoadTitleMapByTmdbAsync(MediaType.Show, items.Where(i => i.Show is not null).Select(i => i.Show!.Ids.Tmdb), ct);

        var known = movieMap.Values.Concat(showMap.Values).Concat(movieMapByTmdb.Values).Concat(showMapByTmdb.Values)
            .Select(t => t.Id)
            .ToHashSet();
        var now = DateTime.UtcNow;
        var resolved = new List<Title>(items.Count);
        foreach (var item in items)
        {
            if (item.Movie is { } movie)
            {
                var title = ResolveOrAddTitle(movieMap, movieMapByTmdb, MediaType.Movie, movie.Ids.Trakt, movie.Ids.Tmdb, now);
                if (!known.Contains(title.Id))
                {
                    IngestMapper.ApplyMovie(title, movie);
                }

                resolved.Add(title);
            }
            else
            {
                var show = item.Show!;
                var title = ResolveOrAddTitle(showMap, showMapByTmdb, MediaType.Show, show.Ids.Trakt, show.Ids.Tmdb, now);
                if (!known.Contains(title.Id))
                {
                    IngestMapper.ApplyShow(title, show);
                }

                resolved.Add(title);
            }
        }

        return resolved.DistinctBy(t => t.Id).ToList();
    }

    /// <summary>The Trakt hidden-item sections Reel records (FD-17).</summary>
    public static readonly IReadOnlyList<string> HiddenSections = ["progress_watched", "recommendations", "dropped"];

    /// <summary>One recorded hidden item, as stored in <see cref="TraktConnection.HiddenItemsJson"/>.</summary>
    public sealed record RecordedHiddenItem(
        string Section, string Type, long? TraktId, long? TmdbId, Guid? TitleId, DateTime HiddenAt);

    /// <summary>
    /// Records the user's Trakt hidden and dropped items on the connection (FD-17: ingested, NOT
    /// applied — nothing reads them as an exclusion until the founder widens the footprint). Titles
    /// already in the catalogue are resolved to their id; nothing new is added to the catalogue for
    /// a title the user hid. A section Trakt refuses is skipped and the rest are kept. The caller
    /// saves. Returns how many items were recorded.
    /// </summary>
    public async Task<int> IngestHiddenAsync(TraktConnection connection, string token, RatePriority priority, CancellationToken ct)
    {
        var items = new List<TraktHiddenItem>();
        var failedSections = new HashSet<string>(StringComparer.Ordinal);
        foreach (var section in HiddenSections)
        {
            try
            {
                items.AddRange(await trakt.GetHiddenItemsAsync(token, section, priority, ct));
            }
            catch (HttpRequestException ex)
            {
                // One unavailable section must not cost the others, and must not erase what was
                // recorded for it last time: its previous items are carried over below.
                failedSections.Add(section);
                logger.LogWarning(ex, "Trakt hidden section {Section} could not be read; recording the others.", section);
            }
        }

        if (failedSections.Count == HiddenSections.Count)
        {
            return 0;
        }

        var subjects = items
            .Select(i => (Item: i, Ids: i.Movie?.Ids ?? i.Show?.Ids, Media: i.Movie is not null ? MediaType.Movie : MediaType.Show))
            .Where(x => x.Ids is not null)
            .ToList();
        var movieMap = await LoadTitleMapAsync(MediaType.Movie, subjects.Where(s => s.Media == MediaType.Movie).Select(s => s.Ids!.Trakt), ct);
        var showMap = await LoadTitleMapAsync(MediaType.Show, subjects.Where(s => s.Media == MediaType.Show).Select(s => s.Ids!.Trakt), ct);

        var recorded = subjects
            .Select(s => new RecordedHiddenItem(
                s.Item.Section ?? "unknown",
                s.Item.Type,
                s.Ids!.Trakt,
                s.Ids.Tmdb,
                (s.Media == MediaType.Movie ? movieMap : showMap).TryGetValue(s.Ids.Trakt, out var title) ? title.Id : null,
                ToUtc(s.Item.HiddenAt)))
            .Concat(PreviousHiddenItems(connection).Where(p => failedSections.Contains(p.Section)))
            .ToList();

        connection.HiddenItemsJson = JsonSerializer.Serialize(recorded);
        connection.HiddenItemsSyncedAt = DateTime.UtcNow;
        logger.LogInformation(
            "Recorded {Count} Trakt hidden item(s) for {AccountId} (not applied as exclusions — FD-17).",
            recorded.Count, connection.AccountId);
        return recorded.Count;
    }

    private static IReadOnlyList<RecordedHiddenItem> PreviousHiddenItems(TraktConnection connection)
    {
        if (string.IsNullOrWhiteSpace(connection.HiddenItemsJson))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<RecordedHiddenItem>>(connection.HiddenItemsJson) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static DateTime ToUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };

    private static Guid? OutboxTitleOf(string payloadJson)
    {
        try
        {
            return JsonSerializer.Deserialize<OutboxDispatcher.OutboxPayload>(payloadJson)?.TitleId;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static Dictionary<string, object?> Progress(string kind, int found) => new()
    {
        ["kind"] = kind,
        ["found"] = found,
    };

    private async Task<Dictionary<long, Title>> LoadTitleMapAsync(MediaType mediaType, IEnumerable<long> traktIds, CancellationToken ct)
    {
        var ids = traktIds.Distinct().ToList();
        var map = new Dictionary<long, Title>(ids.Count);
        foreach (var chunk in ids.Chunk(1000))
        {
            var titles = await db.Titles
                .Where(t => t.MediaType == mediaType && t.TraktId != null && chunk.Contains(t.TraktId.Value))
                .ToListAsync(ct);
            foreach (var title in titles)
            {
                map[title.TraktId!.Value] = title;
            }
        }

        return map;
    }

    private async Task<Dictionary<long, Title>> LoadTitleMapByTmdbAsync(MediaType mediaType, IEnumerable<long?> tmdbIds, CancellationToken ct)
    {
        var ids = tmdbIds.Where(id => id is not null).Select(id => id!.Value).Distinct().ToList();
        var map = new Dictionary<long, Title>(ids.Count);
        foreach (var chunk in ids.Chunk(1000))
        {
            var titles = await db.Titles
                .Where(t => t.MediaType == mediaType && t.TmdbId != null && chunk.Contains(t.TmdbId.Value))
                .ToListAsync(ct);
            foreach (var title in titles)
            {
                map[title.TmdbId!.Value] = title;
            }
        }

        return map;
    }

    /// <summary>
    /// Resolves the catalog Title for a Trakt subject, deduping by BOTH Trakt id and TMDB id. A title
    /// can already exist under its TMDB id alone (discovered via TMDB, no Trakt id yet); matching only
    /// on Trakt id would then insert a second row and violate the (MediaType, TmdbId) unique index,
    /// failing the whole SaveChanges. Newly created titles are indexed into both maps so later items
    /// in the same batch reuse them.
    /// </summary>
    private Title ResolveOrAddTitle(
        Dictionary<long, Title> byTrakt, Dictionary<long, Title> byTmdb,
        MediaType mediaType, long traktId, long? tmdbId, DateTime now)
    {
        if (byTrakt.TryGetValue(traktId, out var existing))
        {
            return existing;
        }

        if (tmdbId is { } tid && byTmdb.TryGetValue(tid, out var byTmdbHit))
        {
            // Adopt the TMDB-discovered row; ApplyMovie/ApplyShow stamps its TraktId. Index it under
            // the Trakt id so any later item in this batch resolves to the same instance.
            byTrakt[traktId] = byTmdbHit;
            return byTmdbHit;
        }

        var created = new Title { Id = Guid.NewGuid(), MediaType = mediaType, TraktId = traktId, CreatedAt = now };
        byTrakt[traktId] = created;
        if (tmdbId is { } newTid)
        {
            byTmdb[newTid] = created;
        }

        db.Titles.Add(created);
        return created;
    }

    private async Task<Dictionary<Guid, WatchedTitle>> LoadWatchedMapAsync(Guid accountId, CancellationToken ct) =>
        await db.WatchedTitles.Where(w => w.AccountId == accountId).ToDictionaryAsync(w => w.TitleId, ct);

    /// <summary>Returns true when this call INSERTED the row — i.e. the watch is new to Reel.</summary>
    private bool UpsertWatched(
        Dictionary<Guid, WatchedTitle> map, Guid accountId, Guid titleId,
        int plays, DateTime? lastWatchedAt, bool isFullyWatched, DateTime now)
    {
        var added = false;
        if (!map.TryGetValue(titleId, out var watched))
        {
            watched = new WatchedTitle { Id = Guid.NewGuid(), AccountId = accountId, TitleId = titleId, FirstSyncedAt = now };
            map[titleId] = watched;
            db.WatchedTitles.Add(watched);
            added = true;
        }

        watched.Plays = plays;
        watched.LastWatchedAt = lastWatchedAt;
        watched.IsFullyWatched = isFullyWatched;
        watched.UpdatedAt = now;
        return added;
    }
}
