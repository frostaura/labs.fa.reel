using System.Globalization;
using FrostAura.Reel.Application.Search.AskReel;
using FrostAura.Reel.Application.Search.Retrieval;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Filters;

namespace FrostAura.Reel.Tests.Search;

/// <summary>
/// The none-found reply, pinned verbatim. Every number comes from the supply report; nothing comes
/// from a model, and no seen title is ever named (the FD-3 default: prose gives counts only).
/// </summary>
public class NoneFoundCopyTests
{
    /// <summary>The founder's ninja request, shaped like the SQL truth the assessment counted.</summary>
    private static SupplyReport Ninja(params ConditionKnockout[] knockouts) =>
        new(
            MatchingInCatalogue: 11, AlreadySeen: 6, SavedOrDismissed: 1, ExcludedByConditions: 4, Eligible: 0,
            BelowFloor: 0, Unscored: 0, Knockouts: knockouts, LowVoteMatches: 9, MinVotes: 50,
            PredictedFloor: 6m, KnockoutsMeasured: true);

    private static ConditionKnockout Knockout(string label, int unlock) =>
        new(FilterKind.Language, "en", null, label, unlock, [new TitleRef(Guid.NewGuid(), "Shinobi", 2005, MediaType.Movie)]);

    [Fact]
    public void Nothing_left_names_the_partition_and_the_relaxations()
    {
        var text = NoneFoundCopy.Compose(new NoneFoundCopy.Facts(
            Ninja(Knockout("English only", 11), Knockout("2000 onwards", 3), Knockout("crowd 6.0 or better", 2)),
            "ninja", MediaScope.Movies, [], 0, 6m));

        Assert.Equal(
            "There are no ninja movies left for you. "
            + "Of the 11 ninja movies in the catalogue with at least 50 votes, you've seen 6, 1 is saved or dismissed and your conditions hold out 4. "
            + "Turning off “English only” would unlock 11; turning off “2000 onwards” would unlock 3.",
            text);
    }

