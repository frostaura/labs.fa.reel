using System.Text.Json;
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
using Microsoft.EntityFrameworkCore;

namespace FrostAura.Reel.Api.Endpoints;

/// <summary>
/// Reactions are free for every tier — they feed the model. Each endpoint commits the local
/// change AND its Trakt outbox row in one transaction, then returns instantly; the dispatcher
/// makes Trakt consistent within seconds.
/// <para>
/// Every mutation here commits through <see cref="UniqueRaceSave.SaveAbsorbingRebuildRaceAsync"/>,
/// never a bare SaveChanges. Each one asks <see cref="FeedRefreshCoordinator"/> for a rebuild, and
/// that enqueue is a check-then-insert against a partial unique index — so a second rapid reaction,
/// or a delta sync enqueuing concurrently, loses the race with Postgres 23505. Unabsorbed, that
/// rolled back the user's reaction, its managed-list row and its outbox rows and answered 500: the
/// save the user just made silently vanished, in exactly the queueing burst that provokes the race.
/// The reaction is what must survive; the rebuild is already owned by the job that won.
/// </para>
/// <para>
/// Every handler body is a public static method rather than an inline lambda, so integration tests
/// exercise the exact endpoint semantics — including that save path — without an HTTP host.
/// </para>
/// </summary>
public static class ReactionEndpoints
{
    public record RateRequest(short Rating, bool MarkWatched = true);

    public record ReactionRequest(string? Reason);

    public static void MapReactionEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/titles/{mediaType}/{tmdbId:long}").RequireAccount();

        // ── Seen it + rate: one gesture ─────────────────────────────────────────────────
        group.MapPost("/rating", async (
            string mediaType, long tmdbId, RateRequest request,
            IReelDbContext db, IAccountContext accountContext, FeedRefreshCoordinator feedRefresh,
            OnlineResidualModel residual, RatingCalibrationService calibrator, RatingVerdictQuery verdicts,
            CancellationToken ct) =>
            await RateAsync(mediaType, tmdbId, request, db, accountContext.AccountId!.Value, feedRefresh, residual, calibrator, verdicts, ct));

        group.MapDelete("/rating", async (
            string mediaType, long tmdbId,
            IReelDbContext db, IAccountContext accountContext, FeedRefreshCoordinator feedRefresh, CancellationToken ct) =>
            await RemoveRatingAsync(mediaType, tmdbId, db, accountContext.AccountId!.Value, feedRefresh, ct));

        // ── Not interested / save for later ─────────────────────────────────────────────
        group.MapPost("/reactions/not_interested", async (
            string mediaType, long tmdbId, ReactionRequest request,
            IReelDbContext db, IAccountContext accountContext, FeedRefreshCoordinator feedRefresh,
            OnlineResidualModel residual, DislikeSignal dislikes, CancellationToken ct) =>
            await NotInterestedAsync(
                mediaType, tmdbId, request, db, accountContext.AccountId!.Value, feedRefresh, residual, dislikes, ct));

        // ── Drop a show (manual) — leaves Continue Watching AND every suggestion surface ──
        group.MapPost("/reactions/dropped", async (
            string mediaType, long tmdbId,
            IReelDbContext db, IAccountContext accountContext, FeedRefreshCoordinator feedRefresh,
            OnlineResidualModel residual, DislikeSignal dislikes, CancellationToken ct) =>
            await DroppedAsync(
                mediaType, tmdbId, db, accountContext.AccountId!.Value, feedRefresh, residual, dislikes, ct));

        group.MapPost("/reactions/save_for_later", async (
            string mediaType, long tmdbId,
            IReelDbContext db, IAccountContext accountContext, FeedRefreshCoordinator feedRefresh,
            OnlineResidualModel residual, CancellationToken ct) =>
            await SaveForLaterAsync(
                mediaType, tmdbId, db, accountContext.AccountId!.Value, feedRefresh, residual, ct));

