using FrostAura.Reel.Domain.Tenancy;

namespace FrostAura.Reel.Domain.Ml;

/// <summary>
/// The per-account ONLINE RESIDUAL HEAD: a tiny linear model that learns the frozen FastTree
/// base's residual error from live events (ratings, not-interested, saves, passive watches) and
/// is ADDED to the base score at serving time. One row per account. Zero-initialised, so it is a
/// strict no-op until events arrive (no regression). <see cref="BaseArtifactId"/> pins the state
/// to the <see cref="ModelArtifact"/> it corrects — when a retrain ships a new Active artifact the
/// id no longer matches, so the stale residual goes dormant automatically (the auto-reset) with no
/// TrainJobHandler change. Bounded weights/bias keep the correction from ever dominating the base.
/// </summary>
public class OnlineModelState : IAccountScoped
{
    public Guid Id { get; set; }

    public Guid AccountId { get; set; }

    /// <summary>The Active <see cref="ModelArtifact"/> this residual corrects — a mismatch resets it.</summary>
    public Guid BaseArtifactId { get; set; }

    /// <summary>Serialized float[] of length <c>FeatureSchema.VectorLength</c> (little-endian, one per feature).</summary>
    public byte[] Weights { get; set; } = [];

    /// <summary>The residual intercept.</summary>
    public double Bias { get; set; }

    /// <summary>How many online SGD updates have been applied since the last reset (0 = dormant no-op).</summary>
    public long UpdateCount { get; set; }

    /// <summary>
    /// Which online update rule produced <see cref="Weights"/>. A second auto-reset key alongside
    /// <see cref="BaseArtifactId"/>: weights learned by an earlier rule are not interpretable by a
    /// later one, so a mismatch discards them rather than serving a correction whose scale means
    /// something different than the loader assumes. Legacy rows default to 0 (the unnormalised rule
    /// that could saturate the output clamp on a single event) and are therefore reset on first read.
    /// </summary>
    public int AlgorithmVersion { get; set; }

    public DateTime UpdatedAt { get; set; }
}
