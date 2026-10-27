using FrostAura.Reel.Application.Ranking;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Feed;

namespace FrostAura.Reel.Tests.Ml;

/// <summary>
/// The honest-explanations floor. Two properties are pinned: a person-affinity contribution with
/// no VERIFIED user-rated person says nothing at all (the old generic lines — "the cast overlaps
/// your favourites", "the director is in your top tier" — asserted overlaps the card could not
/// substantiate), and phrasing variants are picked deterministically per title so rails vary their
/// sentence shape without any card ever changing its own sentence between renders.
/// </summary>
public class ExplanationTemplaterTests
{
    [Theory]
    [InlineData("castAffinity")]
    [InlineData("lovedCastCount")]
    [InlineData("directorAffinity")]
    [InlineData("writerAffinity")]
    public void An_unverifiable_person_affinity_claims_nothing(string feature)
    {
        var context = Context(contributions: [(feature, 1.2f)]);

        var sentence = ExplanationTemplater.BuildSentence(context);

        // The clause is skipped entirely — and with nothing else to say, the card says nothing.
        Assert.Equal(string.Empty, sentence);
    }

    [Fact]
    public void Skipping_an_unverified_person_clause_falls_through_to_the_next_contribution()
    {
        var context = Context(contributions: [("castAffinity", 1.5f), ("traktRating", 0.4f)]);

        var sentence = ExplanationTemplater.BuildSentence(context);

        Assert.DoesNotContain("cast", sentence, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("crowd", sentence); // the crowd clause took its place
    }

    [Fact]
    public void A_verified_person_is_still_named_with_their_affinity()
    {
        var persons = new Dictionary<CreditRole, ExplanationTemplater.PersonAffinity>
        {
            [CreditRole.Actor] = new("Frances McDormand", 8.7m),
        };
        var context = Context(contributions: [("castAffinity", 1.5f)], persons: persons);

        var sentence = ExplanationTemplater.BuildSentence(context);

        Assert.Contains("Frances McDormand", sentence);
        Assert.Contains("8.7", sentence);
    }

    [Fact]
    public void The_same_title_always_gets_the_same_sentence()
    {
        var id = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        var first = ExplanationTemplater.BuildSentence(Context([("simLovedCentroid", 0.9f)], titleId: id));
        var second = ExplanationTemplater.BuildSentence(Context([("simLovedCentroid", 0.9f)], titleId: id));

        Assert.Equal(first, second);
    }

    [Fact]
    public void Different_titles_spread_across_phrasing_variants()
    {
        // 40 distinct ids must land on more than one variant of the same clause — a rail of cards
        // sharing one top contribution should not repeat a single sentence shape down the page.
        var sentences = Enumerable.Range(0, 40)
            .Select(i => ExplanationTemplater.BuildSentence(Context(
                [("simLovedCentroid", 0.9f)],
                titleId: new Guid(i + 1, 0, 0, [0, 0, 0, 0, 0, 0, 0, 0]))))
            .Distinct()
            .ToList();

        Assert.True(sentences.Count >= 2, $"expected multiple phrasings, got only: {sentences[0]}");
    }

    [Fact]
    public void An_anchored_sentence_never_opens_with_because_you_loved()
    {
        var context = Context([("simLovedCentroid", 0.9f)], anchorName: "Hokum");

        for (var seed = 0; seed <= 3; seed++)
        {
            var sentence = ExplanationTemplater.BuildSentence(context, seed);
            Assert.False(
                sentence.StartsWith("Because you loved", StringComparison.OrdinalIgnoreCase),
                $"seed {seed} produced the banned opener: {sentence}");
            Assert.Contains("Hokum", sentence);
        }
    }

    [Fact]
    public void An_anchor_clause_cites_the_genuinely_shared_genre()
    {
        var context = Context([("simLovedCentroid", 0.9f)], anchorName: "Hokum", sharedGenre: "science-fiction");

        var sentence = ExplanationTemplater.BuildSentence(context);

        Assert.Contains("science fiction", sentence);
        Assert.Contains("Hokum", sentence);
    }

    [Fact]
    public void An_anchor_clause_prints_a_tone_only_from_the_closed_vocabulary()
    {
        // A closed-vocab tone is citable…
        var clean = ExplanationTemplater.BuildSentence(
            Context([("simLovedCentroid", 0.9f)], anchorName: "Hokum", tone: "brooding"));
        Assert.Contains("brooding", clean);

        // …a polluted pre-enforcement label (the 2026-08-07 vocabulary incident) is never rendered.
        var polluted = ExplanationTemplater.BuildSentence(
            Context([("simLovedCentroid", 0.9f)], anchorName: "Hokum", tone: "peak-2000s-nostalgia"));
        Assert.DoesNotContain("peak-2000s-nostalgia", polluted);
        Assert.Contains("Hokum", polluted); // the anchor clause still fires, just without the tone
    }

    [Fact]
    public void A_director_clause_carries_the_users_average_for_that_person()
    {
        var persons = new Dictionary<CreditRole, ExplanationTemplater.PersonAffinity>
        {
            [CreditRole.Director] = new("Tony Gilroy", 7.8m),
        };
        var context = Context([("directorAffinity", 1.5f)], persons: persons);

        var sentence = ExplanationTemplater.BuildSentence(context);

        Assert.Contains("Tony Gilroy", sentence);
        Assert.Contains("7.8", sentence);
    }

    [Fact]
    public void Variant_seeds_produce_distinct_sentences()
    {
        var context = Context([("simLovedCentroid", 0.9f)], anchorName: "Hokum", sharedGenre: "drama", tone: "tense");

        var canonical = ExplanationTemplater.BuildSentence(context);
        var bumped = ExplanationTemplater.BuildSentence(context, 1);

        Assert.NotEqual(canonical, bumped);
    }

    [Fact]
    public void Two_same_anchor_cards_with_identical_inputs_get_distinct_whys()
    {
        // The founder's complaint verbatim: five straight cards, one identical sentence. Two cards
        // with the SAME anchor and the same verifiable facts must still not render identical text.
        var context = Context([("simLovedCentroid", 0.9f)], anchorName: "Hokum", sharedGenre: "drama");
        var taken = new HashSet<string>(StringComparer.Ordinal);

        var first = ExplanationTemplater.BuildDistinctSentence(context, taken);
        var second = ExplanationTemplater.BuildDistinctSentence(context, taken);

        Assert.NotEqual(first, second);
    }

    // ── Sentence integrity (live regression, 2026-08-10) ─────────────────────────────────────
    // "you rate David Leslie Johnson-McGoldrick 7.2 on average." and "written by Stephen King,
    // who you rate 6.8." shipped as whole whys: clause fragments rendered alone without sentence
    // casing. Every emitted sentence must start with a capital and stand alone.

    [Fact]
    public void A_standalone_person_clause_is_a_capitalized_full_sentence()
    {
        var persons = new Dictionary<CreditRole, ExplanationTemplater.PersonAffinity>
        {
            [CreditRole.Writer] = new("Stephen King", 6.8m),
        };

        for (var seed = 0; seed <= 3; seed++)
        {
            var sentence = ExplanationTemplater.BuildSentence(
                Context([("writerAffinity", 1.5f)], persons: persons), seed);

            Assert.True(char.IsUpper(sentence[0]), $"seed {seed} produced a lowercase opener: '{sentence}'");
            Assert.EndsWith(".", sentence);
            Assert.Contains("Stephen King", sentence);
        }
    }

    [Fact]
    public void Every_clause_variant_starts_with_a_capital_letter_when_standalone()
    {
        string[] features =
        [
            "castAffinity", "lovedCastCount", "directorAffinity", "writerAffinity",
            "userGenreAffinity", "genreDrift", "decadeAffinity", "traktRating",
            "tmdbPopularityLog", "releaseAgeLog", "contrarianAdjustedRating",
            "simLovedCentroid", "simRecentCentroid", "genre:drama", "tone:brooding",
        ];
        var persons = new Dictionary<CreditRole, ExplanationTemplater.PersonAffinity>
        {
            [CreditRole.Actor] = new("Frances McDormand", 8.7m),
            [CreditRole.Director] = new("Tony Gilroy", 7.8m),
            [CreditRole.Writer] = new("Stephen King", 6.8m),
        };

        foreach (var feature in features)
        {
            // Distinct title ids walk the variant pick, and seeds walk it further — between them
            // every phrasing variant of every clause is exercised as the sentence opener.
            for (var i = 0; i < 8; i++)
            {
                for (var seed = 0; seed <= 3; seed++)
                {
                    var sentence = ExplanationTemplater.BuildSentence(Context(
                        [(feature, 1.0f)], persons: persons,
                        titleId: new Guid(i + 1, 0, 0, [0, 0, 0, 0, 0, 0, 0, 0])), seed);

                    // A feature with no clause by design (decadeAffinity, tmdbPopularityLog) leaves
                    // the card with no line at all — nothing to capitalise.
                    if (sentence.Length == 0)
                    {
                        Assert.Contains(feature, new[] { "decadeAffinity", "tmdbPopularityLog" });
                        continue;
                    }

                    Assert.True(
                        char.IsUpper(sentence[0]),
                        $"{feature} seed {seed} title {i} produced a lowercase opener: '{sentence}'");
                    Assert.EndsWith(".", sentence);
                }
            }
        }
    }

    // ── Grammar (R1-21, 2026-10-27) ──────────────────────────────────────────────────────────
    // The catalogue showed "Holds a 8.0", "Still a 8.0" and "Like Heat, it plays crime brooding".

    [Theory]
    [InlineData("8.0", "an")]
    [InlineData("8.5", "an")]
    [InlineData("80", "an")]
    [InlineData("800", "an")]
    [InlineData("11", "an")]
    [InlineData("18", "an")]
    [InlineData("11,000", "an")]
    [InlineData("18500000", "an")]
    [InlineData("7.5", "a")]
    [InlineData("1.0", "a")]
    [InlineData("10.0", "a")]
    [InlineData("110", "a")]
    [InlineData("1,100", "a")]
    [InlineData("action", "an")]
    [InlineData("thriller", "a")]
    public void The_article_follows_how_the_word_or_number_is_read(string word, string article)
    {
        Assert.Equal(article, ExplanationTemplater.IndefiniteArticle(word));
    }

    /// <summary>
    /// The contrarian clause quotes the reader-scale number with the article it is read with: a crowd
    /// of 9.0 marked down by 0.5 is "an 8.5", a crowd of 8.0 marked down is "a 7.3". Both phrasings,
    /// every variant the seed and title can pick, and the guard agrees with the sentence.
    /// </summary>
    [Theory]
    [InlineData(9.0, 50_000, "an 8.")]
    [InlineData(8.0, 400, "a 7.")]
    public void The_contrarian_clause_reads_an_eight_and_a_seven(double crowd, int votes, string expected)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < 8; i++)
        {
            for (var seed = 0; seed <= 3; seed++)
            {
                var context = Context([("contrarianAdjustedRating", 1.0f)], titleId: new Guid(i + 1, 0, 0, [0, 0, 0, 0, 0, 0, 0, 0]));
                context = context with { Title = WithCrowd(context.Title, (decimal)crowd, votes) };
                var sentence = ExplanationTemplater.BuildSentence(context, seed);
                Assert.Contains(expected, sentence, StringComparison.Ordinal);
                Assert.False(FeedWhyGuard.HasWrongArticle(sentence), sentence);
                seen.Add(sentence[..5]);
            }
        }

