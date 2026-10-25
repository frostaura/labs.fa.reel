using FrostAura.Reel.Application.Lens;
using FrostAura.Reel.Application.Ranking;
using FrostAura.Reel.Domain.Lens;
using FrostAura.Reel.Domain.Ports;

namespace FrostAura.Reel.Tests.Lens;

/// <summary>
/// What a card shows from a lens: a complete clause, never a severed sentence, and never a clause
/// that argues against its own pick — plus the hedge guard the lens, the re-rank parser and the
/// feed now share.
///
/// <para>Round-1 surface review: 6 of 8 cards carrying a lens clause showed a mid-sentence "…", and
/// two rendered hedges on Home ("…though this is a much slower drama than the action and adventure
/// you often prefer", "…a quieter, heavier detour").</para>
/// </summary>
public class LensClauseTests
{
    private static LensNote Note(string lens, string hook = "The bleak crime piece you have been circling.") =>
        new(lens, hook, null, [], "heavy", "tonight-alone");

    // ── Complete clauses ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_long_first_sentence_yields_its_leading_clauses_closed_with_a_full_stop()
    {
        // 205 characters before the first full stop: the old rule cut this at a space and added "…".
        var lens = "A washed-up fighter takes one last bout in a city that has forgotten him, while his estranged "
            + "daughter keeps the books and the promoter keeps the lights on for the one night that might still "
            + "matter. You will know the register.";

        var clause = Note(lens).FirstClause()!;

        Assert.Equal("A washed-up fighter takes one last bout in a city that has forgotten him.", clause);
        Assert.DoesNotContain("…", clause, StringComparison.Ordinal);
    }

    [Fact]
    public void Clauses_are_joined_while_they_fit()
    {
        var lens = "A con artist runs one more job in Lagos; the mark is her brother, the stakes are the family house, "
            + "and the clock is the rainy season that turns every road to mud before the money can move.";

        var clause = Note(lens).FirstClause()!;

        Assert.Equal("A con artist runs one more job in Lagos.", clause);

        // Wider limit, more clauses — the cut moves to the next real boundary, never into a list.
        var wider = Note(lens).FirstClause(200)!;
        Assert.Equal(
            "A con artist runs one more job in Lagos; the mark is her brother, the stakes are the family house, and the clock is the rainy season that turns every road to mud before the money can move.",
            wider);
    }

    [Fact]
    public void A_comma_inside_a_list_is_never_a_cut_point()
    {
        // The list commas do not open a clause; the only boundary is ", where".
        var lens = "Heists, double-crosses, rooftop chases and a slow-burn betrayal stretched across three cities and two "
            + "decades of grudges, where every partner is lying to you about something and the money is never the point.";

        var clause = Note(lens).FirstClause()!;

        Assert.Equal(
            "Heists, double-crosses, rooftop chases and a slow-burn betrayal stretched across three cities and two decades of grudges.",
            clause);
    }

    [Theory]
    [InlineData("S.W.A.T. officers hold a precinct through one long night. The siege is the whole film.", "S.W.A.T. officers hold a precinct through one long night.")]
    [InlineData("Mr. Ripley talks his way into a life that is not his. You watch him do it.", "Mr. Ripley talks his way into a life that is not his.")]
    [InlineData("U.S. marshals chase a man who says he is innocent. It never lets up.", "U.S. marshals chase a man who says he is innocent.")]
    [InlineData("Dr. Strangelove plays the end of the world as farce! You laugh anyway.", "Dr. Strangelove plays the end of the world as farce!")]
    public void Abbreviations_do_not_end_the_sentence(string lens, string expected)
    {
        Assert.Equal(expected, Note(lens).FirstClause());
    }

    [Fact]
    public void A_dash_closed_up_against_its_words_is_a_clause_boundary_and_a_hyphen_is_not()
    {
        var lens = "A small-town sheriff and a big-city detective work one murder from opposite ends of the county—and "
            + "neither of them trusts the other with the one witness who saw the car, or with the tape that proves it.";

        Assert.Equal(
            "A small-town sheriff and a big-city detective work one murder from opposite ends of the county.",
            Note(lens).FirstClause());
    }

