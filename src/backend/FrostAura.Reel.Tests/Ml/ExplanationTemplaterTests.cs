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

    private static ExplanationTemplater.Context Context(
        (string Feature, float Contribution)[] contributions,
        IReadOnlyDictionary<CreditRole, ExplanationTemplater.PersonAffinity>? persons = null,
        Guid? titleId = null)
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
            AnchorName: null);
    }
}
