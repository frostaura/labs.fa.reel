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
