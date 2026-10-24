using FrostAura.Reel.Domain.Tenancy;

namespace FrostAura.Reel.Domain.Search;

/// <summary>
/// The closed vocabulary for <see cref="SearchTurnCard.Tier"/>.
/// </summary>
public static class SearchTurnCardTier
{
    /// <summary>A pick: in the grid, logged as an exposure.</summary>
    public const string Pick = "Pick";

    /// <summary>The judge called it a partial match: the collapsed "Close, but not …" tray. Never logged.</summary>
    public const string NearMiss = "NearMiss";

    /// <summary>A match under the account's For-you bar: the "under your bar" tray. Never logged.</summary>
    public const string BelowFloor = "BelowFloor";

    /// <summary>
    /// The tier a stored card belongs to. A row with no tier predates the column and is read from its
    /// <see cref="SearchTurnCard.BelowFloor"/> flag. A tier this build does not know was written by a
    /// later one, and it is read as <see cref="NearMiss"/> — a tray, never the grid — because the one
    /// wrong answer that matters is promoting a card nobody recommended.
    /// </summary>
    public static string Of(SearchTurnCard card) => card.Tier switch
    {
        null or "" => card.BelowFloor ? BelowFloor : Pick,
        Pick => Pick,
        NearMiss => NearMiss,
        BelowFloor => BelowFloor,
        _ => NearMiss,
    };
}

/// <summary>
/// One card an Ask Reel turn actually returned, stored so the turn can be replayed. Modelled on
/// <c>FeedItem</c>: the same shape of thing — a ranked pick belonging to one durable serve — and
/// the same rule that <see cref="Rank"/> is the position on screen rather than a score to re-sort
/// by.
///
/// <para><b>Not an exposure row.</b> <c>ExposureEvent</c> records that a card was SERVED and is
/// append-only evidence for the closed loop; this records WHAT the turn returned so a reload shows
/// the same answer. Replaying a conversation writes neither — the user is looking at the same
/// serve again, not a new one — and deleting a conversation deletes these and never touches the
/// exposure log.</para>
///
/// <para><b><see cref="ConversationId"/> and <see cref="TmdbId"/> are denormalised on purpose.</b>
/// The no-repeat set is <c>SELECT DISTINCT "TmdbId" WHERE "ConversationId" = @c</c>, computed on
/// every turn before discovery runs; through the turn table that is a join on the hot path of a
/// person waiting. <see cref="TmdbId"/> rather than <see cref="TitleId"/> because the no-repeat
/// filter is applied to TMDB discovery results, which have no local id yet.</para>
///
/// <para><b><c>seen</c> is not stored.</b> It is a fact about the user's footprint at the moment of
/// rendering, not about the card, and the replay recomputes it — along with the eligibility
/// re-filter that drops anything the user has interacted with since. A stored badge would be a
/// promise that goes stale silently.</para>
///
/// <para><b>The tray is stored too — see <see cref="BelowFloor"/>.</b> A card the rating floor put
/// under the fence is a card the user SAW, and it therefore belongs to the conversation's
/// no-repeat set exactly as a top pick does. It rides the same table for that reason and for no
/// other: one table, one <c>SELECT DISTINCT "TmdbId"</c>, one definition of "already shown".</para>
/// </summary>
public class SearchTurnCard : IAccountScoped
{
    public Guid Id { get; set; }

    public Guid AccountId { get; set; }

    /// <summary>Denormalised from the turn — see the class remarks.</summary>
    public Guid ConversationId { get; set; }

    public Guid TurnId { get; set; }

    public Guid TitleId { get; set; }

    /// <summary>Denormalised no-repeat key. Null for a title with no TMDB id, which cannot repeat anyway.</summary>
    public long? TmdbId { get; set; }

