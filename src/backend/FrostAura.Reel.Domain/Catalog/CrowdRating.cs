namespace FrostAura.Reel.Domain.Catalog;

/// <summary>Which community a crowd rating came from.</summary>
public enum CrowdSource
{
    Trakt,
    Tmdb,
}

/// <summary>
/// THE single resolution of the community ("crowd") rating a card shows next to the personal
/// prediction, and the one ranking reads. Every card surface — feed hero/rows, /more, Ask Reel,
/// lexical search, typeahead, saved, the actor page, title detail — reads it from here so the badge
/// cannot disagree with itself between two screens.
///
/// <para><b>Vote-shrunk.</b> The badge is a Bayesian average: the source's rating pulled toward a
/// catalogue prior of <see cref="Prior"/> by <see cref="PriorVotes"/> phantom votes,
/// <c>(v·R + C·m) / (v + C)</c>. A raw average on a handful of votes is not a crowd: "Late Night
/// Repairs" showed 10.0 on 2 votes and was served on the ninja turn, and 667 of 38,262 titles carried
/// a 6+ average on fewer than 20 votes (NEW-2, D-12). Shrunk, 10.0 on 2 votes reads 6.6 and ranks
/// below 7.0 on 5,000; no title with fewer than 20 votes can show 9 or more (the ceiling is 7.5).
/// A well-voted title barely moves — at 500 votes the raw rating keeps 91% of its weight. There is
/// no hard vote cutoff, which would silently hide new releases.</para>
///
/// <para><b>Trakt first where it has a crowd.</b> Against the founder's 2,140 ratings, Trakt's
/// rating correlates better with his than TMDB's (Spearman 0.398 vs 0.313; under 200 votes 0.367 vs
/// 0.188). So Trakt leads when it has at least <see cref="TraktFirstMinVotes"/> votes; otherwise TMDB
/// (which covers ~100% of the catalogue against Trakt's ~5%), and Trakt alone only when TMDB has no
/// rating. A zero reads as "never rated", both sources' unset value.</para>
///
/// <para><b>No vote count, no badge.</b> A rating with no vote count behind it cannot be weighed;
/// showing it raw would undo the shrinkage and showing the prior would invent a number, so it is
/// null.</para>
///
/// <para><b>The crowd FLOOR is not this number yet.</b> The account's <c>MinCrowdRating</c> rule
/// still compares the RAW TMDB-then-Trakt average (<see cref="ResolveRaw"/>), re-expressed inline in
/// <c>EligibilityQueryBuilder.ContentFilteredTitles</c>, until the founder confirms the switch
/// (FD-7). The filter cost meter reports what the pool would be under the shrunk floor first, so the
/// change is visible before it is made. Change the resolution here and the SQL twin in
/// <c>CrowdVotes.ShrunkScore</c> together.</para>
/// </summary>
public static class CrowdRating
{
    /// <summary>The catalogue prior every rating is pulled toward (m).</summary>
    public const decimal Prior = 6.5m;

    /// <summary>
    /// The phantom votes at the prior (C). Fifty is where a crowd starts to count everywhere else in
    /// the product (the supply reports' vote floor) — below it the prior dominates, above it the crowd.
    /// </summary>
    public const int PriorVotes = 50;

    /// <summary>The Trakt vote count from which Trakt's rating leads TMDB's.</summary>
    public const int TraktFirstMinVotes = 50;

    /// <summary>The shrunk 0–10 community rating rounded to one decimal, or null when there is none.</summary>
    public static decimal? For(Title title) => Resolve(title).Rating;

    /// <summary>The vote count behind <see cref="For"/>, or null when there is no rating.</summary>
    public static int? VotesFor(Title title) => Resolve(title).Votes;

    /// <summary>
    /// The shrunk rating and its vote count, resolved together so the count always belongs to the
    /// source that won. Rounded to one decimal, away from zero, as the badge prints it.
    /// </summary>
    public static (decimal? Rating, int? Votes) Resolve(Title title) =>
        Source(title) switch
        {
            CrowdSource.Trakt => (Round(Shrink(title.TraktRating!.Value, title.TraktVotes)), title.TraktVotes),
            CrowdSource.Tmdb => (Round(Shrink(title.TmdbVoteAverage!.Value, title.TmdbVoteCount)), title.TmdbVoteCount),
            _ => (null, null),
        };

    /// <summary>The source <see cref="Resolve"/> reads, or null when neither has a rating with votes.</summary>
    public static CrowdSource? Source(Title title)
    {
        var trakt = title.TraktRating is > 0m && title.TraktVotes > 0;
        if (trakt && title.TraktVotes >= TraktFirstMinVotes)
        {
            return CrowdSource.Trakt;
        }

        if (title.TmdbVoteAverage is > 0m && title.TmdbVoteCount > 0)
        {
            return CrowdSource.Tmdb;
        }

        return trakt ? CrowdSource.Trakt : null;
    }

    /// <summary>The unrounded Bayesian average of <paramref name="rating"/> on <paramref name="votes"/> votes.</summary>
    public static decimal Shrink(decimal rating, int votes)
    {
        var v = Math.Max(0, votes);
        return ((v * rating) + (PriorVotes * Prior)) / (v + PriorVotes);
    }

    /// <summary>
    /// The RAW average the <c>MinCrowdRating</c> floor compares today — TMDB first, Trakt as the
    /// fallback, unshrunk, with its own source's vote count (null when unknown). Not for display.
    /// </summary>
    public static (decimal? Rating, int? Votes) ResolveRaw(Title title)
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
