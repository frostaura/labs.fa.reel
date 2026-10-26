using FrostAura.Reel.Domain.Tenancy;

namespace FrostAura.Reel.Domain.Ml;

/// <summary>
/// Leakage-clean eval outcome for one training run — the M2 gate record.
/// <para><see cref="PassedGate"/> now reads NDCG@10: the bootstrap 95% CI on the PAIRED
/// model-minus-baseline lift must exclude zero in the model's favour, AND the mean relative NDCG
/// lift across the five rolling split origins must clear 20%. <see cref="PassedLegacyGate"/> keeps
/// the retired precision@10 gate so the twelve historical ledger rows stay comparable.</para>
/// <para>Every column added by the 2026-09 instrument repair is nullable: rows written before it
/// genuinely do not have these numbers, and a zero would read as a measurement.</para>
/// </summary>
public class EvaluationResult : IAccountScoped
{
    public Guid Id { get; set; }

    public Guid AccountId { get; set; }

    public Guid TrainingRunId { get; set; }

    /// <summary>DISPLAY ONLY — quantised to 0.1 by a ten-slot cut. See EvalHarness.PrecisionQuantisationCaveat.</summary>
    public decimal ModelPrecisionAt10 { get; set; }

    /// <inheritdoc cref="ModelPrecisionAt10"/>
    public decimal BaselinePrecisionAt10 { get; set; }

    /// <summary>(model − baseline) / baseline on precision@10 — the legacy lift, kept for continuity.</summary>
    public decimal RelativeImprovement { get; set; }

    public decimal Rmse { get; set; }

    public decimal Mae { get; set; }

    public decimal SpearmanRho { get; set; }

    public int HoldoutPositiveCount { get; set; }

    /// <summary>True when holdout positives &lt; 10 and the threshold fell back to user-median.</summary>
    public bool LowSample { get; set; }

    /// <summary>NDCG CI excludes zero AND mean relative NDCG lift ≥ 0.20 across the rolling origins.</summary>
    public bool PassedGate { get; set; }

    /// <summary>The retired precision@10 gate (relative lift ≥ 0.20) — ledger continuity only.</summary>
    public bool PassedLegacyGate { get; set; }

    /// <summary>NDCG@10 of the model's ranking over the eligible holdout (graded relevance).</summary>
    public decimal? NdcgAt10Model { get; set; }

    /// <summary>NDCG@10 of the popularity baseline over the identical pool.</summary>
    public decimal? NdcgAt10Baseline { get; set; }

    /// <summary>Lower bound of the bootstrap 95% CI on the paired NDCG lift (model − baseline).</summary>
    public decimal? NdcgLiftCiLow { get; set; }

    /// <inheritdoc cref="NdcgLiftCiLow"/>
    public decimal? NdcgLiftCiHigh { get; set; }

    /// <summary>1/√(n−3) at the holdout size — ρ reads far more precise than it is without it.</summary>
    public decimal? SpearmanSe { get; set; }

    /// <summary>Mean(predicted − actual) on raw base scores. Positive = the model over-promises.</summary>
    public decimal? CalibrationBias { get; set; }

    /// <summary>Expected Calibration Error over ten predicted-rating buckets.</summary>
    public decimal? Ece { get; set; }

    /// <summary>Holdout titles that survived the account's content rules — what was measured.</summary>
    public int? EligibleHoldoutCount { get; set; }

    /// <summary>Holdout titles before the content rules — the difference is what the filters dropped.</summary>
    public int? UnfilteredHoldoutCount { get; set; }

    /// <summary>Per-origin NDCG and precision for the five rolling split origins (jsonb).</summary>
    public string? OriginsJson { get; set; }

    /// <summary>
    /// The baselines ledger at the primary origin (jsonb): the model's NDCG@10 beside random,
    /// popularity, raw Trakt rating, vote-shrunk crowd and genre-affinity rankers over the identical
    /// holdout, each with the paired lift and its bootstrap CI, and the as-of caveat. Null on rows
    /// written before 2026-10-26 — they never measured these.
    /// </summary>
    public string? BaselinesJson { get; set; }

    /// <summary>
    /// SHA-256 (lowercase hex) of the primary origin's exact inputs — feature names, training matrix
    /// and labels in training order, holdout vectors and ratings, threshold. With an equal
    /// <c>TrainingRun.ConfigHash</c>, an equal value means an identical NDCG. Null before 2026-10-26.
    /// </summary>
    public string? FeatureSetHash { get; set; }

    /// <summary>Per-rank hit table, feature importances, calibration, gates, caveats (jsonb).</summary>
    public string DetailJson { get; set; } = "{}";

    public DateTime ComputedAt { get; set; }
}
