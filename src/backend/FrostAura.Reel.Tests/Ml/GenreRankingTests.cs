using FrostAura.Reel.Application.Ml;
using FrostAura.Reel.Domain.Lens;

namespace FrostAura.Reel.Tests.Ml;

/// <summary>
/// Which genres a taste profile is allowed to open on.
///
/// <para><b>The defect these pin down produced true numbers and a false sentence.</b> The
/// founder's narrative read "you usually settle into adventure, family, superhero, fantasy and
/// science-fiction… Holiday stories and family stories sit especially high" for an account whose
/// rated library is thriller, action, drama, comedy and crime by a factor of thirty. Nothing was
/// miscomputed. <c>topGenres</c> ranked on shrunken affinity ALONE, so twenty-six holiday films at
/// a high average outranked eight hundred and forty-nine thrillers, and the prose faithfully
/// described the ranking it was handed.</para>
///
/// <para>The distribution below is shaped like the live one deliberately — two large pools rated
/// near the account's own mean, and two small pools rated well above it. That is the shape the
/// affinity-only ranking gets wrong, and asserting the ORDER rather than the membership is what
/// makes the test about the defect rather than about the threshold.</para>
/// </summary>
public class GenreRankingTests
{
    /// <summary>thriller 900 @ 6.9 · horror 600 @ 6.6 · holiday 12 @ 8.4 · family 40 @ 7.9.</summary>
    private static readonly TasteFactsBuilder.GenreStat[] LiveShaped =
    [
        // Affinity-ordered, which is the contract of the input.
        new("holiday", 8.4m, 12),
        new("family", 7.9m, 40),
        new("thriller", 6.9m, 900),
        new("horror", 6.6m, 600),
    ];

    private const int RatingsCount = 1552;

    private static TasteFactsBuilder.GenreRanking Rank(
        IReadOnlyList<TasteFactsBuilder.GenreStat>? stats = null, int ratingsCount = RatingsCount) =>
        TasteFactsBuilder.RankGenres(stats ?? LiveShaped, ratingsCount);

    [Fact]
    public void What_the_account_actually_watches_leads_the_volume_list()
    {
        var watched = Rank().MostWatchedGenres.Select(g => g.Genre).ToList();

        // ORDER, not membership: the whole defect was an ordering that put a twelve-title genre
        // ahead of a nine-hundred-title one.
        Assert.Equal(["thriller", "horror", "family", "holiday"], watched);
    }

    [Fact]
    public void A_genre_rated_twelve_times_is_not_a_preference()
    {
        var top = Rank().TopGenres.Select(g => g.Genre).ToList();

        // 1% of 1,552 is 15, and holiday has 12. It is a high average over almost nothing.
        Assert.DoesNotContain("holiday", top);
        Assert.Contains("thriller", top);
        Assert.Contains("horror", top);
    }

    [Fact]
    public void Among_genres_that_clear_the_bar_affinity_still_decides_the_order()
    {
        // The bar removes noise; it does not replace affinity with volume. family (40 @ 7.9)
        // legitimately outranks thriller (900 @ 6.9) on how WELL they are rated — which is exactly
        // what "Rates highest" claims and nothing more.
        Assert.Equal(["family", "thriller", "horror"], Rank().TopGenres.Select(g => g.Genre));
    }

    [Fact]
    public void The_sparse_but_well_rated_bucket_is_what_is_left_over()
    {
        var ranking = Rank();
        var facts = Facts(ranking);

        // family clears the ratings bar AND is in the top six by volume, so it is a real
        // preference. holiday is in neither list, so it is nowhere — it cannot be claimed at all.
        Assert.Empty(facts.SparseButWellRated());
        Assert.DoesNotContain("holiday", facts.TopGenres.Select(g => g.Genre));
    }

    [Fact]
    public void A_genre_that_is_rated_well_and_watched_rarely_lands_in_the_qualified_bucket()
    {
        // Seven distinct genres, so the top six by volume cannot swallow the whole list, and one
        // genre that clears the ratings bar while sitting outside the volume top six.
        var stats = new TasteFactsBuilder.GenreStat[]
        {
            new("western", 8.4m, 60),
            new("thriller", 6.9m, 900),
            new("horror", 6.6m, 600),
            new("action", 6.5m, 500),
            new("drama", 6.4m, 400),
            new("comedy", 6.3m, 300),
            new("crime", 6.2m, 200),
        };
        var facts = Facts(TasteFactsBuilder.RankGenres(stats, 2960));

        Assert.Equal(["western"], facts.SparseButWellRated().Select(g => g.Genre));
        Assert.DoesNotContain("western", facts.MostWatchedGenres.Select(g => g.Genre));
    }

