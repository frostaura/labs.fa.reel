using FrostAura.Reel.Domain.Catalog;

namespace FrostAura.Reel.Application.Search.Retrieval;

/// <summary>
/// The crowd vote count behind a title's badge. <see cref="AtLeast"/> and <see cref="MostVotedFirst"/> are
/// <see cref="CrowdRating.VotesFor"/> re-expressed for queries (a method call cannot cross into SQL)
/// — TMDB leads, Trakt is the fallback, and the count always belongs to the source whose rating won,
/// so a title badged from TMDB is never vote-counted from Trakt. Zero means unknown. Keep the two in
/// step.
/// </summary>
public static class CrowdVotes
{
    /// <summary>
    /// The default minimum crowd votes a title needs before it counts in a supply report or a
    /// relaxation offer: low-vote titles never inflate "N left" or "relaxing X opens N".
    /// </summary>
    public const int MinRelaxVotes = 50;

    /// <summary>Titles whose resolved crowd vote count is at least <paramref name="minVotes"/>.</summary>
    public static IQueryable<Title> AtLeast(IQueryable<Title> titles, int minVotes) =>
        minVotes <= 0
            ? titles
            : titles.Where(t =>
                (t.TmdbVoteAverage != null && t.TmdbVoteAverage > 0m
                    ? t.TmdbVoteCount
                    : t.TraktRating != null && t.TraktRating > 0m ? t.TraktVotes : 0) >= minVotes);

    /// <summary><paramref name="titles"/> ordered most-voted first, by the same resolution, ties broken by id.</summary>
    public static IOrderedQueryable<Title> MostVotedFirst(IQueryable<Title> titles) =>
        titles
            .OrderByDescending(t =>
                t.TmdbVoteAverage != null && t.TmdbVoteAverage > 0m
                    ? t.TmdbVoteCount
                    : t.TraktRating != null && t.TraktRating > 0m ? t.TraktVotes : 0)
            .ThenBy(t => t.Id);

    /// <summary>The resolved vote count for a materialised title; 0 when unknown.</summary>
    public static int Of(Title title) => CrowdRating.VotesFor(title) ?? 0;
}