        Assert.Equal(2, seen.Count); // both phrasings ("Holds", "Still") were exercised
    }

    /// <summary>
    /// The anchored shared-genre-and-tone line reads as English in every variant: the tone qualifies
    /// the genre ("Brooding crime, just like Heat") instead of trailing it ("it plays crime brooding").
    /// </summary>
    [Fact]
    public void The_anchor_line_puts_the_tone_before_the_genre()
    {
        var lines = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < 12; i++)
        {
            for (var seed = 0; seed <= 3; seed++)
            {
                lines.Add(ExplanationTemplater.BuildSentence(
                    Context([("simLovedCentroid", 0.9f)], titleId: new Guid(i + 1, 0, 0, [0, 0, 0, 0, 0, 0, 0, 0]),
                        anchorName: "Heat", sharedGenre: "crime", tone: "brooding"),
                    seed));
            }
        }

        Assert.DoesNotContain(lines, l => l.Contains("it plays", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("Brooding crime, just like Heat", StringComparison.Ordinal));
        Assert.All(lines, l => Assert.False(FeedWhyGuard.HasWrongArticle(l), l));
    }

    [Theory]
    [InlineData("Holds a 8.0 even on your harsher-than-the-crowd scale.")]
    [InlineData("Brooding crime, just like Heat, and still a 8.5 once the crowd's 9.0 is marked down to your scale.")]
    [InlineData("A 11-part miniseries in the lane you love.")]
    public void A_persisted_a_before_an_eight_is_refused(string why)
    {
        Assert.True(FeedWhyGuard.HasWrongArticle(why));
        Assert.False(FeedWhyGuard.IsServable(why));
    }

    [Theory]
    [InlineData("Holds a 7.5 even on your harsher-than-the-crowd scale.")]
    [InlineData("Still a 10.0 once the crowd's 10.0 is marked down to your scale.")]
    [InlineData("A 110-minute heist, and you rate Tony Gilroy 8.1.")]
    public void A_before_a_consonant_sounding_number_is_kept(string why)
    {
        Assert.False(FeedWhyGuard.HasWrongArticle(why));
    }

    private static Title WithCrowd(Title title, decimal rating, int votes)
    {
        title.TraktRating = rating;
        title.TraktVotes = votes;
        title.TmdbVoteAverage = rating;
        title.TmdbVoteCount = votes;
        return title;
    }

    // ── No stub, ever (IREV1-SYS-05 / IR1S-01, 2026-10-26) ───────────────────────────────────
    // The generic sentence came back on 16 of 169 keyless why-lines: 12 alone, and 4 spliced after an
    // anchor clause as "…, and Strong fit with your taste profile." with its capital S mid-sentence.

    public static TheoryData<string, float> NothingCitable => new()
    {
        { "decadeAffinity", 1.4f },        // the retired era template: no clause by design
        { "tmdbPopularityLog", 0.9f },     // a popularity level is not a trend: no clause
        { "directorAffinity", 0.27f },     // positive, but no verified director to name
        { "writerAffinity", 0.17f },       // positive, but no verified writer to name
        { "userGenreAffinity", -0.6f },    // negative: never a reason on a pick
        { "era:timeless", 0.8f },          // the era one-hots have no clause
    };

    [Theory]
    [MemberData(nameof(NothingCitable))]
    public void An_unanchored_card_with_nothing_citable_says_nothing(string feature, float contribution)
    {
        for (var seed = 0; seed < 4; seed++)
        {
            Assert.Equal(string.Empty, ExplanationTemplater.BuildSentence(Context([(feature, contribution)]), seed));
        }

        Assert.Equal(string.Empty, ExplanationTemplater.BuildSentence(Context([])));
    }

    [Theory]
    [MemberData(nameof(NothingCitable))]
    public void An_anchored_card_with_nothing_citable_says_its_anchor_link_alone(string feature, float contribution)
    {
        // Vary the anchor side too: the admitting evidence of each kind, then the softer genre/tone
        // link, then nothing at all — every one must stand alone as a whole sentence.
        var anchorShapes = new (IReadOnlyList<SharedEvidence>? Evidence, string? Genre, string? Tone)[]
        {
            ([new SharedEvidence("person", "Arthur Conan Doyle", "writer")], null, null),
            ([new SharedEvidence("person", "Peter Weir", "director")], null, null),
            ([new SharedEvidence("person", "Russell Crowe", "cast")], null, null),
            ([new SharedEvidence("keyword", "naval warfare")], null, null),
            ([new SharedEvidence("theme", "magic")], null, null),
            (null, "action", "gritty"),
            (null, "drama", null),
            (null, null, null),
        };

        for (var i = 0; i < 40; i++)
        {
            var titleId = new Guid(i, 7, 9, 1, 2, 3, 4, 5, 6, 7, (byte)(i * 37));
            foreach (var (evidence, genre, tone) in anchorShapes)
            {
                for (var seed = 0; seed < 4; seed++)
                {
                    var context = Context([(feature, contribution)], titleId: titleId, anchorName: "Sherlock",
                        sharedGenre: genre, tone: tone) with
                    {
                        AnchorEvidence = evidence,
                    };
                    var sentence = ExplanationTemplater.BuildSentence(context, seed);

                    Assert.Contains("Sherlock", sentence);
                    Assert.DoesNotContain("taste profile", sentence, StringComparison.OrdinalIgnoreCase);
                    Assert.DoesNotContain(", and ", sentence, StringComparison.Ordinal);
                    Assert.True(char.IsUpper(sentence[0]), sentence);
                    Assert.EndsWith(".", sentence);
                    Assert.False(FeedWhyGuard.IsStub(sentence), sentence);
                }
            }
        }
    }

    [Fact]
    public void The_writer_evidence_card_from_the_review_reads_as_its_anchor_clause()
    {
        // The exact card IR1S-01 saw: a Sherlock rail member admitted on Conan Doyle, no clause.
        var context = Context([("directorAffinity", 0.2f)], anchorName: "Sherlock: The Abominable Bride")
            with
        {
            AnchorEvidence = [new SharedEvidence("person", "Arthur Conan Doyle", "writer")],
        };

        var sentences = Enumerable.Range(0, 2).Select(seed => ExplanationTemplater.BuildSentence(context, seed)).ToHashSet();

        Assert.Equal(
            new HashSet<string>
            {
                "Written by Arthur Conan Doyle, like Sherlock: The Abominable Bride.",
                "Same writer as Sherlock: The Abominable Bride: Arthur Conan Doyle.",
            },
            sentences);
    }

    [Fact]
    public void A_card_with_a_real_clause_still_gets_it_after_the_anchor()
    {
        var context = Context([("traktRating", 0.4f)], anchorName: "Sherlock")
            with
        {
            AnchorEvidence = [new SharedEvidence("keyword", "detective")],
        };

        var sentence = ExplanationTemplater.BuildSentence(context);

        Assert.Contains("Sherlock", sentence);
        Assert.Contains(", and ", sentence);
        Assert.Contains("7.8 on Trakt", sentence);
    }

    [Fact]
    public void An_empty_line_is_never_counted_as_a_line_shown()
    {
        // Two silent cards on one rail: neither is a "repeat" of the other, and neither counts toward
        // a phrase family's share of what the rail shows.
        var taken = new HashSet<string>(StringComparer.Ordinal);

        Assert.Equal(string.Empty, ExplanationTemplater.BuildDistinctSentence(Context([("decadeAffinity", 1f)]), taken));
        Assert.Equal(string.Empty, ExplanationTemplater.BuildDistinctSentence(
            Context([("tmdbPopularityLog", 1f)], titleId: Guid.NewGuid()), taken));
        Assert.Empty(taken);
    }

    [Fact]
    public void A_line_never_cites_a_rank_four_push_behind_two_clause_less_ones()
    {
        // Master and Commander on the fresh build: era +1.0 and the popularity level +0.7 have no
        // clause, so the sentence fell through to feels-like-your-favourites at +0.058 and the crowd at
        // +0.044 — "In the same lane as the titles you love, and the crowd backs it too". Neither moved
        // the number; the card says nothing rather than cite them.
        var context = Context([
            ("decadeAffinity", 1.0f), ("tmdbPopularityLog", 0.7f), ("simTopLovedMean", 0.058f), ("traktRating", 0.044f),
        ]);

        Assert.Equal(string.Empty, ExplanationTemplater.BuildSentence(context));
    }

    [Theory]
    [InlineData("simTopLovedMean", 0.15f, true)]
    [InlineData("simTopLovedMean", 0.149f, false)]
    [InlineData("traktRating", 0.2f, true)]
    [InlineData("traktRating", 0.088f, false)]
    [InlineData("castAffinity", 0.06f, false)]
    [InlineData("castAffinity", 0.31f, true)]
    public void A_push_outside_the_top_two_is_citable_only_from_the_floor_up(string feature, float value, bool cited)
    {
        var persons = new Dictionary<CreditRole, ExplanationTemplater.PersonAffinity>
        {
            [CreditRole.Actor] = new("Russell Crowe", 7.9m),
        };
        var context = Context([("decadeAffinity", 1.0f), ("tmdbPopularityLog", 0.7f), (feature, value)], persons: persons);

        Assert.Equal(cited, ExplanationTemplater.BuildSentence(context).Length > 0);
        Assert.Equal(cited, ExplanationTemplater.IsCitable(context.Contributions, 2));
    }

    [Fact]
    public void A_small_push_that_is_one_of_the_two_largest_positive_ones_is_still_a_reason()
    {
        // Negatives are not reasons and take no top-two seat: behind one clause-less push and a large
        // demotion, a +0.05 crowd term is the second-biggest thing pushing the number UP.
        var context = Context([("decadeAffinity", 1.0f), ("dislikedSimilarity", -0.8f), ("traktRating", 0.05f)]);

        Assert.Contains("crowd", ExplanationTemplater.BuildSentence(context), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Citability_ranks_by_value_whatever_order_the_caller_passes()
    {
        (string, float)[] contributions =
            [("simTopLovedMean", 0.04f), ("traktRating", 0.05f), ("decadeAffinity", 1.0f), ("castAffinity", -0.3f)];

        Assert.False(ExplanationTemplater.IsCitable(contributions, 0)); // third-largest positive, under the floor
        Assert.True(ExplanationTemplater.IsCitable(contributions, 1));
        Assert.True(ExplanationTemplater.IsCitable(contributions, 2));
        Assert.False(ExplanationTemplater.IsCitable(contributions, 3)); // negative: never a reason
    }

    [Fact]
    public void Equal_small_pushes_give_the_top_two_seats_to_the_earlier_entries()
    {
        (string, float)[] contributions = [("a", 0.05f), ("b", 0.05f), ("c", 0.05f)];

        Assert.True(ExplanationTemplater.IsCitable(contributions, 0));
        Assert.True(ExplanationTemplater.IsCitable(contributions, 1));
        Assert.False(ExplanationTemplater.IsCitable(contributions, 2));
    }

    private static ExplanationTemplater.Context Context(
        (string Feature, float Contribution)[] contributions,
        IReadOnlyDictionary<CreditRole, ExplanationTemplater.PersonAffinity>? persons = null,
        Guid? titleId = null,
        string? anchorName = null,
        string? sharedGenre = null,
        string? tone = null)
    {
        var title = new Title
        {
            Id = titleId ?? Guid.Parse("12345678-1234-1234-1234-123456789012"),
            Name = "Sample",
            Year = 1994,
            Genres = ["drama"],
            // Set so the crowd-rating, contrarian and fresh-release clauses are exercisable by the
            // sentence-integrity sweep: a real crowd (≥ 50 votes, badge 7.8) the clauses may quote.
            TraktRating = 8.0m,
            TraktVotes = 400,
            ReleasedAt = DateTime.UtcNow.AddDays(-10),
        };
        return new ExplanationTemplater.Context(
            title,
            contributions,
            persons ?? ExplanationTemplater.NoPersons,
            new HashSet<string> { "drama" },
            anchorName,
            sharedGenre,
            tone,
            // A reader harsher than the crowd, so the contrarian clause is reachable in the sweep.
            AccountContrarianOffset: -0.5m);
    }
}
