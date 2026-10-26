using FrostAura.Reel.Domain.Lens;
using FrostAura.Reel.Infrastructure.Adapters;

namespace FrostAura.Reel.Tests.Lens;

/// <summary>The lens templates say "an atmospheric", never "A atmospheric" (WS-1 task 7, F-11).</summary>
public class LensArticleTests
{
    [Theory]
    [InlineData("atmospheric", "an")]
    [InlineData("intense", "an")]
    [InlineData("eerie", "an")]
    [InlineData("offbeat", "an")]
    [InlineData("uplifting", "an")]
    [InlineData("hour-long", "an")]
    [InlineData("honest", "an")]
    [InlineData("bleak", "a")]
    [InlineData("unique", "a")]
    [InlineData("universal", "a")]
    [InlineData("one-room", "a")]
    [InlineData("European", "a")]
    [InlineData("", "a")]
    public void The_article_follows_the_sound_of_the_next_word(string word, string article)
    {
        Assert.Equal(article, LensClauses.IndefiniteArticle(word));
    }

    [Theory]
    [InlineData("atmospheric", "An atmospheric crime piece")]
    [InlineData("intense", "An intense crime piece")]
    [InlineData("bleak", "A bleak crime piece")]
    public void The_stub_lens_opens_with_the_right_article(string tone, string opening)
    {
        var title = new LensTitleInput(
            Guid.Empty, "Soldado", 2018, "Movie", 122, ["crime"], tone, null, [], null, null, null, null, null,
            null, null, null, null, [], null, [], null, null);

        var note = OpenRouterLensWriter.StubLens(title, "likes crime");

        Assert.StartsWith(opening, note.Lens, StringComparison.Ordinal);
    }
}
