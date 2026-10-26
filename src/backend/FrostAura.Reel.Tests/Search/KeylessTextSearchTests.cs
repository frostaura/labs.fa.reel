using FrostAura.Reel.Application.Search.Retrieval;
using FrostAura.Reel.Application.Search.Retrieval.Concepts;

namespace FrostAura.Reel.Tests.Search;

/// <summary>
/// The typo-tolerance and unit-splitting contract behind keyless search (task 12): the exact
/// misspellings users actually type must land on the concept ("medevil" → "medieval"), unrelated words
/// must not be folded into concepts they don't mean, and a named phrase stays one unit.
/// </summary>
public class KeylessTextSearchTests
{
    [Theory]
    [InlineData("medevil", "medieval", true)]   // the founder's own query
    [InlineData("medieval", "medieval", true)]
    [InlineData("mediival", "medieval", true)]
    [InlineData("funy", "funny", true)]
    [InlineData("scarry", "scary", true)]
    [InlineData("romantik", "romantic", true)]
    [InlineData("zombe", "zombie", true)]
    [InlineData("dark", "dragon", false)]       // unrelated words stay apart
    [InlineData("war", "western", false)]
    [InlineData("space", "scary", false)]
    public void Edit_distance_tolerance_matches_what_users_mean(string typed, string concept, bool shouldMatch)
    {
        var distance = KeylessTextSearch.EditDistance(typed, concept);
        var threshold = concept.Length >= 8 ? 3 : concept.Length >= 5 ? 2 : 1;

        Assert.Equal(shouldMatch, distance <= threshold);
    }

    [Fact]
    public void Edit_distance_is_symmetric_and_zero_on_identity()
    {
        Assert.Equal(0, KeylessTextSearch.EditDistance("heist", "heist"));
        Assert.Equal(
            KeylessTextSearch.EditDistance("medevil", "medieval"),
            KeylessTextSearch.EditDistance("medieval", "medevil"));
    }

    [Fact]
    public void Edit_distance_short_circuits_on_large_length_gap()
    {
        Assert.Equal(int.MaxValue, KeylessTextSearch.EditDistance("war", "documentary"));
    }

    private static readonly IReadOnlyList<VocabularyLabel> Vocabulary =
    [
        new("medieval", 120), new("time loop", 40), new("devil", 300), new("heist", 90), new("medical", 80),
    ];

    [Theory]
    [InlineData("medevil", "medieval")]
    [InlineData("funy", "funny")]
    [InlineData("scarry", "scary")]
    [InlineData("comdy", "comedy")]
    [InlineData("hiest", "heist")]
    public void A_word_that_reaches_nothing_is_corrected_to_what_the_user_meant(string typed, string expected)
    {
        Assert.Equal(expected, KeylessTextSearch.Correct(typed, Vocabulary));
    }

    [Theory]
    [InlineData("war")]          // too short to correct
    [InlineData("evil")]         // "devil" is one edit away but starts differently
    [InlineData("zzzzzz")]
    public void A_word_is_never_folded_into_something_it_does_not_mean(string typed)
    {
        Assert.Null(KeylessTextSearch.Correct(typed, Vocabulary));
    }

    [Fact]
    public void A_named_phrase_stays_one_unit_and_stop_words_go()
    {
        Assert.Equal(["time loop", "medieval"], KeylessTextSearch.Units("something with a time loop and medieval", Vocabulary));
        Assert.Equal(["feel good", "heist"], KeylessTextSearch.Units("a feel good heist movie please", Vocabulary));
        Assert.Equal(["survival at sea"], KeylessTextSearch.Units("survival at sea", Vocabulary));
        Assert.Empty(KeylessTextSearch.Units("something to watch tonight", Vocabulary));
    }
}
