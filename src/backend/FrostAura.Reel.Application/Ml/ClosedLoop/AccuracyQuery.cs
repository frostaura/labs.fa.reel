using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Feed;
using FrostAura.Reel.Domain.Library;
using FrostAura.Reel.Domain.Ports;
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

/// <summary>
/// How the titles carrying one cross-reference standing actually turned out — the ONLINE half of
/// the cross-reference report, and corroboration only.
///
/// <para><b>It can never be the gate, and the shape says so.</b> Only about 2% of this account's
/// ratings are made in app, so the join behind it is temporal rather than causal, and the standing
/// itself is written on the exposure row at serve time rather than reconstructed later. Every rate
/// is nullable and <see cref="Answerable"/> is false until the count clears
/// <see cref="AccuracyQuery.MinimumVerdictOutcomes"/>: a mean later rating over four titles is a
/// number the page must refuse to draw a conclusion from, and refusing in words is the only way to
/// stop it being read as one.</para>
/// </summary>
/// <param name="Verdict">The <c>AggregateVerdict</c> name as recorded on the exposure row.</param>
/// <param name="Count">Distinct titles served under this standing that have since been rated.</param>
/// <param name="MeanLaterRating">What the account went on to give them, on average.</param>
/// <param name="LovedRate">Share later rated 8 or higher.</param>
/// <param name="DislikedRate">Share later rated 4 or lower.</param>
/// <param name="Answerable">Whether <paramref name="Count"/> is large enough to mean anything.</param>
public sealed record CrossReferenceVerdictOutcome(
    string Verdict,
    int Count,
    double? MeanLaterRating,
    double? LovedRate,
    double? DislikedRate,
    bool Answerable);

/// <summary>
/// The OFFLINE half: the newest pooled backtest at one prompt generation. This is the evidence base
/// that actually decided what the verdicts are allowed to do, so it is reported per generation
/// rather than collapsed — v1 and v2 are different questions and a single "latest" row would hide
/// which one is being served.
/// </summary>
/// <param name="PromptVersion">The generation this run judged under.</param>
/// <param name="EvaluatedAt">When the run finished.</param>
/// <param name="HoldoutJudged">Holdout titles judged, pooled across the split origins.</param>
/// <param name="Auc">Rank AUC of the signed confidence, loved against disliked. 0.5 is no discrimination.</param>
/// <param name="MeanLaterRatingKeep">Mean later rating of the titles it kept.</param>
/// <param name="MeanLaterRatingOut">Mean later rating of the titles it ruled out.</param>
/// <param name="KeepLovedRate">Share of kept titles later rated 8 or higher.</param>
/// <param name="KeepDislikedRate">Share of kept titles later rated 4 or lower.</param>
/// <param name="FalseRuleOutRate">Share of later-loved titles the judge ruled out.</param>
/// <param name="FalseRuleOutLow">Lower 95% Wilson bound on that.</param>
/// <param name="FalseRuleOutHigh">Upper 95% Wilson bound — the gate reads this one.</param>
/// <param name="TrueRuleOutRate">Share of later-disliked titles the judge ruled out.</param>
/// <param name="UnsureRate">Share of judged titles it could not call.</param>
/// <param name="Gating">What that earned: <c>gating</c> or <c>demote-only</c>.</param>
/// <param name="Caveat">The persisted "how to read this", naming n and the interval.</param>
/// <param name="Current">Whether this is the generation the job is writing today.</param>
public sealed record CrossReferenceBacktestSummary(
    int PromptVersion,
    DateTime EvaluatedAt,
    int HoldoutJudged,
    double? Auc,
    double? MeanLaterRatingKeep,
    double? MeanLaterRatingOut,
    double? KeepLovedRate,
    double? KeepDislikedRate,
    double FalseRuleOutRate,
    double FalseRuleOutLow,
    double FalseRuleOutHigh,
    double TrueRuleOutRate,
    double UnsureRate,
    string Gating,
    string Caveat,
    bool Current);

