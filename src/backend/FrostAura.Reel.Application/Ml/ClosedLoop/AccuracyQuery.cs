using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Feed;
using FrostAura.Reel.Domain.Library;
using FrostAura.Reel.Domain.Sync;
using Microsoft.EntityFrameworkCore;

namespace FrostAura.Reel.Application.Ml.ClosedLoop;

/// <summary>Where a number on the track record came from. Shown to the user, not hidden.</summary>
public static class AccuracySource
{
    /// <summary>Computed from the exposure log — a card that was demonstrably on screen.</summary>
    public const string Exposures = "exposures";

    /// <summary>
    /// Computed from the retro join (a persisted score that predates the rating). Weaker evidence
    /// that the user SAW the number, and the honest fallback until the exposure log has history.
    /// </summary>
    public const string Retro = "retro";

    /// <summary>Nothing to compute from. Not an error — a new account's true state.</summary>
    public const string None = "none";
}

/// <summary>One predicted band and how the ratings inside it actually landed.</summary>
public sealed record CalibrationBucket(int Index, decimal Low, decimal High, int Count, double? MeanPredicted, double? MeanActual);

/// <summary>One "here is a specific thing I got right or wrong" row for the moments strip.</summary>
public sealed record AccuracyMoment(
    Guid TitleId,
    string Name,
    int? Year,
    string? PosterPath,
    string MediaType,
    long? TmdbId,
    double Predicted,
    int Actual,
    string? Surface,
    DateTime? ShownAt,
    DateTime RatedAt,
    int? DaysToOutcome);

/// <summary>
/// The funnel. Every field is nullable because a rate over a zero denominator is not zero — it is
/// unknown, and rendering 0% for "we have never shown you anything" is a lie the page must not tell.
/// </summary>
public sealed record ConversionStats(
    int Shown,
    int Saved,
    int Watched,
    int Rated,
    double? ShownToSaved,
    double? SavedToWatched,
    double? ShownToRated,
    double? MedianDaysToWatch);

/// <summary>The whole /api/accuracy payload, before presentation rounding.</summary>
public sealed record AccuracyReport(
    string Window,
    DateTime? From,
    DateTime To,
    string Source,
    PredictionAccuracy Headline,
    IReadOnlyList<CalibrationBucket> Calibration,
    IReadOnlyList<AccuracyMoment> Moments,
    ConversionStats Conversion,
    IReadOnlyList<CounterfactualWindow> Counterfactual,
    ModelLearned? LearnedThisWeek);

/// <summary>
/// "How wrong am I, and what am I doing about it" — computed live, per account, from what already
/// happened. This is the query behind <c>/api/accuracy</c> and the <c>/track-record</c> page.
///
/// <para><b>Two evidence bases, and the page says which one it used.</b> The exposure log is the
/// strong one: a row there means a card carrying that exact number was returned to this account at
/// a known moment, so a later rating is genuinely a verdict on a promise. But the log starts empty
/// on the day it ships, and a track record that reads "no data" on day one is the opposite of
/// honest from day one. So when the exposure log holds nothing in the window, this falls back to
/// the retro join — a <c>TitleScore</c> written strictly before the rating — which is weaker
/// evidence (the score existed; we cannot prove the user saw it) and is labelled
/// <c>"retro"</c> so the difference is visible rather than smoothed over.</para>
///
/// <para><b>Nothing here is softened.</b> On the founder's live data the honest headline is a
/// within-one-point rate around a third and a correlation indistinguishable from zero. The
/// arithmetic is <see cref="RetroLoopStatistics"/>, shared with <c>/lab</c>, so the two surfaces
/// cannot drift into two different accounts of the same history. Rounding is the endpoint's job.</para>
///
/// <para>Tenancy is the DbContext's: every set touched is <c>IAccountScoped</c> and carries its
/// global filter, so no predicate here names an account id and <c>IgnoreQueryFilters</c> is never
/// called. Run the same query under two scopes and get two answers — which is what the isolation
/// test asserts.</para>
/// </summary>
public sealed class AccuracyQuery(IReelDbContext db, IAccountContext accountContext, RetroLoopQuery retro, ModelLearnedQuery learned)
{
    /// <summary>How many moments the strip shows. Newest first — the recent misses are the ones that sting.</summary>
    public const int MomentLimit = 50;

    /// <summary>Predicted-rating bands the calibration chart is cut into, across the full 0–10 range.</summary>
    public const int CalibrationBuckets = 10;

    /// <summary>The windows the endpoint accepts. Anything else is a 400, never a silent default.</summary>
    public static readonly IReadOnlyDictionary<string, int?> Windows = new Dictionary<string, int?>(StringComparer.OrdinalIgnoreCase)
    {
        ["90d"] = 90,
        ["365d"] = 365,
        ["all"] = null,
    };

