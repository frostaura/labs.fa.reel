using FrostAura.Reel.Application.Lens;
using FrostAura.Reel.Domain.Lens;

namespace FrostAura.Reel.Tests.Lens;

/// <summary>
/// The deterministic half of the lens guard chain — everything that can be decided without a
/// database.
///
/// <para>These are tuned for false positives over false negatives, deliberately, and the tests say
/// so: losing a good note costs one templater fallback, and shipping a spoiler costs the trust the
/// whole honest-copy programme is built on.</para>
/// </summary>
public class LensGuardTests
{
    private static LensNote Note(
        string lens = "You get the slow-burn register you keep coming back to, on its own terms.",
        string hook = "The bleak crime piece you have been circling.",
        string? caution = null,
        LensAnchor[]? anchors = null,
        string mood = "heavy") =>
        new(lens, hook, caution, anchors ?? [], mood, "tonight-alone");

    [Fact]
    public void A_clean_note_passes()
    {
        Assert.Null(LensGuards.Inspect(Note(), "Sicario: Day of the Soldado"));
    }

    // ── A cut-off hook is never served (IR2S-03) ────────────────────────────────────────────

    [Theory]
    // The two the eval's Home rail rendered, verbatim, and two stored shapes from its database.
    [InlineData("You get a heavy family drama where grief and pregnancy bring strangers into one…")]
    [InlineData("Give this a quiet evening if you want friendship and emotional weight over…")]
    [InlineData("You get the long con played straight to the…")]
    [InlineData("You get a crew with nothing left in…  ")]
    // A written-out ellipsis trails off just the same.
    [InlineData("You get a quiet heist that never quite...")]
    // A row stored before the repair: it sits on its cap, mid-word, with no terminal at all.
    [InlineData("You get a heavy family drama where grief and pregnancy bring strangers into one ro")]
    public void A_hook_cut_off_is_dropped_and_the_rest_of_the_note_is_served(string hook)
    {
        var note = Note(hook: hook);

        Assert.True(LensGuards.IsCutOff(hook));
        var served = LensGuards.ForServing(note);
        Assert.NotNull(served);
        Assert.Equal(string.Empty, served.Hook);
        Assert.Equal(note.Lens, served.Lens); // the lens is complete prose and stays on every surface
        Assert.Equal(note.Mood, served.Mood);
        Assert.Null(LensGuards.ServableHook(note));
    }

    [Theory]
    [InlineData("The bleak crime piece you have been circling.")]
    [InlineData("Expect a slow hour that earns its last ten minutes!")]
    [InlineData("You'll want to know who's lying?")]
    [InlineData("Give this an evening alone")] // a short headline without a stop: nothing was lost
    [InlineData("You get the \"one more job\" story, done properly.\u201D")]
    public void A_finished_hook_is_served_untouched(string hook)
    {
        var note = Note(hook: hook);

        Assert.False(LensGuards.IsCutOff(hook));
        Assert.Same(note, LensGuards.ForServing(note));
        Assert.Equal(hook, LensGuards.ServableHook(note));
    }

    [Fact]
    public void A_hedged_hook_still_takes_the_whole_note_off_the_ladder()
    {
        // The cut-off rule must not soften the hedge rule: a hook that argues against its pick is
        // a reason to fall back to an older note, not to show this one's lens.
        var note = Note(hook: "A quieter, heavier detour you might still take.");

        Assert.Null(LensGuards.ForServing(note));
        Assert.Null(LensGuards.ServableHook(note));
        Assert.Null(LensGuards.ServableHook(null));
    }

    [Fact]
    public void What_tidy_leaves_on_a_capped_hook_is_what_serving_drops()
    {
        // The repair and the serve rule agree by construction: a hook the schema cut at its cap
        // is tidied to an ellipsis at write time, and that ellipsis is what keeps it off the card.
        const string written = "You get a heavy family drama where grief and pregnancy bring strangers into one room together.";
        var capped = written[..LensNote.RequestHookChars];

        var tidied = LensGuards.Tidy(Note(hook: capped));

        Assert.EndsWith("…", tidied.Hook, StringComparison.Ordinal);
        Assert.Equal(string.Empty, LensGuards.ForServing(tidied)!.Hook);
    }

