using System.Linq.Expressions;
using FrostAura.Reel.Domain.Catalog;

namespace FrostAura.Reel.Application.Search.Retrieval;

/// <summary>
/// The crowd rating and vote count behind a title's badge, re-expressed for queries (a method call
/// cannot cross into SQL): <see cref="CrowdRating.Source"/> picks the source — Trakt when it has at
/// least <see cref="CrowdRating.TraktFirstMinVotes"/> votes, else TMDB when it has a rating with
/// votes, else Trakt with any votes — and the count and the shrunk score always belong to that
/// source. Zero votes means unknown. Keep these in step with <see cref="CrowdRating"/>; the parity is
/// pinned by <c>CrowdScoreParityTests</c> on real Postgres.
/// </summary>
public static class CrowdVotes
{
    /// <summary>
    /// The default minimum crowd votes a title needs before it counts in a supply report or a
    /// relaxation offer: low-vote titles never inflate "N left" or "relaxing X opens N".
    /// </summary>
    public const int MinRelaxVotes = 50;

    /// <summary>The badge's vote count as SQL: the winning source's count, 0 when there is no rating with votes.</summary>
    public static readonly Expression<Func<Title, int>> Votes = t =>
        t.TraktRating != null && t.TraktRating > 0m && t.TraktVotes >= CrowdRating.TraktFirstMinVotes
            ? t.TraktVotes
            : t.TmdbVoteAverage != null && t.TmdbVoteAverage > 0m && t.TmdbVoteCount > 0
                ? t.TmdbVoteCount
                : t.TraktRating != null && t.TraktRating > 0m && t.TraktVotes > 0
                    ? t.TraktVotes
                    : 0;

    /// <summary>
    /// The badge's shrunk score as SQL, unrounded, or null when there is none — the ranking signal
    /// (§4.8: "a vote-shrunk crowd score") and what a shrunk crowd floor would compare.
    /// </summary>
    public static readonly Expression<Func<Title, decimal?>> ShrunkScore = t =>
        t.TraktRating != null && t.TraktRating > 0m && t.TraktVotes >= CrowdRating.TraktFirstMinVotes
            ? ((t.TraktVotes * t.TraktRating) + (CrowdRating.PriorVotes * CrowdRating.Prior)) / (t.TraktVotes + CrowdRating.PriorVotes)
            : t.TmdbVoteAverage != null && t.TmdbVoteAverage > 0m && t.TmdbVoteCount > 0
                ? ((t.TmdbVoteCount * t.TmdbVoteAverage) + (CrowdRating.PriorVotes * CrowdRating.Prior)) / (t.TmdbVoteCount + CrowdRating.PriorVotes)
                : t.TraktRating != null && t.TraktRating > 0m && t.TraktVotes > 0
                    ? ((t.TraktVotes * t.TraktRating) + (CrowdRating.PriorVotes * CrowdRating.Prior)) / (t.TraktVotes + CrowdRating.PriorVotes)
                    : null;

    /// <summary>Titles whose resolved crowd vote count is at least <paramref name="minVotes"/>.</summary>
    public static IQueryable<Title> AtLeast(IQueryable<Title> titles, int minVotes)
    {
        if (minVotes <= 0)
        {
            return titles;
        }

        var parameter = Votes.Parameters[0];
        var predicate = Expression.Lambda<Func<Title, bool>>(
            Expression.GreaterThanOrEqual(Votes.Body, Expression.Constant(minVotes)), parameter);
        return titles.Where(predicate);
    }

    /// <summary><paramref name="titles"/> ordered most-voted first, by the same resolution, ties broken by id.</summary>
    public static IOrderedQueryable<Title> MostVotedFirst(IQueryable<Title> titles) =>
        titles.OrderByDescending(Votes).ThenBy(t => t.Id);

    /// <summary>
    /// <paramref name="titles"/> ordered best shrunk crowd score first (titles with none last), then
    /// most voted, then id — the crowd half of a ranking, in SQL.
    /// </summary>
    public static IOrderedQueryable<Title> BestShrunkFirst(IQueryable<Title> titles)
    {
        // Postgres sorts NULL first under DESC, so "no score" is ordered explicitly, last.
        var unscored = Expression.Lambda<Func<Title, bool>>(
            Expression.Equal(ShrunkScore.Body, Expression.Constant(null, typeof(decimal?))),
            ShrunkScore.Parameters[0]);
        return titles.OrderBy(unscored).ThenByDescending(ShrunkScore).ThenByDescending(Votes).ThenBy(t => t.Id);
    }

    /// <summary>
    /// Titles whose shrunk crowd score, at badge precision, is at least <paramref name="floor"/> — the
    /// floor as it would read after FD-7. Measurement only until the founder confirms the switch.
    /// </summary>
    public static Expression<Func<Title, bool>> ShrunkAtLeast(decimal floor)
    {
        var parameter = ShrunkScore.Parameters[0];
        var rounded = Expression.Call(
            typeof(Math).GetMethod(nameof(Math.Round), [typeof(decimal), typeof(int)])!,
            Expression.Convert(ShrunkScore.Body, typeof(decimal)),
            Expression.Constant(1));
        var body = Expression.AndAlso(
            Expression.NotEqual(ShrunkScore.Body, Expression.Constant(null, typeof(decimal?))),
            Expression.GreaterThanOrEqual(rounded, Expression.Constant(floor)));
        return Expression.Lambda<Func<Title, bool>>(body, parameter);
    }

    /// <summary>The resolved vote count for a materialised title; 0 when unknown.</summary>
    public static int Of(Title title) => CrowdRating.VotesFor(title) ?? 0;
}
