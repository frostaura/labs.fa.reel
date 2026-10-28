namespace FrostAura.Reel.Domain.Catalog;

/// <summary>Edge between a title and a person in a specific role (affinity-feature joins).</summary>
public class TitleCredit
{
    public Guid Id { get; set; }

    public Guid TitleId { get; set; }

    public Guid PersonId { get; set; }

    public CreditRole Role { get; set; }

    /// <summary>
    /// TMDB's billing order within the role: cast order for an actor (top-billed cast carries more
    /// affinity weight), the <c>created_by</c> order for a <see cref="CreditRole.Creator"/>. Null for
    /// directors and writers, whose TMDB crew list has no meaningful order.
    /// </summary>
    public int? CastOrder { get; set; }

    public string? CharacterName { get; set; }
}
