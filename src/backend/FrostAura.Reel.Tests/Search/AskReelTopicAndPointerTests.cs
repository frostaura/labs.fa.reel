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

    /// <summary>
    /// HREV6-ASK-02: a pointer the user rejects — a cue before it in its clause ("not", "skip",
    /// "didn't like", "anything but", "not a fan of") or a verdict after it ("is terrible") — is a
    /// rejection, never a card to search near. A pointer asked for, a cue in another clause, a cue
    /// about something else, and a question about the card are not.
    /// </summary>
    [Theory]
    [InlineData("not the first one", 1)]
    [InlineData("Not the first one!", 1)]
    [InlineData("I didn't like the second one", 2)]
    [InlineData("i did not like the 2nd one", 2)]
    [InlineData("anything but the third one", 3)]
    [InlineData("skip the first one, show me others", 1)]
    [InlineData("the second one is terrible", 2)]
    [InlineData("the second one was kinda boring", 2)]
    [InlineData("the first one isn't for me", 1)]
    [InlineData("I hated the fourth one", 4)]
    [InlineData("don't show me the last one again", AskReelPointer.Last)]
    [InlineData("not a fan of the second one, something darker", 2)]
    [InlineData("nothing like the first one", 1)]
    [InlineData("drop the third film", 3)]
    [InlineData("more like the first one", null)]
    [InlineData("something like the second one but not too dark", null)]
    [InlineData("no superheroes, more like the first one", null)]
    [InlineData("I liked the third one, more like that", null)]
    [InlineData("the second one sounds fun", null)]
    [InlineData("is the first one not too violent?", null)]
    [InlineData("isn't the second one terrible?", null)]
    [InlineData("not superheroes", null)]
    // HREV6-ASK-02-R2: a refusal AFTER the card in a clause of its own, a refusal word before it, the
    // "#1" and "number 1" pointers, "no to", contractions of the card noun, and the rejected card
    // second ("but not the second one"). Varied over the punctuation, the refusal's words and the rank.
    [InlineData("the first one? nah", 1)]
    [InlineData("first one, no thanks", 1)]
    [InlineData("the second one, not for me", 2)]
    [InlineData("the third one. pass", 3)]
    [InlineData("no to the first one", 1)]
    [InlineData("nah the first one", 1)]
    [InlineData("nope, not the first one", 1)]
    [InlineData("not #1", 1)]
    [InlineData("not number 1", 1)]
    [InlineData("not #3 please", 3)]
    [InlineData("more like the first one but not the second one", 2)]
    [InlineData("I liked the first one but not the second one", 2)]
    [InlineData("the first one's terrible", 1)]
    [InlineData("first one's boring", 1)]
    [InlineData("the second one’s not for me", 2)]
    [InlineData("the first one? no, the second one", 1)]
    [InlineData("the first one? yes", null)]
    [InlineData("the first one, more like that", null)]
    [InlineData("more like the first one, no superheroes", null)]
    [InlineData("the first one's great", null)]
    [InlineData("#2 please", null)]
    [InlineData("is there anything like the first one?", null)]
    public void A_rejected_pointer_is_read_off_the_message(string message, int? position) =>
        Assert.Equal(position, AskReelPointer.Rejected(message));

    /// <summary>
    /// HREV6-ASK-02-R1: one message can reject one card and ask for more like another. Each pointer is
    /// judged on its own, so "not the first one, more like the second one" rejects the first card and
    /// keeps the second as the card to search near — never both rejected, never the second dropped.
    /// </summary>
    [Theory]
    [InlineData("not the first one, more like the second one", "1-|2+")]
    [InlineData("the first one was rubbish, more like the second one", "1-|2+")]
    [InlineData("more like the first one but not the second one", "1+|2-")]
    [InlineData("I liked the first one but not the second one", "1+|2-")]
    [InlineData("skip the third one, something like #5", "3-|5+")]
    [InlineData("the first one? nah. the last one though", "1-|-1+")]
    [InlineData("more like the second one", "2+")]
    [InlineData("not the first one", "1-")]
    public void Every_pointer_in_a_message_is_read_with_whether_it_is_rejected(string message, string expected)
    {
        var read = AskReelPointer.Pointers(message).Select(p => $"{p.Position}{(p.Rejected ? '-' : '+')}");
        Assert.Equal(expected.Split('|'), read);
    }

    /// <summary>
    /// HREV6-ASK-02-R2/R3: the text a model-less reading takes is the message less every clause that
    /// only rejects a card — the card's own clause, a refusal clause after it ("? nah") and an
    /// interjection before it ("nope,") — so neither the refusal nor the interjection becomes a topic.
    /// </summary>
    [Theory]
    [InlineData("nope, not the first one", "")]
    [InlineData("ugh, not the first one", "")]
    [InlineData("the first one? nah", "")]
    [InlineData("the second one, not for me", "")]
    [InlineData("the first one's terrible", "")]
    [InlineData("not #1", "")]
    [InlineData("not the first one, more like the second one", "more like the second one")]
    [InlineData("more like the first one but not the second one", "more like the first one")]
    [InlineData("not the first one but something darker", "but something darker")]
    [InlineData("superhero movies? no thanks. not the first one", "superhero movies? no thanks.")]
    public void The_readable_request_is_the_message_less_its_rejecting_clauses(string message, string expected) =>
        Assert.Equal(expected, AskReelPointer.WithoutRejectedClause(message));

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