    [Fact]
    public void An_opening_clause_longer_than_the_limit_is_still_trimmed_on_a_word_with_an_ellipsis()
    {
        var lens = string.Join(" ", Enumerable.Repeat("wordy", 50)) + ".";

        var clause = Note(lens).FirstClause(60)!;

        Assert.True(clause.Length <= 61, clause);
        Assert.EndsWith("wordy…", clause, StringComparison.Ordinal);
    }

    // ── Never a hedge ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_hedging_clause_is_cut_and_the_clause_before_it_stands()
    {
        // The Greatest, as rendered on Home in the round-1 review — the whole sentence fits in 160,
        // which is exactly why the old rule showed it.
        var lens = "A father and son square up over boxing and legacy, though this is a much slower drama than the "
            + "action and adventure you often prefer.";

        var clause = Note(lens).FirstClause();

        Assert.Equal("A father and son square up over boxing and legacy.", clause);
        Assert.False(SecondPersonVoice.SoundsHedged(clause));
    }

    [Theory]
    [InlineData("Though it is slower than you usually like, this is a careful study of a marriage under strain.")]
    [InlineData("A quieter, heavier detour from the thrillers you rate.")]
    public void A_lens_that_opens_on_a_hedge_gives_no_clause_at_all(string lens)
    {
        // Nothing before the hedge to keep: null, so the card falls back to its own line rather
        // than rendering an empty string or the hedge.
        Assert.Null(Note(lens).FirstClause());
    }

    [Fact]
    public void An_empty_lens_gives_no_clause()
    {
        Assert.Null(Note("   ").FirstClause());
    }

    [Theory]
    [InlineData("A quieter, heavier detour you will still want to take.")]
    [InlineData("A heist film, though slower than you usually like.")]
    public void A_hedged_hook_is_refused_at_generation_and_not_served_from_storage(string hook)
    {
        var note = Note("You get the slow-burn register you keep coming back to, on its own terms.", hook);

        Assert.Equal(LensRejectionReasons.HedgedHook, LensGuards.Inspect(note, "Parked"));
        Assert.False(LensGuards.IsServable(note));
    }

    [Fact]
    public void A_clean_hook_is_served_and_a_hedge_in_the_body_alone_does_not_refuse_the_note()
    {
        // The body may weigh the pick — a plot "though" reads exactly like a hedge to a regex — so
        // the note stands, and only what captions the card is cut.
        var note = Note("A widow rebuilds the farm, though the town wants her gone. You get the patient register you rate.");

        Assert.Null(LensGuards.Inspect(note, "The Farm"));
        Assert.True(LensGuards.IsServable(note));
        Assert.Equal("A widow rebuilds the farm.", note.FirstClause());
    }

    // ── One hedge definition ─────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("A sharp heist, though slower than you like.", true)]
    [InlineData("Taut, but its pacing is slower than your usual.", true)]
    [InlineData("A softer match for your taste in heists.", true)]
    [InlineData("This one may test your patience.", true)]
    [InlineData("It is far from your usual fare.", true)]
    [InlineData("A lighter sci-fi than you seek.", true)]
    [InlineData("Nothing less than a triumph of craft.", false)]
    [InlineData("A little of the old magic you rate.", false)]
    [InlineData("Detour is the lean noir you keep returning to.", false)]
    [InlineData("All the menace, but without the gore you dislike.", false)]
    [InlineData("Despite its quiet start, it builds to the payoff you rate.", false)]
    public void The_shared_hedge_guard_agrees_with_the_feeds_measured_list(string line, bool hedged)
    {
        // Two definitions exist until WS-5 folds FeedWhyGuard.IsHedged into this one (change
        // request); until then they must not disagree on the measured shapes.
        Assert.Equal(hedged, SecondPersonVoice.SoundsHedged(line));
        Assert.Equal(hedged, FeedWhyGuard.IsHedged(line));
    }

    [Theory]
    [InlineData("…a quieter, heavier detour")]
    [InlineData("A slower, more deliberate, heavier detour from your thrillers.")]
    public void The_shared_guard_catches_a_detour_with_several_adjectives(string line)
    {
        // The one widening over the feed's list, which allowed a single adjective and no comma.
        Assert.True(SecondPersonVoice.SoundsHedged(line));
    }
}
