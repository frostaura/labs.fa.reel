using FrostAura.Reel.Domain.Tenancy;

namespace FrostAura.Reel.Domain.Library;

public enum ReactionKind
{
    NotInterested,
    SaveForLater,

    /// <summary>
    /// User manually dropped a show. Hides it from Continue Watching AND from every discovery
    /// surface that composes <c>EligibilityQueryBuilder.EligibleTitles</c> — feed, search
    /// suggestions, Ask Reel. It was Continue-Watching-only until 2026-08-07, which meant a dropped
    /// title with no watch footprint stayed eligible and was re-picked on every feed build.
    /// Revocable via <see cref="UserTitleReaction.RevokedAt"/>, which restores eligibility.
    /// Exclude-only: no rating, no model penalty, no Trakt write-back (Trakt has no dropped concept).
    /// </summary>
    Dropped,
}

public enum ReactionReason
{
    Genre,
    SeenEnough,
    Tone,
}

/// <summary>
/// Explicit feed reactions. NotInterested permanently excludes a title from discovery
/// (revocable via Undo → <see cref="RevokedAt"/>); SaveForLater is the in-app watchlist
/// and feeds the managed "Reel — Up Next" Trakt list.
/// </summary>
public class UserTitleReaction : IAccountScoped
{
    public Guid Id { get; set; }

    public Guid AccountId { get; set; }

    public Guid TitleId { get; set; }

    public ReactionKind Kind { get; set; }

    public ReactionReason? Reason { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? RevokedAt { get; set; }
}
