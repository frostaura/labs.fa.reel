using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Library;
using FrostAura.Reel.Domain.Sync;
using Microsoft.EntityFrameworkCore;

namespace FrostAura.Reel.Application.Ml.ClosedLoop;

/// <summary>One conversion the model got badly wrong — ordered by how far it over-promised.</summary>
public sealed record RetroMiss(Guid TitleId, string Name, int? Year, double Predicted, int Actual, double Gap);

/// <summary>
/// The saved-via-Reel → watched → rated loop: the only genuine "a recommendation was consumed"
/// event the product records today. <paramref name="Count"/> counts conversions;
/// <paramref name="RatedCount"/> counts the subset that can be scored at all.
/// </summary>
public sealed record RetroConversionReport(
    int Count,
    int RatedCount,
    int ScoredCount,
    PredictionAccuracy Accuracy,
    IReadOnlyList<KeyValuePair<int, int>> RatingDistribution,
    IReadOnlyList<RetroMiss> WorstMisses);

/// <summary>
/// The within-user counterfactual over one time window: how titles that reached the user through
/// Reel rated against what else the user rated AROUND THE SAME TIME. This is the number that says
/// whether the product helped, and it is deliberately a comparison against the user's own baseline
/// rather than against any model metric.
///
/// <para>Each via-Reel rating is matched to the user's other ratings made within
/// <see cref="RetroLoopQuery.MatchBandDays"/> days of it, so a user whose ratings drift over the
/// years is compared with themselves at the same moment, not with who they were in 2019. A via-Reel
/// rating with nothing else rated inside its band has no same-period baseline and is counted in
/// <paramref name="ViaReelUnmatchedCount"/> instead of in the means.</para>
/// </summary>
/// <param name="ViaReelCount">Via-Reel ratings that found a same-period baseline.</param>
/// <param name="ViaReelMeanRating">Their mean.</param>
/// <param name="OtherCount">Distinct other ratings inside at least one via-Reel rating's band.</param>
/// <param name="OtherMeanRating">The matched baseline: the mean, over the matched via-Reel ratings, of
/// each one's band mean — so <paramref name="Delta"/> is exactly the two means' difference.</param>
/// <param name="Delta">Mean of (via-Reel rating − its band's mean of the user's other ratings).</param>
/// <param name="ViaReelUnmatchedCount">Via-Reel ratings left out for want of a same-period baseline.</param>
/// <param name="MatchDays">The half-width of the band, in days.</param>
public sealed record CounterfactualWindow(
    string Window,
    int? Days,
    int ViaReelCount,
    double? ViaReelMeanRating,
    int OtherCount,
    double? OtherMeanRating,
    double? Delta,
    int ViaReelUnmatchedCount = 0,
    int MatchDays = RetroLoopQuery.MatchBandDays);

/// <summary>The whole founder-visible track record, computed live from existing tables — no new schema.</summary>
public sealed record RetroLoopReport(
    DateTime ComputedAt,
    RetroConversionReport Conversions,
    PredictionAccuracy PredictedVsActual,
    int PredictedVsActualPairs,
    IReadOnlyList<RetroMiss> PredictedVsActualWorstMisses,
    IReadOnlyList<CounterfactualWindow> Counterfactual);

/// <summary>
/// The retro closed loop, as a query rather than a pipeline: it joins what Reel predicted to what
/// the user actually did, using only tables that already exist (ManagedListItems, UserRatings,
/// TitleScores, FeedItems). Nothing here is cached or persisted — it is a measurement of history,
/// so it must be recomputable from history alone.
///
/// Tenancy is the DbContext's job. Every set touched below is <c>IAccountScoped</c> and carries its
/// global query filter, so this class NEVER names an account id in a predicate and never calls
/// <c>IgnoreQueryFilters</c>. The account comes from the ambient <see cref="IAccountContext"/>, which
/// is also what makes the isolation testable: run the same query under two scopes, get two answers.
/// </summary>
public sealed class RetroLoopQuery(IReelDbContext db, IAccountContext accountContext)
{
    /// <summary>How many over-promises the misses table shows.</summary>
    public const int WorstMissCount = 10;

    /// <summary>The rolling window the counterfactual reports alongside all-time.</summary>
    public const int RecentWindowDays = 365;

    /// <summary>
    /// Half-width, in days, of the same-period band each via-Reel rating is compared inside. Two
    /// weeks either side is wide enough that an ordinary month of rating finds a baseline, and
    /// narrow enough that a taste that drifts by half a point a year cannot pass for Reel's effect
    /// (HREV4-DESK-02: the founder's yearly means fall from about 7.0 to 6.06, and an unmatched
    /// all-time baseline turned that drift into a red −0.71).
    /// </summary>
    public const int MatchBandDays = 14;

    public async Task<RetroLoopReport> ComputeAsync(DateTime now, CancellationToken ct = default)
    {
        if (accountContext.AccountId is null)
        {
            throw new InvalidOperationException(
                "RetroLoopQuery requires an account-scoped context — an unscoped run would aggregate every tenant.");
        }

        var conversions = await ConversionsAsync(ct);
        var (widerAccuracy, widerPairs, widerMisses) = await PredictedVsActualAsync(ct);
        var counterfactual = await CounterfactualAsync(now, ct);

        return new RetroLoopReport(now, conversions, widerAccuracy, widerPairs, widerMisses, counterfactual);
    }

