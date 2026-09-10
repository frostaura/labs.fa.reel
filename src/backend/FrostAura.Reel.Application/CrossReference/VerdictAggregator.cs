using FrostAura.Reel.Domain.CrossReference;

namespace FrostAura.Reel.Application.CrossReference;

/// <summary>What several rounds of judging add up to for one title.</summary>
public enum AggregateVerdict
{
    /// <summary>Two or more keeps and no out. The only state that may be trusted upward.</summary>
    ConfirmedKeep,

    /// <summary>One keep, no out. A single opinion — worth a nudge, not a promotion.</summary>
    ProvisionalKeep,

    /// <summary>An out at ≥ 0.70 confidence. The only state that demotes, and the only one that may
    /// ever bar a hero — and then only once the backtest has earned it.</summary>
    Out,

    /// <summary>Everything else, including keep-and-out disagreement and a low-confidence out.</summary>
    Unsure,

    /// <summary>Nothing was ever judged. Distinct from <see cref="Unsure"/>, which is a judgement.</summary>
    None,
}

/// <summary>One title's rolled-up standing, and the ranking multiplier that follows from it.</summary>
/// <param name="Verdict">The aggregate state.</param>
/// <param name="Factor">
/// The multiplier applied to <c>RankKey</c> — ORDERING ONLY. The displayed
/// <c>PredictedRating</c> is never touched by any of this: that is the 2026-09-10 ranking-layer
/// contract, and moving an LLM opinion into the shown number would make <c>/api/accuracy</c>
/// uninterpretable, because the calibration curve would then be measuring the judge.
/// </param>
/// <param name="HeroAllowed">
/// False only for a fresh <see cref="AggregateVerdict.Out"/>. A bar is honest where a multiplier is
/// a hope: rails order by <c>RankKey</c>, so a title demoted only in the hero key sits atop every
/// rail regardless.
/// </param>
/// <param name="Confidence">The confidence the factor was computed from, 0–1.</param>
/// <param name="Stale">True when the strongest evidence was judged at a superseded taste fingerprint.</param>
public readonly record struct VerdictFactor(
    AggregateVerdict Verdict, double Factor, bool HeroAllowed, double Confidence, bool Stale)
{
    /// <summary>The answer for a title nothing has ever judged: ordered exactly as today.</summary>
    public static readonly VerdictFactor Neutral =
        new(AggregateVerdict.None, 1.0d, HeroAllowed: true, Confidence: 0d, Stale: false);
}

/// <summary>
/// One observation, reduced to what aggregation needs. Pure input — no entity, no tracking — so
/// every rule below is testable without a database.
/// </summary>
/// <param name="Fresh">
/// Whether this verdict was judged at the account's CURRENT taste fingerprint. A superseded one is
/// evidence at half strength, not evidence to be deleted: a full re-judgement per fingerprint step
/// would cost more than the feature is worth, and the lens's Fresh → Stale → None ladder already
/// establishes that old copy beats no copy.
/// </param>
public readonly record struct VerdictObservation(
    CrossReferenceRound Round, CrossReferenceVerdict Verdict, double Confidence, bool Fresh);

/// <summary>
/// The pure aggregation rule — several rounds of judging into one standing and one multiplier.
///
/// <para><b>The rules, and why each is what it is.</b></para>
/// <list type="bullet">
/// <item><b>Out beats everything, at ≥ 0.70.</b> Ruling out is the judge's actual job; the prompt
/// is written to make <c>out</c> the confident answer and <c>unsure</c> the default. A single
/// confident out therefore carries more information than two keeps, and the threshold is what
/// separates "confident" from "answered".</item>
/// <item><b>Confirmed Keep needs TWO keeps and no out.</b> One keep is one prompt's opinion at one
/// position in one batch; two keeps across rounds with different neighbours is the closest thing
/// this design has to independent corroboration.</item>
/// <item><b>Keep-and-out disagreement is Unsure, not a tie-break.</b> A contradiction between two
/// passes is evidence about the JUDGE, not about the title, and resolving it by picking the
/// stronger confidence would quietly convert judge noise into ranking signal.</item>
/// <item><b>A low-confidence out is Unsure, not a weak Out.</b> Anything else puts a continuum
/// under a decision the product presents as binary.</item>
/// <item><b>Everything is bounded at ±0.2 around 1.0 except the Out.</b> A Keep must never
/// outweigh a rating point of model opinion — the model is trained on this person's own labels and
/// the judge is not.</item>
/// </list>
///
/// <para><b>Stale halves the strength, it never flips the sign.</b> An Out at a superseded
/// fingerprint demotes at 0.75 rather than 0.5 and stops barring the hero, because the taste it
/// was judged against is no longer the taste being served. A stale Keep is likewise halved. This
/// is the same ladder the lens uses and the same reason: the alternative is re-judging the whole
/// pool every time the fingerprint moves.</para>
/// </summary>
public static class VerdictAggregator
{
    /// <summary>The confidence at or above which an <c>out</c> is a decision rather than a lean.</summary>
    public const double OutConfidenceThreshold = 0.70d;