    /// <summary>
    /// Position in the answer, zero-based. Unique within the turn; never re-sorted on read.
    ///
    /// <para>One counter across BOTH blocks: the picks take 0..n-1 and the turn's
    /// <see cref="BelowFloor"/> tray continues at n. A separate per-block sequence would have made
    /// <c>(TurnId, Rank)</c> — the index that says a turn cannot store two cards at one position —
    /// collide on every turn that had a tray at all.</para>
    /// </summary>
    public int Rank { get; set; }

    /// <summary>
    /// Whether the account's rating floor put this card in the tray rather than in the picks.
    ///
    /// <para><b>Why a tray card is stored at all.</b> The founder-locked rule is <i>never show
    /// things I've already seen</i>, and the tray is on the glass — rendered under the "Show the N
    /// under your bar" fence, one tap away. Before this column existed the tray was emitted and
    /// then forgotten, so the next page's no-repeat set — <c>SELECT DISTINCT "TmdbId"</c> over the
    /// conversation's cards — could not contain it, and the smoke run watched 13 of one turn's 14
    /// below-floor titles come back as the NEXT turn's top picks, visible twice on one screen. The
    /// fix is not a second exclusion list; it is storing what was shown.</para>
    ///
    /// <para><b>It is not a pick, and three places must keep knowing that.</b> The replay hands
    /// these back as <c>belowFloor</c> and never merges them into <c>cards</c>, so a resumed grid
    /// is the grid that was there. The exposure log gets NO row for them — an exposure is evidence
    /// that the product RECOMMENDED a title, and a card the floor pushed below the fence was
    /// deliberately not recommended; logging it would put titles the ranker rejected into the
    /// denominator of every accuracy number. And the reason line's counts are built from the
    /// floor's own verdict, never from a row count over this table.</para>
    ///
    /// <para>Default false, so every row written before this column existed is a pick — which is
    /// what they all were, since the tray was not stored.</para>
    /// </summary>
    public bool BelowFloor { get; set; }

    /// <summary>
    /// Which part of the answer this card sat in: one of <see cref="SearchTurnCardTier"/>. Null on
    /// every row written before the tier existed; <see cref="SearchTurnCardTier.Of"/> reads those
    /// from <see cref="BelowFloor"/>, which is exactly what they meant.
    ///
    /// <para><b><see cref="BelowFloor"/> stays true for EVERY non-pick tier</b>, not only the floor
    /// tray. An image from before this column reads only that flag, so a near-miss row written with
    /// it false would replay on a rollback as a PICK — the promotion the tier exists to prevent.
    /// Mislabelled as under the bar is the safe way to be wrong; promoted into the grid is not.</para>
    /// </summary>
    public string? Tier { get; set; }

    /// <summary>
    /// The judge's request verdict for this title — <c>yes</c>, <c>partial</c> or <c>no</c> — or null
    /// where it was not judged (a failed batch, a stub, a row older than the verdict). Null is
    /// UNJUDGED, never <c>no</c>, and is never re-shown as judged.
    /// </summary>
    public string? Satisfies { get; set; }

    /// <summary>
    /// Where <see cref="LlmFit"/> came from (<c>Llm</c>, <c>Facet</c>, <c>Fallback</c>), typed at the
    /// point it was produced. Null on rows older than the column.
    /// </summary>
    public string? FitSource { get; set; }

    /// <summary>The closest-but-outside reserve flag, exactly as it was on the wire.</summary>
    public bool OutsideRequest { get; set; }

    /// <summary>Cosine similarity to the turn's ranking vector.</summary>
    public double? Similarity { get; set; }

    /// <summary>The prediction the card carried. Re-scored on replay when the turn's artifact no longer applies.</summary>
    public decimal? PredictedRating { get; set; }

    /// <summary>The re-rank fit, where the turn computed one.</summary>
    public decimal? LlmFit { get; set; }

    /// <summary>The server's own ranking value — the number <see cref="Rank"/> came from.</summary>
    public decimal Blend { get; set; }

    /// <summary>The why-line rendered, verbatim.</summary>
    public string? Why { get; set; }

    /// <summary>How that sentence was produced — an <c>ExposureWhySource</c> value.</summary>
    public string WhySource { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }
}
