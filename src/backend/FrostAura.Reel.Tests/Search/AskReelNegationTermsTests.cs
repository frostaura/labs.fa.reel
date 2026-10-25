using FrostAura.Reel.Application.Search;
using FrostAura.Reel.Application.Search.AskReel;

namespace FrostAura.Reel.Tests.Search;

/// <summary>
/// How an exclusion term matches a title. The live "no superheroes please" turn showed eleven
/// superhero titles as picks because the plural never matched the singular label (A-9); a substring
/// rule let a standing "music" exclusion trip every "musical" (M NEW-3).
/// </summary>
public class AskReelNegationTermsTests
{
    [Theory]
    [InlineData("superheroes", "superhero")]
    [InlineData("ghosts", "ghost")]
    [InlineData("zombies", "zombie")]
    [InlineData("witches", "witch")]
    [InlineData("parodies", "parody")]
    [InlineData("superhero", "superheroes")]
    [InlineData("ghost", "ghosts")]
    [InlineData("parody", "parodies")]
    public void Singular_and_plural_are_the_same_term(string term, string other) =>
        Assert.Contains(other, NegationTerms.Forms(term));

    [Theory]
    [InlineData("superhero", "superheroes")]
    [InlineData("ghost story", "ghosts")]
    [InlineData("supernatural horror", "horror")]
    [InlineData("based on comic", "comic")]
    [InlineData("Female Ninja", "ninja")]
    public void A_term_matches_a_label_as_a_whole_phrase(string label, string term) =>
        Assert.True(NegationTerms.MatchesPhrase(label, term));

    [Theory]
    [InlineData("musical", "music")]
    [InlineData("warm hearted", "war")]
    [InlineData("ghostwriter", "ghost")]
    [InlineData("superheroine", "superhero")]
    public void A_term_never_matches_inside_another_word(string label, string term) =>
        Assert.False(NegationTerms.MatchesPhrase(label, term));

    [Fact]
    public void A_genre_slug_equals_the_term_in_either_number()
    {
        Assert.True(NegationTerms.EqualsAnyForm("superhero", "superheroes"));
        Assert.True(NegationTerms.EqualsAnyForm("Horror", "horror"));
        Assert.False(NegationTerms.EqualsAnyForm("horror", "thriller"));
        Assert.False(NegationTerms.EqualsAnyForm(null, "horror"));
    }

    /// <summary>
    /// The vocabulary gate the user's own negations pass through. "superheroes" used to singularise to
    /// "superheroe", fail the gate, and stop being a negation at all — the live turn's real cause.
    /// </summary>
    [Theory]
    [InlineData("superheroes")]
    [InlineData("zombies")]
    [InlineData("witches")]
    [InlineData("ghosts")]
    public void A_plural_of_a_negatable_word_is_negatable(string token) =>
        Assert.True(SearchVocabulary.IsNegatable(token));

    /// <summary>
    /// D6: a word that only looks plural generates nothing — "news" stripped to "new" made a standing
    /// "news" exclusion cull, airtight, every "new york city" label and "a new teacher" overview.
    /// </summary>
    [Theory]
    [InlineData("news", "new")]
    [InlineData("riches", "rich")]
    [InlineData("series", "sery")]
    [InlineData("christmas", "christma")]
    public void A_word_that_only_looks_plural_generates_no_other_form(string term, string absent)
    {
        Assert.Equal([term], NegationTerms.Forms(term));
        Assert.DoesNotContain(absent, NegationTerms.Forms(term));
        Assert.False(NegationTerms.AnyLabelMatches(["new york city"], "news"));
    }

    /// <summary>
    /// D6: free prose matches the term as typed only. "times" still reaches the label "time" (a tag's
    /// singular is the same tag) but never the word "time" in an overview.
    /// </summary>
    [Fact]
    public void Prose_matches_only_the_literal_term_while_labels_match_every_form()
    {
        Assert.True(NegationTerms.MatchesPhrase("time travel", "times"));
        Assert.False(NegationTerms.MatchesPhrase("Once upon a time in the west", "times", literalOnly: true));
        Assert.True(NegationTerms.MatchesPhrase("A tale of two times", "times", literalOnly: true));
        Assert.False(NegationTerms.MatchesPhrase("A superhero film", "superheroes", literalOnly: true));
        Assert.True(NegationTerms.MatchesPhrase("superhero", "superheroes"));
    }

    [Fact]
    public void Blank_terms_match_nothing()
    {
        Assert.Empty(NegationTerms.Forms("  "));
        Assert.False(NegationTerms.MatchesPhrase("anything at all", " "));
        Assert.False(NegationTerms.AnyLabelMatches(null, "horror"));
    }
}
