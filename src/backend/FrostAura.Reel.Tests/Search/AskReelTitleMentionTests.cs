using FrostAura.Reel.Application.Search.AskReel;

namespace FrostAura.Reel.Tests.Search;

/// <summary>
/// HREV3-ASK-A / HREV3-RET-06: the runs of a message's words that may name a stored title, which the
/// engine matches when the planner left an item question without a reference. A run is matched as
/// typed and with its punctuation trimmed; a run of filler never is, and a single word only when the
/// user capitalised it — so "is it violent?" never answers about It, and "is Heat violent?" names Heat.
/// </summary>
public class AskReelTitleMentionTests
{
    [Theory]
    [InlineData("is Blade of the 47 Ronin a ninja film or a samurai film?", "blade of the 47 ronin", true)]
    [InlineData("wait, is Blade of the 47 Ronin actually a ninja film or a samurai film?", "blade of the 47 ronin", true)]
    [InlineData("is Heat violent?", "heat", true)]
    [InlineData("is heat violent?", "heat", false)]
    [InlineData("is it violent?", "it", false)]
    [InlineData("is the first one violent?", "the first one", false)]
    [InlineData("what is Heat's ending like?", "heat", true)]
    [InlineData("is John Wick: Chapter 4 too long?", "john wick: chapter 4", true)]
    [InlineData("is \"Airplane!\" still funny?", "airplane!", true)]
    [InlineData("who directed 1917?", "1917", true)]
    public void A_run_of_the_messages_words_is_a_candidate_name_only_when_it_could_be_one(string message, string name, bool candidate)
    {
        var spans = AskReelTitleMention.Spans(message);

        Assert.Equal(candidate, spans.Any(s => s.Text == name));
    }

    [Fact]
    public void The_longer_run_comes_first_so_the_full_name_beats_a_name_inside_it()
    {
        var spans = AskReelTitleMention.Spans("is Blade of the 47 Ronin a ninja film?");

        var full = spans.First(s => s.Text == "blade of the 47 ronin");
        var part = spans.First(s => s.Text == "blade");
        Assert.True(full.Words > part.Words);
        Assert.True(spans.ToList().IndexOf(full) < spans.ToList().IndexOf(part));
    }

    [Theory]
    [InlineData("Heat", "heat", null)]
    [InlineData("“Heat” (1995)", "heat", 1995)]
    [InlineData("  'Blade of the 47 Ronin' (2022) ", "blade of the 47 ronin", 2022)]
    [InlineData("Blade Runner 2049", "blade runner 2049", null)]
    public void A_planner_name_is_trimmed_of_quotes_and_a_bracketed_year(string name, string normalised, int? year)
    {
        Assert.Equal(normalised, AskReelTitleMention.Normalise(name));
        Assert.Equal(year, AskReelTitleMention.YearOf(name));
    }

    [Fact]
    public void A_message_with_no_words_has_no_candidates_and_an_essay_is_read_only_so_far()
    {
        Assert.Empty(AskReelTitleMention.Spans("   "));
        var essay = string.Join(' ', Enumerable.Repeat("Word", 100)) + " Tail";
        Assert.DoesNotContain(AskReelTitleMention.Spans(essay), s => s.Text == "tail");
    }
}
