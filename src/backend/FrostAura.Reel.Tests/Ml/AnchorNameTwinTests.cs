using FrostAura.Reel.Application.Ranking;

namespace FrostAura.Reel.Tests.Ml;

/// <summary>
/// HREV2-RET-05: which neighbours are neighbours by NAME — the embedding text leads with it — and so
/// do not get to set an anchor's similarity floor.
/// </summary>
public class AnchorNameTwinTests
{
    [Theory]
    [InlineData("House", "Life as a House", true)]
    [InlineData("House", "Hell House LLC", true)]
    [InlineData("House", "Dream House", true)]
    [InlineData("The Dark Knight", "The Dark Knight Rises", true)]
    [InlineData("Grey's Anatomy", "Grey's Anatomy: B-Team", true)]
    [InlineData("It", "It Follows", true)]
    [InlineData("Heat", "heat", true)] // case-blind
    [InlineData("Life as a House", "House", true)] // either way round
    [InlineData("House", "Madhouse", false)] // a word inside a longer word is not the word
    [InlineData("House", "The Real Housewives of New Jersey", false)]
    [InlineData("Heat", "Heathers", false)]
    [InlineData("House", "Grey's Anatomy", false)]
    [InlineData("The Dark Knight", "Knight and Day", false)] // a shared word is not the run
    [InlineData("", "House", false)]
    [InlineData(null, "House", false)]
    [InlineData("!!!", "House", false)]
    public void A_name_twin_contains_the_other_name_as_a_run_of_whole_words(string? anchor, string? other, bool twin) =>
        Assert.Equal(twin, AnchorRowGate.IsNameTwin(anchor, other));
}
