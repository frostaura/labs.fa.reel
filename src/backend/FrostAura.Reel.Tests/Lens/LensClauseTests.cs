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
    public void An_opening_clause_with_no_complete_cut_inside_the_limit_gives_no_clause_rather_than_a_severed_one()
    {
        // The old rule trimmed this on a word and added "…" — the severed caption R1-25 is about.
        // A missing caption is honest: the card shows its own line instead.
        var lens = string.Join(" ", Enumerable.Repeat("wordy", 50)) + ".";

        Assert.Null(Note(lens).FirstClause(60));
    }

    // ── Complete clauses out of a long main clause (stored lenses, verbatim) ─────────────────

    [Theory]
    [InlineData(
        "If you want a lighter adventure that still keeps the pace moving, Treasure Buddies gives you a race against a devious cat, booby traps, puzzles, and a mysterious tomb. It is family-sized spectacle with a treasure hunt spine.",
        "If you want a lighter adventure that still keeps the pace moving, Treasure Buddies gives you a race against a devious cat, booby traps and puzzles.")]
    [InlineData(
        "You get a crime-and-ghosts procedural built around Allison Dubois, who works in the District Attorney’s office and uses intuition, dreams, and chats with the dead to help solve crimes and find missing people. It keeps the casework forward.",
        "You get a crime-and-ghosts procedural built around Allison Dubois, who works in the District Attorney’s office and uses intuition and dreams.")]
    [InlineData(
        "If you like brisk genre pieces like The Great Wall and Ready Player One, Abigail gives you a closed city, a missing father, and a hidden layer of magic under the rules. It keeps the fantasy front and center.",
        "If you like brisk genre pieces like The Great Wall and Ready Player One, Abigail gives you a closed city and a missing father.")]
    public void A_list_cut_short_is_closed_with_its_own_and(string lens, string expected)
    {
        // The refuter's round-1 sample still read "booby traps, puzzles, and a…"; a list cut at a
        // comma without its "and" reads as broken off just the same.
        Assert.Equal(expected, Note(lens).FirstClause());
    }

    [Theory]
    [InlineData(
        "If you like brisk, tense genre setups with a slightly off-kilter edge, Time Trap gives you a college crew trapped in a mysterious cave where time runs differently underground than on the surface. It leans into climbing.",
        "If you like brisk, tense genre setups with a slightly off-kilter edge, Time Trap gives you a college crew trapped in a mysterious cave.")]
    [InlineData(
        "If you want crime with a fast, urban climb and a dirty edge, this is Ace moving from a dry cleaning job into Harlem cocaine dealing after Mitch goes to prison and a stray pocket find drops him into Lulu’s orbit. It keeps the street-level pressure.",
        "If you want crime with a fast, urban climb and a dirty edge, this is Ace moving from a dry cleaning job into Harlem cocaine dealing.")]
    public void A_long_main_clause_is_cut_before_a_subordinate_clause(string lens, string expected)
    {
        Assert.Equal(expected, Note(lens).FirstClause());
    }

    [Fact]
    public void A_cut_never_strands_an_adjective_from_its_noun()
    {
        // "sorting through intense," was a candidate: the comma sits between two modifiers of
        // "messages". The cut falls before the participle phrase instead.
        var lens = "Newlywed Melinda Gordon tries to help the dead communicate with loved ones, sorting through intense, confusing "
            + "messages and conflicts that keep spirits from passing over. You usually favor brisk films.";

        Assert.Equal("Newlywed Melinda Gordon tries to help the dead communicate with loved ones.", Note(lens).FirstClause());
    }

    [Fact]
    public void A_participle_keeps_the_after_it_needs()
    {
        // "a new Terminator sent after the future leader" — cut before "after", it read "…a new
        // Terminator sent."
        var lens = "If you want brisk, dark sci-fi action with a tense chase spine, this gives you Sarah Connor back in fight mode, "
            + "a new Terminator sent after the future leader, and an unexpected ally beside an enhanced super soldier. It leans into cyborg pursuit.";

        Assert.Equal(
            "If you want brisk, dark sci-fi action with a tense chase spine, this gives you Sarah Connor back in fight mode.",
            Note(lens).FirstClause());
    }

    [Fact]
    public void An_apposition_after_a_name_is_kept_whole_and_never_joined_with_and()
    {
        var lens = "If you like tense crime stories with a bleak edge and little patience for fluff, this puts Liam, an ex-con, back "
            + "in London ahead of the 2012 Olympics, trying to win back his family while Clifford Cullen’s crime syndicate reaches "
            + "into politics, finance, and the police. It leans on corruption.";

        Assert.Equal(
            "If you like tense crime stories with a bleak edge and little patience for fluff, this puts Liam, an ex-con, back in London ahead of the 2012 Olympics.",
            Note(lens).FirstClause());
    }

    [Fact]
    public void A_list_cut_never_turns_an_apposition_into_a_second_item()
    {
        // Cut after the apposition at 70 characters. Re-closing the list here would read "Ralph
        // Fiennes and a man fresh out of an asylum" — two people where the lens names one.
        var lens = "Spider gives you Ralph Fiennes, a man fresh out of an asylum, a London halfway house, and a childhood "
            + "memory he keeps replaying until it starts to swallow the present whole.";

        Assert.Equal("Spider gives you Ralph Fiennes, a man fresh out of an asylum.", Note(lens).FirstClause(70));
    }

    [Fact]
    public void A_list_that_opens_the_main_clause_gives_no_clause()
    {
        // No comma cut can leave more than a run of nouns ("Heists, double-crosses."), and the
        // sentence has no clause boundary inside the limit.
        var lens = "Heists, double-crosses, rooftop chases and a slow-burn betrayal stretched across three cities and two decades of "
            + "grudges and old debts that nobody in the crew will ever admit to owing anyone at all.";

        Assert.Null(Note(lens).FirstClause());
    }

    [Fact]
    public void An_opening_sentence_about_the_viewer_alone_is_skipped_for_the_one_about_the_title()
    {
        var lens = "You favor brisk thrillers and sci-fi, with more appetite for intensity than humor. This short film puts four "
            + "survivors in a small escape pod, trying to discover what destroyed their spaceship; the deep-space isolation and "
            + "alien threat bring a paranoid edge.";

        Assert.Equal(
            "This short film puts four survivors in a small escape pod, trying to discover what destroyed their spaceship.",
            Note(lens).FirstClause());
    }

    [Theory]
    [InlineData("If you like your genre picks dark, brisk, and a little odd, Ginger Snaps gives you two outcast sisters in Bailey Downs, a savage attack on Ginger’s first period, and Brigitte trying to hold the whole thing together. It mixes coming of age with werewolf body horror.")]
    [InlineData("If you can take a bleak crime-drama, Bully gives you a South Florida teen group trapped around one abusive bully, Bobby, with violence, sexual abuse, and a murder plot kept in plain view. It leans on toxic youth.")]
    [InlineData("Murder by Numbers gives you a tense homicide case with Cassie Mayweather and her still-green partner chasing two cold, calculating young men through a high school setting, forensic work, and profiling. It leans into darkness.")]
    [InlineData("Code Black sets up the busiest, most notorious ER in the nation, with nurses, doctors, and residents pushing against a challenged system to protect patients and their own ideals. For you, it leans on pressure.")]
    [InlineData("Centurion drops you into Britain, A.D. 117, with Quintus Dias as the sole survivor of a Pictish raid, then sends him north with General Virilus and the Ninth Legion on a brutal march through ancient Britain. It is all Roman warfare.")]
    [InlineData("Since you favor brisk action and adventure, this is a darker, slower detour: a flesh-eating undead girl haunts the remote woods where she was murdered, then finds a kidnapped, abused boy hiding in the woods.")]
    public void A_stored_lens_never_captions_a_card_with_a_severed_or_hedged_clause(string lens)
    {
        // The property the round-1 refuter measured over every stored body: whatever comes back
        // is a closed sentence inside the limit that does not argue against its pick — or nothing.
        var clause = Note(lens).FirstClause();
        if (clause is null)
        {
            return;
        }

        Assert.DoesNotContain("…", clause, StringComparison.Ordinal);
        Assert.True(clause.Length <= 160, clause);
        Assert.Matches(@"[.!?]$", clause);
        Assert.False(SecondPersonVoice.SoundsHedged(clause), clause);
        Assert.DoesNotMatch(@"\b(?:two cold|a new Terminator sent|intense)\.$", clause);
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