    [Fact]
    public void Matches_under_the_bar_are_named_because_they_are_on_screen_below()
    {
        var supply = Ninja() with { Eligible = 1, BelowFloor = 1 };
        var text = NoneFoundCopy.Compose(new NoneFoundCopy.Facts(
            supply, "samurai", MediaScope.Movies, ["Blade of the 47 Ronin"], 0, 6m));

        Assert.Contains("Under your 6.0 bar: Blade of the 47 Ronin — shown below.", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// R1-01: the samurai turn's only find was judged a PARTIAL match and sat in the near-miss tray of
    /// a reply that never named it. The near misses are named, three at most, the rest counted; with
    /// no names to hand the reply still counts them.
    /// </summary>
    [Theory]
    [InlineData(new[] { "Blade of the 47 Ronin" }, 1, "Close, but not quite: Blade of the 47 Ronin — tucked away below.")]
    [InlineData(new[] { "A", "B" }, 2, "Close, but not quite: A and B — tucked away below.")]
    [InlineData(new[] { "A", "B", "C", "D" }, 4, "Close, but not quite: A, B, C and 1 more — tucked away below.")]
    [InlineData(new string[0], 3, "3 close-but-not-quite titles are tucked away below.")]
    public void Near_misses_are_named_in_a_none_found_reply(string[] names, int count, string sentence)
    {
        var text = NoneFoundCopy.Compose(new NoneFoundCopy.Facts(
            Ninja() with { Eligible = 1, BelowFloor = 1 }, "samurai", MediaScope.Movies, [], count, 6m, NearMissNames: names));

        Assert.EndsWith(sentence, text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_long_list_under_the_bar_is_never_joined_twice()
    {
        var text = NoneFoundCopy.Compose(new NoneFoundCopy.Facts(
            Ninja() with { Eligible = 5, BelowFloor = 5 }, "samurai", MediaScope.Movies, ["A", "B", "C", "D", "E"], 0, 6m));

        Assert.Contains("Under your 6.0 bar: A, B, C and 2 more — shown below.", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Acceptance D5: the live samurai reply said "There are no samurai movies left for you" above a
    /// tray holding one. With titles under the bar, the honest sentence is that none clears it.
    /// </summary>
    [Fact]
    public void Titles_under_the_bar_are_never_called_none_left()
    {
        var supply = Ninja() with { Eligible = 1, BelowFloor = 1 };
        var text = NoneFoundCopy.Compose(new NoneFoundCopy.Facts(
            supply, "samurai", MediaScope.Movies, ["Blade of the 47 Ronin"], 0, 6m));

        Assert.StartsWith("None of your unseen samurai movies clears your 6.0 bar.", text, StringComparison.Ordinal);
        Assert.DoesNotContain("left for you", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// D4: titles this conversation already showed were excluded before the judge saw anything, so
    /// they are named as shown — never "unseen … none of the ones I checked is a match".
    /// </summary>
    [Fact]
    public void Titles_already_shown_here_are_named_as_shown_not_as_rejected()
    {
        var all = NoneFoundCopy.Compose(new NoneFoundCopy.Facts(
            Ninja() with { Eligible = 6 }, "ninja", MediaScope.Movies, [], 0, 6m, ShownHere: 6));
        Assert.Equal("All 6 unseen ninja movies inside your conditions were already shown in this conversation.", all);

        var one = NoneFoundCopy.Compose(new NoneFoundCopy.Facts(
            Ninja() with { Eligible = 1 }, "ninja", MediaScope.Movies, [], 0, 6m, ShownHere: 1));
        Assert.Equal("The one unseen ninja movie inside your conditions was already shown in this conversation.", one);

        var some = NoneFoundCopy.Compose(new NoneFoundCopy.Facts(
            Ninja() with { Eligible = 8 }, "ninja", MediaScope.Movies, [], 0, 6m, ShownHere: 6, Checked: 2, CheckedOfTagged: 2));
        Assert.Equal(
            "6 of your unseen ninja movies were already shown in this conversation. "
            + "You have 2 other unseen ninja movies tagged that way, but none of the ones I checked is really a match.",
            some);
    }

    /// <summary>D1: with the judge down, no sentence claims a verdict, and the unchecked tray is announced.</summary>
    [Fact]
    public void A_failed_judge_claims_no_verdict_and_points_at_the_unchecked_tray()
    {
        var tagged = NoneFoundCopy.Compose(new NoneFoundCopy.Facts(
            Ninja() with { Eligible = 3 }, "ninja", MediaScope.Movies, [], 0, 6m, Unchecked: 3, JudgedAny: false));
        Assert.Equal(
            "You have 3 unseen ninja movies tagged that way, but I couldn't check them against what you asked this time. "
            + "3 titles I couldn't check against your request are below, marked as unchecked.",
            tagged);
        Assert.DoesNotContain("is really a match", tagged, StringComparison.Ordinal);

        var untagged = NoneFoundCopy.Compose(new NoneFoundCopy.Facts(
            null, "feel-good", MediaScope.Movies, [], 0, 0m, Unchecked: 1, JudgedAny: false));
        Assert.Equal(
            "I couldn't check what I found against what you asked this time. "
            + "1 title I couldn't check against your request is below, marked as unchecked.",
            untagged);
    }

    /// <summary>D3: a match held back only because it has no score is never said to be under the bar.</summary>
    [Fact]
    public void A_match_with_no_score_is_named_as_unscored_not_as_under_the_bar()
    {
        var text = NoneFoundCopy.Compose(new NoneFoundCopy.Facts(
            null, null, MediaScope.Movies, [], 0, 6m, UnscoredHeld: ["Arrival", "Dune"]));
        Assert.EndsWith("Arrival and Dune have no score from your model yet, so your 6.0 bar holds them back — shown below.", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Under your", text, StringComparison.Ordinal);
    }

    /// <summary>Acceptance "weak": the ninja follow-up repeated the first reply word for word.</summary>
    [Fact]
    public void A_reply_identical_to_the_previous_one_says_it_looked_again()
    {
        const string reply = "There are no ninja movies left for you.";
        Assert.Equal($"{NoneFoundCopy.AgainLead} {reply}", NoneFoundCopy.Again(reply, reply));
        Assert.Equal($"{NoneFoundCopy.AgainLead} {reply}", NoneFoundCopy.Again(reply, $"{NoneFoundCopy.AgainLead} {reply}"));
        Assert.Equal(reply, NoneFoundCopy.Again(reply, "Here are three samurai films."));
        Assert.Equal(reply, NoneFoundCopy.Again(reply, null));
    }

    [Fact]
    public void Unscored_titles_and_near_misses_are_counted()
    {
        var supply = Ninja() with { Eligible = 2, Unscored = 2 };
        var text = NoneFoundCopy.Compose(new NoneFoundCopy.Facts(supply, "ninja", MediaScope.Movies, [], 3, 6m));

        Assert.StartsWith("None of your unseen ninja movies clears your 6.0 bar.", text, StringComparison.Ordinal);
        Assert.Contains("2 have no score from your model yet.", text, StringComparison.Ordinal);
        Assert.Contains("3 close-but-not-quite titles are tucked away below.", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// IREV2-SYS-01: the supply counts every tagged title, the judge rules on at most 16 a turn. "None
    /// of the ones I checked is really a match" stands only when every tagged title that clears the
    /// bar was checked; otherwise the reply says how many were. Varied over all, some, one, none of
    /// the tagged titles checked, a single tagged title, a single title checked, and no check at all.
    /// </summary>
    [Theory]
    [InlineData(5, 16, 5, "You have 5 unseen heist movies tagged that way, but none of the ones I checked is really a match.")]
    [InlineData(5, 16, 9, "You have 5 unseen heist movies tagged that way, but none of the ones I checked is really a match.")]
    [InlineData(20, 16, 16, "You have 20 unseen heist movies tagged that way. I checked 16 of them and none is really a match; the other 4 weren't among the 16 titles I checked this time.")]
    [InlineData(5, 16, 1, "You have 5 unseen heist movies tagged that way. I checked 1 of them and it isn't really a match; the other 4 weren't among the 16 titles I checked this time.")]
    [InlineData(5, 16, 4, "You have 5 unseen heist movies tagged that way. I checked 4 of them and none is really a match; the other 1 wasn't among the 16 titles I checked this time.")]
    [InlineData(5, 16, 0, "You have 5 unseen heist movies tagged that way, but none of them was among the 16 titles I checked this time, and none of those is really a match.")]
    [InlineData(1, 16, 0, "You have 1 unseen heist movie tagged that way, but it wasn't among the 16 titles I checked this time, and none of those is really a match.")]
    [InlineData(5, 1, 0, "You have 5 unseen heist movies tagged that way, but none of them was among the 1 title I checked this time, and that one isn't really a match.")]
    [InlineData(5, 0, 0, "You have 5 unseen heist movies tagged that way, but I didn't check them against what you asked this time.")]
    [InlineData(1, 0, 0, "You have 1 unseen heist movie tagged that way, but I didn't check it against what you asked this time.")]
    public void A_tagged_supply_sentence_never_claims_a_check_that_did_not_happen(
        int clearing, int judged, int checkedOfTagged, string expected)
    {
        var text = NoneFoundCopy.Compose(new NoneFoundCopy.Facts(
            Ninja() with { Eligible = clearing }, "heist", MediaScope.Movies, [], 0, 6m,
            Checked: judged, CheckedOfTagged: checkedOfTagged));

        Assert.Equal(expected, text);
    }

    /// <summary>
    /// HREV1-RET-01: v2 never counted which tagged titles its judge saw, and every none-found reply
    /// it gave said "none of the ones I checked is really a match" — about the Liam Neeson titles the
    /// previous turn had shown, and about heist titles no model judged. An uncounted check is no
    /// check. Varied: with titles judged elsewhere, and with nothing judged at all.
    /// </summary>
    [Theory]
    [InlineData(16, "You have 3 unseen ninja movies tagged that way, but none of them was among the 16 titles I checked this time, and none of those is really a match.")]
    [InlineData(0, "You have 3 unseen ninja movies tagged that way, but I didn't check them against what you asked this time.")]
    public void An_uncounted_check_claims_no_check(int judged, string expected)
    {
        var text = NoneFoundCopy.Compose(new NoneFoundCopy.Facts(
            Ninja() with { Eligible = 3 }, "ninja", MediaScope.Movies, [], 0, 6m, Checked: judged, CheckedOfTagged: null));

        Assert.Equal(expected, text);
    }

    /// <summary>With the judge down, the sentence agrees with the one title it is about.</summary>
    [Theory]
    [InlineData(1, "You have 1 unseen kaiju movie tagged that way, but I couldn't check it against what you asked this time.")]
    [InlineData(2, "You have 2 unseen kaiju movies tagged that way, but I couldn't check them against what you asked this time.")]
    public void An_unjudged_tagged_supply_agrees_in_number(int clearing, string expected)
    {
        var text = NoneFoundCopy.Compose(new NoneFoundCopy.Facts(
            Ninja() with { Eligible = clearing }, "kaiju", MediaScope.Movies, [], 0, 6m, JudgedAny: false));

        Assert.Equal(expected, text);
    }

    [Fact]
    public void Tagged_titles_the_judge_rejected_are_not_called_none_left()
    {
        var supply = Ninja() with { Eligible = 3 };
        var text = NoneFoundCopy.Compose(new NoneFoundCopy.Facts(supply, "ninja", MediaScope.Movies, [], 0, 6m, Checked: 3, CheckedOfTagged: 3));

        Assert.StartsWith(
            "You have 3 unseen ninja movies tagged that way, but none of the ones I checked is really a match.",
            text,
            StringComparison.Ordinal);
        Assert.DoesNotContain("left for you", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// No count stands behind the topic words, so they are not repeated back: the live "like John
    /// Wick but funnier" turn read "I couldn't find any witty action playful tone movies".
    /// </summary>
    [Theory]
    [InlineData(MediaScope.Movies)]
    [InlineData(MediaScope.Any)]
    public void No_supply_still_answers_honestly_without_repeating_the_models_words(MediaScope media) =>
        Assert.Equal(
            "Nothing I found matches what you asked closely enough to show.",
            NoneFoundCopy.Compose(new NoneFoundCopy.Facts(null, "witty action", media, [], 0, 0m)));

    [Fact]
    public void No_concept_says_nothing_matched()
    {
        Assert.Equal(
            "Nothing I found matches what you asked closely enough to show.",
            NoneFoundCopy.Compose(new NoneFoundCopy.Facts(null, null, MediaScope.Any, [], 0, 0m)));
    }

    [Theory]
    [InlineData("de-DE")]
    [InlineData("ar-SA")]
    public void The_copy_is_invariant(string culture)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            var text = NoneFoundCopy.Compose(new NoneFoundCopy.Facts(
                Ninja() with { Eligible = 1, BelowFloor = 1 }, "ninja", MediaScope.Movies, ["Blade"], 0, 6.5m));
            Assert.Contains("6.5 bar", text, StringComparison.Ordinal);
            Assert.Contains("Of the 11 ninja movies", text, StringComparison.Ordinal);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    /// <summary>
    /// HREV5-RET-03: the supply counts only the TAGGED shelf, and the floor tray may hold a match
    /// another arm found. "There are no period drama shows left for you … Your 6.0 bar holds back 1
    /// title I couldn't check against your request — shown below" denied the card it then showed. With
    /// a possible match on screen the lead is about the tagged ones, and the tray's cards are named as
    /// OTHER possible matches. Varied over each tray that can hold one, and over a reply with none.
    /// </summary>
    [Theory]
    // The floor tray holds an unjudged card (the keyless "period drama series" turn).
    [InlineData(0, 1, 0, 0,
        "None of the tagged period drama shows are left for you.",
        "Your 6.0 bar holds back 1 other possible match I couldn't check against your request — shown below.")]
    [InlineData(0, 2, 0, 0,
        "None of the tagged period drama shows are left for you.",
        "Your 6.0 bar holds back 2 other possible matches I couldn't check against your request — shown below.")]
    // A judged match under the bar, named.
    [InlineData(1, 0, 0, 0, "None of the tagged period drama shows are left for you.", "Under your 6.0 bar: Bleak House — shown below.")]
    // The unchecked tray.
    [InlineData(0, 0, 3, 0,
        "None of the tagged period drama shows are left for you.",
        "3 other possible matches I couldn't check against your request are below, marked as unchecked.")]
    // A match the account's model has not scored yet.
    [InlineData(0, 0, 0, 1, "None of the tagged period drama shows are left for you.", "Bleak House has no score from your model yet")]
    // Nothing on screen: the shelf really is empty, and the reply says so.
    [InlineData(0, 0, 0, 0, "There are no period drama shows left for you.", "")]
    public void A_none_found_lead_never_denies_a_card_it_shows(int underBar, int held, int uncheckedCards, int unscored, string lead, string tray)
    {
        var supply = new SupplyReport(
            MatchingInCatalogue: 145, AlreadySeen: 7, SavedOrDismissed: 1, ExcludedByConditions: 137, Eligible: 0,
            BelowFloor: 0, Unscored: 0, Knockouts: [], LowVoteMatches: 0, MinVotes: 50, PredictedFloor: 6m, KnockoutsMeasured: true);
        var names = Enumerable.Range(0, underBar).Select(_ => "Bleak House").ToList();
        var text = NoneFoundCopy.Compose(new NoneFoundCopy.Facts(
            supply, "period drama", MediaScope.Shows, names, 0, 6m,
            Unchecked: uncheckedCards, JudgedAny: underBar > 0, HeldUnchecked: held,
            UnscoredHeld: unscored > 0 ? ["Bleak House"] : null));

        Assert.StartsWith(lead, text, StringComparison.Ordinal);
        Assert.Contains(tray, text, StringComparison.Ordinal);
        if (underBar + held + uncheckedCards + unscored > 0)
        {
            Assert.DoesNotContain("There are no", text, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// HREV5-RET-02: a turn no model read that found nothing at all says the reading failed — never
    /// "I didn't find anything to show for that", which told the founder an actor with 80 films in the
    /// catalogue had nothing. Behind a lead that already says the request was not fully read, it says
    /// so without repeating itself. A model's reading that found nothing keeps the plain line.
    /// </summary>
    [Theory]
    [InlineData(true, false, NoneFoundCopy.UnreadNothing)]
    [InlineData(true, true, NoneFoundCopy.UnreadNothingLed)]
    [InlineData(false, false, NoneFoundCopy.NothingFoundUnchecked)]
    public void A_model_less_turn_that_found_nothing_owns_up_to_the_reading(bool unread, bool led, string expected)
    {
        foreach (var supply in new SupplyReport?[] { null, Ninja() with { MatchingInCatalogue = 0, AlreadySeen = 0, SavedOrDismissed = 0, ExcludedByConditions = 0 } })
        {
            var text = NoneFoundCopy.Compose(new NoneFoundCopy.Facts(
                supply, "liam neeson", MediaScope.Movies, [], 0, 6m, JudgedAny: false, Unread: unread, UnreadLed: led));

            Assert.Equal(expected, text);
        }
    }
}
