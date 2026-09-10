using FrostAura.Reel.Domain.Tenancy;

namespace FrostAura.Reel.Domain.Feed;

/// <summary>
/// Where a card was actually rendered. One value per surface that RETURNS a card to a client,
/// because "which surface earns its keep" is a question the rollup has to answer per-surface.
/// <para>
/// <see cref="FeedHero"/> and <see cref="FeedRow"/> are written by the feed BUILD against the
/// snapshot it just wrote; <see cref="FeedRefill"/> is the live backfill <c>GET /api/feed</c>
/// performs when a since-interacted title leaves the snapshot — those cards are rendered and have
/// never existed as <c>FeedItem</c> rows, so nothing else records them.
/// </para>
/// <para>
/// Typeahead is deliberately absent from the write sites even though <see cref="Search"/> exists:
/// typeahead is lookup, not suggestion — the same distinction <c>CLAUDE.md</c> already draws for
/// eligibility. <see cref="Saved"/> is reserved for the queue surface and is not written yet.
/// </para>
/// </summary>
public enum ExposureSurface
{
    /// <summary>Tonight's picks, from the snapshot build.</summary>
    FeedHero,

    /// <summary>A built rail (because-you-loved / deep-cuts / new-for-you), from the snapshot build.</summary>
    FeedRow,

    /// <summary>A card the feed endpoint backfilled live because the snapshot's pick went stale.</summary>
    FeedRefill,

    /// <summary>A paginated card from <c>POST /api/feed/more</c>.</summary>
    FeedPage,

    /// <summary>A card from a genre rail's pagination.</summary>
    GenreRail,

    /// <summary>A card in the weekly digest as served by <c>GET /api/digest/current</c>.</summary>
    Digest,

    /// <summary>A card returned by an Ask Reel turn (the candidate-reranked set).</summary>
    AskReel,

    /// <summary>Reserved: search result surfaces. Typeahead is deliberately never logged.</summary>
    Search,

    /// <summary>A card from the detail page's "more like this" rail.</summary>
    Similar,

    /// <summary>Reserved: the Saved queue, once it renders predictions of its own.</summary>
    Saved,
}

/// <summary>
/// One card, actually served, with the exact number that was on it. APPEND-ONLY: nothing in the
/// application ever updates or deletes a row here, and the feed builder's 7-snapshot prune cannot
/// reach it — <see cref="FeedSnapshotId"/> is a soft reference with no foreign key, which is the
/// single line that decouples the durable record from the prunable one.
///
/// <para><b>Why per-serve and not de-duplicated per (title, surface, artifact).</b> The displayed
/// number moves WITHIN an artifact era — the online residual head and the calibrator both change it
/// between builds — so one stored <see cref="PredictedRating"/> per key is the wrong resolution for
/// a calibration curve. A counter also cannot answer "this has been in my hero all week and I keep
/// skipping it": the build suppresses its own write when the feed is unchanged, so a show-count
/// would undercount a card that never moved and overcount a burst of rebuilds nobody opened. Raw
/// events can always be rolled up; a counter can never be disaggregated.</para>
///
/// <para><b>Cost.</b> ~55 cards per build, single-digit builds a day: on the order of 10⁵ rows per
/// account-year at ~300 bytes. There is deliberately no retention rule yet.</para>
/// </summary>
public class ExposureEvent : IAccountScoped
{
    public Guid Id { get; set; }

    public Guid AccountId { get; set; }

    public Guid TitleId { get; set; }

    /// <summary>When the card was served. The causal anchor for every outcome join.</summary>
    public DateTime OccurredAt { get; set; }

    public ExposureSurface Surface { get; set; }

    /// <summary>
    /// The rail's identity where one exists — anchor-qualified, so two because-you-loved rails in
    /// the same build stay distinguishable. Null on hero, digest and search surfaces.
    /// </summary>
    public string? RowKey { get; set; }

    /// <summary>Position within the row, zero-based.</summary>
    public int Rank { get; set; }

    /// <summary>Pagination depth; 0 for the first page and every non-paginated surface.</summary>
    public int PageIndex { get; set; }