    public async Task<AccuracyReport> ComputeAsync(string window, DateTime now, CancellationToken ct = default)
    {
        if (accountContext.AccountId is null)
        {
            throw new InvalidOperationException(
                "AccuracyQuery requires an account-scoped context — an unscoped run would aggregate every tenant.");
        }

        if (!Windows.TryGetValue(window, out var days))
        {
            throw new ArgumentOutOfRangeException(
                nameof(window), window, $"window must be one of: {string.Join(", ", Windows.Keys)}");
        }

        var from = days is { } d ? now.AddDays(-d) : (DateTime?)null;

        var pairs = await ExposurePairsAsync(from, ct);
        var source = pairs.Count > 0 ? AccuracySource.Exposures : AccuracySource.Retro;
        if (pairs.Count == 0)
        {
            pairs = await RetroPairsAsync(from, ct);
            if (pairs.Count == 0)
            {
                source = AccuracySource.None;
            }
        }

        var headline = RetroLoopStatistics.Summarise(
            pairs.Select(p => new PredictionPair(p.Predicted, p.Actual)).ToList());

        // The counterfactual and "learned this week" are the same reads /lab already makes. Reusing
        // them is not laziness: two implementations of "did Reel help" would eventually disagree,
        // and the one on the founder-facing page is the one that would be wrong.
        var report = await retro.ComputeAsync(now, ct);

        return new AccuracyReport(
            window,
            from,
            now,
            source,
            headline,
            Bucket(pairs.Select(p => new PredictionPair(p.Predicted, p.Actual)).ToList()),
            pairs
                .OrderByDescending(p => p.RatedAt)
                .ThenBy(p => p.Name, StringComparer.Ordinal)
                .Take(MomentLimit)
                .Select(p => new AccuracyMoment(
                    p.TitleId, p.Name, p.Year, p.PosterPath, p.MediaType, p.TmdbId,
                    p.Predicted, p.Actual, p.Surface, p.ShownAt,
                    p.RatedAt,
                    p.ShownAt is { } shown ? (int)Math.Round((p.RatedAt - shown).TotalDays, MidpointRounding.AwayFromZero) : null))
                .ToList(),
            await ConversionAsync(from, ct),
            report.Counterfactual,
            await learned.GetAsync(accountContext.AccountId.Value, ct));
    }

    /// <summary>
    /// The strong evidence base: the outcome view, restricted to exposures that were followed by a
    /// rating. The view's own time guard is what makes it causal, so no ordering is re-imposed here
    /// — a rating that predates its exposure never reaches this query with an ActualRating at all.
    ///
    /// <para>A title can be exposed many times before it is rated. The pair kept is the EARLIEST
    /// exposure that still postdates nothing — the first time Reel made the claim — because
    /// "predicted 8.4 twelve days before you rated it 5" is the honest framing, and taking the last
    /// exposure would flatter the number by scoring the most recently recalibrated promise.</para>
    /// </summary>
    private async Task<List<Pair>> ExposurePairsAsync(DateTime? since, CancellationToken ct)
    {
        var rows = await (
            from outcome in db.ExposureOutcomes
            where outcome.ActualRating != null && outcome.RatedAt != null
            where since == null || outcome.OccurredAt >= since
            join title in db.Titles on outcome.TitleId equals title.Id
            select new
            {
                outcome.TitleId,
                title.Name,
                title.Year,
                title.PosterPath,
                title.MediaType,
                title.TmdbId,
                Predicted = outcome.PredictedRating,
                Actual = outcome.ActualRating!.Value,
                outcome.Surface,
                outcome.OccurredAt,
                RatedAt = outcome.RatedAt!.Value,
            }).ToListAsync(ct);

        return rows
            .GroupBy(r => r.TitleId)
            .Select(g => g.OrderBy(r => r.OccurredAt).First())
            .Select(r => new Pair(
                r.TitleId, r.Name, r.Year, r.PosterPath, r.MediaType.ToString(), r.TmdbId,
                (double)r.Predicted, r.Actual, r.Surface.ToString(), r.OccurredAt, r.RatedAt))
            .ToList();
    }

    /// <summary>
    /// The fallback: every whole-title rating that arrived after Reel had already committed a score
    /// for that title. Strictly <c>ScoredAt &lt; FirstRatedAt</c> — a score written in the same
    /// instant as the rating is not a prediction of it. This is the same join <c>/lab</c>'s wider
    /// evidence base uses, so day one is not empty; it simply cannot name a surface or a shown-at,
    /// and the payload leaves both null rather than inventing them.
    /// </summary>
    private async Task<List<Pair>> RetroPairsAsync(DateTime? since, CancellationToken ct)
    {
        var rows = await (
            from rating in db.UserRatings
            where rating.SubjectType == RatingSubjectType.Movie || rating.SubjectType == RatingSubjectType.Show
            where since == null || rating.FirstRatedAt >= since
            where db.TitleScores.Any(s => s.TitleId == rating.TitleId && s.ScoredAt < rating.FirstRatedAt)
            join title in db.Titles on rating.TitleId equals title.Id
            select new
            {
                rating.TitleId,
                title.Name,
                title.Year,
                title.PosterPath,
                title.MediaType,
                title.TmdbId,
                Actual = (int)rating.Rating,
                Predicted = db.TitleScores
                    .Where(s => s.TitleId == rating.TitleId && s.ScoredAt < rating.FirstRatedAt)
                    .OrderByDescending(s => s.ScoredAt)
                    .Select(s => s.PredictedRating)
                    .First(),
                rating.FirstRatedAt,
            }).ToListAsync(ct);

        return rows
            .Select(r => new Pair(
                r.TitleId, r.Name, r.Year, r.PosterPath, r.MediaType.ToString(), r.TmdbId,
                (double)r.Predicted, r.Actual, Surface: null, ShownAt: null, r.FirstRatedAt))
            .ToList();
    }

