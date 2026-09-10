using FrostAura.Reel.Domain.Tenancy;
using Pgvector;

namespace FrostAura.Reel.Domain.Ml;

/// <summary>
/// One persisted pocket of an account's taste, as computed by the day's feed build.
///
/// <para><b>Why it is stored at all.</b> The clustering itself is cheap enough to recompute, and the
/// serving path does exactly that behind a short cache. What cannot be recomputed later is what the
/// partition LOOKED LIKE on the day a card was served — which is the only way to read
/// <c>FeedBuildStat.AnnPerClusterJson</c> back against anything, and the only way to answer "did the
/// pocket that produced this pick still exist a month later". Fourteen days per account is kept,
/// pruned nightly, because that is the window the probe's before/after comparison reads and rather
/// less than the cost of keeping a 6 KB vector per cluster for ever.</para>
///
/// <para><b>Written from the feed build only.</b> Request paths read the in-memory partition and
/// never write one: a page load is not evidence about taste, and five harness passes over one
/// account would otherwise mint five days of rows for a measurement that is supposed to be
/// read-only. A failed write is logged and swallowed — the build's own partition is already in hand,
/// so a persistence failure must never cost the founder a feed.</para>
///
/// <para><b>Never a feature.</b> Nothing in <c>FeatureVectorBuilder</c> reads this table, and the
/// leakage rule says why: a partition of the user's own labels cannot be leave-one-out'd, so a
/// feature derived from it would leak the holdout into the training row by construction.</para>
/// </summary>
public class TasteCluster : IAccountScoped
{
    public Guid Id { get; set; }

    public Guid AccountId { get; set; }

    /// <summary>
    /// The UTC day this partition describes. Day-granular on purpose: the stable-tonight contract
    /// rebuilds a feed several times within one day, and three rebuilds an hour apart must share one
    /// partition or the "identical rebuild assembles identically" guarantee is gone.
    /// </summary>
    public DateOnly ComputedForDate { get; set; }

    /// <summary>Canonical rank within the day — weight descending, then lowest member title id.</summary>
    public int Ordinal { get; set; }

    /// <summary>
    /// The unit-length centroid, as a real <c>vector(1536)</c> column configured exactly like
    /// <c>TitleEmbedding.Embedding</c>. Never JSON floats: 1,536 invariant-formatted numbers is
    /// ~30 KB per row that Postgres cannot compare, and a single culture slip in the writer would
    /// produce a row that parses on one machine and not another.
    /// </summary>
    public Vector Centroid { get; set; } = new(Array.Empty<float>());

    /// <summary>
    /// Share of the account's attention this pocket holds — the sum of its members' rating-over-mean
    /// × recency weights, with unrated rewatches contributing a small behavioural weight. It is what
    /// the ANN budget is split by, so it is stored rather than re-derived.
    /// </summary>
    public decimal Weight { get; set; }

    public int MemberCount { get; set; }

    /// <summary>The three member titles nearest the centroid, as a JSON array of ids — what a human reads it by.</summary>
    public string ExemplarTitleIdsJson { get; set; } = "[]";

    /// <summary>
    /// A human name for the pocket. Always null today: naming a cluster is an LLM call with its own
    /// budget and its own failure modes, and the column exists so adding one later is not a migration.
    /// </summary>
    public string? Label { get; set; }

    public DateTime ComputedAt { get; set; }
}
