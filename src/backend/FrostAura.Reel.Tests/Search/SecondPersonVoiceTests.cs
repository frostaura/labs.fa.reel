using FrostAura.Reel.Domain.Ports;

namespace FrostAura.Reel.Tests.Search;

/// <summary>
/// The voice guard behind every served "why" line: cards speak TO the viewer, so any sentence
/// talking ABOUT them ("their tastes", "the user's") is a prompt leak and must be discarded for
/// the deterministic second-person fallback. Word boundaries matter — "theme" and "anthem"
/// contain t-h-e-m and must pass.
/// </summary>
public class SecondPersonVoiceTests
{
    [Theory]
    [InlineData("Matches their taste for slow-burn thrillers")]
    [InlineData("They love ensemble casts")]
    [InlineData("gives them exactly the pacing that lands")]
    [InlineData("Right up the user's alley")]
    [InlineData("THE USER rates this genre highly")]
    [InlineData("A pick the user would rate well")]
    public void Third_person_leaks_are_caught(string why) =>
        Assert.True(SecondPersonVoice.LeaksThirdPerson(why));

    [Theory]
    [InlineData("Right in your wheelhouse — you rate this cast highly")]
    [InlineData("A theme of loss anchors this anthem to memory")]
    [InlineData("Close in feel to the titles you love")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Second_person_and_lookalike_words_pass(string? why) =>
        Assert.False(SecondPersonVoice.LeaksThirdPerson(why));
}
