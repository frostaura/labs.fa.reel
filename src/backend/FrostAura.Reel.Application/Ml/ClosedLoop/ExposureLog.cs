using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Domain.Feed;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.Extensions.Logging;

namespace FrostAura.Reel.Application.Ml.ClosedLoop;

/// <summary>
/// One card about to be logged. Everything the exposure row needs that the caller already has —
/// nothing here is re-derived from the database, because a serve path must not pay a second read
/// to record what it is already returning.
/// </summary>
/// <param name="TitleId">The title on the card.</param>
/// <param name="Rank">Position within the row, zero-based.</param>
/// <param name="PredictedRating">The prediction behind the card, at full precision — rounded to 2 dp here.</param>
/// <param name="BaseScore">Base model score plus the online residual, before calibration and demotion.</param>
/// <param name="CalibrationDelta">How far the monotone calibrator moved <paramref name="BaseScore"/>.</param>
/// <param name="ResidualApplied">The online residual head's scalar contribution.</param>
/// <param name="DemotionApplied">The dislike demotion subtracted after calibration.</param>
/// <param name="LlmFit">The re-rank fit where the surface computed one.</param>
/// <param name="WhySentence">The exact why-line rendered.</param>
/// <param name="WhySource">How that sentence was produced.</param>
/// <param name="RowKey">The rail's identity, anchor-qualified; null on hero/digest/search surfaces.</param>
/// <param name="PageIndex">Pagination depth; 0 on the first page and every non-paginated surface.</param>
public readonly record struct ExposureCard(
    Guid TitleId,
    int Rank,
    decimal PredictedRating,
    decimal BaseScore = 0m,
    decimal CalibrationDelta = 0m,
    decimal ResidualApplied = 0m,
    decimal DemotionApplied = 0m,
    decimal? LlmFit = null,
    string? WhySentence = null,
    string WhySource = ExposureWhySource.Unknown,
    string? RowKey = null,
    int PageIndex = 0);

/// <summary>
/// The closed vocabulary for <see cref="ExposureEvent.WhySource"/>. Strings rather than an enum
/// because the producing surfaces disagree about what a "source" is and a shared enum would force
/// a migration every time one of them learns a new way to caption a card.
/// </summary>
public static class ExposureWhySource
{
    /// <summary>An LLM curation/why pass wrote it.</summary>
    public const string Llm = "Llm";

    /// <summary>Served from the per-(account, title, row) fit cache.</summary>
    public const string Cache = "Cache";

    /// <summary>The deterministic templater wrote it from verified contributions.</summary>
    public const string Templater = "Templater";

    /// <summary>Carried over unchanged from the previous snapshot's row.</summary>
    public const string Preserved = "Preserved";

    /// <summary>The single generic floor sentence, or an empty caption.</summary>
    public const string Generic = "Generic";

    /// <summary>The digest's own fixed reason copy.</summary>
    public const string Digest = "Digest";

    /// <summary>The surface did not say. Never guessed at — an unattributed why is recorded as one.</summary>
    public const string Unknown = "Unknown";
}

