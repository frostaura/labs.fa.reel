using FrostAura.Reel.Application.Lens;
using FrostAura.Reel.Domain.Lens;

namespace FrostAura.Reel.Tests.Lens;

/// <summary>
/// The length repair, pinned against the two shapes that actually reached the founder's screen.
///
/// <para>Both came from the same cause — a structured-output <c>maxLength</c> is enforced by
/// constrained decoding, so the string stops on the exact character rather than at the end of a
/// thought. One stopped mid-word; the other closed a clause it had already abandoned.</para>
///
/// <para>The other half of these tests is the more important half: everything that is ALREADY
/// well-formed must come back untouched. A repair that quietly eats the last word of a short hook
/// is worse than the defect it was written for, because the defect is visible.</para>
/// </summary>
public class LensCopyTidyTests
{
    /// <summary>
    /// Live example one, reconstructed to the character. A lens that ran to the old 420-char cap
    /// and stopped inside "switch" — the reader saw "…a crowd-pleasing sw".
    /// </summary>
    private static string CutMidWord()
    {
        const string tail = "and a crowd-pleasing switch";

        // Four characters longer than the budget, so slicing at the cap lands inside "switch" and
        // leaves the "sw" the founder actually read.
        var lead = new string('a', LensNote.RequestLensChars - tail.Length + 4);
        return (lead + tail)[..LensNote.RequestLensChars];
    }

    [Fact]
    public void A_word_the_schema_cut_in_half_is_dropped_and_the_cut_is_admitted()
    {
        var cut = CutMidWord();
        Assert.EndsWith(" sw", cut, StringComparison.Ordinal);

        var tidied = LensGuards.TidyField(cut, LensNote.RequestLensChars);

        Assert.EndsWith("and a crowd-pleasing…", tidied, StringComparison.Ordinal);
        Assert.DoesNotContain(" sw…", tidied, StringComparison.Ordinal);
        Assert.True(tidied.Length < cut.Length);
    }

    [Fact]
    public void A_clause_abandoned_before_the_full_stop_loses_the_dangling_comma()
    {
        const string live = "Bleak where you like bleak, and it leans hard on the marriage strain,.";

        var tidied = LensGuards.TidyField(live, LensNote.RequestCautionChars);

        Assert.Equal("Bleak where you like bleak, and it leans hard on the marriage strain.", tidied);
    }

    [Theory]
    [InlineData("It leans on the marriage strain,", "It leans on the marriage strain…")]
    [InlineData("It leans on the marriage strain -", "It leans on the marriage strain…")]
    [InlineData("It leans on the marriage strain;.", "It leans on the marriage strain.")]
    [InlineData("It leans on the marriage strain ,.", "It leans on the marriage strain.")]
    [InlineData("It leans on the marriage strain..", "It leans on the marriage strain.")]
    [InlineData("It leans on the marriage strain,?", "It leans on the marriage strain?")]
    [InlineData("It leans on the marriage strain,…", "It leans on the marriage strain…")]
    public void Stranded_punctuation_collapses_to_one_mark(string raw, string expected)
    {
        Assert.Equal(expected, LensGuards.TidyField(raw, LensNote.RequestCautionChars));
    }

    [Theory]
    [InlineData("The bleak crime piece you have been circling.")]
    [InlineData("Are you sure you want this on a Tuesday?")]
    [InlineData("It does not let up. Not once!")]
    [InlineData("You already know how this ends…")]
    [InlineData("It takes its time, then it does not…")]
    [InlineData("She says only \"drive.\"")]
    [InlineData("The tense one you keep scrolling past")]
    [InlineData("Wait for it...")]
    [InlineData("A hard yes, with one reservation?!")]
    public void Copy_that_is_already_well_formed_is_returned_untouched(string clean)
    {
        Assert.Same(clean, LensGuards.TidyField(clean, LensNote.RequestCautionChars));
    }

    [Fact]
    public void A_short_field_with_no_full_stop_keeps_its_last_word()
    {
        // The length gate is the whole reason this is safe: a hook is a headline, and headlines
        // routinely have no terminal punctuation. Nothing was truncated here, so nothing goes.
        const string headline = "The tense crime piece you have been circling";

        Assert.Same(headline, LensGuards.TidyField(headline, LensNote.RequestHookChars));
    }

    [Fact]
    public void A_hook_that_ran_to_its_own_cap_is_repaired_on_the_hook_budget()
    {
        const string tail = "you keep circling back to it lately";
        var hook = (new string('b', LensNote.RequestHookChars - tail.Length) + tail)[..LensNote.RequestHookChars];

        var tidied = LensGuards.TidyField(hook, LensNote.RequestHookChars);

        Assert.EndsWith("back to it…", tidied, StringComparison.Ordinal);
    }

    [Fact]
    public void A_null_caution_stays_null()
    {
        Assert.Null(LensGuards.TidyCopy(null, LensNote.RequestCautionChars));
    }

    [Fact]
    public void Tidy_repairs_every_prose_field_and_leaves_the_rest_of_the_note_alone()
    {
        var anchors = new[] { new LensAnchor("Sicario", "the same cold procedural register, and you went for it") };
        var note = new LensNote(
            CutMidWord(),
            "The bleak crime piece you have been circling.",
            "It leans hard on the marriage strain,.",
            anchors,
            "heavy",
            "tonight-alone");

        var tidied = LensGuards.Tidy(note);

        Assert.EndsWith("and a crowd-pleasing…", tidied.Lens, StringComparison.Ordinal);
        Assert.Equal("It leans hard on the marriage strain.", tidied.Caution);
        Assert.Same(note.Hook, tidied.Hook);
        Assert.Same(note.Anchors, tidied.Anchors);
        Assert.Equal(note.Mood, tidied.Mood);
        Assert.Equal(note.WhenToWatch, tidied.WhenToWatch);
    }

    [Fact]
    public void A_note_that_needs_nothing_is_the_same_object()
    {
        var note = new LensNote(
            "You get the slow-burn register you keep coming back to, on its own terms.",
            "The bleak crime piece you have been circling.",
            null,
            [],
            "heavy",
            "tonight-alone");

        Assert.Same(note, LensGuards.Tidy(note));
    }

    [Fact]
    public void The_requested_budgets_sit_under_the_ceilings_that_parse_and_storage_still_enforce()
    {
        // The two numbers are allowed to differ but not to invert: a request above the parse
        // ceiling would ask for copy the parser then refuses, which is the worst of both.
        Assert.True(LensNote.RequestLensChars < LensNote.MaxLensChars);
        Assert.True(LensNote.RequestHookChars < LensNote.MaxHookChars);
        Assert.True(LensNote.RequestCautionChars < LensNote.MaxCautionChars);
    }
}
