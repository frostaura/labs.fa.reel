using FrostAura.Reel.Application.Search.Typeahead;

namespace FrostAura.Reel.Tests.Search;

/// <summary>
/// The lookup's tiers (C-8, C*3): a name the user typed in full beats one that starts with it, which
/// beats one where it starts a word, which beats a substring or a look-alike — "heat" must find Heat
/// before Cheaters, "godfather" The Godfather before Godfather of Harlem. The SQL ranks by the same
/// rules; these pin the in-memory twin and the literal-input helpers.
/// </summary>
public class TypeaheadRelevanceTests
{
    [Theory]
    [InlineData("Heat", "heat", MatchTier.Exact)]
    [InlineData("The Godfather", "godfather", MatchTier.Exact)]
    [InlineData("An Education", "education", MatchTier.Exact)]
    [InlineData("Godfather of Harlem", "godfather", MatchTier.Prefix)]
    [InlineData("The Godfather Part II", "godfather", MatchTier.Prefix)]
    [InlineData("Heat Wave", "heat", MatchTier.Prefix)]
    [InlineData("Christopher Nolan", "nolan", MatchTier.WordStart)]
    [InlineData("In the Heat of the Night", "heat", MatchTier.WordStart)]
    [InlineData("Cheaters", "heat", MatchTier.Fuzzy)]
    [InlineData("Theater Camp", "heat", MatchTier.Fuzzy)]
    [InlineData("", "heat", MatchTier.Fuzzy)]
    [InlineData("Amélie", "amelie", MatchTier.Exact)]
    [InlineData("Whitstable Pearl", "nolan", MatchTier.Fuzzy)]
    public void Tiers_order_exact_prefix_word_start_then_fuzzy(string name, string term, MatchTier expected) =>
        Assert.Equal(expected, TypeaheadSearch.TierOf(name, term));

    [Fact]
    public void Case_and_surrounding_space_do_not_change_the_tier()
    {
        Assert.Equal(MatchTier.Exact, TypeaheadSearch.TierOf("ALIEN", "  Alien "));
        Assert.Equal(MatchTier.Exact, TypeaheadSearch.TierOf("Alien", "alien"));
    }

    [Theory]
    [InlineData("%", "\\%")]
    [InlineData("100%", "100\\%")]
    [InlineData("a_b", "a\\_b")]
    [InlineData("back\\slash", "back\\\\slash")]
    [InlineData("heat", "heat")]
    public void Like_input_is_escaped_so_it_matches_literally(string term, string escaped) =>
        Assert.Equal(escaped, TypeaheadSearch.EscapeLike(term));

    [Fact]
    public void The_word_start_pattern_escapes_regex_metacharacters()
    {
        Assert.Equal(@"\mnolan", TypeaheadSearch.WordStartPattern("nolan"));
        Assert.Equal(@"\m50\.\(\)", TypeaheadSearch.WordStartPattern("50.()"));
    }
}
