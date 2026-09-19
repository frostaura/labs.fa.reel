using FrostAura.Reel.Domain.Tenancy;

namespace FrostAura.Reel.Domain.Search;

/// <summary>
/// A durable Ask Reel conversation — so a chat survives a reload and resumes where it left off.
/// Turns hang off it; the title is the first user message (for a future conversation list).
/// </summary>
public class SearchConversation : IAccountScoped
{
    public Guid Id { get; set; }

    public Guid AccountId { get; set; }

    /// <summary>The opening message, truncated — a human label for the conversation.</summary>
    public string Title { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }

    public DateTime LastTurnAt { get; set; }

    /// <summary>
    /// How many <see cref="SearchTurn"/> rows this conversation holds — both roles, every status.
    /// Denormalised so the conversation LIST is one query rather than one plus a group-by; kept in
    /// step by the endpoint that writes the turns, and backfilled by count.
    /// </summary>
    public int TurnCount { get; set; }

    /// <summary>
    /// When this conversation last stored cards. <b>NULL means transcript only</b> — every
    /// conversation that predates the turn record has messages and no results, and the resume
    /// surface must say so rather than rendering an empty grid that reads as "no matches". It is a
    /// different fact from "the last turn returned nothing", which is a completed turn with zero
    /// cards and a reason that explains itself.
    /// </summary>
    public DateTime? LastCardsAt { get; set; }
}
