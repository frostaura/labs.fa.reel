using FrostAura.Reel.Domain.Tenancy;

namespace FrostAura.Reel.Domain.Ml;

public enum ArtifactStatus
{
    Active,
    Superseded,
    Failed,
}

/// <summary>
/// A trained per-account model. Bytes live in Postgres (multi-instance safe, inside pg
/// backups; FastTree zips are &lt;5 MB). Exactly one Active artifact per account.
/// </summary>
public class ModelArtifact : IAccountScoped
{
    public Guid Id { get; set; }

    public Guid AccountId { get; set; }

    /// <summary>Per-account incrementing version.</summary>
    public int Version { get; set; }

    public string Algo { get; set; } = "FastTree";

    public byte[] ArtifactBytes { get; set; } = [];

    /// <summary>Ordered feature schema (jsonb) the artifact was trained against.</summary>
    public string FeatureSchemaJson { get; set; } = "[]";

    public Guid TrainingRunId { get; set; }

    public ArtifactStatus Status { get; set; } = ArtifactStatus.Active;

    public DateTime TrainedAt { get; set; }

    /// <summary>
    /// The rating calibrator PINNED to this artifact (jsonb): fitted once, the first time the artifact
    /// serves a number, and applied unchanged to every later build and on-demand score of it. Before
    /// the pin the map refitted on every rating and every ten minutes, so two builds of one artifact
    /// with no new ratings could still show different numbers. Null until first served; a new
    /// artifact gets its own fit. Read through <c>RatingCalibrator.FromJson</c>, which refits on
    /// anything it cannot interpret.
    /// </summary>
    public string? CalibratorJson { get; set; }

    /// <summary>When <see cref="CalibratorJson"/> was fitted and pinned.</summary>
    public DateTime? CalibratorPinnedAt { get; set; }
}
