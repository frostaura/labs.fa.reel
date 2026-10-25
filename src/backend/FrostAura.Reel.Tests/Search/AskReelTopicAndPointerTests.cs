using FrostAura.Reel.Application.Search;
using FrostAura.Reel.Application.Search.AskReel;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Ports;

namespace FrostAura.Reel.Tests.Search;

/// <summary>
/// Round 1's pure rules: the resolved topic the supply counts and the reply names (R1-02), the
/// pointer that has nothing to point at (R1-04), the keyless copy (R1-10) and the effort the turn
/// records (R1-09) — pinned here because each is a table a live model cannot be made to exercise.
/// </summary>
public class AskReelTopicAndPointerTests
{
    // ── R1-02: one topic for the count and the words ─────────────────────────────────────────

    [Fact]
    public void The_topic_is_the_keywords_the_user_said_in_this_message()
    {
        var topic = AskReelTopic.Resolve(
            ["ninja", "martial arts", "ninja warrior"], "none of the above are ninja movies. I want ninja movies", []);

        Assert.Equal(["ninja"], topic.Keywords);
        Assert.Equal("ninja", topic.Label);
        Assert.True(topic.Anchored);
    }

    [Fact]
    public void A_follow_up_that_names_no_topic_takes_it_from_the_newest_earlier_ask()
    {
        IReadOnlyList<ChatTurn> history =
        [
            new("user", "heist films"),
            new("assistant", "Here are some."),
            new("user", "ninja movies"),
            new("assistant", "None left."),
        ];

        var topic = AskReelTopic.Resolve(["ninja", "heist", "martial arts"], "more like that please", history);

        Assert.Equal(["ninja"], topic.Keywords);
        Assert.True(topic.Anchored);
    }

    [Theory]
    [InlineData("ninjas please", "ninja")]
    [InlineData("a NINJA film", "ninja")]
    [InlineData("ninja and samurai movies", "ninja or samurai")]
    public void Plural_and_case_forms_anchor_and_several_said_keywords_are_all_named(string message, string label)
    {
        var topic = AskReelTopic.Resolve(["ninja", "samurai", "martial arts"], message, []);

        Assert.Equal(label, topic.Label);
    }

    [Fact]
    public void With_no_keyword_said_every_keyword_counted_is_named()
    {
        var topic = AskReelTopic.Resolve(["swordplay", "warrior", "ronin"], "samurai films", []);

        Assert.Equal(["swordplay", "warrior", "ronin"], topic.Keywords);
        Assert.Equal("swordplay, warrior or ronin", topic.Label);
        Assert.False(topic.Anchored);
    }

    [Fact]
    public void No_keywords_is_no_topic()
    {
        Assert.Same(AskReelTopic.None, AskReelTopic.Resolve([], "something cosy", []));
        Assert.Same(AskReelTopic.None, AskReelTopic.Resolve(["  "], "something cosy", []));
    }

    // ── R1-04: pointers and the words around them ────────────────────────────────────────────

    [Theory]
    [InlineData("more like the second one you showed me first time round", 2)]
    [InlineData("is the first one violent?", 1)]
    [InlineData("the 3rd film looks good", 3)]
    [InlineData("that last one", AskReelPointer.Last)]
    [InlineData("second one please", 2)]
    [InlineData("show me #4", 4)]
    [InlineData("number 2 sounds right", 2)]
    public void A_pointer_is_read_off_the_message(string message, int position) =>
        Assert.Equal(position, AskReelPointer.Ordinal(message));

    [Theory]
    [InlineData("ninja movies")]
    [InlineData("my first horror film ever was great, more like that")]
    [InlineData("something for a first date")]
    [InlineData("more first time round")]
    [InlineData("# of seasons under 3")]
    public void A_message_that_names_no_card_is_not_a_pointer(string message) =>
        Assert.Null(AskReelPointer.Ordinal(message));

    [Fact]
    public void A_pointer_resolves_only_when_some_answer_shows_that_many_cards()
    {
        var two = Presented((1, 2), (2, 1));

        Assert.False(AskReelPointer.CanResolve(1, []));
        Assert.False(AskReelPointer.CanResolve(AskReelPointer.Last, []));
        Assert.True(AskReelPointer.CanResolve(2, two));
        Assert.False(AskReelPointer.CanResolve(3, two));
        Assert.True(AskReelPointer.CanResolve(AskReelPointer.Last, two));
    }

