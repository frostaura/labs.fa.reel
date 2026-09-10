using System.Text.Json;
using FrostAura.Reel.Application.Ml;
using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Application.Search;
using FrostAura.Reel.Domain.CrossReference;
using FrostAura.Reel.Domain.Library;
using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Domain.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FrostAura.Reel.Application.CrossReference;

/// <summary>
/// The offline backtest of the cross-reference judge — the ONLY thing that may promote a verdict
/// from "demotes a title" to "hides a title", and the whole reason PR1 exists before the job does.
///
/// <para><b>Leakage first, everything else second.</b> Every input is cut at the split point on the
/// immutable <c>UserRating.FirstRatedAt</c> axis: the pool comes from
/// <see cref="EligibilityQueryBuilder.EligibleTitlesAsOf"/>, the persona from
/// <see cref="PersonalisationContext.PersonaAsOfAsync"/> (never the stored narrative, which was
/// written from every rating including the holdout), and the neighbours from
/// <see cref="NeighbourFinder"/> with its as-of filter set. <c>CrossReferenceLeakageTests</c>
/// asserts all three directly, and was written before this class was.</para>
///
/// <para><b>Round 1 only.</b> Round 2 exists to make a Confirmed Keep mean something; the question
/// here is entirely about ruling OUT, and round 1 is the pass that does that. Judging both would
/// double the cost of the measurement to answer a question the measurement is not asking.</para>
///
/// <para><b>The gate is a bound, not a point estimate.</b> See
/// <see cref="CrossReferenceEvaluation"/>: at n≈55 an observed 9% false-rule-out has a 95% upper
/// bound near 20%, so reading the 9% as "under 15%" is reading noise as a result. All three clauses
/// must hold, at every origin, or the decision is <c>demote-only</c> and the band on the feed says
/// so in words with the real number in it.</para>
/// </summary>
public sealed class CrossReferenceBacktest(
    IReelDbContext db,
    ICrossReferenceJudge judge,
    FeatureVectorBuilder features,
    PersonalisationContext personalisation,
    EligibilityQueryBuilder eligibility,
    CrossReferenceCandidateBuilder candidates,
    ILogger<CrossReferenceBacktest> logger)
{
    /// <summary>Candidates per provider call. The whole budget arithmetic is written against 60.</summary>
    public const int BatchSize = 60;

    /// <summary>Rated at or above this is "loved" — the same bar the eval harness uses.</summary>
    public const short LovedFloor = 8;

    /// <summary>Rated at or below this is "disliked".</summary>
    public const short DislikedCeiling = 4;

    /// <summary>Below this many ratings there is no history to split. Mirrors the harness's floor.</summary>
    public const int MinRatingsToEvaluate = 50;

    /// <summary>Wilson-upper on the false-rule-out rate must be at or under this to gate.</summary>
    public const decimal FalseRuleOutCeiling = 0.15m;

    /// <summary>And the judge must actually catch this share of the titles the viewer went on to dislike.</summary>
    public const decimal TrueRuleOutFloor = 0.40m;

    /// <summary>And it must answer at all: more unsure than this is honest and useless.</summary>
    public const decimal UnsureCeiling = 0.60m;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>One completed run: the five origins plus the mean row that summarises them.</summary>
    public sealed record Report(
        Guid RunId,
        IReadOnlyList<CrossReferenceEvaluation> Origins,
        CrossReferenceEvaluation Mean);

    /// <summary>
    /// Runs the backtest at the judge's CURRENT prompt generation.
    /// </summary>
    public Task<Report?> RunAsync(Guid accountId, CancellationToken ct) =>
        RunAsync(accountId, judge.PromptVersion, ct);

    /// <summary>
    /// Runs the backtest at an explicit prompt generation and persists one
    /// <see cref="CrossReferenceEvaluation"/> per split origin plus a mean row, with one
    /// <see cref="CrossReferenceEvaluationItem"/> per judged title. Returns null when the account
    /// has too little history to split.
    ///
    /// <para><b>The version is a parameter here and nowhere else.</b> A prompt generation reaches
    /// the feed by being measured against the one it replaces, not by a caller selecting it —
    /// which is why the serving job rides <c>ICrossReferenceJudge.PromptVersion</c> and only this
    /// method, reached only from the founder-only dev endpoint, can name one.</para>
    /// </summary>
    public async Task<Report?> RunAsync(Guid accountId, int promptVersion, CancellationToken ct)
    {
        var labels = await db.UserRatings
            .Where(r => r.AccountId == accountId
                && (r.SubjectType == RatingSubjectType.Movie || r.SubjectType == RatingSubjectType.Show))
            .OrderBy(r => r.FirstRatedAt)
            .Select(r => new EvalHarness.SplitLabel(r.TitleId, r.Rating, r.FirstRatedAt))
            .ToListAsync(ct);

        if (labels.Count < MinRatingsToEvaluate)
        {
            logger.LogWarning(
                "Account {AccountId} has {Count} ratings — below the {Min} cross-reference backtest floor.",
                accountId, labels.Count, MinRatingsToEvaluate);
            return null;
        }

        var runId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var origins = new List<CrossReferenceEvaluation>();

        // Every origin's items, pooled for the mean row. The five origins overlap heavily, so a
        // title judged at three of them contributes three observations — which is the same pooling
        // the false- and true-rule-out counts already use, for the reason recorded on MeanRow:
        // averaging five per-origin answers lets a small origin outvote a large one, and the
        // interval and the AUC are only valid readings of the pooled evidence.
        var pooled = new List<(CrossReferenceVerdict Verdict, double Confidence, short LaterRating)>();

        foreach (var fraction in EvalHarness.SplitOrigins)
        {
            var row = await EvaluateOriginAsync(accountId, runId, labels, fraction, promptVersion, now, pooled, ct);
            if (row is not null)
            {
                origins.Add(row);
                db.CrossReferenceEvaluations.Add(row);
            }
        }

        if (origins.Count == 0)
        {
            logger.LogWarning(
                "Account {AccountId}: no split origin produced an evaluable cross-reference holdout.", accountId);
            return null;
        }

        var mean = MeanRow(accountId, runId, origins, pooled, judge.ModelId, promptVersion, now);
        db.CrossReferenceEvaluations.Add(mean);
        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Cross-reference backtest for {AccountId}: {Origins} origin(s), mean false-rule-out {Rate} "
            + "(Wilson upper {High}), true-rule-out {True}, unsure {Unsure} — {Decision}.",
            accountId, origins.Count, mean.FalseRuleOutRate, mean.WilsonHigh,
            mean.TrueRuleOutRate, mean.UnsureRate, mean.GatingDecision);

        return new Report(runId, origins, mean);
    }

    private async Task<CrossReferenceEvaluation?> EvaluateOriginAsync(
        Guid accountId, Guid runId, IReadOnlyList<EvalHarness.SplitLabel> labels, double fraction,
        int promptVersion, DateTime now,
        List<(CrossReferenceVerdict Verdict, double Confidence, short LaterRating)> pooled,
        CancellationToken ct)
    {
        // The split point, on the same axis and by the same index rule the M2 harness uses, so a
        // cross-reference origin and an NDCG origin describe the same instant.
        var splitAt = labels[EvalHarness.SplitIndex(labels.Count, fraction)].Axis;

        // The holdout: rated strictly after the split, one row per title at its highest rating.
        var holdout = labels
            .Where(l => l.Axis > splitAt)
            .GroupBy(l => l.TitleId)
            .Select(g => (TitleId: g.Key, Rating: g.Max(l => l.Rating)))
            .ToList();
        if (holdout.Count == 0)
        {
            return null;
        }

        // …and eligible AS OF the split. A title the account had already watched or reacted to
        // before the split would never have been shown to the judge in the first place, so scoring
        // the judge on it measures nothing the product would ever have done.
        var eligibleIds = await eligibility.EligibleTitlesAsOf(accountId, splitAt)
            .Select(t => t.Id)
            .ToListAsync(ct);
        var eligible = eligibleIds.ToHashSet();

        var pool = holdout.Where(h => eligible.Contains(h.TitleId)).ToList();
        if (pool.Count == 0)
        {
            return null;
        }

        // As-of taste state → as-of persona. The stored narrative is deliberately never read: it
        // was written from every rating this account has ever made, holdout included.
        var taste = await features.BuildTasteStateAsync(accountId, splitAt, ct);
        var persona = await personalisation.PersonaAsOfAsync(accountId, taste, splitAt, ct);

        // The model's own number is deliberately NOT supplied. A TitleScore is written by a present
        // day artifact trained on the whole history, so putting one in an as-of prompt is the same
        // leak in a different costume — the judge sees the catalogue evidence and the viewer's own
        // neighbours, and nothing that knows the answer.
        var noPredictions = new Dictionary<Guid, decimal>();
        // The evidence this generation is shown, from the ONE place that mapping lives.
        var neighbourOptions = NeighbourFinder.ForPromptVersion(promptVersion, splitAt);

        // The genre affinity line and the base rates: both from state cut at the split, and the
        // affinity from the as-of TASTE STATE rather than the stored fact sheet, which is cached,
        // current and built partly from the holdout. v1 renders neither.
        // …and computed only where a generation renders them. The adapter would ignore them at v1
        // anyway, but a v1 options record carrying v2's inputs invites the next reader to render
        // them "since they are already there", and the byte-identity of a v1 prompt is the thing
        // that keeps a stored v1 verdict meaning what it meant.
        var renderable = promptVersion >= CrossReferencePromptVersions.Second;
        var (lovedShare, dislikedShare) = renderable
            ? GenreAffinityLine.BaseRates(labels.Where(l => l.Axis <= splitAt).Select(l => l.Rating).ToList())
            : (null, null);
        var judgeOptions = new CrossReferenceJudgeOptions(
            promptVersion, renderable ? GenreAffinityLine.Build(taste) : null, lovedShare, dislikedShare);

        var verdicts = new Dictionary<Guid, CrossReferenceEntry>();
        var neighboursShown = new Dictionary<Guid, IReadOnlyList<CrossReferenceNeighbour>>();
        var batchOf = new Dictionary<Guid, int>();
        var noEmbedding = 0;
        long promptTokens = 0;
        long completionTokens = 0;

        // Deterministic order in, deterministic batches: the judge shuffles WITHIN a batch, so the
        // batching itself must not depend on Postgres row order or two runs disagree.
        var ordered = pool.Select(p => p.TitleId).OrderBy(id => id).ToList();
        var batchIndex = 0;
        for (var offset = 0; offset < ordered.Count; offset += BatchSize, batchIndex++)
        {
            var slice = ordered.Skip(offset).Take(BatchSize).ToList();
            var batch = await candidates.BuildAsync(accountId, slice, noPredictions, neighbourOptions, ct);
            noEmbedding += batch.SkippedForNoSignal;
            if (batch.Candidates.Count == 0)
            {
                continue;
            }

            var result = await judge.JudgeAsync(
                batch.Candidates, persona, CrossReferenceRound.First, ApiUsagePurpose.CrossReferenceEval,
                judgeOptions, ct);
            promptTokens += result.PromptTokens ?? 0;
            completionTokens += result.CompletionTokens ?? 0;
            foreach (var entry in result.Entries)
            {
                verdicts[entry.TitleId] = entry;
                neighboursShown[entry.TitleId] = batch.Neighbours.GetValueOrDefault(entry.TitleId) ?? [];
                batchOf[entry.TitleId] = batchIndex;
            }
        }

        var ratingById = pool.ToDictionary(p => p.TitleId, p => p.Rating);
        var judged = verdicts.Count;
        var loved = verdicts.Keys.Where(id => ratingById[id] >= LovedFloor).ToList();
        var disliked = verdicts.Keys.Where(id => ratingById[id] <= DislikedCeiling).ToList();

        bool RuledOut(Guid id) =>
            verdicts[id].Verdict == CrossReferenceVerdict.Out
            && verdicts[id].Confidence >= VerdictAggregator.OutConfidenceThreshold;

        var falseRuleOuts = loved.Count(RuledOut);
        var trueRuleOuts = disliked.Count(RuledOut);
        var unsure = verdicts.Values.Count(v => v.Verdict == CrossReferenceVerdict.Unsure);

        var (low, high) = WilsonInterval(falseRuleOuts, loved.Count);
        var falseRate = Rate(falseRuleOuts, loved.Count);
        var trueRate = Rate(trueRuleOuts, disliked.Count);
        var unsureRate = Rate(unsure, judged);
        var decision = Decide(high, trueRate, unsureRate, loved.Count);

        // Does the answer carry information? The three clauses of the gate each read one rate in
        // isolation, and "12% is under the 15% ceiling" is a true sentence about an instrument whose
        // true-rule-out rate is 10.1% — i.e. about a coin. These compare the two sides to each other.
        var observations = verdicts.Values
            .Select(v => (v.Verdict, v.Confidence, LaterRating: ratingById[v.TitleId]))
            .ToList();
        pooled.AddRange(observations);
        var discrimination = DiscriminationMetrics.Summarise(observations);

        var evaluationId = Guid.NewGuid();

        // One row per judged title, written in the same SaveChanges as the aggregate they roll into.
        // The whole reason this PR exists: an aggregate cannot be re-interrogated, so the first live
        // run's obvious follow-up ("does `keep` carry signal even though `out` does not?") needed
        // the entire paid run repeated to ask.
        foreach (var (titleId, entry) in verdicts.OrderBy(v => v.Key))
        {
            db.CrossReferenceEvaluationItems.Add(new CrossReferenceEvaluationItem
            {
                Id = Guid.NewGuid(),
                AccountId = accountId,
                EvaluationId = evaluationId,
                TitleId = titleId,
                SplitFraction = fraction,
                Verdict = entry.Verdict,
                ReasonCode = entry.Reason,
                Confidence = Math.Round((decimal)entry.Confidence, 3, MidpointRounding.AwayFromZero),
                LaterRating = ratingById[titleId],
                NeighbourIdsJson = JsonSerializer.Serialize(
                    (neighboursShown.GetValueOrDefault(titleId) ?? []).Select(n => n.TitleId), JsonOptions),
                BatchIndex = batchOf.GetValueOrDefault(titleId),
            });
        }

        return new CrossReferenceEvaluation
        {
            Id = evaluationId,
            AccountId = accountId,
            RunId = runId,
            SplitFraction = fraction,
            SplitAt = splitAt,
            HoldoutJudged = judged,
            LovedCount = loved.Count,
            DislikedCount = disliked.Count,
            FalseRuleOuts = falseRuleOuts,
            TrueRuleOuts = trueRuleOuts,
            FalseRuleOutRate = falseRate,
            TrueRuleOutRate = trueRate,
            WilsonLow = low,
            WilsonHigh = high,
            UnsureRate = unsureRate,
            NoEmbeddingCount = noEmbedding,
            KeepCount = discrimination.KeepCount,
            OutCount = discrimination.OutCount,
            UnsureCount = discrimination.UnsureCount,
            MeanLaterRatingKeep = discrimination.MeanLaterRatingKeep,
            MeanLaterRatingOut = discrimination.MeanLaterRatingOut,
            MeanLaterRatingUnsure = discrimination.MeanLaterRatingUnsure,
            KeepLovedRate = discrimination.KeepLovedRate,
            KeepDislikedRate = discrimination.KeepDislikedRate,
            OutLovedRate = discrimination.OutLovedRate,
            OutDislikedRate = discrimination.OutDislikedRate,
            SpearmanConfidenceVsRating = discrimination.Spearman,
            AucKeepVsOut = discrimination.Auc,
            GatingDecision = decision,
            Caveat = BuildCaveat(
                loved.Count, disliked.Count, judged, falseRate, low, high, noEmbedding,
                trueRate, discrimination.Auc),
            ModelId = judge.ModelId,
            PromptVersion = promptVersion,
            PromptTokens = promptTokens,
            CompletionTokens = completionTokens,
            EvaluatedAt = now,
        };
    }

    /// <summary>
    /// The run's summary row — means across the origins, and the decision recomputed from the
    /// POOLED counts rather than averaged from the per-origin decisions.
    ///
    /// <para>Averaging five decisions would let two origins that each gated on twenty loved titles
    /// outvote three that did not; pooling asks the question once, of all the evidence, which is
    /// the only reading of it that the Wilson bound is valid for.</para>
    /// </summary>
    private static CrossReferenceEvaluation MeanRow(
        Guid accountId, Guid runId, IReadOnlyList<CrossReferenceEvaluation> origins,
        IReadOnlyList<(CrossReferenceVerdict Verdict, double Confidence, short LaterRating)> pooled,
        string modelId, int promptVersion, DateTime now)
    {
        var judged = origins.Sum(o => o.HoldoutJudged);
        var loved = origins.Sum(o => o.LovedCount);
        var disliked = origins.Sum(o => o.DislikedCount);
        var falseOuts = origins.Sum(o => o.FalseRuleOuts);
        var trueOuts = origins.Sum(o => o.TrueRuleOuts);
        var noEmbedding = origins.Sum(o => o.NoEmbeddingCount);

        var (low, high) = WilsonInterval(falseOuts, loved);
        var falseRate = Rate(falseOuts, loved);
        var trueRate = Rate(trueOuts, disliked);
        // Unsure is the one figure that is a mean of the origins rather than a pooled count: each
        // origin's denominator is its own judged set, and those sets overlap heavily.
        var unsureRate = origins.Count == 0 ? 0m : origins.Average(o => o.UnsureRate);
        var discrimination = DiscriminationMetrics.Summarise(pooled);

        return new CrossReferenceEvaluation
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            RunId = runId,
            SplitFraction = null,
            SplitAt = default,
            HoldoutJudged = judged,
            LovedCount = loved,
            DislikedCount = disliked,
            FalseRuleOuts = falseOuts,
            TrueRuleOuts = trueOuts,
            FalseRuleOutRate = falseRate,
            TrueRuleOutRate = trueRate,
            WilsonLow = low,
            WilsonHigh = high,
            UnsureRate = unsureRate,
            NoEmbeddingCount = noEmbedding,
            KeepCount = discrimination.KeepCount,
            OutCount = discrimination.OutCount,
            UnsureCount = discrimination.UnsureCount,
            MeanLaterRatingKeep = discrimination.MeanLaterRatingKeep,
            MeanLaterRatingOut = discrimination.MeanLaterRatingOut,
            MeanLaterRatingUnsure = discrimination.MeanLaterRatingUnsure,
            KeepLovedRate = discrimination.KeepLovedRate,
            KeepDislikedRate = discrimination.KeepDislikedRate,
            OutLovedRate = discrimination.OutLovedRate,
            OutDislikedRate = discrimination.OutDislikedRate,
            SpearmanConfidenceVsRating = discrimination.Spearman,
            AucKeepVsOut = discrimination.Auc,
            GatingDecision = Decide(high, trueRate, unsureRate, loved),
            Caveat = BuildCaveat(
                loved, disliked, judged, falseRate, low, high, noEmbedding, trueRate, discrimination.Auc),
            ModelId = modelId,
            PromptVersion = promptVersion,
            PromptTokens = origins.Sum(o => o.PromptTokens),
            CompletionTokens = origins.Sum(o => o.CompletionTokens),
            EvaluatedAt = now,
        };
    }

    /// <summary>
    /// The gate. Three clauses, all of which must hold — and a fourth, unwritten one: with no loved
    /// holdout titles at all there is no evidence, and no evidence is <c>demote-only</c>. A Wilson
    /// interval on zero observations is [0, 1], so the arithmetic already refuses; the explicit
    /// check is here so the reason is legible rather than incidental.
    /// </summary>
    internal static string Decide(decimal wilsonHigh, decimal trueRuleOutRate, decimal unsureRate, int lovedCount) =>
        lovedCount > 0
            && wilsonHigh <= FalseRuleOutCeiling
            && trueRuleOutRate >= TrueRuleOutFloor
            && unsureRate <= UnsureCeiling
            ? GatingDecisions.Gating
            : GatingDecisions.DemoteOnly;

    /// <summary>
    /// The 95% Wilson score interval for a binomial proportion — the bound the kill criterion
    /// reads, and deliberately not the normal-approximation ("Wald") interval.
    ///
    /// <para>At the n and the rates this measurement lives at — 55 loved titles, an observed rate
    /// near 0.05 — Wald is badly wrong: it produces intervals that fall below zero and it is
    /// anti-conservative exactly where the decision is being made. Wilson has no such degeneracy
    /// and needs no continuity fudge. With n = 0 it returns [0, 1], which is the correct statement
    /// that nothing is known.</para>
    /// </summary>
    internal static (decimal Low, decimal High) WilsonInterval(int successes, int trials)
    {
        if (trials <= 0)
        {
            return (0m, 1m);
        }

        // 1.959964 = the two-sided 95% normal quantile. Written out rather than computed, because
        // it is a constant of the method and not a parameter of the data.
        const double z = 1.959963984540054d;
        var n = (double)trials;
        var p = successes / n;
        var z2 = z * z;
        var denominator = 1d + (z2 / n);
        var centre = p + (z2 / (2d * n));
        var margin = z * Math.Sqrt((p * (1d - p) / n) + (z2 / (4d * n * n)));

        var low = Math.Clamp((centre - margin) / denominator, 0d, 1d);
        var high = Math.Clamp((centre + margin) / denominator, 0d, 1d);
        return (Round(low), Round(high));
    }

    private static decimal Rate(int numerator, int denominator) =>
        denominator <= 0 ? 0m : Round(numerator / (double)denominator);

    private static decimal Round(double value) =>
        Math.Round((decimal)value, 4, MidpointRounding.AwayFromZero);

    /// <summary>
    /// How this row must be read, in words, naming n and the interval.
    ///
    /// <para>Persisted rather than rendered for the same reason <c>EvaluationResult</c> persists
    /// its popularity caveat: a number and the reason to be careful with it must travel together,
    /// or the number gets quoted alone six weeks later.</para>
    ///
    /// <para><b>It ends with whether the verdicts discriminate at all</b>, whenever the two rule-out
    /// rates are within five points of each other. That is not a significance test and does not
    /// pretend to be one — at these sample sizes the intervals are ten times wider than the gap —
    /// but it stops "it caught 10% of the duds" being quoted without the clause that says the 10%
    /// was free.</para>
    /// </summary>
    internal static string BuildCaveat(
        int loved, int disliked, int judged, decimal falseRate, decimal low, decimal high, int noEmbedding,
        decimal trueRate, decimal? auc)
    {
        var caveat = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"Measured on {loved} holdout title(s) this account went on to rate 8 or higher and {disliked} it "
            + $"rated 4 or lower, out of {judged} judged. The false-rule-out rate is {falseRate:0.000} with a "
            + $"95% Wilson interval of [{low:0.000}, {high:0.000}] — the gate reads the UPPER bound, because at "
            + $"this sample size the point estimate alone cannot distinguish a good judge from a lucky one. "
            + $"{noEmbedding} eligible holdout title(s) were skipped for having no plot embedding and no "
            + $"extracted attributes, and are not in any denominator here.");

        // The sentence the first live run needed and did not have. A false-rule-out rate of 12.0%
        // reads as a cautious instrument until you put it beside a true-rule-out rate of 10.1%, at
        // which point it reads as a coin — and the gate's three clauses, each of which looks at one
        // rate alone, would never have said so.
        var discrimination = DiscriminationMetrics.DiscriminationCaveat(falseRate, trueRate, auc);
        return discrimination is null ? caveat : caveat + " " + discrimination;
    }
}
