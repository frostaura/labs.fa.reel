using FrostAura.Reel.Application.Ml;
using FrostAura.Reel.Domain.Catalog;

namespace FrostAura.Reel.Application.Ranking.Interleaving;

/// <summary>
/// The four signals the transparent challenger ranks by, read for one title. Every one of them is a
/// number a reader can check: the badge's own crowd score, how many people it rests on, how this
/// account rates the title's genres and people, and how close the title sits to what it loved.
/// </summary>
/// <param name="ShrunkCrowd">The vote-shrunk crowd score the badge shows; null when there is none.</param>
/// <param name="LogVotes">log(1 + the crowd's vote count).</param>
/// <param name="Affinity">Mean of the account's genre, director, cast and writer affinities for the title.</param>
/// <param name="LovedSimilarity">Mean plot-embedding similarity to the account's top loved titles.</param>
public sealed record ChallengerSignals(double? ShrunkCrowd, double LogVotes, double Affinity, double LovedSimilarity)
{
    private static readonly string[] Names = FeatureSchema.AllNames();
    private static readonly int GenreSlot = Array.IndexOf(Names, "userGenreAffinity");
    private static readonly int DirectorSlot = Array.IndexOf(Names, "directorAffinity");
    private static readonly int CastSlot = Array.IndexOf(Names, "castAffinity");
    private static readonly int WriterSlot = Array.IndexOf(Names, "writerAffinity");
    private static readonly int LovedSlot = Array.IndexOf(Names, "simTopLovedMean");

    /// <summary>
    /// The signals for <paramref name="title"/> from its feature vector (built as of whatever instant
    /// the caller built it — the split, for the eval) and its catalogue row.
    /// </summary>
    public static ChallengerSignals From(float[] vector, Title title)
    {
        static double Slot(float[] v, int index) => index >= 0 && index < v.Length ? v[index] : 0d;

        return new ChallengerSignals(
            CrowdPrior.ShrunkScoreOf(title) is { } shrunk ? (double)shrunk : null,
            Math.Log(1d + Math.Max(0, CrowdRating.VotesFor(title) ?? 0)),
            (Slot(vector, GenreSlot) + Slot(vector, DirectorSlot) + Slot(vector, CastSlot) + Slot(vector, WriterSlot)) / 4d,
            Slot(vector, LovedSlot));
    }
}

/// <summary>
/// The transparent challenger (WS-5 task 11, FD-4): ranks by
/// <c>z(shrunk crowd) + z(log votes) + wₐ·z(affinity) + wₛ·z(similarity to loved titles)</c>, the
/// z-scores taken within the pool being ordered. Every term is a number the card or the title page
/// can show, which is the point — if it ranks as well as FastTree, the product can say why a pick
/// is a pick in one line a reader can check.
///
/// <para><b>How the weights are set.</b> The crowd terms are fixed at 1; <c>wₐ</c> and
/// <c>wₛ</c> are chosen per account by the eval harness from <see cref="WeightGrid"/>, by mean
/// NDCG@10 over the 60/65/70% split origins ONLY (<see cref="Fit"/>). The 75% and 80% origins are
/// never read by the fit, so the primary-origin comparison in the ledger is out of sample. The
/// read is pre-registered in <see cref="PreRegistration"/>.</para>
///
/// <para>Served only behind the interleaving flag (<c>INTERLEAVE_ENABLED</c> with
/// <c>INTERLEAVE_POLICY_B=TransparentBlend</c>), with the account's fitted weights; FastTree keeps
/// serving otherwise (FD-4 default).</para>
/// </summary>
public sealed class TransparentBlendPolicy(double affinityWeight = 1d, double similarityWeight = 1d) : IRankingPolicy
{
    /// <summary>The id written to <c>ExposureEvent.PolicyId</c>.</summary>
    public const string Id = "TransparentBlend";

    /// <summary>The candidate weights for affinity and loved-title similarity.</summary>
    public static readonly double[] WeightGrid = [0d, 0.25d, 0.5d, 1d, 2d, 4d];

    /// <summary>The split origins the weights may be fitted on. Nothing later is read by the fit.</summary>
    public const double FitOriginCeiling = 0.70d;

    /// <summary>
    /// The pre-registration, persisted verbatim with every evaluation that reports the challenger.
    /// Written and committed before any challenger number existed; a reading that departs from it is
    /// a new experiment, not a reinterpretation of this one.
    /// </summary>
    public const string PreRegistration =
        "Pre-registered 2026-10-26, before any challenger number was computed. Hypothesis: a transparent blend of crowd "
        + "quality (vote-shrunk crowd score, log votes), the account's genre and person affinity, and similarity to its "
        + "top loved titles ranks the account's later ratings at least as well as FastTree. Weights: crowd terms fixed "
        + "at 1; affinity and similarity weights chosen from {0, 0.25, 0.5, 1, 2, 4} by mean NDCG@10 on the 60/65/70% "
        + "origins only; the 75% and 80% origins are never read by the fit. Primary metric: NDCG@10 at the 80% origin, "
        + "paired bootstrap 95% CI (1,000 resamples, seed 20260909) on challenger minus FastTree over the identical "
        + "holdout. Reading: lower bound above 0 = better; upper bound below 0 = worse; otherwise no measurable "
        + "difference. Secondary: the same against the popularity baseline. Offline, crowd values are present-day "
        + "(look-ahead), so interleaving on live traffic remains the judge for FD-4.";

