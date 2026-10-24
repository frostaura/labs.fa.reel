using FrostAura.Reel.Application.Search.AskReel;

namespace FrostAura.Reel.Tests.Search;

/// <summary>B-16: the transcript rendered "*Waist Deep*" — markdown reached a plain-text surface.</summary>
public class AskReelTextTests
{
    [Theory]
    [InlineData("Try *Waist Deep* tonight.", "Try Waist Deep tonight.")]
    [InlineData("**Heat** and __Ronin__ both fit.", "Heat and Ronin both fit.")]
    [InlineData("See `Collateral` — a [classic](https://example.com).", "See Collateral — a classic.")]
    [InlineData("## Picks\n- Heat\n- Ronin", "Picks Heat Ronin")]
    [InlineData("It's 2 * 3 = 6 stars, a_b_c.", "It's 2 * 3 = 6 stars, a_b_c.")]
    [InlineData("Plain sentence.", "Plain sentence.")]
    public void Markup_is_reduced_to_its_visible_words(string input, string expected) =>
        Assert.Equal(expected, AskReelText.StripMarkdown(input));

    [Fact]
    public void Null_and_empty_pass_through()
    {
        Assert.Null(AskReelText.StripMarkdown(null));
        Assert.Equal(string.Empty, AskReelText.StripMarkdown(string.Empty));
    }
}