    /// <summary>Keeps needed for a Confirmed Keep.</summary>
    public const int ConfirmingKeeps = 2;

    /// <summary>The Out multiplier at the current fingerprint. Halves the title's rank key.</summary>
    public const double OutFactor = 0.5d;

    /// <summary>The Out multiplier at a superseded fingerprint — half the demotion, no hero bar.</summary>
    public const double StaleOutFactor = 0.75d;

    /// <summary>Confidence coefficient on a Confirmed Keep: at most +0.2.</summary>
    public const double ConfirmedKeepCoefficient = 0.2d;

    /// <summary>Confidence coefficient on a Provisional Keep: at most +0.1.</summary>
    public const double ProvisionalKeepCoefficient = 0.1d;

    /// <summary>
    /// Rolls one title's observations into a standing and a multiplier.
    ///
    /// <para>Order-independent by construction — the observations are counted, never folded — so
    /// two runs that judged the same title in a different sequence agree. The tests assert that
    /// directly, because an order-dependent aggregate would make the feed depend on batch order.</para>
    /// </summary>
    public static VerdictFactor Aggregate(IReadOnlyList<VerdictObservation> observations)
    {
        if (observations.Count == 0)
        {
            return VerdictFactor.Neutral;
        }

        var outs = observations
            .Where(o => o.Verdict == CrossReferenceVerdict.Out && o.Confidence >= OutConfidenceThreshold)
            .ToList();
        var keeps = observations.Where(o => o.Verdict == CrossReferenceVerdict.Keep).ToList();

        if (outs.Count > 0)
        {
            // A fresh out is the operative one wherever both exist: the current fingerprint is the
            // taste being served, so evidence gathered against it outranks evidence against a taste
            // the account has since moved off.
            var fresh = outs.Where(o => o.Fresh).ToList();
            var operative = fresh.Count > 0 ? fresh : outs;
            var confidence = operative.Max(o => o.Confidence);
            return fresh.Count > 0
                ? new VerdictFactor(AggregateVerdict.Out, OutFactor, HeroAllowed: false, confidence, Stale: false)
                : new VerdictFactor(AggregateVerdict.Out, StaleOutFactor, HeroAllowed: true, confidence, Stale: true);
        }

        // Any out at all, even below the threshold, blocks a keep from being promoted: two passes
        // that disagree are not two passes that agree.
        var contradicted = observations.Any(o => o.Verdict == CrossReferenceVerdict.Out);

        if (keeps.Count >= ConfirmingKeeps && !contradicted)
        {
            var stale = keeps.All(k => !k.Fresh);
            var confidence = keeps.Average(k => k.Confidence);
            var lift = ConfirmedKeepCoefficient * confidence;
            return new VerdictFactor(
                AggregateVerdict.ConfirmedKeep,
                1d + (stale ? lift / 2d : lift),
                HeroAllowed: true,
                confidence,
                stale);
        }

        if (keeps.Count == 1 && !contradicted)
        {
            var keep = keeps[0];
            var lift = ProvisionalKeepCoefficient * keep.Confidence;
            return new VerdictFactor(
                AggregateVerdict.ProvisionalKeep,
                1d + (keep.Fresh ? lift : lift / 2d),
                HeroAllowed: true,
                keep.Confidence,
                !keep.Fresh);
        }

        // Unsure verdicts, a sub-threshold out on its own, and every keep/out contradiction. All of
        // them order exactly as they do today, which is the honest answer to "we do not know".
        return new VerdictFactor(
            AggregateVerdict.Unsure, 1.0d, HeroAllowed: true, Confidence: 0d,
            Stale: observations.All(o => !o.Fresh));
    }

    /// <summary>
    /// The round-3 fit stand-in for a Confirmed Keep that never reached the LLM fit pass:
    /// <c>0.5 + confidence</c>, clamped to the fit scale. A confirmed keep with no fit score would
    /// otherwise fall back to the deterministic stub fit and lose the only independent evidence
    /// anything had gathered about it.
    /// </summary>
    public static double FitFor(VerdictFactor factor) =>
        factor.Verdict == AggregateVerdict.ConfirmedKeep
            ? Math.Clamp(0.5d + factor.Confidence, 0d, 1d)
            : throw new ArgumentException(
                "A fit stand-in exists only for a Confirmed Keep; every other state keeps its own fit.",
                nameof(factor));
}