/// <summary>
/// Writes the exposure log. The rule this class exists to enforce is that <b>recording a serve may
/// never break the serve</b>: a card the user can see is worth more than the row that says so.
///
/// <para><b>Two entry points, deliberately.</b> <see cref="Stage"/> adds rows to the caller's
/// already-tracked context and saves nothing — that is what the feed build uses, so the exposures
/// commit in the same transaction as the snapshot they describe, at zero extra round-trips.
/// <see cref="RecordAsync"/> stages and then saves inside a guard, and is what read-path endpoints
/// use, because a GET has no transaction of its own to ride.</para>
///
/// <para><b>Idempotency is applied twice, on purpose.</b> The unique index on
/// <c>(AccountId, TitleId, Surface, FeedSnapshotId, MinuteBucket)</c> is the structural truth, but
/// leaving it to throw would put a 23505 on the user's path. So a read filters the batch against
/// what the same minute already holds — which handles the ordinary case, an RTK Query refetch on
/// mount or tag invalidation — and the index only ever fires on a genuine concurrent race, where
/// <see cref="RecordAsync"/> absorbs it by detaching and moving on.</para>
/// </summary>
public sealed class ExposureLog(IReelDbContext db, ILogger<ExposureLog> logger)
{
    /// <summary>
    /// Stages exposure rows on the caller's context WITHOUT saving. Rows already present for the
    /// same idempotency key are skipped. Returns the entries staged, so a caller that later needs
    /// to abandon them (a guarded save) can detach exactly those.
    /// </summary>
    public async Task<IReadOnlyList<EntityEntry<ExposureEvent>>> Stage(
        Guid accountId,
        ExposureSurface surface,
        Guid? feedSnapshotId,
        Guid modelArtifactId,
        DateTime occurredAt,
        IReadOnlyList<ExposureCard> cards,
        CancellationToken ct)
    {
        if (cards.Count == 0)
        {
            return [];
        }

        var minuteBucket = Truncate(occurredAt);
        var titleIds = cards.Select(c => c.TitleId).Distinct().ToList();

        // The already-logged set for this exact key. Scoped by the caller's account explicitly as
        // well as by the global filter: this method is also reachable from a background job whose
        // context is unpinned, where the filter admits every tenant.
        var alreadyLogged = (await db.ExposureEvents
                .Where(e => e.AccountId == accountId
                    && e.Surface == surface
                    && e.FeedSnapshotId == feedSnapshotId
                    && e.MinuteBucket == minuteBucket
                    && titleIds.Contains(e.TitleId))
                .Select(e => e.TitleId)
                .ToListAsync(ct))
            .ToHashSet();

        var staged = new List<EntityEntry<ExposureEvent>>(cards.Count);
        // A single payload can legitimately carry one title twice (two rails, one film). The
        // idempotency key does not separate them, so the batch dedupes against itself too —
        // otherwise the two inserts collide inside one SaveChanges and neither lands.
        var seen = new HashSet<Guid>();
        foreach (var card in cards)
        {
            if (alreadyLogged.Contains(card.TitleId) || !seen.Add(card.TitleId))
            {
                continue;
            }

            staged.Add(db.ExposureEvents.Add(new ExposureEvent
            {
                Id = Guid.NewGuid(),
                AccountId = accountId,
                TitleId = card.TitleId,
                OccurredAt = occurredAt,
                Surface = surface,
                RowKey = card.RowKey,
                Rank = card.Rank,
                PageIndex = card.PageIndex,
                FeedSnapshotId = feedSnapshotId,
                ModelArtifactId = modelArtifactId,
                PredictedRating = Math.Round(card.PredictedRating, 2, MidpointRounding.AwayFromZero),
                BaseScore = Round3(card.BaseScore),
                CalibrationDelta = Round3(card.CalibrationDelta),
                ResidualApplied = Round3(card.ResidualApplied),
                DemotionApplied = Round3(card.DemotionApplied),
                LlmFit = card.LlmFit is { } fit ? Math.Round(fit, 3, MidpointRounding.AwayFromZero) : null,
                WhySentence = string.IsNullOrWhiteSpace(card.WhySentence) ? null : card.WhySentence,
                WhySource = card.WhySource,
            }));
        }

        return staged;
    }

    /// <summary>
    /// Stages and saves, swallowing every failure. Returns the number of rows written; zero is a
    /// perfectly ordinary answer (everything was already logged this minute).
    ///
    /// <para>The catch is deliberately unconditional. A serve path calls this AFTER it has decided
    /// what to return, and there is no failure here — a unique race, a transient connection drop, a
    /// column that grew a constraint nobody expected — that justifies turning a rendered page into a
    /// 500. Staged entries are detached on the way out so the caller's context is left exactly as it
    /// was found.</para>
    /// </summary>
    public async Task<int> RecordAsync(
        Guid accountId,
        ExposureSurface surface,
        Guid? feedSnapshotId,
        Guid modelArtifactId,
        DateTime occurredAt,
        IReadOnlyList<ExposureCard> cards,
        CancellationToken ct)
    {
        IReadOnlyList<EntityEntry<ExposureEvent>> staged = [];
        try
        {
            staged = await Stage(accountId, surface, feedSnapshotId, modelArtifactId, occurredAt, cards, ct);
            if (staged.Count == 0)
            {
                return 0;
            }

            await db.SaveChangesAsync(ct);
            return staged.Count;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Detach(staged);
            throw; // the caller is gone; this is not a logging failure
        }
        catch (Exception ex)
        {
            Detach(staged);
            logger.LogWarning(
                ex,
                "Exposure logging failed for {Surface} on {AccountId} ({Count} cards) — the response is unaffected.",
                surface, accountId, cards.Count);
            return 0;
        }
    }

    /// <summary>
    /// The minute bucket, computed the same way Postgres computes the generated column. Kept here
    /// rather than inlined so the idempotency read and the stored value can never drift apart.
    /// </summary>
    public static DateTime Truncate(DateTime occurredAt) =>
        new(occurredAt.Year, occurredAt.Month, occurredAt.Day, occurredAt.Hour, occurredAt.Minute, 0, DateTimeKind.Utc);

    private static void Detach(IReadOnlyList<EntityEntry<ExposureEvent>> staged)
    {
        foreach (var entry in staged)
        {
            entry.State = EntityState.Detached;
        }
    }

    private static decimal Round3(decimal value) => Math.Round(value, 3, MidpointRounding.AwayFromZero);
}
