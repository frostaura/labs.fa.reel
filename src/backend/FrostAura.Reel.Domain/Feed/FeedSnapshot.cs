using FrostAura.Reel.Domain.Tenancy;

namespace FrostAura.Reel.Domain.Feed;

public enum SnapshotStatus
{
    Active,
    Superseded,
}

/// <summary>
/// One materialized feed build (hero + rows). Exactly one Active per account; the feed
/// endpoint is a pure DB read. Last 7 retained, pruned nightly.
/// </summary>
public class FeedSnapshot : IAccountScoped
{
    public Guid Id { get; set; }

    public Guid AccountId { get; set; }

    public Guid ModelArtifactId { get; set; }

    public DateTime GeneratedAt { get; set; }

    public SnapshotStatus Status { get; set; } = SnapshotStatus.Active;

    /// <summary>
    /// Why this build is not what the account's settings literally asked for — null when it is.
    /// Set when the predicted-rating floor had to be relaxed to avoid a blank page, and when the
    /// candidate pool came back empty so the feed can say WHICH condition emptied it. A blank feed
    /// with no reason is the failure mode this column exists to make impossible.
    /// </summary>
    public string? DegradedReason { get; set; }
}
