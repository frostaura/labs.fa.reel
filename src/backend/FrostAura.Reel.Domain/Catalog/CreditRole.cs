namespace FrostAura.Reel.Domain.Catalog;

/// <summary>
/// What a person did on a title. Actor, Director and Writer feed the affinity features; other crew is
/// noise for taste.
/// </summary>
public enum CreditRole
{
    Actor,
    Director,
    Writer,

    /// <summary>
    /// A SHOW's creator (TMDB <c>created_by</c>: Breaking Bad → Vince Gilligan) — its auteur the way a
    /// film's director is. A show's crew names a director per EPISODE, so without this role "Directors"
    /// on a show page was a list of whoever directed episodes and the person the show is "by" was not
    /// named at all (HREV2-D-02). <see cref="TitleCredit.CastOrder"/> carries TMDB's own order.
    ///
    /// <para><b>Not an affinity role yet.</b> Readers that branch on the three roles above must decide
    /// what a creator is to them rather than fall into a default branch: a creator is not cast. Stored
    /// as its name like every enum, and appended last so no existing ordering by role moves.</para>
    /// </summary>
    Creator,
}