/// <summary>
/// The cross-reference block of the track record: what the backtest measured, and what the titles
/// actually served under each standing went on to do.
/// </summary>
/// <param name="Online">Per standing, from the exposure log. Corroboration, never the gate.</param>
/// <param name="Backtests">Newest pooled run per prompt generation, newest generation first.</param>
/// <param name="MinimumOutcomes">The count below which the online half declines to answer.</param>
public sealed record CrossReferenceAccuracy(
    IReadOnlyList<CrossReferenceVerdictOutcome> Online,
    IReadOnlyList<CrossReferenceBacktestSummary> Backtests,
    int MinimumOutcomes);

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
    ModelLearned? LearnedThisWeek,
    CrossReferenceAccuracy CrossReference);

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
public sealed class AccuracyQuery(
    IReelDbContext db,
    IAccountContext accountContext,
    RetroLoopQuery retro,
    ModelLearnedQuery learned,
    // Injected for ONE reason: which prompt generation the job is writing today, so the page can
    // mark the backtest row that actually governs what is being served. Reading it from a literal
    // here would be a second copy of a persisted key part, and the two would drift the first time
    // one moved.
    ICrossReferenceJudge judge)
{
    /// <summary>How many moments the strip shows. Newest first — the recent misses are the ones that sting.</summary>
    public const int MomentLimit = 50;

    /// <summary>Predicted-rating bands the calibration chart is cut into, across the full 0–10 range.</summary>
    public const int CalibrationBuckets = 10;

    /// <summary>
    /// Outcomes needed under one cross-reference standing before the page will read anything into
    /// it. Below this the block reports the count and says, in words, that it cannot yet answer.
    ///
    /// <para>Twenty is not a significance threshold and is not presented as one — at n = 20 a
    /// difference of a whole rating point is still inside the noise. It is the point below which a
    /// mean is actively misleading rather than merely imprecise, and it exists because this panel
    /// was never going to reach significance: demoted titles are served less often and only about
    /// 2% of this account's ratings are made in the app, which is why the OFFLINE backtest is the
    /// gate and this half is corroboration.</para>
    /// </summary>
    public const int MinimumVerdictOutcomes = 20;

    /// <summary>Rated this or higher is "loved" — the same bar the backtest and the harness use.</summary>
    private const int LovedThreshold = 8;

    /// <summary>Rated this or lower is "disliked".</summary>
    private const int DislikedThreshold = 4;

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
            await learned.GetAsync(accountContext.AccountId.Value, ct),
            await CrossReferenceAsync(from, ct));
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

    /// <summary>
    /// The cross-reference block: the offline runs that decided what a verdict may do, and the
    /// online outcomes of the cards actually served under each standing.
    ///
    /// <para><b>The temporal join is the same one the outcome view makes</b> — a rating counts only
    /// when its immutable <c>FirstRatedAt</c> POSTDATES the serve — and it is written out here
    /// rather than read off <c>ExposureOutcomes</c> because that view was created before the two
    /// xref columns existed and does not carry them. Adding them to the view would be a migration,
    /// and the join is four lines.</para>
    ///
    /// <para><b>NULL verdicts are excluded, not counted as Unsure.</b> A null means the pass had
    /// not reached the title when the card was served — by far the commonest state on an account
    /// mid-pass — and folding it into any bucket would put thousands of unjudged serves behind a
    /// number describing a judgement.</para>
    ///
    /// <para><b>One row per title, at its EARLIEST qualifying serve</b>, exactly as the headline
    /// pairs do: a title shown twenty times before it was rated is one outcome, and the first claim
    /// is the honest one to score.</para>
    /// </summary>
    private async Task<CrossReferenceAccuracy> CrossReferenceAsync(DateTime? since, CancellationToken ct)
    {
        var rows = await (
            from exposure in db.ExposureEvents
            where exposure.XrefVerdict != null
            where since == null || exposure.OccurredAt >= since
            join rating in db.UserRatings
                on new { exposure.AccountId, exposure.TitleId }
                equals new { rating.AccountId, rating.TitleId }
            where rating.SubjectType == RatingSubjectType.Movie || rating.SubjectType == RatingSubjectType.Show
            where rating.SeasonNumber == 0 && rating.EpisodeNumber == 0
            // The causal guard, and the reason FirstRatedAt exists: RatedAt is rewritten by every
            // Trakt ratings walk, so an old opinion would drift forward past a new serve.
            where rating.FirstRatedAt > exposure.OccurredAt
            select new
            {
                exposure.TitleId,
                Verdict = exposure.XrefVerdict!,
                exposure.OccurredAt,
                Actual = (int)rating.Rating,
            }).ToListAsync(ct);

        var online = rows
            .GroupBy(r => r.TitleId)
            .Select(g => g.OrderBy(r => r.OccurredAt).First())
            .GroupBy(r => r.Verdict, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new CrossReferenceVerdictOutcome(
                g.Key,
                g.Count(),
                g.Average(r => (double)r.Actual),
                Rate(g.Count(r => r.Actual >= LovedThreshold), g.Count()),
                Rate(g.Count(r => r.Actual <= DislikedThreshold), g.Count()),
                g.Count() >= MinimumVerdictOutcomes))
            .ToList();

        // The summary rows only — SplitFraction null is the pooled mean across the five origins,
        // and a per-origin row would be one fifth of the evidence presented as all of it.
        var evaluations = await db.CrossReferenceEvaluations
            .Where(e => e.SplitFraction == null)
            .ToListAsync(ct);

        var backtests = evaluations
            .GroupBy(e => e.PromptVersion)
            .Select(g => g.OrderByDescending(e => e.EvaluatedAt).First())
            .OrderByDescending(e => e.PromptVersion)
            .Select(e => new CrossReferenceBacktestSummary(
                e.PromptVersion,
                e.EvaluatedAt,
                e.HoldoutJudged,
                (double?)e.AucKeepVsOut,
                (double?)e.MeanLaterRatingKeep,
                (double?)e.MeanLaterRatingOut,
                (double?)e.KeepLovedRate,
                (double?)e.KeepDislikedRate,
                (double)e.FalseRuleOutRate,
                (double)e.WilsonLow,
                (double)e.WilsonHigh,
                (double)e.TrueRuleOutRate,
                (double)e.UnsureRate,
                e.GatingDecision,
                e.Caveat,
                e.PromptVersion == judge.PromptVersion))
            .ToList();

        return new CrossReferenceAccuracy(online, backtests, MinimumVerdictOutcomes);
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