    [Fact]
    public void Nothing_to_point_at_says_what_is_on_screen_and_asks()
    {
        Assert.Equal(
            "There’s nothing on screen yet for “the second one” to point at — I haven’t shown you any titles in this conversation. Tell me what you’re in the mood for and I’ll look.",
            AskReelPointer.NothingToPointAt(2, []));
        Assert.Equal(
            "My answers here have shown at most 1 title at a time, so there’s no third one to point at. Which title did you mean?",
            AskReelPointer.NothingToPointAt(3, Presented((1, 1))));
        Assert.Equal(
            "My answers here have shown at most 2 titles at a time, so there’s no title #12 to point at. Which title did you mean?",
            AskReelPointer.NothingToPointAt(12, Presented((1, 2))));
    }

    [Theory]
    [InlineData("you")]
    [InlineData("showed")]
    [InlineData("round")]
    [InlineData("you showed me")]
    [InlineData("first time round")]
    [InlineData("the one before")]
    public void Pronouns_and_conversational_words_are_never_a_topic(string keyword) =>
        Assert.True(AskReelIntentSanitizer.RejectedKeyword(keyword, "more like the second one you showed me first time round"));

    [Theory]
    [InlineData("time travel")]
    [InlineData("found footage")]
    [InlineData("ninja")]
    [InlineData("one piece")]
    public void A_keyword_with_a_real_topic_word_survives(string keyword) =>
        Assert.False(AskReelIntentSanitizer.RejectedKeyword(keyword, "something like that"));

    // ── R1-10: the keyless reply ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0, 0, 6.0, "A plain keyword search for your words found nothing new either.")]
    [InlineData(1, 0, 6.0, "Below is 1 title carrying your words — a keyword match only, marked as unchecked.")]
    [InlineData(3, 2, 6.0, "Below are 3 titles carrying your words — a keyword match only, marked as unchecked. 2 of them score under your 6.0 bar.")]
    [InlineData(1, 1, 7.5, "Below is 1 title carrying your words — a keyword match only, marked as unchecked. It scores under your 7.5 bar.")]
    [InlineData(2, 1, 6.0, "Below are 2 titles carrying your words — a keyword match only, marked as unchecked. 1 of them scores under your 6.0 bar.")]
    [InlineData(4, 4, 0.0, "Below are 4 titles carrying your words — a keyword match only, marked as unchecked.")]
    public void The_keyless_reply_says_what_happened_and_counts_the_bar(int shown, int underBar, double floor, string tail)
    {
        var copy = LiveSearchExpansionService.KeylessCopy(shown, underBar, (decimal)floor);

        Assert.StartsWith(
            "Ask Reel has no language model connected right now, so I can’t read what you asked or check anything against it. ",
            copy,
            StringComparison.Ordinal);
        Assert.EndsWith(tail, copy, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ninja movies", new[] { MediaType.Movie })]
    [InlineData("a cosy series", new[] { MediaType.Show })]
    [InlineData("films or shows", new MediaType[0])]
    [InlineData("ninja", new MediaType[0])]
    public void A_keyless_turn_reads_its_media_from_the_message(string message, MediaType[] media) =>
        Assert.Equal(media, LiveSearchExpansionService.KeylessMedia(
            message, new LiveSearchExpansionService.AskTurnInput(message, [], [], [], "US", [], "rerank-ask")));

    [Fact]
    public void A_keyless_turn_keeps_the_previous_media_and_the_ui_filter_outranks_the_words()
    {
        var prior = new SearchIntent([], [], [MediaType.Show], string.Empty, null, null);
        Assert.Equal(
            [MediaType.Show],
            LiveSearchExpansionService.KeylessMedia(
                "ninja", new LiveSearchExpansionService.AskTurnInput("ninja", [], [], [], "US", [], "rerank-ask", PriorIntent: prior)));
        Assert.Equal(
            [MediaType.Show],
            LiveSearchExpansionService.KeylessMedia(
                "ninja movies", new LiveSearchExpansionService.AskTurnInput("ninja movies", [], [], [], "US", [MediaType.Show], "rerank-ask")));
    }

    private static List<PresentedCard> Presented(params (int Turn, int Cards)[] answers) =>
        [.. answers.SelectMany(a => Enumerable.Range(1, a.Cards)
            .Select(rank => new PresentedCard(a.Turn, rank, Guid.NewGuid(), $"T{a.Turn} card {rank}", 2012, MediaType.Movie)))];
}
