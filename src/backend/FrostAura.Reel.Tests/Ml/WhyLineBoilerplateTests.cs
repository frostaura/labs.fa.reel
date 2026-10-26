using FrostAura.Reel.Application.Ranking;
using FrostAura.Reel.Domain.Catalog;

namespace FrostAura.Reel.Tests.Ml;

/// <summary>
/// The why-line boilerplate the 2026-10-25 surface review measured (R1-13, R1-49): the contrarian
/// clause on 116 of 150 lines whatever the reader's contrarian score, "trending right now" on a
/// 2004 show from a popularity LEVEL, and crowd clauses quoting a raw rating with no vote floor.
/// Every gate is pinned on both sides of its threshold, and the share cap is measured over a rail.
/// </summary>
public class WhyLineBoilerplateTests
{
    // ── Contrarian clause: only for a reader genuinely harsher than the crowd ────────────────

    [Theory]
    [InlineData(null)]       // no taste state — nothing backs a claim about the reader
    [InlineData(0.4)]        // kinder than the crowd
    [InlineData(-0.2)]       // the founder: Taste DNA says "with the crowd"
    [InlineData(-0.3)]       // the DNA threshold itself is still "with the crowd"
    public void The_contrarian_clause_is_silent_unless_the_reader_is_harsher_than_the_crowd(double? offset)
    {
        foreach (var context in Variants([("contrarianAdjustedRating", 1.2f)], offset: (decimal?)offset))
        {
            var sentence = ExplanationTemplater.BuildSentence(context);

            Assert.False(ExplanationTemplater.IsContrarianFamily(sentence), sentence);
            Assert.DoesNotContain("contrarian", sentence, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("tougher", sentence, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Theory]
    [InlineData(-0.31)]
    [InlineData(-0.8)]
    public void A_harsher_reader_gets_the_clause_with_the_marked_down_number(double offset)
    {
        // Trakt 8.0 on 400 votes → badge 7.8; marked down by the reader's offset.
        var expected = 7.8m + Math.Round((decimal)offset, 1, MidpointRounding.AwayFromZero);
        foreach (var context in Variants([("contrarianAdjustedRating", 1.2f)], offset: (decimal)offset))
        {
            var sentence = ExplanationTemplater.BuildSentence(context);

            Assert.True(ExplanationTemplater.IsContrarianFamily(sentence), sentence);
            Assert.Contains(expected.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture), sentence);
            Assert.True(FeedWhyGuard.IsServable(sentence), sentence);
            Assert.True(FeedWhyGuard.Accepts(sentence), sentence);
        }
    }

    [Fact]
    public void The_contrarian_clause_needs_to_be_one_of_the_two_biggest_contributions()
    {
        // Master and Commander: era +1.0 (no clause), similarity +0.5, contrarian +0.1. The
        // contrarian contribution is third, so it does not get to explain the pick.
        foreach (var context in Variants(
                     [("decadeAffinity", 1.0f), ("simLovedCentroid", 0.5f), ("contrarianAdjustedRating", 0.1f)],
                     offset: -0.6m))
        {
            Assert.False(ExplanationTemplater.IsContrarianFamily(ExplanationTemplater.BuildSentence(context)));
        }

        // Second-biggest — even behind a feature with no clause — it is a real reason.
        foreach (var context in Variants(
                     [("decadeAffinity", 1.0f), ("contrarianAdjustedRating", 0.6f)], offset: -0.6m))
        {
            Assert.True(ExplanationTemplater.IsContrarianFamily(ExplanationTemplater.BuildSentence(context)));
        }
    }

    [Fact]
    public void The_contrarian_clause_never_praises_a_crowd_score_that_marks_down_below_seven()
    {
        // Trakt 7.5 on 400 votes → 7.39; a −0.5 reader reads it as 6.89, which is not "holds up".
        var title = Title(traktRating: 7.5m, traktVotes: 400);
        var sentence = ExplanationTemplater.BuildSentence(Context(title, [("contrarianAdjustedRating", 1.2f)], -0.5m));

        Assert.False(ExplanationTemplater.IsContrarianFamily(sentence), sentence);
    }

    // ── Crowd clauses: the badge's number, its source, and a vote floor ───────────────────────

    [Theory]
    [InlineData(49, false)]
    [InlineData(50, true)]
    [InlineData(5000, true)]
    public void Crowd_clauses_need_the_vote_floor(int tmdbVotes, bool fires)
    {
        var title = Title(tmdbAverage: 8.5m, tmdbVotes: tmdbVotes);
        foreach (var feature in new[] { "traktRating", "contrarianAdjustedRating" })
        {
            var sentence = ExplanationTemplater.BuildSentence(Context(title, [(feature, 1.2f)], -0.31m));

            Assert.Equal(fires, sentence != ExplanationTemplater.GenericFallback);
        }
    }

    [Fact]
    public void The_crowd_clause_quotes_the_badge_and_names_its_source()
    {
        // Trakt leads at ≥ 50 votes even when TMDB has more: badge 7.8 on Trakt, not 6.0 on TMDB.
        var traktFirst = Title(traktRating: 8.0m, traktVotes: 400, tmdbAverage: 6.0m, tmdbVotes: 4000);
        var traktLine = ExplanationTemplater.BuildSentence(Context(traktFirst, [("traktRating", 1f)]));
        Assert.Contains("7.8 on Trakt", traktLine);
        Assert.Equal(7.8m, CrowdRating.For(traktFirst));

        // Trakt below its lead threshold: the badge is TMDB's, so the line says TMDB.
        var tmdbOnly = Title(traktRating: 9.5m, traktVotes: 12, tmdbAverage: 8.2m, tmdbVotes: 900);
        var tmdbLine = ExplanationTemplater.BuildSentence(Context(tmdbOnly, [("traktRating", 1f)]));
        Assert.Contains("on TMDB", tmdbLine);
        Assert.Contains(CrowdRating.For(tmdbOnly)!.Value.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture), tmdbLine);
        Assert.DoesNotContain("9.5", tmdbLine);
    }

    [Fact]
    public void The_crowd_clause_does_not_call_an_average_crowd_backing()
    {
        // TMDB 7.0 on 400 votes shrinks to 6.94 — at the catalogue's average, not behind the pick.
        var title = Title(tmdbAverage: 7.0m, tmdbVotes: 400);

        Assert.Equal(ExplanationTemplater.GenericFallback, ExplanationTemplater.BuildSentence(Context(title, [("traktRating", 1f)])));
    }

    // ── "Trending": a popularity level is not a trend ─────────────────────────────────────────

    [Fact]
    public void Popularity_never_claims_trending()
    {
        foreach (var released in new DateTime?[] { new(2004, 11, 16), DateTime.UtcNow.AddDays(-3), null })
        {
            for (var i = 0; i < 8; i++)
            {
                for (var seed = 0; seed <= 3; seed++)
                {
                    var title = Title(traktRating: 8.0m, traktVotes: 400, id: i);
                    title.ReleasedAt = released;
                    var sentence = ExplanationTemplater.BuildSentence(Context(title, [("tmdbPopularityLog", 2.0f)]), seed);

                    Assert.DoesNotContain("trending", sentence, StringComparison.OrdinalIgnoreCase);
                    Assert.DoesNotContain("right now", sentence, StringComparison.OrdinalIgnoreCase);
                }
            }
        }
    }

    // ── The share cap ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_rail_of_harsh_reader_cards_holds_the_contrarian_family_to_a_quarter()
    {
        var taken = new HashSet<string>(StringComparer.Ordinal);
        var lines = Enumerable.Range(0, 12)
            .Select(i => ExplanationTemplater.BuildDistinctSentence(
                Context(
                    Title(traktRating: 8.0m, traktVotes: 400, id: i),
                    [("contrarianAdjustedRating", 1.2f), ("userGenreAffinity", 0.8f), ("simLovedCentroid", 0.5f)],
                    -0.6m),
                taken))
            .ToList();

        var family = lines.Count(ExplanationTemplater.IsContrarianFamily);
        Assert.InRange(family, 1, 3); // 25% of 12
        // The cards over the cap still say something true — the next clauses, never a blank.
        Assert.All(lines, l => Assert.NotEqual(string.Empty, l));
        Assert.All(lines.Where(l => !ExplanationTemplater.IsContrarianFamily(l)), l => Assert.Contains("drama", l, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_card_over_the_cap_keeps_its_own_next_reason()
    {
        // Two contrarian lines already shown in three: a third is over the cap, so the card drops
        // that clause and says what else moved its prediction — the crowd number, then the genre.
        var set = new HashSet<string>(StringComparer.Ordinal)
        {
            "Holds a 7.4 even on your harsher-than-the-crowd scale.",
            "Still a 7.3 once the crowd's 7.9 is marked down to your scale.",
            "Right in your comedy wheelhouse.",
        };

        var line = ExplanationTemplater.BuildDistinctSentence(
            Context(
                Title(traktRating: 8.4m, traktVotes: 900),
                [("contrarianAdjustedRating", 1.2f), ("traktRating", 1.0f), ("userGenreAffinity", 0.4f)],
                -0.5m),
            set);

        Assert.False(ExplanationTemplater.IsContrarianFamily(line), line);
        Assert.Contains("crowd", line, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("drama", line, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_founder_shaped_rail_carries_no_contrarian_line_at_all()
    {
        var taken = new HashSet<string>(StringComparer.Ordinal);
        var lines = Enumerable.Range(0, 21)
            .Select(i => ExplanationTemplater.BuildDistinctSentence(
                Context(
                    Title(traktRating: 8.4m, traktVotes: 3000, id: i),
                    [("decadeAffinity", 1.0f), ("contrarianAdjustedRating", 0.6f), ("simLovedCentroid", 0.3f)],
                    -0.2m),
                taken))
            .ToList();

        Assert.DoesNotContain(lines, ExplanationTemplater.IsContrarianFamily);
    }

    [Theory]
    [InlineData(0, 0, false)]  // the first line of a family always fits
    [InlineData(2, 0, false)]  // 1 of 3 — floor(3 × .25) = 0 → raised to 1
    [InlineData(3, 1, true)]   // second of 5 — allowed 1
    [InlineData(7, 1, false)]  // second of 9 — allowed 2
    [InlineData(7, 2, true)]   // third of 10 — allowed 2
    public void Exceeds_family_share_counts_the_lines_already_shown(int others, int inFamily, bool exceeds)
    {
        var taken = Enumerable.Range(0, others).Select(i => $"Right in your drama wheelhouse {i}.")
            .Concat(Enumerable.Range(0, inFamily).Select(i => $"Holds a 7.{i} even on your harsher-than-the-crowd scale."))
            .ToList();

        Assert.Equal(exceeds, ExplanationTemplater.ExceedsFamilyShare("Holds a 7.9 even on your harsher-than-the-crowd scale.", taken));
        Assert.False(ExplanationTemplater.ExceedsFamilyShare("Right in your drama wheelhouse.", taken));
    }

    // ── Persisted copies of the retired phrasings ─────────────────────────────────────────────

    [Theory]
    [InlineData("Scores well even by your contrarian standards.")]
    [InlineData("Right in your drama wheelhouse, and holds up even by your tougher-than-the-crowd standards.")]
    [InlineData("Trending right now.")]
    [InlineData("You rate Tony Gilroy 7.8 on average, and having a moment right now.")]
    public void A_persisted_retired_phrasing_is_not_served(string why)
    {
        Assert.True(FeedWhyGuard.IsRetiredBoilerplate(why));
        Assert.False(FeedWhyGuard.IsServable(why));
        Assert.Equal(string.Empty, FeedWhyGuard.Servable(why));
    }

    [Theory]
    [InlineData("Holds a 7.4 even on your harsher-than-the-crowd scale.")]
    [InlineData("The crowd backs it too (7.8 on Trakt).")]
    [InlineData("A thriller that keeps the crowd guessing right up to the last scene.")]
    [InlineData("Fresh off release, and right in your drama wheelhouse.")]
    public void Lines_outside_the_retired_phrasings_are_left_alone(string why) =>
        Assert.False(FeedWhyGuard.IsRetiredBoilerplate(why));

    // ── Helpers ───────────────────────────────────────────────────────────────────────────────

    /// <summary>The same context over eight titles, so every phrasing variant is exercised.</summary>
    private static IEnumerable<ExplanationTemplater.Context> Variants(
        (string Feature, float Contribution)[] contributions, decimal? offset) =>
        Enumerable.Range(0, 8).Select(i => Context(Title(traktRating: 8.0m, traktVotes: 400, id: i), contributions, offset));

    private static ExplanationTemplater.Context Context(
        Title title, (string Feature, float Contribution)[] contributions, decimal? offset = null) =>
        new(
            title,
            contributions,
            ExplanationTemplater.NoPersons,
            new HashSet<string> { "drama" },
            AnchorName: null,
            AccountContrarianOffset: offset);

    private static Title Title(
        decimal? traktRating = null, int traktVotes = 0, decimal? tmdbAverage = null, int tmdbVotes = 0, int id = 0) =>
        new()
        {
            Id = new Guid(id + 1, 7, 7, [1, 2, 3, 4, 5, 6, (byte)(id * 37), (byte)(id * 91)]),
            Name = "Sample",
            Year = 2003,
            Genres = ["drama"],
            TraktRating = traktRating,
            TraktVotes = traktVotes,
            TmdbVoteAverage = tmdbAverage,
            TmdbVoteCount = tmdbVotes,
        };
}
