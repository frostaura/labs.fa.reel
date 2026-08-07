using FrostAura.Reel.Application.Ranking;
using FrostAura.Reel.Domain.Catalog;

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

        // The clause is skipped entirely — with nothing else to say, only the neutral floor remains.
        Assert.Equal("Strong fit with your taste profile.", sentence);
    }

    [Fact]
    public void Skipping_an_unverified_person_clause_falls_through_to_the_next_contribution()
    {
        var context = Context(contributions: [("castAffinity", 1.5f), ("tmdbPopularityLog", 0.4f)]);

        var sentence = ExplanationTemplater.BuildSentence(context);

        Assert.DoesNotContain("cast", sentence, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("right now", sentence); // the popularity clause took its place
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
        };
        return new ExplanationTemplater.Context(
            title,
            contributions,
            persons ?? ExplanationTemplater.NoPersons,
            new HashSet<string> { "drama" },
            anchorName,
            sharedGenre,
            tone);
    }
}
