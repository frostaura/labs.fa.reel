using FrostAura.Reel.Domain.Tenancy;

namespace FrostAura.Reel.Domain.Search;

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

    /// <summary>Position in the answer, zero-based. Unique within the turn; never re-sorted on read.</summary>
    public int Rank { get; set; }

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
