using FrostAura.Reel.Domain.Tenancy;

namespace FrostAura.Reel.Domain.Feed;

/// <summary>What a build actually did to the page.</summary>
public enum FeedBuildOutcome
{
    /// <summary>A new feed was dealt and written.</summary>
    Fresh,

    /// <summary>Tonight's contract held: hero and anchors preserved, and a snapshot was still written.</summary>
    Preserve,

    /// <summary>The assembled feed was item-for-item what was already Active, so nothing was written.</summary>
    NoOp,
}

/// <summary>
/// One row per BuildFeed run — the build-quality instrument the pipeline never had.
///
/// Every number here was already computed on every build and thrown into a log line: how many
/// candidates were scored, how many cleared the hero confidence floor before the never-open-blank
/// backfill relaxed it, how the shortlist's predictions were distributed, and how many newly
/// discovered titles the pool refresh actually added. Without them, "the picks get weaker every
/// round" is an argument; with them it is a chart. The no-op path writes a row too — a build that
/// changed nothing is exactly the observation worth keeping.
///
/// <see cref="FeedSnapshotId"/> carries NO foreign key on purpose: snapshots are pruned to the last
/// seven within minutes of active use, and the whole point of this table is to outlive them.
/// </summary>
public class FeedBuildStat : IAccountScoped
{
    public Guid Id { get; set; }

    public Guid AccountId { get; set; }

    /// <summary>The snapshot this build produced — null on the no-op path, and dangling once pruned.</summary>
    public Guid? FeedSnapshotId { get; set; }

    /// <summary>The Active artifact whose opinion this build served.</summary>
    public Guid ModelArtifactId { get; set; }

    public DateTime BuiltAt { get; set; }

    public FeedBuildOutcome Mode { get; set; }

    /// <summary>TMDB items the pool refresh returned across all discovery arms this build.</summary>
    public int CandidateCount { get; set; }

    /// <summary>
    /// Eligible titles for this account at build time, under the founder's own content rules. The
    /// supply ceiling — when retrieval asks for more slots than this, the pool is exhausted and no
    /// amount of ranking helps.
    /// </summary>
    public int EligiblePoolCount { get; set; }

    /// <summary>Candidates that survived first-pass ranking and were fully scored with contributions.</summary>
    public int ShortlistCount { get; set; }

    /// <summary>
    /// Candidates clearing the hero confidence floor BEFORE the never-open-blank backfill relaxes
    /// it. Zero here with a full hero on screen means the hero is silently reaching.
    /// </summary>
    public int HeroEligibleCount { get; set; }

    /// <summary>Median predicted rating across the scored shortlist.</summary>
    public decimal? ShortlistP50 { get; set; }

    /// <summary>90th-percentile predicted rating across the scored shortlist.</summary>
    public decimal? ShortlistP90 { get; set; }

    /// <summary>Titles this build's pool refresh created that the catalogue had never seen.</summary>
    public int NewCandidatesUpserted { get; set; }

    /// <summary>Rows that assembled but fell below the minimum-members floor and were dropped.</summary>
    public int RailsDropped { get; set; }

    /// <summary>The snapshot's degraded reason, copied here so it survives the snapshot prune.</summary>
    public string? DegradedReason { get; set; }

    public int DurationMs { get; set; }
}
