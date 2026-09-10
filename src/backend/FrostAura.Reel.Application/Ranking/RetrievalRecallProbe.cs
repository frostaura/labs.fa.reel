using System.Diagnostics;
using FrostAura.Reel.Application.Ml;
using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Application.Search;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Library;
using Microsoft.EntityFrameworkCore;

namespace FrostAura.Reel.Application.Ranking;

/// <summary>
/// THE offline retrieval-recall test: of the titles this account went on to love, how many would
/// the first-pass pool have contained on the day before they were rated — and which arm would have
/// put them there?
///
/// <para><b>The question it settles.</b> Every modelling iteration to date has measured RANKING:
/// given a pool, does the model order it better than popularity does. None of them could see a
/// title the pool never contained, and a title that is never retrieved cannot be ranked, explained,
/// or recovered by any amount of model work. This measures the ceiling above the ranker. The plan's
/// decision rule reads directly off the union number: only if the union is recall-limited does a
/// public-corpus collaborative arm get considered, because that is an expensive licensing decision
/// and it is only worth taking if the arms Reel already has are demonstrably leaving titles behind.
/// </para>
///
/// <para><b>Read the two denominators separately, or the number will mislead you.</b> A loved title
/// can be missed for two completely different reasons, and they call for opposite responses:
/// <list type="number">
/// <item><b>It was not in the catalogue that day</b> — a SUPPLY failure. No retrieval arm can
/// return a row that does not exist, so widening retrieval budgets cannot help; discovery has to
/// find it earlier. This is what <c>InCatalogue</c> and the <c>NotReachable</c> list count.</item>
/// <item><b>It was in the catalogue and eligible, and no arm reached it</b> — a RETRIEVAL failure,
/// and the only one a bigger pool or a new arm would fix. This is <c>RecallGivenReachable</c>.</item>
/// </list>
/// Reporting only the raw recall conflates the two and points at the wrong fix.</para>
///
/// <para><b>What it cannot answer, stated up front.</b> TMDB's recommendations / similar /
/// person / keyword discover arms are NOT replayable offline: they are live calls whose answers on
/// a past day are gone. What survives is their deposit — a title they found is a row in the
/// catalogue with a <c>CreatedAt</c>. So catalogue membership as-of is used as the reachability
/// proxy for all four discovery arms at once, it is an UPPER BOUND on them, and it cannot separate
/// them from each other. It is not reported as an arm alongside popularity and ANN, because it is
/// not one — it is the precondition all of them share.</para>
///
/// <para>Read-only and persists nothing. It reuses <see cref="CandidateRetriever"/> and
/// <see cref="EligibilityQueryBuilder"/> rather than re-deriving either, so the recall it reports
/// describes the pool the feed actually assembles; a second copy of that composition would
/// eventually answer this question about a pool nothing serves.</para>
/// </summary>
public sealed class RetrievalRecallProbe(
    IReelDbContext db,
    FeatureVectorBuilder featureBuilder,
    CandidateRetriever retriever,
    EligibilityQueryBuilder eligibility)
{
    /// <summary>Ratings at or above this are "loved" — the titles the pool is judged on finding.</summary>
    public const short LovedThreshold = 8;

    /// <summary>Ratings at or below this are the false-positive set: things the pool DID surface and shouldn't have.</summary>
    public const short DislikedThreshold = 4;

    /// <summary>
    /// Default arm budgets, mirroring <c>BuildFeedJobHandler</c>'s <c>FirstPassPopPool</c> /
    /// <c>FirstPassAnnPool</c>. They are duplicated rather than shared deliberately: the endpoint
    /// takes both as parameters, so "would a bigger pool have caught these?" is answered by re-running
    /// with larger numbers rather than by shipping a change and waiting. Keep them in step with the
    /// feed's when the feed's move — a default that no longer matches the serving budget reports a
    /// recall nothing is achieving.
    /// </summary>
    public const int DefaultPopCount = 700;

    /// <inheritdoc cref="DefaultPopCount"/>
    public const int DefaultAnnCount = 500;

    /// <summary>Names listed in the not-reachable report before it is truncated.</summary>
    private const int MaxNamedMisses = 100;

    /// <summary>
    /// Runs the probe. Days are processed newest-first so that exhausting
    /// <paramref name="budget"/> costs the OLDEST evidence rather than the most relevant, and the
    /// report says how much it got through either way — a truncated run is reported as truncated,
    /// never rounded up into a complete one.
    /// </summary>
    public async Task<RetrievalRecallReport> RunAsync(
        Guid accountId,
        int windowDays,
        int popCount,
        int annCount,
        TimeSpan budget,
        DateTime now,
        CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        var since = now.AddDays(-windowDays);
        var maturityCeiling = await db.Accounts
            .Where(a => a.Id == accountId)
            .Select(a => a.Settings.MaturityCeiling)
            .FirstOrDefaultAsync(ct);

        // Movie/show-level ratings only: an episode rating is not a verdict on a title the feed
        // could have suggested. FirstRatedAt (never RatedAt) is the axis, for the same reason
        // EvalHarness splits on it — RatedAt is overwritten by every Trakt ratings walk, so a
        // re-rate would drag a title backwards across its own as-of line.
        var targets = await (
            from rating in db.UserRatings
            join title in db.Titles on rating.TitleId equals title.Id
            where rating.AccountId == accountId
                && (rating.SubjectType == RatingSubjectType.Movie || rating.SubjectType == RatingSubjectType.Show)
                && rating.FirstRatedAt >= since
                && (rating.Rating >= LovedThreshold || rating.Rating <= DislikedThreshold)
            select new Target(
                title.Id, title.Name, title.Year, rating.Rating, rating.FirstRatedAt, title.CreatedAt))
            .ToListAsync(ct);

        // One as-of instant per DAY, not per rating: the taste state and both retrievals are the
        // expensive part and they are identical for two titles rated on the same day.
        var byDay = targets
            .GroupBy(t => DateTime.SpecifyKind(t.FirstRatedAt.Date.AddDays(-1), DateTimeKind.Utc))
            .OrderByDescending(g => g.Key)
            .ToList();

        var outcomes = new List<Outcome>(targets.Count);
        var daysEvaluated = 0;
        var exhausted = false;

        foreach (var day in byDay)
        {
            if (stopwatch.Elapsed >= budget)
            {
                exhausted = true;
                break;
            }

            ct.ThrowIfCancellationRequested();
            var asOf = day.Key;

            // Everything user-side rebuilt strictly from events <= asOf, which is what makes the
            // centroids the ones the ANN arms would actually have used that day rather than today's.
            var taste = await featureBuilder.BuildTasteStateAsync(accountId, asOf, ct);

            // The serving lane, plus the reachability proxy: a title the catalogue did not hold on
            // that day could not have been returned by anything, so it must not be in the pool the
            // arms are judged against either.
            Func<IQueryable<Title>, IQueryable<Title>> lane =
                q => q.Where(t => t.TmdbId != null && t.PosterPath != null && t.CreatedAt <= asOf);

            var pool = await retriever.RetrieveWithArmsAsync(
                accountId, taste, lane, popCount, annCount, maturityCeiling, ct, asOf: asOf);

            // A second pass with the popularity arm switched OFF. Attribution in the combined pool
            // is first-claim (matching the cross-arm dedup), so popularity — which runs first —
            // shadows every title ANN would also have found, and ANN's own reach is unreadable from
            // it. Popularity needs no such pass: nothing runs ahead of it, so its first-claim
            // number IS its reach.
            var annOnly = annCount > 0
                ? await retriever.RetrieveWithArmsAsync(
                    accountId, taste, lane, popCount: 0, annCount, maturityCeiling, ct, asOf: asOf)
                : [];

            var pooled = pool.ToDictionary(hit => hit.Title.Id, hit => hit.Arm);
            var annReach = annOnly.ToDictionary(hit => hit.Title.Id, hit => hit.Arm);

            // "Eligible" here is membership of the as-of eligible catalogue, which the lane above
            // has already narrowed to titles that existed then — so a target that is in the
            // catalogue but absent from this set was excluded by a content rule or an interaction,
            // not by not existing yet.
            var dayIds = day.Select(t => t.TitleId).ToList();
            var eligibleRows = await lane(eligibility.EligibleTitlesAsOf(accountId, asOf))
                .Where(t => dayIds.Contains(t.Id))
                .ToListAsync(ct);
            var eligibleIds = eligibleRows
                // The maturity ceiling cannot order in SQL, so retrieval applies it in memory after
                // the fetch — and so must this, or a restricted profile reads as "retrieval missed
                // it" for titles the ceiling was always going to drop.
                .Where(t => EligibilityQueryBuilder.PassesMaturity(t, maturityCeiling))
                .Select(t => t.Id)
                .ToHashSet();

            foreach (var target in day)
            {
                outcomes.Add(new Outcome(
                    target,
                    asOf,
                    InCatalogue: target.CreatedAt <= asOf,
                    Eligible: eligibleIds.Contains(target.TitleId),
                    PoolArm: pooled.TryGetValue(target.TitleId, out var arm) ? arm : null,
                    AnnArm: annReach.TryGetValue(target.TitleId, out var ann) ? ann : null));
            }

            daysEvaluated++;
        }

        var evaluatedIds = outcomes.Select(o => o.Target.TitleId).ToHashSet();
        var skipped = targets.Where(t => !evaluatedIds.Contains(t.TitleId)).ToList();

        return new RetrievalRecallReport(
            WindowDays: windowDays,
            PopCount: popCount,
            AnnCount: annCount,
            AsOfRule: "the day before UserRating.FirstRatedAt, UTC",
            GeneratedAt: now,
            Budget: new BudgetReport(
                (int)budget.TotalSeconds,
                (int)stopwatch.ElapsedMilliseconds,
                exhausted,
                daysEvaluated,
                byDay.Count,
                skipped.Count),
            Loved: Summarise($">= {LovedThreshold}", outcomes.Where(o => o.Target.Rating >= LovedThreshold).ToList()),
            Disliked: Summarise($"<= {DislikedThreshold}", outcomes.Where(o => o.Target.Rating <= DislikedThreshold).ToList()),
            NotReachable: outcomes
                .Where(o => o.Target.Rating >= LovedThreshold && o.PoolArm is null)
                .OrderByDescending(o => o.Target.FirstRatedAt)
                .Take(MaxNamedMisses)
                .Select(o => new MissedTitle(
                    o.Target.TitleId,
                    o.Target.Name,
                    o.Target.Year,
                    o.Target.Rating,
                    o.Target.FirstRatedAt,
                    o.AsOf,
                    Reason(o)))
                .ToList());
    }

    /// <summary>
    /// Why one loved title was missed, in the order that matters operationally: a title that did not
    /// exist in the catalogue cannot also be described as "retrieval missed it", and widening a
    /// retrieval budget would not have helped it.
    /// </summary>
    private static string Reason(Outcome outcome) =>
        !outcome.InCatalogue ? "not in the catalogue that day — discovery, not retrieval"
        : !outcome.Eligible ? "in the catalogue but not eligible that day — a content rule or an interaction excluded it"
        : outcome.AnnArm is not null ? "reachable by ANN alone, but crowded out of the combined pool"
        : "eligible and reachable, and no arm returned it — a genuine retrieval miss";

    private static ArmRecall Summarise(string threshold, IReadOnlyList<Outcome> outcomes)
    {
        var total = outcomes.Count;
        var inCatalogue = outcomes.Count(o => o.InCatalogue);
        var eligible = outcomes.Count(o => o.Eligible);

        int Pooled(RetrievalArm arm) => outcomes.Count(o => o.PoolArm == arm);
        int Reached(RetrievalArm arm) => outcomes.Count(o => o.AnnArm == arm);

        var union = outcomes.Count(o => o.PoolArm is not null);

        return new ArmRecall(
            threshold,
            total,
            inCatalogue,
            eligible,
            new Hits(
                Popularity: Pooled(RetrievalArm.Popularity),
                AnnLoved: Reached(RetrievalArm.AnnLoved),
                AnnRecent: Reached(RetrievalArm.AnnRecent),
                Union: union),
            new Rates(
                Popularity: Rate(Pooled(RetrievalArm.Popularity), total),
                AnnLoved: Rate(Reached(RetrievalArm.AnnLoved), total),
                AnnRecent: Rate(Reached(RetrievalArm.AnnRecent), total),
                Union: Rate(union, total),
                InCatalogue: Rate(inCatalogue, total)),
            // The retrieval-only number: of the titles that existed AND were eligible that day, how
            // many did the arms return. This is the one a bigger pool or a new arm could move.
            RecallGivenReachable: Rate(union, eligible));
    }

    /// <summary>
    /// Four decimals, and null rather than 0 when the denominator is empty — "0% recall over zero
    /// titles" is the shape of a number that gets quoted in a decision later.
    /// </summary>
    private static double? Rate(int hits, int total) =>
        total == 0 ? null : Math.Round(hits / (double)total, 4);

    private readonly record struct Target(
        Guid TitleId, string Name, int? Year, short Rating, DateTime FirstRatedAt, DateTime CreatedAt);

    private readonly record struct Outcome(
        Target Target,
        DateTime AsOf,
        bool InCatalogue,
        bool Eligible,
        RetrievalArm? PoolArm,
        RetrievalArm? AnnArm);
}

