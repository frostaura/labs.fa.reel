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
    public void A_single_long_sentence_is_trimmed_on_a_word_boundary()
    {
        var note = Note(lens: string.Join(" ", Enumerable.Repeat("word", 80)));
        var clause = note.FirstClause(60);

        Assert.True(clause.Length <= 61, clause);
        Assert.EndsWith("…", clause, StringComparison.Ordinal);
        Assert.DoesNotContain("wor…", clause, StringComparison.Ordinal);
    }
}
