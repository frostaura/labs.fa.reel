using FrostAura.Reel.Api.Endpoints;

namespace FrostAura.Reel.Tests.Search;

/// <summary>
/// Pins the relevance tiebreak that keeps a typo'd LOOKUP pointing at the title the user meant.
///
/// Adding trigram matching to typeahead widened the candidate set: before it, every row was an
/// exact substring match, so re-sorting the survivors purely by predicted rating was harmless.
/// Afterwards a loosely-similar title could outrank the one actually typed — "spiderman" surfaced
/// Superman above Spider-Man, because Superman simply scored better for that account. Relevance
/// has to dominate for a lookup; the personal lens is the tiebreak within a tier.
/// </summary>
public class TypeaheadRelevanceTests
{
    [Theory]
    [InlineData("Spider-Man", "spiderman")]
    [InlineData("Inception", "inceptoin")]
    [InlineData("Breaking Bad", "breakin bad")]
    [InlineData("WALL·E", "wall e")]
    public void The_intended_title_scores_above_a_merely_similar_one(string intended, string typed)
    {
        // The distractors are real cases from the live catalogue that outranked the intended title
        // when predicted rating alone decided the order.
        string[] distractors = ["Superman", "Deception", "Breakdown", "Wallace & Gromit"];

        var intendedScore = SearchEndpoints.TitleSimilarity(intended, typed);
        foreach (var distractor in distractors)
        {
            Assert.True(
                intendedScore > SearchEndpoints.TitleSimilarity(distractor, typed),
                $"'{typed}' should score '{intended}' above '{distractor}'.");
        }
    }

    [Fact]
    public void An_exact_term_scores_higher_than_a_transposition()
    {
        Assert.True(
            SearchEndpoints.TitleSimilarity("Inception", "inception")
            > SearchEndpoints.TitleSimilarity("Inception", "inceptoin"));
    }

    [Fact]
    public void Punctuation_and_case_do_not_change_the_score()
    {
        // pg_trgm folds case and treats non-alphanumerics as separators, so "Spider-Man" and
        // "spider man" must be indistinguishable — that equivalence is what makes a hyphen-less
        // query find a hyphenated title at all.
        Assert.Equal(
            SearchEndpoints.TitleSimilarity("Spider-Man", "spiderman"),
            SearchEndpoints.TitleSimilarity("spider man", "spiderman"),
            precision: 10);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("!!!")]
    public void A_degenerate_name_scores_zero_rather_than_throwing(string? name)
    {
        Assert.Equal(0d, SearchEndpoints.TitleSimilarity(name, "inception"));
    }
}
