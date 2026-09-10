using FrostAura.Reel.Domain.Tenancy;

namespace FrostAura.Reel.Domain.CrossReference;

/// <summary>What the backtest concluded the verdicts have earned the right to do.</summary>
public static class GatingDecisions
{
    /// <summary>An Out may bar a title from the hero, and may replace the round-3 fit slice.</summary>
    public const string Gating = "gating";

    /// <summary>An Out still demotes (×0.5) so the closed loop can observe it, but bars nothing.
    /// The default, and the only decision a run that has not cleared the bar may record.</summary>
    public const string DemoteOnly = "demote-only";
}

/// <summary>
/// One offline backtest of the cross-reference judge, at one split origin — the ONLY thing that
/// may promote a verdict from "demotes" to "hides".
///
/// <para><b>Deliberately not <c>EvaluationResult</c>.</b> That table is the M2 modelling ledger,
/// its iteration counter is the kill criterion, and its rows are comparable to each other across
/// sixteen iterations of one estimand. Writing a judge's rule-out rate into it would make the
/// kill-criterion count meaningless and the ledger unreadable.</para>
///
/// <para><b>The gate is a BOUND, not a point estimate.</b> At n≈55 loved holdout titles an
/// observed false-rule-out of 9% has a 95% upper bound around 20%: reading the 9% as "under 15%"
/// is reading sampling noise as a result. The rule is therefore
/// <see cref="WilsonHigh"/> ≤ 0.15 AND <see cref="TrueRuleOutRate"/> ≥ 0.40 AND
/// <see cref="UnsureRate"/> ≤ 0.60 — the second clause because a judge that rules nothing out is
/// harmless and useless, the third because 90% unsure is honest and useless, which is the same
/// failure the calibrator collapse guard exists for.</para>
///
/// <para><see cref="Caveat"/> is persisted rather than rendered, for the same reason
/// <c>EvaluationResult</c> persists its popularity caveat: the number and the reason it should be
/// read cautiously must travel together, or the number gets quoted alone.</para>
/// </summary>
public class CrossReferenceEvaluation : IAccountScoped
{
    public Guid Id { get; set; }

    public Guid AccountId { get; set; }

    /// <summary>Groups the five origins of one run, so a mean row and its parts stay joined.</summary>
    public Guid RunId { get; set; }

    /// <summary>
    /// The split origin as a fraction of the ordered rating history — one of
    /// <c>EvalHarness.SplitOrigins</c>. NULL on the run's summary row, which is the mean across
    /// them and belongs to no single origin.
    /// </summary>
    public double? SplitFraction { get; set; }

    /// <summary>Where the split cut. Default on the summary row.</summary>
    public DateTime SplitAt { get; set; }

    /// <summary>Holdout titles that were eligible as-of the split and were judged.</summary>
    public int HoldoutJudged { get; set; }

    /// <summary>Of those, the ones the account went on to rate ≥ 8. The false-rule-out denominator.</summary>
    public int LovedCount { get; set; }

    /// <summary>Of those, the ones the account went on to rate ≤ 4. The true-rule-out denominator.</summary>
    public int DislikedCount { get; set; }

    /// <summary>Loved titles the judge ruled Out. Every one of these is a film the viewer loved and
    /// the instrument would have hidden.</summary>
    public int FalseRuleOuts { get; set; }

    /// <summary>Disliked titles the judge ruled Out. The value side.</summary>
    public int TrueRuleOuts { get; set; }

    public decimal FalseRuleOutRate { get; set; }

    public decimal TrueRuleOutRate { get; set; }

    /// <summary>Lower 95% Wilson bound on <see cref="FalseRuleOutRate"/>.</summary>
    public decimal WilsonLow { get; set; }

    /// <summary>Upper 95% Wilson bound on <see cref="FalseRuleOutRate"/> — the gate reads THIS.</summary>
    public decimal WilsonHigh { get; set; }

    /// <summary>Share of judged holdout titles the judge answered Unsure on.</summary>
    public decimal UnsureRate { get; set; }

    /// <summary>Eligible holdout titles skipped for want of a plot embedding, so the coverage
    /// claim on any surface can be stated honestly rather than rounded away.</summary>
    public int NoEmbeddingCount { get; set; }

    /// <summary>One of <see cref="GatingDecisions"/>.</summary>
    public string GatingDecision { get; set; } = GatingDecisions.DemoteOnly;

    /// <summary>How this row must be read — names n and the interval in words.</summary>
    public string Caveat { get; set; } = string.Empty;

    public string ModelId { get; set; } = string.Empty;

    public int PromptVersion { get; set; }

    public long PromptTokens { get; set; }

    public long CompletionTokens { get; set; }

    public DateTime EvaluatedAt { get; set; }
}