    // ── Spoilers ────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("The twist lands in the second hour and reframes everything.")]
    [InlineData("It turns out the detective was compromised all along.")]
    [InlineData("You watch the final scene knowing exactly what it cost.")]
    [InlineData("The lead dies before the halfway mark.")]
    [InlineData("By the end you understand why she left.")]
    [InlineData("The killer is closer than the case suggests.")]
    [InlineData("It reveals that the town has been covering for him.")]
    public void The_spoiler_lexicon_refuses_a_note(string lens)
    {
        Assert.Equal(LensRejectionReasons.Spoiler, LensGuards.Inspect(Note(lens: lens), "Some Film"));
        Assert.True(SpoilerLexicon.Spoils(lens));
    }

    [Fact]
    public void A_spoiler_in_the_hook_is_caught_too()
    {
        // The most visible place a spoiler could land — a one-line hook the card renders whole.
        Assert.Equal(
            LensRejectionReasons.Spoiler,
            LensGuards.Inspect(Note(hook: "The ending will sit with you."), "Some Film"));
    }

    [Fact]
    public void A_spoiler_in_an_anchor_relation_is_caught_too()
    {
        Assert.Equal(
            LensRejectionReasons.Spoiler,
            LensGuards.Inspect(
                Note(anchors: [new LensAnchor("Heat", "the same weight, and it dies harder")]),
                "Some Film"));
    }

    [Fact]
    public void First_act_premise_language_is_not_a_spoiler()
    {
        var lens = "A cartel task force opens with a raid that goes wrong, and you get the moral "
            + "flatness you keep rating highly.";
        Assert.False(SpoilerLexicon.Spoils(lens));
        Assert.Null(LensGuards.Inspect(Note(lens: lens), "Sicario"));
    }

    // ── Voice ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_third_person_leak_is_refused()
    {
        Assert.Equal(
            LensRejectionReasons.ThirdPerson,
            LensGuards.Inspect(Note(lens: "This matches their taste for slow procedurals."), "Some Film"));
    }

    [Fact]
    public void The_banned_opener_is_refused()
    {
        Assert.Equal(
            LensRejectionReasons.BannedOpener,
            LensGuards.Inspect(Note(lens: "Because you loved Heat, this one lands the same way."), "Some Film"));
    }

    // ── Numbers ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Restating_the_prediction_is_refused()
    {
        // The number is given to the model as context so it knows how confidently to pitch, and is
        // forbidden in the prose. That is what lets the cache key omit the model artifact — a note
        // quoting 8.4 would be wrong the morning after a retrain.
        Assert.Equal(
            LensRejectionReasons.RestatedNumber,
            LensGuards.Inspect(Note(lens: "Reel puts this at 8.4 for you, and it is not wrong."), "Some Film"));
        Assert.Equal(
            LensRejectionReasons.RestatedNumber,
            LensGuards.Inspect(Note(hook: "A near-perfect ten out of ten for your taste."), "Some Film"));
    }

    [Fact]
    public void A_title_with_a_year_in_its_name_is_not_read_as_a_score()
    {
        // The guard that cannot tell "Blade Runner 2049" from "8.4 for you" refuses every note
        // about a film with a number in its name.
        Assert.Null(LensGuards.Inspect(
            Note(lens: "Blade Runner 2049 territory, slowed down and stripped of its neon."),
            "Blade Runner 2049"));

        Assert.Null(LensGuards.Inspect(
            Note(
                lens: "It sits somewhere near 1917 in how relentlessly it moves.",
                anchors: [new LensAnchor("1917", "the same unbroken forward push")]),
            "Some Film"));
    }

    // ── The caution asymmetry ───────────────────────────────────────────────────────────────

    [Fact]
    public void A_caution_may_argue_against_its_own_card()
    {
        // The whole point of the field, and the one place SoundsAntiRecommendation is deliberately
        // NOT applied. A reservation that can never voice a reservation is what makes copy read
        // like marketing.
        var note = Note(caution: "You may bounce off how slowly it commits — not for you on a tired night.");
        Assert.Null(LensGuards.Inspect(note, "Some Film"));
    }