    /// <summary>
    /// Ten equal predicted-rating bands over the full 0–10 range. Every band is emitted, including
    /// the empty ones: a chart drawn only over the bands that happen to have data hides that the
    /// model has never once predicted below 4, which is itself the finding.
    /// </summary>
    internal static List<CalibrationBucket> Bucket(IReadOnlyList<PredictionPair> pairs)
    {
        var width = 10m / CalibrationBuckets;
        var buckets = new List<CalibrationBucket>(CalibrationBuckets);
        for (var i = 0; i < CalibrationBuckets; i++)
        {
            var low = i * width;
            var high = low + width;
            // The top band is closed at both ends so a perfect 10 lands somewhere.
            var members = pairs
                .Where(p => (decimal)p.Predicted >= low
                    && (i == CalibrationBuckets - 1 ? (decimal)p.Predicted <= high : (decimal)p.Predicted < high))
                .ToList();

            buckets.Add(new CalibrationBucket(
                i,
                low,
                high,
                members.Count,
                members.Count == 0 ? null : members.Average(p => p.Predicted),
                members.Count == 0 ? null : members.Average(p => p.Actual)));
        }

        return buckets;
    }

    /// <summary>
    /// The funnel, in the honest direction: shown → saved → watched, plus shown → rated, and the
    /// median days a save waits before it is watched.
    ///
    /// <para>The denominator is DISTINCT titles exposed, not exposure rows — a page reloaded ten
    /// times has shown one title, and dividing by rows would report a conversion rate an order of
    /// magnitude too low. When the exposure log is empty the funnel falls back to the managed list,
    /// which is exactly the saved→watched loop the retro query already measures, and
    /// <see cref="ConversionStats.Shown"/> is then zero because nothing can honestly be claimed as
    /// shown.</para>
    /// </summary>
    private async Task<ConversionStats> ConversionAsync(DateTime? since, CancellationToken ct)
    {
        var exposed = await db.ExposureEvents
            .Where(e => since == null || e.OccurredAt >= since)
            .Select(e => e.TitleId)
            .Distinct()
            .CountAsync(ct);

        var saves = await db.ManagedListItems
            .Where(m => since == null || m.AddedAt >= since)
            .Select(m => new { m.TitleId, m.AddedAt, m.RemovedAt, m.RemovalReason })
            .ToListAsync(ct);

        var savedTitleIds = saves.Select(s => s.TitleId).Distinct().ToList();
        var watchedSaves = saves.Where(s => s.RemovalReason == ListRemovalReason.Watched && s.RemovedAt != null).ToList();

        var ratedAfterSave = await db.UserRatings
            .Where(r => savedTitleIds.Contains(r.TitleId))
            .Where(r => r.SubjectType == RatingSubjectType.Movie || r.SubjectType == RatingSubjectType.Show)
            .Select(r => r.TitleId)
            .Distinct()
            .CountAsync(ct);

        var daysToWatch = watchedSaves
            .Select(s => (s.RemovedAt!.Value - s.AddedAt).TotalDays)
            .Where(d => d >= 0)
            .OrderBy(d => d)
            .ToList();

        return new ConversionStats(
            exposed,
            savedTitleIds.Count,
            watchedSaves.Select(s => s.TitleId).Distinct().Count(),
            ratedAfterSave,
            Rate(savedTitleIds.Count, exposed),
            Rate(watchedSaves.Select(s => s.TitleId).Distinct().Count(), savedTitleIds.Count),
            Rate(ratedAfterSave, exposed),
            Median(daysToWatch));
    }

    /// <summary>A rate, or null. Never zero over an empty denominator — "unknown" is not "none".</summary>
    internal static double? Rate(int numerator, int denominator) =>
        denominator == 0 ? null : numerator / (double)denominator;

    /// <summary>Median of an ALREADY-SORTED sequence; null when empty.</summary>
    internal static double? Median(IReadOnlyList<double> sorted) =>
        sorted.Count == 0
            ? null
            : sorted.Count % 2 == 1
                ? sorted[sorted.Count / 2]
                : (sorted[(sorted.Count / 2) - 1] + sorted[sorted.Count / 2]) / 2d;

    /// <summary>One predicted-vs-actual observation with enough identity to render a poster for it.</summary>
    private sealed record Pair(
        Guid TitleId,
        string Name,
        int? Year,
        string? PosterPath,
        string MediaType,
        long? TmdbId,
        double Predicted,
        int Actual,
        string? Surface,
        DateTime? ShownAt,
        DateTime RatedAt);
}
