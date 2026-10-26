using FrostAura.Reel.Application.Search.Retrieval;
using FrostAura.Reel.Application.Search.Retrieval.Concepts;
using FrostAura.Reel.Domain.Catalog;

namespace FrostAura.Reel.Tests.Filters;

/// <summary>
/// The mood lexicon and the attribute steer, without a database: what a mood word reads as, and how a
/// steer leans relative to its pivot.
/// </summary>
public class MoodLexiconTests
{
    [Theory]
    [InlineData("cosy", "cozy")]
    [InlineData("a cosy movie", "cozy")]
    [InlineData("Feel-good", "feel-good")]
    [InlineData("something feel good", "feel-good")]
    [InlineData("nail-biting", "tense")]
    [InlineData("mind-bending films", "mind-bending")]
    public void Mood_words_read_as_their_entry(string words, string entry)
    {
        Assert.Equal(entry, MoodLexicon.Read(words)?.Mood);
    }

    [Fact]
    public void Every_tone_a_mood_names_is_one_the_extractor_produces()
    {
        foreach (var word in MoodLexicon.Words)
        {
            Assert.All(MoodLexicon.Read(word)!.Tones, tone => Assert.Contains(tone, MoodLexicon.KnownTones));
        }
    }

    [Fact]
    public void Every_steer_a_mood_implies_is_one_the_extractor_measures()
    {
        foreach (var word in MoodLexicon.Words)
        {
            Assert.All(MoodLexicon.Read(word)!.Directions, d => Assert.NotNull(AttributeSteer.Canonical(d.Attribute)));
        }
    }

    [Fact]
    public void A_bare_tone_reads_as_that_tone_and_an_unknown_word_as_nothing()
    {
        Assert.Equal(["brooding"], MoodLexicon.Read("brooding")!.Tones);
        Assert.Null(MoodLexicon.Read("ninja"));
        Assert.Null(MoodLexicon.Read("   "));
    }

    [Fact]
    public void An_explicit_steer_wins_over_a_moods_implied_one_and_unknown_attributes_are_dropped()
    {
        var merged = AttributeSteer.Merge(
            [new AttributeDirection("Humour", AttributeTrend.Less), new AttributeDirection("budget", AttributeTrend.More)],
            [new AttributeDirection("humor", AttributeTrend.More), new AttributeDirection("darkness", AttributeTrend.Less)]);

        Assert.Equal([("humor", AttributeTrend.Less, true), ("darkness", AttributeTrend.Less, false)], merged);
    }

    [Fact]
    public void A_title_leans_relative_to_the_pivot_and_only_explicit_steers_must_hold()
    {
        var steers = AttributeSteer.Merge(
            [new AttributeDirection("humor", AttributeTrend.More)],
            [new AttributeDirection("darkness", AttributeTrend.Less)]);
        var pivots = new Dictionary<string, decimal> { ["humor"] = 0.4m };
        var funnierButDark = new TitleAttributes { Humor = 0.6m, Darkness = 0.9m };
        var graver = new TitleAttributes { Humor = 0.3m, Darkness = 0.1m };

        Assert.True(AttributeSteer.Leans(funnierButDark, steers, pivots));
        Assert.False(AttributeSteer.Leans(graver, steers, pivots));
        Assert.Equal((0.6m - 0.4m) - (0.9m - 0.5m), AttributeSteer.Fit(funnierButDark, steers, pivots));
        Assert.Equal("steer:darkness-", AttributeSteer.Evidence("darkness", AttributeTrend.Less));
    }
}
