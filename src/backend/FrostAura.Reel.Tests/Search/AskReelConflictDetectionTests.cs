using FrostAura.Reel.Application.Search;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Infrastructure.Adapters;
using FrostAura.Reel.Infrastructure.Telemetry;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrostAura.Reel.Tests.Search;

/// <summary>
/// The collision rung, pinned against the thing that actually broke it: PROMPT LAUNDERING.
///
/// <para>Ask Reel tells the interpret model what the account has permanently excluded, and the
/// model — correctly — stops asking for it. "I want a comedy tonight" therefore comes back as
/// <c>genres: []</c>, <c>excludeGenres: ["comedy"]</c>. The collision test then intersected the
/// account's exclusions with <c>intent.Genres</c> and found the empty set, on every turn, for
/// every account: the better the prompt behaved, the less the rung could ever fire. Three
/// independent finders reported the notice never appearing, and the only acknowledgement a user
/// ever got was whatever prose the model chose to write — which on one turn said the opposite.</para>
///
/// <para>The fix is a source the prompt cannot launder: the interpreter's free deterministic
/// reading of the user's own words, carried on <see cref="AgentReply.Requested"/>. These tests
/// read the same sentences the finders used, through the same heuristic the agent uses.</para>
/// </summary>
public class AskReelConflictDetectionTests
{
    private static ISearchQueryInterpreter Stub()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["TestHelpers:ModelStubMode"] = "true" })
            .Build();
        var scopeFactory = new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        var recorder = new ApiUsageRecorder(scopeFactory, NullLogger<ApiUsageRecorder>.Instance);
        return new OpenRouterSearchInterpreter(new HttpClient(), recorder, config, NullLogger<OpenRouterSearchInterpreter>.Instance);
    }

    /// <summary>The laundered intent a well-behaved prompt returns: the genre moved to the exclusion side.</summary>
    private static SearchIntent Laundered(params string[] excluded) =>
        new([], ["tonight"], [], "a comedy tonight", null, null, null, excluded, []);

    [Fact]
    public void A_genre_the_prompt_laundered_out_of_the_intent_still_collides()
    {
        var rules = FilterRuleSet.Create(["comedy"], null, null, null, null, null, null);
        var requested = Stub().Heuristic("I want a comedy tonight");

        // The intent alone — what the rung used to read — sees nothing.
        Assert.Empty(AskReelStandingFilters.CollidingGenres(rules, Laundered("comedy")));

        Assert.Equal(
            ["comedy"],
            AskReelStandingFilters.CollidingGenres(rules, Laundered("comedy"), requested));
    }

    /// <summary>
    /// The model is not always the launderer — a turn that resolves normally must still collide,
    /// which is the half the original implementation got right and this change must not lose.
    /// </summary>
    [Fact]
    public void A_genre_the_model_left_in_the_intent_still_collides()
    {
        var rules = FilterRuleSet.Create(["horror"], null, null, null, null, null, null);
        var intent = new SearchIntent(["Horror", "thriller"], [], [], "", null, null);

        Assert.Equal(["Horror"], AskReelStandingFilters.CollidingGenres(rules, intent, requested: null));
    }

    [Fact]
    public void A_genre_neither_source_asked_for_is_not_a_collision()
    {
        var rules = FilterRuleSet.Create(["horror"], null, null, null, null, null, null);
        var requested = Stub().Heuristic("something tense and clever");

        Assert.Empty(AskReelStandingFilters.CollidingGenres(
            rules, new SearchIntent(["thriller"], [], [], "", null, null), requested));
    }

    /// <summary>
    /// "an anime series" — F3-04's second repro. <c>anime</c> is not a TMDB genre, so the account
    /// holds it as an avoided KEYWORD; the collision is the same fact about the same turn and is
    /// owed the same sentence.
    /// </summary>
    [Fact]
    public void An_avoided_keyword_the_turn_asked_for_collides()
    {
        var rules = FilterRuleSet.Create(null, null, ["anime", "musical"], null, null, null, null);
        var requested = Stub().Heuristic("an anime series");

        Assert.Equal(
            ["anime"],
            AskReelStandingFilters.CollidingKeywords(rules, Laundered(), requested));
    }

    /// <summary>
    /// F5-03, the half that survived. The founder holds "anime" as an excluded GENRE; the heuristic
    /// reads "an anime series" as a KEYWORD. Each list was intersected with its own vocabulary, so
    /// neither side matched and the turn emitted no conflict frame at all — while the model's prose
    /// in the same stream said the standing filters excluded anime. Which list the conditions editor
    /// happened to write a rule to is not a fact about what the user asked for.
    /// </summary>
    [Fact]
    public void A_standing_genre_the_user_typed_as_a_keyword_still_collides()
    {
        var rules = FilterRuleSet.Create(["anime", "animation"], null, null, null, null, null, null);
        var requested = Stub().Heuristic("an anime series");

        Assert.Equal(
            ["anime"],
            AskReelStandingFilters.CollidingGenres(rules, Laundered(), requested));
    }

    /// <summary>
    /// The mirror: a standing avoid-KEYWORD the user typed as a genre word. <c>excludedKeywords</c>
    /// was empty on every conflict frame ever captured, and this is one of the two reasons why.
    /// </summary>
    [Fact]
    public void A_standing_keyword_the_user_typed_as_a_genre_still_collides()
    {
        var rules = FilterRuleSet.Create(null, null, ["comedy"], null, null, null, null);
        var requested = Stub().Heuristic("something genuinely funny");

        Assert.Equal(
            ["comedy"],
            AskReelStandingFilters.CollidingKeywords(rules, Laundered(), requested));
    }

    /// <summary>
    /// The raw sentence is the last reading, and it is not decoration: the heuristic drops stop
    /// words and the model drops whatever the prompt told it to, so a word BOTH discarded can still
    /// be the one the standing rule names.
    /// </summary>
    [Fact]
    public void A_word_only_the_raw_sentence_still_carries_collides()
    {
        var askedFor = AskReelStandingFilters.AskedFor(
            "find me something about the history of jazz",
            new SearchIntent([], ["jazz"], [], "jazz", null, null),
            requested: null);

        Assert.Equal(["history"], AskReelStandingFilters.Colliding(["history"], askedFor));
    }

    /// <summary>
    /// The probe: one field moved. A standing exclusion the turn did NOT ask for — in any of the
    /// three readings — is not a collision, so the raw-token pass cannot turn the notice into
    /// noise that fires on every turn an account with twelve rules ever makes.
    /// </summary>
    [Fact]
    public void A_standing_exclusion_the_turn_never_named_is_not_a_collision()
    {
        var askedFor = AskReelStandingFilters.AskedFor(
            "a tense heist movie",
            new SearchIntent(["thriller"], ["heist"], [], "heist", null, null),
            requested: null);

        Assert.Empty(AskReelStandingFilters.Colliding(FounderRules.ExcludeGenres, askedFor));
    }

    /// <summary>
    /// The founder's own filter set against the sentence the finders used. Both halves fire: comedy
    /// is permanently excluded and the 90s are outside the standing year window.
    /// </summary>
    [Fact]
    public void The_founder_filter_set_collides_on_both_the_genre_and_the_decade()
    {
        var requested = Stub().Heuristic("a 90s romantic comedy");
        var laundered = new SearchIntent([], [], [], "a 90s romantic comedy", 2000, null);

        Assert.Contains(
            "comedy",
            AskReelStandingFilters.CollidingGenres(FounderRules, laundered, requested));
        Assert.True(AskReelStandingFilters.YearWindowCollides(FounderRules, laundered, requested));
    }

    /// <summary>
    /// F4-07. The same sentence names TWO excluded genres and the frame reported one. The
    /// interpreter collapsed "romantic comedy" to comedy and filed "romantic" under keywords, and
    /// no reading of the turn ever spelled the word "romance" — so a standing <c>ExcludeGenre</c>
    /// on romance could not intersect anything, while the prose in the same bubble said "your
    /// standing filters exclude romance and comedy". The alias table is a fact about English, not
    /// about which reading found the word.
    ///
    /// <para>Order is ordinal and therefore stable: Postgres does not promise row order, and a turn
    /// that named its two collisions one way live and the other way on the next resume would look
    /// like two different answers to the same question.</para>
    /// </summary>
    [Fact]
    public void A_romantic_comedy_names_both_excluded_genres_in_a_stable_order()
    {
        var requested = Stub().Heuristic("a 90s romantic comedy");
        var laundered = new SearchIntent([], [], [], "a 90s romantic comedy", 2000, null);

        Assert.Equal(
            ["comedy", "romance"],
            AskReelStandingFilters.CollidingGenres(FounderRules, laundered, requested));
    }

    /// <summary>The heuristic's half of the same fix: "romantic" is a genre word, not a stray term.</summary>
    [Theory]
    [InlineData("a 90s romantic comedy", "romance")]
    [InlineData("a 90s romantic comedy", "comedy")]
    [InlineData("a rom-com from the 2010s", "romance")]
    [InlineData("a rom-com from the 2010s", "comedy")]
    [InlineData("a romcom", "romance")]
    public void The_heuristic_resolves_the_romance_vocabulary(string message, string slug)
    {
        Assert.Contains(slug, Stub().Heuristic(message).Genres);
    }

    /// <summary>
    /// The clause the user reads names both too — it is built from the same collision data as the
    /// frame, so the two cannot disagree, and F4-07's contradiction was a sentence that named
    /// romance in its first half and dropped it from its last.
    /// </summary>
    [Fact]
    public void The_clause_names_both_excluded_genres()
    {
        var requested = Stub().Heuristic("a 90s romantic comedy");
        var laundered = new SearchIntent([], [], [], "a 90s romantic comedy", 2000, null);
        var colliding = AskReelStandingFilters.CollidingGenres(FounderRules, laundered, requested);

        Assert.Equal(
            "Coming up — under your conditions that means no comedy or romance and titles from 2000 on.",
            AskReelStandingFilters.Qualify("Coming up.", colliding, [], yearCollides: true, FounderRules));
    }

    /// <summary>
    /// F5-03's pivot, stated as a rule. Which list a colliding term is REPORTED under is decided by
    /// which list the standing rule lives in, never by which bucket the interpreter put the word in.
    /// The founder's <c>anime</c> is an <c>ExcludeGenre</c> and the heuristic reads "an anime
    /// series" as a keyword; the frame still owes the client <c>excludedGenres</c>, because a genre
    /// chip and a term chip lead to different rows in the conditions editor.
    /// </summary>
    [Fact]
    public void A_colliding_term_is_reported_under_the_list_its_rule_lives_in()
    {
        var asGenre = FilterRuleSet.Create(["anime"], null, null, null, null, null, null);
        var asKeyword = FilterRuleSet.Create(null, null, ["anime"], null, null, null, null);
        var requested = Stub().Heuristic("an anime series");

        Assert.Equal(["anime"], AskReelStandingFilters.CollidingGenres(asGenre, Laundered(), requested));
        Assert.Empty(AskReelStandingFilters.CollidingKeywords(asGenre, Laundered(), requested));

        Assert.Equal(["anime"], AskReelStandingFilters.CollidingKeywords(asKeyword, Laundered(), requested));
        Assert.Empty(AskReelStandingFilters.CollidingGenres(asKeyword, Laundered(), requested));
    }

    /// <summary>
    /// Matching is whole-token, never substring: "war" must not collide with "warm", and a
    /// twelve-rule account would otherwise see the notice on nearly every turn.
    /// </summary>
    [Fact]
    public void A_standing_exclusion_does_not_match_a_word_that_merely_contains_it()
    {
        var askedFor = AskReelStandingFilters.AskedFor(
            "something warm and hopeful", new SearchIntent([], ["warm"], [], "warm", null, null), null);

        Assert.Empty(AskReelStandingFilters.Colliding(["war"], askedFor));
    }

    /// <summary>
    /// The account the whole round was measured against: eight excluded genres and a 2000 floor.
    /// Kept as a fixture so a change to the collision rule is judged against a real filter set
    /// rather than against a single convenient exclusion.
    /// </summary>
    private static readonly FilterRuleSet FounderRules = FilterRuleSet.Create(
        ["anime", "animation", "comedy", "documentary", "history", "music", "musical", "romance"],
        null, null, ["en"], 6.0m, 2000, null);

    [Fact]
    public void A_keyword_the_account_does_not_avoid_is_not_a_collision()
    {
        var rules = FilterRuleSet.Create(null, null, ["anime"], null, null, null, null);
        var requested = Stub().Heuristic("a tense heist series");

        Assert.Empty(AskReelStandingFilters.CollidingKeywords(rules, Laundered(), requested));
    }

    /// <summary>
    /// The year half launders identically: told "min year 2000", the model answers the 90s request
    /// with <c>minYear: 2000</c> and the window it returns no longer collides with anything.
    /// </summary>
    [Fact]
    public void A_decade_the_prompt_laundered_into_the_standing_window_still_collides()
    {
        var rules = FilterRuleSet.Create(null, null, null, null, null, 2000, null);
        var requested = Stub().Heuristic("a light-hearted romantic comedy from the 1990s");
        var laundered = new SearchIntent([], ["romantic"], [], "", 2000, null);

        Assert.False(AskReelStandingFilters.YearWindowCollides(rules, laundered));
        Assert.True(AskReelStandingFilters.YearWindowCollides(rules, laundered, requested));
    }

    /// <summary>The probe: one field moved. A decade INSIDE the standing window is not a collision.</summary>
    [Fact]
    public void A_decade_inside_the_standing_window_is_not_a_collision()
    {
        var rules = FilterRuleSet.Create(null, null, null, null, null, 2000, null);
        var requested = Stub().Heuristic("a romantic comedy from the 2010s");

        Assert.False(AskReelStandingFilters.YearWindowCollides(
            rules, new SearchIntent([], [], [], "", 2010, 2019), requested));
    }

    /// <summary>
    /// The three sentences the finders used, read by the heuristic. Pinned because the whole fix
    /// rests on this reading existing — a tokenizer change that stopped seeing "comedy" would
    /// silently restore the original defect.
    /// </summary>
    [Theory]
    [InlineData("I want a comedy tonight", "comedy")]
    [InlineData("something genuinely funny", "comedy")]
    [InlineData("a light-hearted romantic comedy from the 1990s", "comedy")]
    public void The_heuristic_sees_the_genre_the_user_typed(string message, string genre)
    {
        Assert.Contains(genre, Stub().Heuristic(message).Genres);
    }

    [Fact]
    public void The_heuristic_sees_the_decade_the_user_typed()
    {
        var requested = Stub().Heuristic("a light-hearted romantic comedy from the 1990s");

        Assert.Equal(1990, requested.MinYear);
        Assert.Equal(1999, requested.MaxYear);
    }

    [Fact]
    public void The_heuristic_sees_the_avoided_word_the_user_typed()
    {
        var requested = Stub().Heuristic("an anime series");

        Assert.Contains("anime", requested.Keywords);
        Assert.Contains(MediaType.Show, requested.MediaTypes);
    }

    // ── The clause the reply carries ──────────────────────────────────────────────────────────

    /// <summary>
    /// F1-03: the reply is written before a single candidate exists and used to promise the thing
    /// the conditions were about to remove — "here are gritty 90s action picks" above seven
    /// post-2000 titles. The clause is deterministic, built from the same collision data as the
    /// frame, and appended BEFORE the reply is emitted or persisted, so the transcript carries it.
    /// </summary>
    [Fact]
    public void The_clause_names_the_excluded_genre_and_the_year_bound()
    {
        var rules = FilterRuleSet.Create(["comedy"], null, null, null, null, 2000, null);
        var qualified = AskReelStandingFilters.Qualify(
            "Absolutely — here are gritty 90s action picks.", ["comedy"], [], yearCollides: true, rules);

        Assert.Equal(
            "Absolutely — here are gritty 90s action picks — under your conditions that means no comedy and titles from 2000 on.",
            qualified);
    }

    /// <summary>
    /// Acceptance D6: the live "older stuff is fine" reply already said "from 2000 onward", then had
    /// "titles from 2000 on" appended. A reply that names the bound is not told it twice; the genre
    /// clause it does not name still lands.
    /// </summary>
    [Fact]
    public void A_reply_that_already_names_the_year_bound_is_not_told_it_again()
    {
        var rules = FilterRuleSet.Create(["comedy"], null, null, null, null, 2000, null);

        Assert.Equal(
            "Older shows it is — from 2000 onward.",
            AskReelStandingFilters.Qualify("Older shows it is — from 2000 onward.", [], [], yearCollides: true, rules));
        Assert.Equal(
            "Older shows it is, from 2000 onward — under your conditions that means no comedy.",
            AskReelStandingFilters.Qualify("Older shows it is, from 2000 onward.", ["comedy"], [], yearCollides: true, rules));
        Assert.Equal(
            "Shows from 20000 leagues — under your conditions that means titles from 2000 on.",
            AskReelStandingFilters.Qualify("Shows from 20000 leagues.", [], [], yearCollides: true, rules));
    }

    [Fact]
    public void The_clause_lists_an_avoided_word_beside_the_genre()
    {
        var rules = FilterRuleSet.Create(["comedy"], null, ["anime"], null, null, null, null);
        var qualified = AskReelStandingFilters.Qualify(
            "Coming up.", ["comedy"], ["anime"], yearCollides: false, rules);

        Assert.Equal("Coming up — under your conditions that means no comedy or anime.", qualified);
    }

    [Fact]
    public void A_turn_with_no_collision_is_not_qualified_at_all()
    {
        var rules = FilterRuleSet.Create(["comedy"], null, null, null, null, null, null);

        Assert.Equal(
            "Coming up.",
            AskReelStandingFilters.Qualify("Coming up.", [], [], yearCollides: false, rules));
    }

    /// <summary>Every number in the clause is invariant, like every number this backend formats.</summary>
    [Theory]
    [InlineData(2000, null, "titles from 2000 on")]
    [InlineData(null, 2010, "titles up to 2010")]
    [InlineData(1980, 2010, "titles between 1980 and 2010")]
    public void The_year_half_of_the_clause_renders_its_bounds_invariantly(int? min, int? max, string expected)
    {
        var rules = FilterRuleSet.Create(null, null, null, null, null, min, max);
        var qualified = AskReelStandingFilters.Qualify("Coming up.", [], [], yearCollides: true, rules);

        Assert.Contains(expected, qualified, StringComparison.Ordinal);
        Assert.DoesNotContain(",0", qualified, StringComparison.Ordinal);
    }
}
