using FrostAura.Reel.Domain.Catalog;

namespace FrostAura.Reel.Application.Ranking;

/// <summary>
/// What earns a title a seat on the two labelled kind rows — "Deep cuts — under the radar" and
/// "New &amp; for you" — held in one place so the build that deals the rows and the Home refill that
/// backfills a seat between builds apply the SAME rule (HREV6-RET-01). The labels are claims about
/// the cards under them: a 2005 show is not new, and a show with TMDB popularity 104 is not under
/// anyone's radar, whatever its score. The refill used to fill these seats from the next-best
/// scored title with no row rule at all, so marking one new card seen and reloading Home could put
/// an old title under "New &amp; for you".
/// </summary>
public static class FeedRowRules
{
    /// <summary>"New" is a release inside this many days of the moment the row is dealt or refilled.</summary>
    public const int NewForYouWindowDays = 900;

    /// <summary>A deep cut has to be at least this good for the reader, whatever their own bar is.</summary>
    public const decimal DeepCutPredictedFloor = 6m;

    /// <summary>
    /// The share of the build's candidate pool, by popularity, a deep cut has to sit under: the pool
    /// skews popular, so "under the radar" is below its 70th percentile.
    /// </summary>
    public const int DeepCutPopularityPercentile = 70;

    /// <summary>The release that dates a title: a film's release, else a show's first air date.</summary>
    public static DateTime? ReleaseOf(Title title) => title.ReleasedAt ?? title.FirstAiredAt;

    /// <summary>A popularity the deep-cuts rule reads; unknown counts as nobody watching (0).</summary>
    public static decimal PopularityOf(Title title) => title.TmdbPopularity ?? 0m;

    /// <summary>
    /// The build's deep-cuts bound: the candidate pool's <see cref="DeepCutPopularityPercentile"/>th
    /// percentile popularity, which a deep cut must sit strictly below. An empty pool admits nothing.
    /// </summary>
    public static PopularityBound DeepCutBoundFromPool(IReadOnlyCollection<Title> candidatePool)
    {
        if (candidatePool.Count == 0)
        {
            return PopularityBound.Below(0m);
        }

        var ordered = candidatePool.Select(PopularityOf).OrderBy(p => p).ToList();
        return PopularityBound.Below(ordered[candidatePool.Count * DeepCutPopularityPercentile / 100]);
    }

    /// <summary>
    /// The refill's deep-cuts bound, read off the snapshot rather than recomputed: the build's pool
    /// threshold is not stored, but every deep cut it dealt sat strictly under it, so a title no more
    /// popular than the most popular of them sits under it too. A sound under-approximation — it can
    /// refuse a title the build would have taken, never admit one it would not. No dealt deep cuts:
    /// nothing is under a radar nobody measured.
    /// </summary>
    public static PopularityBound DeepCutBoundFromDealt(IEnumerable<Title> dealtDeepCuts)
    {
        var dealt = dealtDeepCuts.Select(PopularityOf).ToList();
        return dealt.Count == 0 ? PopularityBound.Below(0m) : PopularityBound.AtMost(dealt.Max());
    }

    /// <summary>
    /// A deep cut: at <see cref="DeepCutPredictedFloor"/> or the reader's bar, whichever is higher
    /// (held to the badge, as every bar is), and inside <paramref name="popularity"/>.
    /// </summary>
    public static bool IsDeepCut(decimal predicted, Title title, decimal bar, PopularityBound popularity) =>
        ForYouBar.Clears(predicted, Math.Max(DeepCutPredictedFloor, bar))
        && popularity.Admits(PopularityOf(title));

    /// <summary>
    /// New for you: clears the reader's bar and was released no more than
    /// <see cref="NewForYouWindowDays"/> days before <paramref name="now"/>. An undated title is not new.
    /// </summary>
    public static bool IsNewForYou(decimal predicted, Title title, decimal bar, DateTime now) =>
        ForYouBar.Clears(predicted, bar)
        && ReleaseOf(title) is { } released
        && (now - released).TotalDays <= NewForYouWindowDays;

    /// <summary>A popularity ceiling, strict (the build's percentile) or inclusive (the dealt maximum).</summary>
    public readonly record struct PopularityBound(decimal Value, bool Inclusive)
    {
        /// <summary>Admits popularity strictly below <paramref name="value"/>.</summary>
        public static PopularityBound Below(decimal value) => new(value, Inclusive: false);

        /// <summary>Admits popularity at or below <paramref name="value"/>.</summary>
        public static PopularityBound AtMost(decimal value) => new(value, Inclusive: true);

        /// <summary>Does <paramref name="popularity"/> sit inside this bound?</summary>
        public bool Admits(decimal popularity) => Inclusive ? popularity <= Value : popularity < Value;
    }
}
