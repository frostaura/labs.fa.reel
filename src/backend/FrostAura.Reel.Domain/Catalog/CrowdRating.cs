namespace FrostAura.Reel.Domain.Catalog;

/// <summary>
/// THE single resolution of the community ("crowd") rating a card shows next to the personal
/// prediction. Every card surface — feed hero/rows, /more, Ask Reel, lexical search, typeahead,
/// saved, the actor page, title detail — reads it from here so the badge cannot disagree with
/// itself between two screens.
///
/// TMDB leads, Trakt is the fallback. Measured on the live catalogue on 2026-08-07:
/// <c>TmdbVoteAverage</c> covers 3,981 / 3,987 titles (~100%) while <c>TraktRating</c> covers
/// 1,974 / 3,987 (50%), so ranking Trakt first would blank the badge on half the catalogue for no
/// gain. A zero reads as "never rated", not "rated zero" — both sources use 0 as their unset value.
///
/// The same TMDB-then-Trakt precedence is re-expressed inline (it cannot be a method call) inside
/// <c>EligibilityQueryBuilder.ContentFilteredTitles</c> for the <c>MinCrowdRating</c> filter, so the
/// number a user filters on is the number their card shows. Change one, change the other.
/// </summary>
public static class CrowdRating
{
    /// <summary>The 0–10 community rating rounded to one decimal, or null when neither source has one.</summary>
    public static decimal? For(Title title) => Resolve(title).Rating;

    /// <summary>The vote count backing <see cref="For"/>, or null when it is unknown (or zero).</summary>
    public static int? VotesFor(Title title) => Resolve(title).Votes;

    /// <summary>Rating and its vote count resolved together, so the count always belongs to the source that won.</summary>
    public static (decimal? Rating, int? Votes) Resolve(Title title)
    {
        if (title.TmdbVoteAverage is { } tmdb && tmdb > 0m)
        {
            return (Round(tmdb), title.TmdbVoteCount > 0 ? title.TmdbVoteCount : null);
        }

        if (title.TraktRating is { } trakt && trakt > 0m)
        {
            return (Round(trakt), title.TraktVotes > 0 ? title.TraktVotes : null);
        }

        return (null, null);
    }

    private static decimal Round(decimal value) => Math.Round(value, 1, MidpointRounding.AwayFromZero);
}