    [Fact]
    public void The_lens_and_hook_may_not_argue_against_the_card()
    {
        Assert.Equal(
            LensRejectionReasons.AntiRecommendation,
            LensGuards.Inspect(Note(lens: "Honestly, you may bounce off this one entirely."), "Some Film"));
        Assert.Equal(
            LensRejectionReasons.AntiRecommendation,
            LensGuards.Inspect(Note(hook: "Probably one to sit this one out."), "Some Film"));
    }

    // ── Marketing register (prompt v4) ──────────────────────────────────────────────────────

    [Theory]
    [InlineData("gripping")]
    [InlineData("must-see")]
    [InlineData("Must-See")]
    [InlineData("unmissable")]
    [InlineData("masterful")]
    [InlineData("stunning")]
    [InlineData("thrilling ride")]
    [InlineData("edge of your seat")]
    [InlineData("tour de force")]
    [InlineData("unforgettable")]
    public void A_banned_marketing_phrase_in_the_lens_is_refused_with_the_word_that_did_it(string word)
    {
        var reason = LensGuards.Inspect(
            Note(lens: "You get a " + word + " hour of it, and it never lets up on you."),
            "Some Film");

        Assert.Equal("marketing:" + word.ToLowerInvariant(), reason);
    }

    [Fact]
    public void A_hook_that_calls_the_film_unmissable_is_refused()
    {
        // The live register defect, in the field that is most visible: the card and push headline.
        Assert.Equal(
            "marketing:unmissable",
            LensGuards.Inspect(Note(hook: "You will find this unmissable."), "Some Film"));
    }

    [Fact]
    public void The_marketing_token_is_normalised_so_the_audit_can_group_by_it()
    {
        // "Must-See", "must see" and "must-see" are one prompt defect. A token that varies with
        // the model's capitalisation splits one diagnosis into three rows on the audit.
        Assert.Equal("must see", LensGuards.FirstMarketingWord("A Must  See on a slow night."));
        Assert.Equal("edge of your seat", LensGuards.FirstMarketingWord("Edge of your seat stuff."));
        Assert.Null(LensGuards.FirstMarketingWord("A tense, patient hour that never raises its voice."));
    }

    [Fact]
    public void A_caution_may_still_name_the_marketing_word_it_is_arguing_against()
    {
        // Same asymmetry that lets the caution argue against its own card. "It is not the gripping
        // thing the trailer sold" is the reservation doing exactly its job.
        Assert.Null(LensGuards.Inspect(
            Note(caution: "It is not the gripping thing the trailer sold you; it idles for an hour."),
            "Some Film"));
    }

    // ── Hook voice (prompt v4) ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Jodie Foster turns grief into a tight revenge chase.")]
    [InlineData("A grim London mind-warp that stays moving.")]
    [InlineData("A quiet, relentless piece of work.")]
    public void A_third_person_tagline_hook_is_refused(string hook)
    {
        // Both of the first two came off the founder-only audit at prompt v3, where meanVoice sat
        // at 0.57. Neither can be repaired into a hook — the fix is a different sentence — so the
        // note is refused and the surface falls back rather than rendering a poster line.
        Assert.Equal(LensRejectionReasons.HookVoice, LensGuards.Inspect(Note(hook: hook), "Some Film"));
        Assert.False(LensGuards.IsAddressedToTheViewer(hook));
    }

    [Theory]
    [InlineData("You get a dark New York crime-thriller with a revenge engine.")]
    [InlineData("The bleak crime piece you have been circling.")]
    [InlineData("Your slowest evening this week, and it earns it.")]
    [InlineData("Expect a long, flat hour before it commits.")]
    [InlineData("Give this an evening if you have one to lose.")]
    [InlineData("Save it for a night with nothing after it.")]
    public void A_hook_addressed_to_the_viewer_passes(string hook)
    {
        Assert.True(LensGuards.IsAddressedToTheViewer(hook));
        Assert.Null(LensGuards.Inspect(Note(hook: hook), "Some Film"));
    }

    [Fact]
    public void The_imperative_allowlist_is_closed_rather_than_any_leading_verb()
    {
        // "Turns grief into a tight revenge chase" starts with a verb and is precisely the poster
        // line the guard exists to refuse. An open-ended "starts with a verb" test passes it.
        Assert.False(LensGuards.IsAddressedToTheViewer("Turns grief into a tight revenge chase."));
        Assert.False(LensGuards.IsAddressedToTheViewer("Delivers a cold, procedural hour."));
    }

