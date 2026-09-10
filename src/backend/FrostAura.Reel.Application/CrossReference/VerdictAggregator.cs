using FrostAura.Reel.Domain.CrossReference;

namespace FrostAura.Reel.Application.CrossReference;

/// <summary>What several rounds of judging add up to for one title.</summary>
public enum AggregateVerdict
{
    /// <summary>Two or more keeps and no out. The only state that may be trusted upward.</summary>
    ConfirmedKeep,

    /// <summary>One keep, no out. A single opinion — worth a nudge, not a promotion.</summary>
    ProvisionalKeep,

    /// <summary>
    /// An out at ≥ 0.70 confidence. <b>Recorded, and deliberately without a ranking consequence.</b>
    /// It is kept as a state — it is written to the exposure log and counted on the track record —
    /// so the closed loop can go on measuring an opinion the product does not yet act on.
    /// </summary>
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
/// <b>Always true, as of 2026-09-11.</b> The field survives because the shape of the decision is
/// worth keeping visible — a bar is honest where a multiplier is a hope — but no verdict has earned
/// one: on the founder's own holdout the <c>out</c> verdict under prompt v2 caught 16.2% [Wilson
/// 12.3, 21.0] of the titles he went on to rate 8 or higher, against 22.8% of the ones he rated 4
/// or lower. Barring on that would hide roughly one future love in six for a fifth of a dislike.
/// </param>
/// <param name="Confidence">The confidence the factor was computed from, 0–1.</param>
/// <param name="Stale">True when the strongest evidence was judged at a superseded taste fingerprint.</param>
public readonly record struct VerdictFactor(
    AggregateVerdict Verdict, double Factor, bool HeroAllowed, double Confidence, bool Stale)
{
    /// <summary>The answer for a title nothing has ever judged: ordered exactly as today.</summary>
    public static readonly VerdictFactor Neutral =
        new(AggregateVerdict.None, 1.0d, HeroAllowed: true, Confidence: 0d, Stale: false);

    /// <summary>
    /// What the exposure row records in <c>ExposureEvent.XrefVerdict</c>, or null when nothing was
    /// ever judged.
    ///
    /// <para>NULL and <c>"Unsure"</c> are different facts and the column keeps them apart: null
    /// means the pass had not reached this title when the card was served, <c>Unsure</c> means it
    /// looked and could not say. Collapsing the two would make every accuracy denominator on the
    /// track record wrong in the flattering direction, because "never judged" is by far the larger
    /// set on any account the pass has not finished.</para>
    /// </summary>
    public string? ExposureLabel => Verdict == AggregateVerdict.None ? null : Verdict.ToString();
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
/// <para><b>This is a KEEP-ONLY instrument, and the numbers that made it one are below.</b>
/// Measured on the founder's own holdout on 2026-09-11 (five split origins pooled, real provider
/// calls, base rates among judged titles: loved ≈ 23%, disliked ≈ 15%):</para>
///
/// <list type="bullet">
/// <item><b>prompt v1</b>, 1,307 judged — AUC 0.611, Spearman 0.128; later ratings averaged 6.53
/// for keeps against 6.21 for outs; keep → loved 28.4%, keep → disliked 13.0%; false-rule-out 9.1%
/// [Wilson 6.4, 12.7] against true-rule-out 11.5%; 36% unsure.</item>
/// <item><b>prompt v2</b>, 1,194 judged — AUC 0.655, Spearman 0.190; keeps averaged 6.83 against
/// 6.16 for outs; keep → loved 33.2%, keep → disliked 8.1%; false-rule-out 16.2% [Wilson 12.3,
/// 21.0] against true-rule-out 22.8%; 46% unsure.</item>
/// </list>
///
/// <para>Read together: under v2 a KEEP is loved at about 1.4× the base rate and disliked at about
/// half of it, which is a real, bounded ordering signal. An OUT is not the mirror image of that —
/// it catches 16% of future loves to catch 23% of future dislikes, so acting on it would cost
/// roughly one love hidden for every one-and-a-half dislikes demoted, against a base rate that
/// already makes loves the commoner class. Both runs recorded
/// <c>GatingDecision = demote-only</c> under the pre-registered rule, and demote-only now means
/// exactly what it says: <b>Out and Unsure are 1.0, to three decimal places, with the hero
/// allowed.</b> The verdict is still recorded, still logged on the exposure row and still counted
/// on the track record — it simply does not move a pick.</para>
///
/// <para><b>The rules, and why each is what it is.</b></para>
/// <list type="bullet">
/// <item><b>Out is classified at ≥ 0.70 and then does nothing.</b> The threshold still separates
/// "confident" from "answered", because that is the population the backtest measures and the
/// closed loop observes; what changed is that the classification no longer carries a factor.</item>
/// <item><b>Out still outranks a keep as a LABEL.</b> A title the judge said no to at high
/// confidence is not recorded as a keep because a later pass also said yes — the honest standing
/// is the negative one, and it is the standing, not the factor, that reaches the exposure log.</item>
/// <item><b>Confirmed Keep needs TWO keeps and no out.</b> One keep is one prompt's opinion at one
/// position in one batch; two keeps across rounds with different neighbours is the closest thing
/// this design has to independent corroboration. Round 2 does not exist yet, so today every keep
/// that survives is Provisional — which is the honest reading of one opinion.</item>
/// <item><b>Keep-and-out disagreement is Unsure, not a tie-break.</b> A contradiction between two
/// passes is evidence about the JUDGE, not about the title, and resolving it by picking the
/// stronger confidence would quietly convert judge noise into ranking signal.</item>
/// <item><b>A low-confidence out is Unsure, not a weak Out.</b> Anything else puts a continuum
/// under a decision the product presents as binary.</item>
/// <item><b>Everything is bounded at +0.2 around 1.0, and nothing is ever below it.</b> A Keep
/// must never outweigh a rating point of model opinion — the model is trained on this person's own
/// labels and the judge is not.</item>
/// </list>
///
/// <para><b>Stale halves the strength, it never flips the sign.</b> A Keep judged at a superseded
/// fingerprint lifts at half strength, because the taste it was judged against is no longer the
/// taste being served. This is the same ladder the lens uses and the same reason: the alternative
/// is re-judging the whole pool every time the fingerprint moves. A stale Out is 1.0, like a fresh
/// one — halving nothing is still nothing.</para>
/// </summary>
public static class VerdictAggregator
{
    /// <summary>The confidence at or above which an <c>out</c> is a decision rather than a lean.</summary>
    public const double OutConfidenceThreshold = 0.70d;

    /// <summary>Keeps needed for a Confirmed Keep.</summary>
    public const int ConfirmingKeeps = 2;

    /// <summary>
    /// The Out multiplier — <b>exactly 1.0, fresh or stale</b>. Not a tunable: it is the measured
    /// conclusion that the <c>out</c> verdict has not earned a ranking consequence on this history
    /// (v2: 16.2% of future loves ruled out, against 22.8% of future dislikes). Named rather than
    /// inlined so the tests can assert against the concept rather than a literal, and so the day it
    /// changes it changes in one place with a measurement beside it.
    /// </summary>
    public const double OutFactor = 1.0d;

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
            // the account has since moved off. That choice survives the keep-only decision because
            // it still decides which CONFIDENCE is recorded on the exposure row, even though both
            // branches now carry the same factor.
            var fresh = outs.Where(o => o.Fresh).ToList();
            var operative = fresh.Count > 0 ? fresh : outs;
            return new VerdictFactor(
                AggregateVerdict.Out,
                OutFactor,
                HeroAllowed: true,
                operative.Max(o => o.Confidence),
                Stale: fresh.Count == 0);
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
    /// The hero-key fit multiplier a Keep supplies where the round-3 LLM fit pass judged nothing:
    /// <c>0.5 + confidence</c>. Returns null for every other standing.
    ///
    /// <para><b>This is the multiplier, not a raw fit.</b> The hero key multiplies by
    /// <c>NeutralFit + fit</c> = <c>0.5 + fit</c> for a title the fit pass DID judge, so returning
    /// the finished multiplier here puts a keep on exactly the same scale as a fit verdict: a keep
    /// at confidence 0.5 is worth precisely as much to the hero as a judged fit of 0.5, and an
    /// unjudged, unkept title stays at 1.0. Anything else would silently value a keep against a
    /// different yardstick than the pass it stands in for.</para>
    ///
    /// <para><b>Out supplies nothing</b> — deliberately null rather than a number below 1.0. The
    /// hero is the one place a wrong demotion is most expensive, and the <c>out</c> verdict has not
    /// earned one; see the measurements on this class.</para>
    /// </summary>
    public static double? HeroFitFactorFor(VerdictFactor factor) =>
        factor.Verdict is AggregateVerdict.ConfirmedKeep or AggregateVerdict.ProvisionalKeep
            ? 0.5d + factor.Confidence
            : null;
}