/// <summary>The probe's whole answer. Persisted nowhere — it is read, acted on, and thrown away.</summary>
public sealed record RetrievalRecallReport(
    int WindowDays,
    int PopCount,
    int AnnCount,
    string AsOfRule,
    DateTime GeneratedAt,
    BudgetReport Budget,
    ArmRecall Loved,
    ArmRecall Disliked,
    IReadOnlyList<MissedTitle> NotReachable);

/// <summary>
/// How much of the window the run actually covered. <paramref name="Exhausted"/> true means the
/// numbers describe <paramref name="DaysEvaluated"/> of <paramref name="DaysTotal"/> days and
/// nothing more — read the rates as a sample, not as the window.
/// </summary>
public sealed record BudgetReport(
    int CapSeconds, int ElapsedMs, bool Exhausted, int DaysEvaluated, int DaysTotal, int TitlesSkipped);

/// <summary>
/// Recall for one rating band. On the loved band these are the numbers the plan's decision rule
/// reads; on the disliked band the SAME numbers invert into a false-positive rate — a high union
/// there means the arms are efficiently surfacing titles this user goes on to reject, which is a
/// ranking problem, not a supply one.
/// </summary>
public sealed record ArmRecall(
    string Threshold,
    int Targets,
    int InCatalogue,
    int EligibleAsOf,
    Hits Hits,
    Rates Rates,
    double? RecallGivenReachable);

/// <summary>
/// Counts behind <see cref="Rates"/>. <paramref name="Popularity"/> is first-claim from the
/// combined pool (nothing runs ahead of it, so that is its true reach); the two ANN figures come
/// from the popularity-free pass, so they are not shadowed by it. They therefore do NOT sum to
/// <paramref name="Union"/>, and are not meant to.
/// </summary>
public sealed record Hits(int Popularity, int AnnLoved, int AnnRecent, int Union);

/// <summary>
/// <see cref="Hits"/> over the evaluated target count. <paramref name="InCatalogue"/> is the supply
/// ceiling every other rate here sits under: no arm can beat it.
/// </summary>
public sealed record Rates(
    double? Popularity, double? AnnLoved, double? AnnRecent, double? Union, double? InCatalogue);

/// <summary>One loved title no arm returned, with the reason it was missed.</summary>
public sealed record MissedTitle(
    Guid TitleId, string Name, int? Year, short Rating, DateTime FirstRatedAt, DateTime AsOf, string Reason);
