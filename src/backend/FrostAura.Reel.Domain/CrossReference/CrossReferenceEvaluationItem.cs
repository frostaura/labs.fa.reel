using FrostAura.Reel.Domain.Tenancy;

namespace FrostAura.Reel.Domain.CrossReference;

/// <summary>
/// One judged holdout title inside one backtest origin — the verdict, the confidence, the evidence
/// it was shown, and what the viewer went on to rate it.
///
/// <para><b>Why this table exists.</b> The 2026-09-10 live run (prompt v1, gpt-5.4-mini, five
/// origins, 1,305 judged) came back with a false-rule-out rate of 12.0% against a true-rule-out
/// rate of 10.1% — statistically indistinguishable, which says the <c>out</c> verdict carried no
/// information about the outcome on this account's history. Nothing in the persisted rows could
/// answer the obvious follow-up questions: does <c>keep</c> carry signal even though <c>out</c>
/// does not? Which titles did it get wrong, and did they share a shape? Are the confidences
/// ordered at all? The run persisted only per-origin AGGREGATES, so every one of those questions
/// needed the whole £-costing run repeated to ask.</para>
///
/// <para><b>An aggregate cannot be re-interrogated; a row can.</b> Every discrimination metric on
/// <see cref="CrossReferenceEvaluation"/> is derivable from these rows, which means a new metric
/// is a recomputation rather than another 25 provider calls — and a human can read the actual
/// misses. That is the entire justification for the storage.</para>
///
/// <para><b>These rows are evidence, not state.</b> Nothing on a serving path reads them; they are
/// written once per backtest origin and never updated. A verdict that shapes the FEED lives in
/// <see cref="TitleCrossReferenceVerdict"/>, is keyed on the taste fingerprint, and is a different
/// fact about a different instant.</para>
/// </summary>
public class CrossReferenceEvaluationItem : IAccountScoped
{
    public Guid Id { get; set; }

    public Guid AccountId { get; set; }

    /// <summary>
    /// The <see cref="CrossReferenceEvaluation"/> origin row this item was counted into. Always a
    /// per-origin row and never the run's mean row — the mean has no items of its own, it is a
    /// pooling of these.
    /// </summary>
    public Guid EvaluationId { get; set; }

    public Guid TitleId { get; set; }

    /// <summary>
    /// The origin's split fraction, carried alongside <see cref="EvaluationId"/> rather than joined
    /// for it. The five origins share most of their pool, so "the same title at two origins" is the
    /// common case and a reader paging these rows needs to see which origin a row came from without
    /// a join per page.
    /// </summary>
    public double SplitFraction { get; set; }

    public CrossReferenceVerdict Verdict { get; set; }

    /// <summary>One of <see cref="CrossReferenceReasons"/>, legal for <see cref="Verdict"/>.</summary>
    public string ReasonCode { get; set; } = string.Empty;

    /// <summary>0–1 as the judge reported it, at the same three decimals the verdict table uses.</summary>
    public decimal Confidence { get; set; }

    /// <summary>
    /// What this account went on to rate the title AFTER the split — the outcome the verdict is
    /// scored against, and the one field here the judge never saw.
    /// </summary>
    public short LaterRating { get; set; }

    /// <summary>
    /// The neighbours this verdict was shown, as a jsonb array of title ids. The reason a
    /// surprising row can be re-read against the evidence it actually had rather than against
    /// today's nearest neighbours, which move as the account rates more.
    /// </summary>
    public string NeighbourIdsJson { get; set; } = "[]";

    /// <summary>Which batch of the origin's pass this entry came back in. Diagnostic only.</summary>
    public int BatchIndex { get; set; }
}