    /// <summary>The affinity weight this instance ranks with.</summary>
    public double AffinityWeight { get; } = affinityWeight;

    /// <summary>The loved-similarity weight this instance ranks with.</summary>
    public double SimilarityWeight { get; } = similarityWeight;

    /// <inheritdoc />
    public string PolicyId => Id;

    /// <inheritdoc />
    public string Description =>
        "Transparent blend: z(shrunk crowd) + z(log votes) + w·z(genre/person affinity) + w·z(similarity to loved titles).";

    /// <inheritdoc />
    public IReadOnlyList<PolicyCandidate> Order(IReadOnlyList<PolicyCandidate> candidates)
    {
        var scores = Scores([.. candidates.Select(c => c.Challenger)], AffinityWeight, SimilarityWeight);
        return [.. candidates
            .Select((c, i) => (Candidate: c, Score: scores[i]))
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => CalibratedModelPolicy.Key(x.Candidate))
            .ThenBy(x => x.Candidate.TitleId)
            .Select(x => x.Candidate)];
    }

    /// <summary>
    /// The blend score for every member of a pool, z-scored within it. A missing crowd score, and a
    /// candidate with no signals at all, sit at the pool mean (z = 0) — unknown is not evidence either
    /// way. A term with no spread in the pool contributes 0 to everyone.
    /// </summary>
    public static double[] Scores(IReadOnlyList<ChallengerSignals?> pool, double affinityWeight, double similarityWeight)
    {
        var crowd = Z(pool.Select(s => s?.ShrunkCrowd).ToList());
        var votes = Z(pool.Select(s => s?.LogVotes).ToList());
        var affinity = Z(pool.Select(s => s?.Affinity).ToList());
        var similarity = Z(pool.Select(s => s?.LovedSimilarity).ToList());
        var scores = new double[pool.Count];
        for (var i = 0; i < pool.Count; i++)
        {
            scores[i] = crowd[i] + votes[i] + (affinityWeight * affinity[i]) + (similarityWeight * similarity[i]);
        }

        return scores;
    }

    /// <summary>
    /// The weights that maximise mean NDCG@10 over <paramref name="origins"/> (the caller passes only
    /// origins at or below <see cref="FitOriginCeiling"/>). Ties keep the first pair in grid order, so
    /// the fit is deterministic. Null when there is nothing to fit on.
    /// </summary>
    public static (double Affinity, double Similarity, decimal MeanNdcg)? Fit(
        IReadOnlyList<(IReadOnlyList<ChallengerSignals> Signals, IReadOnlyList<short> Ratings, decimal Threshold)> origins)
    {
        if (origins.Count == 0)
        {
            return null;
        }

        (double, double, decimal)? best = null;
        foreach (var wa in WeightGrid)
        {
            foreach (var ws in WeightGrid)
            {
                var mean = origins.Average(o => Ndcg(o.Signals, o.Ratings, o.Threshold, wa, ws));
                if (best is null || mean > best.Value.Item3)
                {
                    best = (wa, ws, mean);
                }
            }
        }

        return best;
    }

    /// <summary>NDCG@10 of the blend's order over one holdout, ties broken by holdout position.</summary>
    public static decimal Ndcg(
        IReadOnlyList<ChallengerSignals> signals, IReadOnlyList<short> ratings, decimal threshold, double affinityWeight, double similarityWeight)
    {
        var scores = Scores([.. signals], affinityWeight, similarityWeight);
        var order = Enumerable.Range(0, ratings.Count).OrderByDescending(i => scores[i]).ThenBy(i => i);
        return EvalHarness.NdcgAtK(order.Select(i => ratings[i]), threshold, EvalHarness.NdcgDepth);
    }

    private static double[] Z(IReadOnlyList<double?> values)
    {
        var known = values.Where(v => v.HasValue).Select(v => v!.Value).ToList();
        var result = new double[values.Count];
        if (known.Count < 2)
        {
            return result;
        }

        var mean = known.Average();
        var sd = Math.Sqrt(known.Sum(v => (v - mean) * (v - mean)) / known.Count);
        if (sd < 1e-12)
        {
            return result;
        }

        for (var i = 0; i < values.Count; i++)
        {
            result[i] = values[i] is { } v ? (v - mean) / sd : 0d;
        }

        return result;
    }
}