    /// <summary>
    /// Conversions: managed-list items Reel put in "Reel — Up Next" that the user then actually
    /// watched. The prediction is the newest TitleScore that existed AT the moment of removal —
    /// scoring a conversion against a model trained afterwards would be hindsight, not a track
    /// record. Titles scored only after the fact fall back to the latest score so a conversion is
    /// never silently dropped from the denominator; that fallback is counted separately.
    /// </summary>
    private async Task<RetroConversionReport> ConversionsAsync(CancellationToken ct)
    {
        var rows = await (
            from item in db.ManagedListItems
            where item.RemovalReason == ListRemovalReason.Watched && item.RemovedAt != null
            join title in db.Titles on item.TitleId equals title.Id
            select new
            {
                item.TitleId,
                title.Name,
                title.Year,
                // The user's own verdict. Max across the whole-title rating rows: a re-rate leaves one
                // row per (title, subject), and Max is the deterministic pick when both a Movie and a
                // Show row somehow exist for the same title id.
                Actual = db.UserRatings
                    .Where(r => r.TitleId == item.TitleId
                        && (r.SubjectType == RatingSubjectType.Movie || r.SubjectType == RatingSubjectType.Show))
                    .Max(r => (short?)r.Rating),
                PredictedAtRemoval = db.TitleScores
                    .Where(s => s.TitleId == item.TitleId && s.ScoredAt <= item.RemovedAt)
                    .OrderByDescending(s => s.ScoredAt)
                    .Select(s => (decimal?)s.PredictedRating)
                    .FirstOrDefault(),
                PredictedLatest = db.TitleScores
                    .Where(s => s.TitleId == item.TitleId)
                    .OrderByDescending(s => s.ScoredAt)
                    .Select(s => (decimal?)s.PredictedRating)
                    .FirstOrDefault(),
            }).ToListAsync(ct);

        var scored = rows
            .Select(r => new
            {
                r.TitleId,
                r.Name,
                r.Year,
                r.Actual,
                Predicted = r.PredictedAtRemoval ?? r.PredictedLatest,
            })
            .Where(r => r.Actual is not null && r.Predicted is not null)
            .Select(r => new
            {
                r.TitleId,
                r.Name,
                r.Year,
                Actual = (int)r.Actual!.Value,
                Predicted = (double)r.Predicted!.Value,
            })
            .ToList();

        var accuracy = RetroLoopStatistics.Summarise(
            scored.Select(r => new PredictionPair(r.Predicted, r.Actual)).ToList());

        var distribution = rows
            .Where(r => r.Actual is not null)
            .GroupBy(r => (int)r.Actual!.Value)
            .OrderBy(g => g.Key)
            .Select(g => new KeyValuePair<int, int>(g.Key, g.Count()))
            .ToList();

        var misses = scored
            .Select(r => new RetroMiss(r.TitleId, r.Name, r.Year, r.Predicted, r.Actual, r.Predicted - r.Actual))
            .OrderByDescending(m => m.Gap)
            .ThenBy(m => m.Name, StringComparer.Ordinal)
            .Take(WorstMissCount)
            .ToList();

        return new RetroConversionReport(
            rows.Count,
            rows.Count(r => r.Actual is not null),
            scored.Count,
            accuracy,
            distribution,
            misses);
    }

    /// <summary>
    /// The wider evidence base: every whole-title rating the user gave AFTER Reel had already
    /// committed to a number for that title. Strictly <c>ScoredAt &lt; RatedAt</c> — a score written
    /// in the same instant as the rating is not a prediction of it. This set is bigger than the
    /// conversions (it includes titles the user found on their own but Reel had scored) and it is
    /// the honest denominator for "how wrong is the number on the card".
    /// </summary>
    private async Task<(PredictionAccuracy Accuracy, int Pairs, IReadOnlyList<RetroMiss> WorstMisses)>
        PredictedVsActualAsync(CancellationToken ct)
    {
        var rows = await (
            from rating in db.UserRatings
            where rating.SubjectType == RatingSubjectType.Movie || rating.SubjectType == RatingSubjectType.Show
            where db.TitleScores.Any(s => s.TitleId == rating.TitleId && s.ScoredAt < rating.RatedAt)
            join title in db.Titles on rating.TitleId equals title.Id
            select new
            {
                rating.TitleId,
                title.Name,
                title.Year,
                Actual = (int)rating.Rating,
                Predicted = db.TitleScores
                    .Where(s => s.TitleId == rating.TitleId && s.ScoredAt < rating.RatedAt)
                    .OrderByDescending(s => s.ScoredAt)
                    .Select(s => s.PredictedRating)
                    .First(),
            }).ToListAsync(ct);

        var pairs = rows
            .Select(r => new PredictionPair((double)r.Predicted, r.Actual))
            .ToList();

        var misses = rows
            .Select(r => new RetroMiss(r.TitleId, r.Name, r.Year, (double)r.Predicted, r.Actual, (double)r.Predicted - r.Actual))
            .OrderByDescending(m => m.Gap)
            .ThenBy(m => m.Name, StringComparer.Ordinal)
            .Take(WorstMissCount)
            .ToList();

        return (RetroLoopStatistics.Summarise(pairs), rows.Count, misses);
    }