    [Fact]
    public void The_volume_share_is_a_share_of_the_accounts_whole_title_ratings()
    {
        var thriller = Rank().MostWatchedGenres.Single(g => g.Genre == "thriller");

        Assert.Equal(900, thriller.Count);
        Assert.Equal(Math.Round(900m / RatingsCount, 3), thriller.Share);
    }

    [Fact]
    public void The_bar_is_the_larger_of_fifteen_and_one_percent()
    {
        // Fifteen ratings is a real pool at two hundred and noise at ten thousand.
        Assert.Equal(15, TasteFactsBuilder.MinGenreRatings(0));
        Assert.Equal(15, TasteFactsBuilder.MinGenreRatings(1_000));
        Assert.Equal(20, TasteFactsBuilder.MinGenreRatings(2_044));
        Assert.Equal(100, TasteFactsBuilder.MinGenreRatings(10_000));
    }

    [Fact]
    public void An_account_too_young_for_the_bar_still_gets_an_affinity_list()
    {
        // Nothing clears fifteen. An empty list would strip the fingerprint of every genre
        // component and leave the narrative with nothing to say, which is worse than a thin list.
        var stats = new TasteFactsBuilder.GenreStat[]
        {
            new("horror", 8.1m, 4),
            new("comedy", 7.2m, 3),
        };
        var ranking = TasteFactsBuilder.RankGenres(stats, 7);

        Assert.Equal(["horror", "comedy"], ranking.TopGenres.Select(g => g.Genre));
        Assert.Equal(["horror", "comedy"], ranking.MostWatchedGenres.Select(g => g.Genre));
    }

    [Fact]
    public void An_account_with_no_ratings_produces_no_genres_and_does_not_divide_by_zero()
    {
        var ranking = TasteFactsBuilder.RankGenres([], 0);

        Assert.Empty(ranking.TopGenres);
        Assert.Empty(ranking.MostWatchedGenres);
    }

    // ── The fingerprint reads the same list, and only that list ──────────────────────────────

    [Fact]
    public void The_fingerprint_reads_top_genres_and_never_the_volume_list()
    {
        // TopGenres is the list TasteProfileHash.TopN was written against, and the volume list is
        // additive. If the volume list ever entered the fingerprint, every account would re-key on
        // deployment for a fact that says nothing new about their taste.
        var facts = Facts(Rank());
        var withDifferentVolumes = facts with
        {
            MostWatchedGenres = [new GenreVolumeFact("documentary", 4.0m, 3, 0.002m)],
        };

        Assert.Equal(
            TasteProfileHash.Compute(facts, [], "v1").Hash,
            TasteProfileHash.Compute(withDifferentVolumes, [], "v1").Hash);
    }

    [Fact]
    public void The_hysteresis_band_survives_the_new_ranking()
    {
        // The fingerprint's whole cache argument is that a component keeps its recorded value
        // until the live number has moved a FULL step. Restricting which genres reach the list
        // must not disturb that: the same genres, nudged within their band, must hash the same.
        var facts = Facts(Rank());
        var first = TasteProfileHash.Compute(facts, [], "v1");

        var nudged = facts with
        {
            TopGenres = facts.TopGenres.Select(g => g with { Affinity = g.Affinity + 0.2m }).ToList(),
        };
        var second = TasteProfileHash.Compute(nudged, [], "v1", first.Components);

        Assert.Equal(first.Hash, second.Hash);
    }

    private static TasteFacts Facts(TasteFactsBuilder.GenreRanking ranking) => new(
        UserMean: 6.75m,
        RatingsCount: RatingsCount,
        MovieRatedShare: 0.6m,
        TopGenres: ranking.TopGenres,
        MostWatchedGenres: ranking.MostWatchedGenres,
        DriftingToward: [],
        LovedAnchors: [],
        RecentLoves: [],
        RecentDislikes: [],
        DeclinedLine: null,
        VerifiedPeople: [],
        Texture: null,
        Contrarian: new ContrarianFact(0m, []),
        PreferenceTags: []);
}
