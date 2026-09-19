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

    // ── Anti-recommendation guard ────────────────────────────────────────────────────────────
    // A pick surface must never caption its own recommendation with a reason to skip it. The
    // first three positives are the captions observed verbatim on the live feed (2026-08-10).

    [Theory]
    [InlineData("You may bounce off the home-invasion gore that drives this one")]
    [InlineData("Your night pick probably skips this Chucky horror-thriller")]
    [InlineData("You likely avoid this sadistic backwoods horror")]
    [InlineData("You'll probably dislike the mean streak here")]
    [InlineData("Honestly not for you unless the mood shifts")]
    [InlineData("Best to give this a miss on a school night")]
    public void Anti_recommendation_captions_are_caught(string why) =>
        Assert.True(SecondPersonVoice.SoundsAntiRecommendation(why));

    [Theory]
    [InlineData("You get a tense FBI cat-and-mouse in your strongest lane")]
    [InlineData("Shares Hokum's slow-burn dread")]
    [InlineData("Darker than most but squarely your lane")]
    [InlineData("You rate Kate Siegel 7.9 on average")]
    [InlineData("Skip the recap — this one earns your full attention")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Positive_pick_captions_pass_the_anti_recommendation_guard(string? why) =>
        Assert.False(SecondPersonVoice.SoundsAntiRecommendation(why));
}