    /// <summary>
    /// "Titles I watched because of Reel average X; titles I found myself, rated in the same weeks,
    /// average Y." Via-Reel means the title passed through a surface Reel owns: the managed list
    /// (live or removed) or any feed item that survives the 7-snapshot prune. That prune makes the
    /// feed arm an UNDER-count, never an over-count, which is the safe direction for a number meant
    /// to hold the product to account.
    ///
    /// <para><b>Matched by time, never pooled.</b> Every via-Reel rating sits in 2026 because that
    /// is when Reel existed; the account's own finds span years, and its yearly means drift. Pooling
    /// every other rating since 2019 as the baseline reported that drift as Reel's effect. Each
    /// via-Reel rating is therefore compared with the user's other ratings within
    /// <see cref="MatchBandDays"/> days of it, and the gap is the mean of those per-rating gaps.
    /// Both halves of a window are limited to ratings inside it.</para>
    /// </summary>
    private async Task<IReadOnlyList<CounterfactualWindow>> CounterfactualAsync(DateTime now, CancellationToken ct)
    {
        // The two Reel surfaces are read as id sets once rather than as a correlated EXISTS per
        // rating: this endpoint is polled from /lab, and a per-row subquery over every rating the
        // user has ever given is a needless quadratic on a page that refreshes every few seconds.
        var viaReelTitleIds = (await db.ManagedListItems.Select(m => m.TitleId).Distinct().ToListAsync(ct))
            .ToHashSet();
        viaReelTitleIds.UnionWith(await db.FeedItems.Select(f => f.TitleId).Distinct().ToListAsync(ct));

        var rows = (await db.UserRatings
                .Where(r => r.SubjectType == RatingSubjectType.Movie || r.SubjectType == RatingSubjectType.Show)
                .Select(r => new { r.TitleId, r.Rating, r.RatedAt })
                .ToListAsync(ct))
            .Select(r => new RatedTitleRow(r.Rating, r.RatedAt, viaReelTitleIds.Contains(r.TitleId)))
            .ToList();

        var cutoff = now.AddDays(-RecentWindowDays);

        return
        [
            MatchedWindow($"last{RecentWindowDays}Days", RecentWindowDays, rows.Where(r => r.RatedAt >= cutoff)),
            MatchedWindow("allTime", null, rows),
        ];
    }

    /// <summary>
    /// The same-period comparison over one window's ratings. Internal and pure so the arithmetic is
    /// pinned without a database: each via-Reel rating against the mean of the other ratings within
    /// <see cref="MatchBandDays"/> days of it (inclusive), unmatched via-Reel ratings counted apart.
    /// </summary>
    internal static CounterfactualWindow MatchedWindow(string name, int? days, IEnumerable<RatedTitleRow> source)
    {
        var all = source.ToList();
        var others = all.Where(r => !r.ViaReel).OrderBy(r => r.RatedAt).ToList();
        var otherTimes = others.Select(r => r.RatedAt).ToList();
        var band = TimeSpan.FromDays(MatchBandDays);

        var matchedVia = new List<double>();
        var matchedBaselines = new List<double>();
        var usedOthers = new HashSet<int>();
        var unmatched = 0;

        foreach (var via in all.Where(r => r.ViaReel))
        {
            // First other rating at or after (via − band), then walk while inside (via + band).
            var lo = LowerBound(otherTimes, via.RatedAt - band);
            var sum = 0d;
            var n = 0;
            for (var i = lo; i < others.Count && others[i].RatedAt <= via.RatedAt + band; i++)
            {
                sum += others[i].Rating;
                n++;
                usedOthers.Add(i);
            }

            if (n == 0)
            {
                unmatched++;
                continue;
            }

            matchedVia.Add(via.Rating);
            matchedBaselines.Add(sum / n);
        }

        var viaMean = RetroLoopStatistics.MeanOrNull(matchedVia);
        var baseline = RetroLoopStatistics.MeanOrNull(matchedBaselines);
        return new CounterfactualWindow(
            name,
            days,
            matchedVia.Count,
            viaMean,
            usedOthers.Count,
            baseline,
            viaMean is not null && baseline is not null ? viaMean - baseline : null,
            unmatched,
            MatchBandDays);
    }

    /// <summary>Index of the first element of an ascending list that is ≥ <paramref name="value"/>.</summary>
    private static int LowerBound(List<DateTime> sorted, DateTime value)
    {
        int lo = 0, hi = sorted.Count;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (sorted[mid] < value)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        return lo;
    }

    /// <summary>One whole-title rating, tagged with whether the title ever passed through a Reel surface.</summary>
    internal sealed record RatedTitleRow(double Rating, DateTime RatedAt, bool ViaReel);
}