        group.MapDelete("/reactions/{kind}", async (
            string mediaType, long tmdbId, string kind,
            IReelDbContext db, IAccountContext accountContext, FeedRefreshCoordinator feedRefresh,
            DislikeSignal dislikes, CancellationToken ct) =>
            await RevokeAsync(
                mediaType, tmdbId, kind, db, accountContext.AccountId!.Value, feedRefresh, dislikes, ct));
    }

    /// <summary>"Not for me" — an exclusion on every suggestion surface, and a soft negative label.</summary>
    public static async Task<IResult> NotInterestedAsync(
        string mediaType, long tmdbId, ReactionRequest request,
        IReelDbContext db, Guid accountId, FeedRefreshCoordinator feedRefresh,
        OnlineResidualModel residual, DislikeSignal dislikes, CancellationToken ct)
    {
        var title = await TitleEndpoints.ResolveTitleAsync(db, mediaType, tmdbId, ct);
        if (title is null)
        {
            return Results.NotFound();
        }

        var existing = await db.UserTitleReactions.FirstOrDefaultAsync(
            r => r.AccountId == accountId && r.TitleId == title.Id
                && r.Kind == ReactionKind.NotInterested && r.RevokedAt == null, ct);
        if (existing is null)
        {
            db.UserTitleReactions.Add(new UserTitleReaction
            {
                Id = Guid.NewGuid(),
                AccountId = accountId,
                TitleId = title.Id,
                Kind = ReactionKind.NotInterested,
                Reason = ParseReason(request.Reason),
                CreatedAt = DateTime.UtcNow,
            });
            // A rejection is a soft-negative label — nudge the residual down. Softened from the
            // original (2, 0.8) to (3, 0.4) in Phase 8b: the reason-weighted DislikeSignal
            // demotion now carries the per-similarity penalty, so the residual only needs to
            // learn the general direction, not shoulder the whole correction.
            await residual.ObserveAsync(accountId, title.Id, 3.0, 0.4, ct);
            // Evict the cached dislike profile so the demotion sees this reaction immediately.
            dislikes.Invalidate(accountId);
            // Rebuild so the rejected title is replaced by the next-best pick, not just left as a gap.
            await feedRefresh.RequestRebuildAsync(accountId, "not interested", ct);
            await db.SaveAbsorbingRebuildRaceAsync(ct);
        }

        return Results.Ok(new { notInterested = true });
    }

    /// <summary>Drop a show the user gave a shot — a milder negative than "not interested".</summary>
    public static async Task<IResult> DroppedAsync(
        string mediaType, long tmdbId,
        IReelDbContext db, Guid accountId, FeedRefreshCoordinator feedRefresh,
        OnlineResidualModel residual, DislikeSignal dislikes, CancellationToken ct)
    {
        var title = await TitleEndpoints.ResolveTitleAsync(db, mediaType, tmdbId, ct);
        if (title is null)
        {
            return Results.NotFound();
        }

        var existing = await db.UserTitleReactions.FirstOrDefaultAsync(
            r => r.AccountId == accountId && r.TitleId == title.Id
                && r.Kind == ReactionKind.Dropped && r.RevokedAt == null, ct);
        if (existing is null)
        {
            // No rating, no managed-list, no Trakt outbox (Trakt has no dropped concept). It
            // DOES teach the model since Phase 8b — founder decision: "the dislikes I give must
            // be taken into account". A drop is milder than not-interested (the user gave it a
            // shot), so the pseudo-label is a lukewarm 4 at a light 0.3 weight, and the dislike
            // profile folds it into the genre map when a reason is ever attached.
            db.UserTitleReactions.Add(new UserTitleReaction
            {
                Id = Guid.NewGuid(),
                AccountId = accountId,
                TitleId = title.Id,
                Kind = ReactionKind.Dropped,
                CreatedAt = DateTime.UtcNow,
            });
            await residual.ObserveAsync(accountId, title.Id, 4.0, 0.3, ct);
            dislikes.Invalidate(accountId);
            // Dropped now excludes from discovery too, so rebuild to backfill the slot it vacates
            // with the next-best pick — same treatment as not-interested / saved-for-later. GET
            // /api/feed also prunes it from the live snapshot immediately, so the picks list is
            // correct before this rebuild lands.
            await feedRefresh.RequestRebuildAsync(accountId, "dropped", ct);
            await db.SaveAbsorbingRebuildRaceAsync(ct);
        }

        return Results.Ok(new { dropped = true });
    }

    /// <summary>Save for later — the managed Trakt list plus the local queue, in one transaction.</summary>
    public static async Task<IResult> SaveForLaterAsync(
        string mediaType, long tmdbId,
        IReelDbContext db, Guid accountId, FeedRefreshCoordinator feedRefresh,
        OnlineResidualModel residual, CancellationToken ct)
    {
        var title = await TitleEndpoints.ResolveTitleAsync(db, mediaType, tmdbId, ct);
        if (title is null)
        {
            return Results.NotFound();
        }

        var now = DateTime.UtcNow;
        var existing = await db.UserTitleReactions.FirstOrDefaultAsync(
            r => r.AccountId == accountId && r.TitleId == title.Id
                && r.Kind == ReactionKind.SaveForLater && r.RevokedAt == null, ct);
        if (existing is null)
        {
            db.UserTitleReactions.Add(new UserTitleReaction
            {
                Id = Guid.NewGuid(),
                AccountId = accountId,
                TitleId = title.Id,
                Kind = ReactionKind.SaveForLater,
                CreatedAt = now,
            });

            var listItem = await db.ManagedListItems.FirstOrDefaultAsync(
                m => m.AccountId == accountId && m.TitleId == title.Id && m.RemovedAt == null, ct);
            if (listItem is null)
            {
                db.ManagedListItems.Add(new ManagedListItem
                {
                    Id = Guid.NewGuid(),
                    AccountId = accountId,
                    TitleId = title.Id,
                    AddedAt = now,
                });
            }

            Enqueue(db, accountId, OutboxKind.AddToWatchlist, title, null, null);
            Enqueue(db, accountId, OutboxKind.ListAdd, title, null, null);
            // A save is a mild positive intent — nudge the residual up (target 7.5, light weight).
            await residual.ObserveAsync(accountId, title.Id, 7.5, 0.4, ct);
            // Saved-for-later is an exclusion on the suggestion surfaces (it lives on Saved), so
            // rebuild to backfill the slot it vacates with the next-best pick.
            await feedRefresh.RequestRebuildAsync(accountId, "saved for later", ct);
            await db.SaveAbsorbingRebuildRaceAsync(ct);
        }

        return Results.Ok(new { savedForLater = true });
    }

    /// <summary>Revoke any of the three exclusions, restoring eligibility symmetrically.</summary>
    public static async Task<IResult> RevokeAsync(
        string mediaType, long tmdbId, string kind,
        IReelDbContext db, Guid accountId, FeedRefreshCoordinator feedRefresh,
        DislikeSignal dislikes, CancellationToken ct)
    {
        var title = await TitleEndpoints.ResolveTitleAsync(db, mediaType, tmdbId, ct);
        if (title is null)
        {
            return Results.NotFound();
        }

        var reactionKind = kind switch
        {
            "not_interested" => ReactionKind.NotInterested,
            "save_for_later" => ReactionKind.SaveForLater,
            "dropped" => ReactionKind.Dropped,
            _ => (ReactionKind?)null,
        };
        if (reactionKind is null)
        {
            return Results.BadRequest(new { error = "unknown reaction kind" });
        }

        var now = DateTime.UtcNow;
        var reaction = await db.UserTitleReactions.FirstOrDefaultAsync(
            r => r.AccountId == accountId && r.TitleId == title.Id
                && r.Kind == reactionKind && r.RevokedAt == null, ct);
        if (reaction is not null)
        {
            reaction.RevokedAt = now;

            if (reactionKind == ReactionKind.SaveForLater)
            {
                var listItem = await db.ManagedListItems.FirstOrDefaultAsync(
                    m => m.AccountId == accountId && m.TitleId == title.Id && m.RemovedAt == null, ct);
                if (listItem is not null)
                {
                    listItem.RemovedAt = now;
                    listItem.RemovalReason = ListRemovalReason.UserRemoved;
                }

                Enqueue(db, accountId, OutboxKind.RemoveFromWatchlist, title, null, null);
                Enqueue(db, accountId, OutboxKind.ListRemove, title, null, null);
            }

            // Revoking any of the three exclusions makes the title a suggestion candidate again —
            // rebuild so it can re-earn a slot live. Dropped is in this list because it now
            // excludes from discovery, so un-dropping must restore eligibility symmetrically.
            // The dislike profile drops the revoked reaction on the next read; the residual
            // observe made on create is deliberately NOT reversed — a single soft-weighted
            // pseudo-label inside a clamped online head is noise-level, and the head auto-resets
            // on every retrain anyway.
            if (reactionKind is ReactionKind.NotInterested or ReactionKind.Dropped)
            {
                dislikes.Invalidate(accountId);
            }

            await feedRefresh.RequestRebuildAsync(accountId, "reaction revoked", ct);

            await db.SaveAbsorbingRebuildRaceAsync(ct);
        }

        return Results.Ok(new { revoked = kind });
    }

    /// <summary>
    /// The "seen it + rate" gesture. Idempotent on the same value (a duplicate click must not
    /// re-send anything), and Trakt history is movie-only: a show rating never touches watched
    /// state — writing a bare show to sync/history marks EVERY aired episode watched. Public so
    /// integration tests exercise the exact endpoint semantics without an HTTP host.
    /// </summary>
    public static async Task<IResult> RateAsync(
        string mediaType, long tmdbId, RateRequest request,
        IReelDbContext db, Guid accountId, FeedRefreshCoordinator feedRefresh,
        OnlineResidualModel residual, RatingCalibrationService calibrator, RatingVerdictQuery verdicts,
        CancellationToken ct)
    {
        if (request.Rating is < 1 or > 10)
        {
            return Results.BadRequest(new { error = "rating must be 1-10" });
        }

        var title = await TitleEndpoints.ResolveTitleAsync(db, mediaType, tmdbId, ct);
        if (title is null)
        {
            return Results.NotFound();
        }

        var now = DateTime.UtcNow;
        var subjectType = title.MediaType == MediaType.Movie ? RatingSubjectType.Movie : RatingSubjectType.Show;

        // Resolved BEFORE anything is written. The rebuild this endpoint enqueues rescores the
        // catalogue, so reading the verdict afterwards would eventually score the rating against a
        // number the rating itself provoked. Read it while the past is still the past.
        var verdict = await verdicts.ForAsync(accountId, title.Id, request.Rating, now, ct);

        var rating = await db.UserRatings.FirstOrDefaultAsync(
            r => r.AccountId == accountId && r.TitleId == title.Id && r.SubjectType == subjectType
                && r.SeasonNumber == 0 && r.EpisodeNumber == 0, ct);
        var watched = await db.WatchedTitles.FirstOrDefaultAsync(
            w => w.AccountId == accountId && w.TitleId == title.Id, ct);

        if (rating is not null && rating.Rating == request.Rating)
        {
            // Same value already stored — return current state and change NOTHING about the rating:
            // no rating outbox rows, no play increments, no residual observation, no feed rebuild.
            // No verdict either: re-submitting a rating you already gave is not a new verdict on the
            // model, and firing the toast again would make a duplicate click look like a second data
            // point. No rating-history row for the same reason.
            //
            // The one exception is a save made AFTER that rating — a rewatch save. "Seen it + rate"
            // on it is the user saying they have now rewatched it, so the save is consumed even
            // though the score did not move. A duplicate click finds no live save and changes nothing.
            if (await SaveConsumption.LiveSaves(db, accountId)
                    .AnyAsync(r => r.TitleId == title.Id && r.CreatedAt > rating.RatedAt, ct))
            {
                await ConsumeSaveAsync(db, accountId, title, now, ct);
                await db.SaveChangesAsync(ct);
            }

            return Results.Ok(new
            {
                userRating = rating.Rating,
                isFullyWatched = watched?.IsFullyWatched ?? false,
                watchedLogged = false,
                verdict = (object?)null,
            });
        }

        // Captured BEFORE the assignment below: it is the only thing separating "first opinion"
        // from "changed their mind", and the ledger needs both.
        var previousRating = rating is null ? (short?)null : rating.Rating;

        if (rating is null)
        {
            rating = new UserRating
            {
                Id = Guid.NewGuid(),
                AccountId = accountId,
                TitleId = title.Id,
                SubjectType = subjectType,
                // Insert only — a re-rate below moves RatedAt and must never move this. It is the
                // eval split axis and the retrain drift axis; a mutable one lets a row cross the
                // train/holdout boundary after the fact.
                FirstRatedAt = now,
            };
            db.UserRatings.Add(rating);
        }

        rating.Rating = request.Rating;
        rating.RatedAt = now;
        rating.SyncedAt = now;
        rating.Source = RatingSource.Reel;

        // In-app ratings are ~2% of this product's labels, but they are the ones with a live
        // exposure behind them — so the ledger row is written in the SAME transaction as the rating
        // it describes. RatedAt and ObservedAt coincide here because Reel is the source.
        RatingEventLog.Stage(
            db, accountId, title.Id, subjectType, 0, 0,
            request.Rating, previousRating, now, now, RatingSource.Reel);

        Enqueue(db, accountId, OutboxKind.AddRating, title, request.Rating, null);

        // Trakt history write-back is movie-only, and only for a movie Reel has no play for yet —
        // an already-watched movie (plays from ingest) gets the rating alone. The one play Reel
        // logs is anchored on ReelLoggedWatchedAt so un-rating can reverse exactly that play.
        var logWatched = title.MediaType == MediaType.Movie && request.MarkWatched;
        var watchedLogged = false;
        if (logWatched && (watched is null || watched.Plays == 0))
        {
            if (watched is null)
            {
                watched = new WatchedTitle
                {
                    Id = Guid.NewGuid(),
                    AccountId = accountId,
                    TitleId = title.Id,
                    FirstSyncedAt = now,
                };
                db.WatchedTitles.Add(watched);
            }

            watched.Plays = 1;
            watched.LastWatchedAt = now;
            watched.IsFullyWatched = true; // "seen it" is the user's word — eligibility honours it
            watched.ReelLoggedWatchedAt = now;
            watched.UpdatedAt = now;
            watchedLogged = true;

            Enqueue(db, accountId, OutboxKind.AddToHistory, title, null, now);
        }

        // The save this rating consumed leaves the live queue in this same transaction.
        await ConsumeSaveAsync(db, accountId, title, now, ct);

        // Teach the online residual head the fresh label FIRST (target = the rating), so the
        // rebuild enqueued next already scores with the updated correction.
        await residual.ObserveAsync(accountId, title.Id, request.Rating, 1.0, ct);
        calibrator.Invalidate(accountId);

        // A fresh rating is the strongest taste signal there is — regenerate the feed now (reuses
        // the Active model) so the picks reflect it within seconds, not at the nightly build.
        await feedRefresh.RequestRebuildAsync(accountId, "in-app rating", ct);

        await db.SaveAbsorbingRebuildRaceAsync(ct);
        return Results.Ok(new
        {
            userRating = rating.Rating,
            isFullyWatched = watched?.IsFullyWatched ?? false,
            watchedLogged,
            // The after-rating moment. Null — not an empty object — when Reel never committed to a
            // number for this title, so the client renders nothing at all rather than a toast with
            // a hole in it. The copy itself is the client's: this endpoint reports the facts.
            verdict = verdict is { } v
                ? new
                {
                    predictedBefore = v.PredictedBefore,
                    surface = v.Surface,
                    shownAt = v.ShownAt,
                    delta = v.Delta,
                }
                : null,
        });
    }

    /// <summary>
    /// Un-rate, reversibly: alongside the rating removal, a movie play that Reel itself logged
    /// (anchored on ReelLoggedWatchedAt) is reversed — locally and via RemoveFromHistory on
    /// Trakt. Ingested plays are never touched. Public for integration tests, like RateAsync.
    /// </summary>
    public static async Task<IResult> RemoveRatingAsync(
        string mediaType, long tmdbId, IReelDbContext db, Guid accountId,
        FeedRefreshCoordinator feedRefresh, CancellationToken ct)
    {
        var title = await TitleEndpoints.ResolveTitleAsync(db, mediaType, tmdbId, ct);
        if (title is null)
        {
            return Results.NotFound();
        }

        var subjectType = title.MediaType == MediaType.Movie ? RatingSubjectType.Movie : RatingSubjectType.Show;
        var rating = await db.UserRatings.FirstOrDefaultAsync(
            r => r.AccountId == accountId && r.TitleId == title.Id && r.SubjectType == subjectType
                && r.SeasonNumber == 0 && r.EpisodeNumber == 0, ct);
        if (rating is not null)
        {
            // Staged BEFORE the delete, while the row still knows what it said. A retraction that
            // does not record the retracted value is a hole in the history, not an entry in it.
            RatingEventLog.StageRetraction(
                db, accountId, title.Id, subjectType, 0, 0,
                rating.Rating, rating.RatedAt, DateTime.UtcNow, rating.Source);

            db.UserRatings.Remove(rating);
            Enqueue(db, accountId, OutboxKind.RemoveRating, title, null, null);

            if (title.MediaType == MediaType.Movie)
            {
                var watched = await db.WatchedTitles.FirstOrDefaultAsync(
                    w => w.AccountId == accountId && w.TitleId == title.Id, ct);
                if (watched?.ReelLoggedWatchedAt is { } loggedAt)
                {
                    Enqueue(db, accountId, OutboxKind.RemoveFromHistory, title, null, loggedAt);
                    if (watched.Plays <= 1)
                    {
                        db.WatchedTitles.Remove(watched); // the Reel-logged play was the only one
                    }
                    else
                    {
                        watched.Plays -= 1;
                        watched.ReelLoggedWatchedAt = null;
                        watched.UpdatedAt = DateTime.UtcNow;
                    }
                }
            }

            // Retracting a rating both changes taste and makes the title eligible again — rebuild.
            await feedRefresh.RequestRebuildAsync(accountId, "rating retracted", ct);
            await db.SaveAbsorbingRebuildRaceAsync(ct);
        }

        return Results.Ok(new { userRating = (short?)null });
    }

    /// <summary>
    /// A rated save is a consumed save, whether or not the gesture logged the play: it leaves the
    /// live queue — the save REVOKED (never deleted) with its Trakt watchlist removal, and the list
    /// row closed with its ListRemove — on the caller's tracked context, so it commits in the same
    /// transaction as the rating. Closing only the list row is what left 116 watched, rated titles on
    /// the Saved shelf. Un-rating does not restore either: the user has seen it.
    /// </summary>
    private static async Task ConsumeSaveAsync(
        IReelDbContext db, Guid accountId, Title title, DateTime now, CancellationToken ct)
    {
        await SaveConsumption.RevokeAsync(db, accountId, title, now, ct);
        var listItem = await db.ManagedListItems.FirstOrDefaultAsync(
            m => m.AccountId == accountId && m.TitleId == title.Id && m.RemovedAt == null, ct);
        if (listItem is not null)
        {
            listItem.RemovedAt = now;
            listItem.RemovalReason = ListRemovalReason.Watched;
            Enqueue(db, accountId, OutboxKind.ListRemove, title, null, null);
        }
    }

    private static void Enqueue(IReelDbContext db, Guid accountId, OutboxKind kind, Title title, short? rating, DateTime? watchedAt)
    {
        var payload = new OutboxDispatcher.OutboxPayload(
            title.Id, title.MediaType, title.TmdbId, title.TraktId, rating, watchedAt);
        db.TraktOutbox.Add(new TraktOutboxEntry
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            Kind = kind,
            PayloadJson = JsonSerializer.Serialize(payload),
            EnqueuedAt = DateTime.UtcNow,
            NextAttemptAt = DateTime.UtcNow,
        });
    }

    // Internal for the mapping test — an unmapped wire value silently degrades to a reasonless
    // reaction, which is exactly the regression the test pins.
    internal static ReactionReason? ParseReason(string? reason) => reason?.ToLowerInvariant() switch
    {
        "genre" => ReactionReason.Genre,
        "seen-enough" or "seen_enough" => ReactionReason.SeenEnough,
        "tone" => ReactionReason.Tone,
        "not-for-me" or "not_for_me" => ReactionReason.NotForMe,
        _ => null,
    };
}
