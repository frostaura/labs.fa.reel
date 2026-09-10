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

    /// <summary>
    /// The build never reached assembly because there was no Active model artifact this binary
    /// could read. Recorded rather than left as a log line: on 2026-09-10 this was the outcome of
    /// EVERY build the running binary claimed for a day, while the instrument that was supposed to
    /// show it wrote nothing at all — because the only path that wrote a row was the one that never
    /// ran. A skipped build is a build.
    /// </summary>
    Skipped,
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

    /// <summary>
    /// Taste clusters the ANN arm actually split its budget across this build. Zero means the
    /// account took the single-centroid path — either its history is below
    /// <c>TasteClusterProvider.MinClusterInputs</c>, or nothing it loved carries an embedding.
    /// </summary>
    public int ClustersUsed { get; set; }

    /// <summary>
    /// Per-cluster ANN accounting: <c>[{ordinal, weight, target, returned}]</c>.
    ///
    /// <para><b>This is the column that decides whether multi-centroid retrieval worked.</b>
    /// <c>returned &lt; target</c> is not a retrieval failure — it says the catalogue holds nothing
    /// near that pocket, which is a per-cluster SUPPLY answer and the first per-pocket supply signal
    /// this product has ever had. And a recall rise spread evenly across every cluster means the
    /// gain was bought with budget rather than with the partition, in which case the honest response
    /// is to raise <c>FirstPassAnnPool</c> and delete the clusterer.</para>
    /// </summary>
    public string? AnnPerClusterJson { get; set; }

    /// <summary>
    /// Candidates the seed arm could have drawn from — the whole loved/rewatched anchor pool, not
    /// the handful it used. Read against <see cref="SeedsUsed"/> it is the coverage question B2 is
    /// gated on: twelve seeds against a thousand anchors is a ninety-week rotation.
    /// </summary>
    public int SeedPoolSize { get; set; }

    /// <summary>Seeds this build actually spent a TMDB call on.</summary>
    public int SeedsUsed { get; set; }

    /// <summary>
    /// Newly-eligible titles attributed to the discovery arm that first returned them:
    /// <c>[{arm, discovered, upserted, newlyEligible}]</c>. Attribution is FIRST-CLAIM, matching the
    /// item dedup — an arm that would also have found a title another arm returned first gets no
    /// credit for it, so these are statements about the pool as assembled.
    /// </summary>
    public string? NewlyEligiblePerArmJson { get; set; }

    public int DurationMs { get; set; }
}