    /// <summary>
    /// The snapshot this card belonged to, when it had one. NO FOREIGN KEY — snapshots are pruned
    /// to the last seven by design and these rows outlive them.
    /// </summary>
    public Guid? FeedSnapshotId { get; set; }

    /// <summary>Which model made the promise. Never null: a card with no attributable model is not logged.</summary>
    public Guid ModelArtifactId { get; set; }

    /// <summary>
    /// The prediction behind the card, at two decimals. The card itself renders one decimal; the
    /// extra digit is kept because a calibration join spanning <c>FeedItem</c> (1 dp) and
    /// <c>TitleScore</c> (2 dp) otherwise inherits a 0.05 quantisation floor. One resolution, chosen
    /// here, for every surface.
    /// </summary>
    public decimal PredictedRating { get; set; }

    /// <summary>Base model score plus the online residual, BEFORE the calibrator and the demotion.</summary>
    public decimal BaseScore { get; set; }

    /// <summary>How far the monotone predicted→actual calibrator moved <see cref="BaseScore"/>.</summary>
    public decimal CalibrationDelta { get; set; }

    /// <summary>The online residual head's scalar contribution — the only way to ever test the head.</summary>
    public decimal ResidualApplied { get; set; }

    /// <summary>The dislike demotion subtracted after calibration. Zero when nothing was demoted.</summary>
    public decimal DemotionApplied { get; set; }

    /// <summary>The LLM re-rank fit (0–1) where a surface computed one; today it is otherwise discarded.</summary>
    public decimal? LlmFit { get; set; }

    /// <summary>The exact why-line rendered on the card.</summary>
    public string? WhySentence { get; set; }

    /// <summary>How that sentence was produced — Llm | Cache | Templater | Preserved | Generic | Digest.</summary>
    public string WhySource { get; set; } = string.Empty;

    /// <summary>
    /// Which ranking policy contributed this card, when the build drafted the slate between two of
    /// them. NULL is the ordinary value and means exactly one thing: nobody drafted this card — the
    /// interleaving switch was off, the build was a Preserve rebuild of a pre-experiment snapshot,
    /// or the surface is a live serve path (the feed refill, pagination, the digest) that has no
    /// slate to belong to. A null here is never "we forgot"; the attribution is written by the only
    /// code that can know it, at the moment the draft happens.
    /// </summary>
    public string? PolicyId { get; set; }

    /// <summary>
    /// The slate this card was drafted into — one id per (snapshot, row), derived deterministically
    /// from both so a build is reproducible. It is the unit the sequential test counts: preference
    /// is measured WITHIN a slate, because that is the only place the two rankers competed over the
    /// same user attention at the same moment. Null wherever <see cref="PolicyId"/> is null.
    /// </summary>
    public Guid? InterleaveGroup { get; set; }

    /// <summary>
    /// The cross-reference standing this card carried when it was served — one of
    /// <c>AggregateVerdict</c>, as a string.
    ///
    /// <para><b>NULL means no verdict existed</b>, which is a different fact from
    /// <c>Unsure</c>: one is "nothing had judged this yet", the other is "something judged it and
    /// could not tell". The online precision panel divides by the wrong denominator if those two
    /// are conflated, and the difference is invisible after the fact.</para>
    ///
    /// <para>Append-only like every other column here: never back-filled onto rows served before
    /// the verdict existed. A back-fill would make the exposure log claim the product knew
    /// something at a moment when it did not, which is the one thing this table is for.</para>
    /// </summary>
    public string? XrefVerdict { get; set; }

    /// <summary>The confidence behind <see cref="XrefVerdict"/>. Null wherever that is null.</summary>
    public decimal? XrefConfidence { get; set; }

    /// <summary>
    /// <see cref="OccurredAt"/> truncated to the minute, computed by Postgres itself (a stored
    /// generated column) so it can never disagree with the timestamp it comes from. It exists only
    /// to carry the idempotency index: RTK Query refetches the feed on tag invalidation and on
    /// mount, and five refetches of one page inside one minute are one exposure, not five.
    /// </summary>
    public DateTime MinuteBucket { get; private set; }
}
