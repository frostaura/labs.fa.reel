using FrostAura.Reel.Domain.Tenancy;

namespace FrostAura.Reel.Domain.Ml;

public enum TrainingRunStatus
{
    Running,
    Succeeded,
    Failed,
}

/// <summary>
/// One training attempt. <see cref="Iteration"/> is the kill-criterion instrument: it bumps
/// when the feature-set/hyperparameter hash changes, and the program dies if no edge after 3.
/// </summary>
public class TrainingRun : IAccountScoped
{
    public Guid Id { get; set; }

    public Guid AccountId { get; set; }

    public int Iteration { get; set; }

    /// <summary>
    /// Hash of the feature-schema version + hyperparameters — same hash, same iteration.
    /// Coverage statistics are deliberately NOT hashed: they grew with every new rating, so each
    /// nightly drift retrain minted a fresh hash and burned an iteration of the kill criterion.
    /// </summary>
    public string ConfigHash { get; set; } = string.Empty;

    public string HyperparamsJson { get; set; } = "{}";

    /// <summary>The time-split point on UserRating.FirstRatedAt: train ≤ SplitAt &lt; holdout.</summary>
    public DateTime SplitAt { get; set; }

    public int TrainRowCount { get; set; }

    /// <summary>Holdout titles actually measured — i.e. after the account's content rules.</summary>
    public int HoldoutRowCount { get; set; }

    /// <summary>
    /// The "loved" threshold this run used: the account's FROZEN top-quintile boundary, floored at
    /// 8. Frozen per account so two ledger rows always mean the same thing by "loved"; a run that
    /// fell back to the train median records the fallback here and is flagged LowSample on its eval.
    /// </summary>
    public decimal PositiveThreshold { get; set; }

    public TrainingRunStatus Status { get; set; } = TrainingRunStatus.Running;

    public DateTime StartedAt { get; set; }

    public DateTime? CompletedAt { get; set; }

    public string? Error { get; set; }
}