    [Fact]
    public void A_worse_fault_in_the_hook_outranks_its_voice()
    {
        // Inspect is ordered worst-first, and the register rules are the least serious thing that
        // can be wrong with a note. A hook that argues against its own card is filed as that.
        Assert.Equal(
            LensRejectionReasons.AntiRecommendation,
            LensGuards.Inspect(Note(hook: "Probably one to sit this one out."), "Some Film"));
        Assert.Equal(
            LensRejectionReasons.Spoiler,
            LensGuards.Inspect(Note(hook: "The ending lands badly."), "Some Film"));
    }

    // ── Provenance ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void An_invented_person_is_fatal_and_outranks_an_invented_title()
    {
        // The token rides the reason so the audit can say WHICH name sank it — a count of eleven
        // refusals is a number, and nine of them naming the same title is a diagnosis.
        Assert.Equal(
            "invented-person:Denis Villeneuve",
            LensGuards.InspectProvenance(["Denis Villeneuve"], ["Some Film Nobody Rated"]));
    }

    [Fact]
    public void A_title_named_in_the_prose_that_is_not_in_the_library_is_fatal()
    {
        Assert.Equal(
            "invented-anchor:Some Film Nobody Rated",
            LensGuards.InspectProvenance([], ["Some Film Nobody Rated"]));
    }

    [Fact]
    public void A_reason_token_is_clipped_to_the_width_of_the_column_that_stores_it()
    {
        // RejectionReason is varchar(128). A pathological name must truncate the EVIDENCE, never
        // the reason — a row reading "invented-anch" would be unreadable and ungroupable.
        var reason = LensRejectionReasons.WithToken(LensRejectionReasons.InventedAnchor, new string('x', 400));

        Assert.True(reason.Length <= 128);
        Assert.StartsWith(LensRejectionReasons.InventedAnchor + ":", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_reason_with_no_token_stays_exactly_the_bare_reason()
    {
        Assert.Equal(
            LensRejectionReasons.Spoiler,
            LensRejectionReasons.WithToken(LensRejectionReasons.Spoiler, null));
        Assert.Equal(
            LensRejectionReasons.Spoiler,
            LensRejectionReasons.WithToken(LensRejectionReasons.Spoiler, "   "));
    }

    [Fact]
    public void Nothing_invented_passes_provenance()
    {
        Assert.Null(LensGuards.InspectProvenance([], []));
    }

    [Fact]
    public void Candidate_names_pick_up_multi_word_proper_nouns()
    {
        var names = LensGuards.CandidateNames(
            Note(lens: "Denis Villeneuve territory, shot by Roger Deakins, with none of the sprawl."));

        Assert.Contains("Denis Villeneuve", names);
        Assert.Contains("Roger Deakins", names);
    }

    [Fact]
    public void Normalising_a_title_ignores_case_quotes_and_spacing()
    {
        Assert.Equal(
            LensGuards.NormaliseTitle("  “Sicario”  "),
            LensGuards.NormaliseTitle("sicario"));
    }

    [Fact]
    public void Masking_removes_the_longest_matching_title_first()
    {
        // Masking "Blade Runner" before "Blade Runner 2049" would leave the year exposed and the
        // note would be refused for restating a number it never restated.
        var masked = LensGuards.MaskTitles(
            "Blade Runner 2049 is closer to this than Blade Runner is.",
            ["Blade Runner", "Blade Runner 2049"]);

        Assert.DoesNotContain("2049", masked, StringComparison.Ordinal);
    }

    // ── FirstClause ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_first_clause_stops_at_the_first_sentence()
    {
        var note = Note(lens: "A cartel task force, told flat. You will recognise the register.");
        Assert.Equal("A cartel task force, told flat.", note.FirstClause());
    }

    [Fact]
    public void A_single_long_sentence_with_nowhere_complete_to_cut_gives_no_clause()
    {
        // Was: trimmed on a word boundary with "…". A severed caption is the R1-25 defect; null
        // lets the card fall back to its own line.
        var note = Note(lens: string.Join(" ", Enumerable.Repeat("word", 80)));

        Assert.Null(note.FirstClause(60));
    }
}
