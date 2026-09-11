using System.Globalization;

namespace FrostAura.Reel.Application.Search;

/// <summary>
/// The one-line account a turn gives of itself, under the results.
///
/// <para>It is a CLAUSE LIST rather than a sentence with optional tails, and that is the fix. The
/// old string nested its conditions, so the floor could only be mentioned in the branch where it
/// had been relaxed — a turn that dropped four cards for failing the user's own 6.0 bar said
/// nothing at all about them, which is the exact shape of a bug report ("it only showed me six").
/// Clauses compose: every fact that is true about the turn gets said, in a fixed order, and none of
/// them can hide behind another.</para>
///
/// <para>Every number here is <see cref="FormattableString.Invariant"/>: this string is compared
/// verbatim by tests, read by a human, and — like everything else this backend formats — must not
/// acquire a comma decimal separator on a differently-configured host.</para>
/// </summary>
/// <summary>
/// One turn's counts, as one value.
///
/// <para>Every clause of the reason line is derived from these and from nothing else, which is the
/// point: the clauses used to take seven loose arguments and compose independently, so a starved
/// turn could say "0 picks matched" and "only 1 pick cleared" and "1 pick shown below" about the
/// same two cards. Four numbers that describe one screen have to travel together or they drift.</para>
/// </summary>
/// <param name="Picks">Cards on screen that matched everything asked for — the grid.</param>
/// <param name="Outside">
/// Cards on screen that trip one of the user's own negations, flagged and shown separately. These
/// are explicitly NOT picks, and no clause may count them as such.
/// </param>
/// <param name="Tray">
/// Cards the account's rating floor put under the "show the N under your bar" fence. Zero on a
/// relaxed turn by construction — relaxation drops nothing.
/// </param>
/// <param name="Pulled">How many candidates discovery returned before any ranking.</param>
/// <param name="AboveFloor">
/// How many of the <see cref="Shown"/> cards cleared the floor. <see cref="AskReelReason.AboveFloorUnknown"/>
/// from a caller that cannot count it. Load-bearing only on a relaxed turn, where nothing is
/// dropped and <see cref="Tray"/> is therefore 0 whether every card cleared the bar or none did.
/// </param>
public readonly record struct TurnCounts(int Picks, int Outside, int Tray, int Pulled, int AboveFloor)
{
    /// <summary>Everything the turn put on the screen — both channels, never the tray.</summary>
    public int Shown => Picks + Outside;
}

public static class AskReelReason
{
    /// <summary>What separates the clauses. A middot reads as a list; a semicolon read as a sentence.</summary>
    public const string Separator = " · ";

    /// <summary>The honest answer when a turn genuinely found nothing.</summary>
    public const string Empty = "no fresh matches — try another angle";

    /// <summary>
    /// A caller that cannot supply the above-floor count. It buys the count-free relaxed clause,
    /// which is true in every branch — never the old absolute, which was true in one of them.
    /// </summary>
    public const int AboveFloorUnknown = -1;

    /// <summary>
    /// Builds the turn's reason line.
    /// </summary>
    /// <param name="picks">Cards shown that matched everything asked for.</param>
    /// <param name="outsideShown">Cards shown that trip one of the user's own exclusions, flagged.</param>
    /// <param name="droppedByFloor">Cards moved to the below-floor tray. Zero on a relaxed turn.</param>
    /// <param name="discovered">How many candidates discovery pulled before any ranking.</param>
    /// <param name="widened">Whether discovery had to relax to fill the screen.</param>
    /// <param name="floorRelaxed">Whether the rating floor was dropped for this turn.</param>
    /// <param name="floor">The account's rating floor. Only rendered when it had something to say.</param>
    /// <param name="aboveFloor">
    /// How many of the shown cards actually cleared <paramref name="floor"/>. Load-bearing on a
    /// relaxed turn and unavailable from any other argument: relaxation drops nothing and keeps
    /// everything, so <paramref name="droppedByFloor"/> is 0 and <paramref name="picks"/> is the
    /// whole screen whether five cards cleared the bar or none did. <see cref="AboveFloorUnknown"/>
    /// from a caller that does not have it.
    /// </param>
    public static string Build(
        int picks, int outsideShown, int droppedByFloor, int discovered, bool widened, bool floorRelaxed, decimal floor,
        int aboveFloor = AboveFloorUnknown) =>
        Build(new TurnCounts(picks, outsideShown, droppedByFloor, discovered, aboveFloor), widened, floorRelaxed, floor);

    /// <summary>
    /// Builds the turn's reason line from ONE set of counts.
    ///
    /// <para><b>Why the counts travel together.</b> Each clause used to be appended independently
    /// and none of them knew what the others had said, so a starved turn produced "0 picks matched
    /// everything you asked · 2 closest-but-outside shown separately · widened to find enough · only
    /// 1 pick cleared your 6.0 bar, so it was relaxed · 1 pick shown below your 6.0 bar" — against
    /// two results, an empty tray and nothing stored below the floor. Two clauses called an
    /// outside-request card a "pick" while the first clause had just said no picks matched, and the
    /// last one borrowed the tray clause's own words ("shown below") for cards that were in the grid.
    /// <see cref="AskReelReasonSelfConsistencyTests"/> parses this method's output back against
    /// these counts across the whole branch space, which is the only way a clause list stays
    /// honest.</para>
    /// </summary>
    public static string Build(TurnCounts counts, bool widened, bool floorRelaxed, decimal floor)
    {
        if (counts.Shown == 0)
        {
            return Empty;
        }

        var clauses = new List<string>(4);
        if (counts.Outside > 0)
        {
            clauses.Add(FormattableString.Invariant($"{counts.Picks} {Pick(counts.Picks)} matched everything you asked"));
            clauses.Add(FormattableString.Invariant($"{counts.Outside} closest-but-outside shown separately"));
        }
        else
        {
            clauses.Add(FormattableString.Invariant(
                $"{counts.Picks} {Pick(counts.Picks)} ranked for you, from {counts.Pulled} pulled"));
        }

        if (widened)
        {
            clauses.Add("widened to find enough");
        }

        // The floor speaks in BOTH branches. This is the clause the old nested string could not
        // reach: a turn that dropped cards for failing the user's own bar said nothing about them.
        if (floorRelaxed)
        {
            clauses.AddRange(RelaxedClauses(counts, floor));
        }
        else if (counts.Tray > 0)
        {
            clauses.Add(FormattableString.Invariant($"{counts.Tray} under your {FormatFloor(floor)} bar — shown below"));
        }

        return string.Join(Separator, clauses);
    }

    /// <summary>
    /// What a relaxed turn says about the bar, as one or two clauses.
    ///
    /// <para>The bug this replaces: relaxation triggers on a THRESHOLD — fewer than six cards clear
    /// the bar — and the copy asserted an ABSOLUTE, "nothing cleared your 6.0 bar". The two are not
    /// the same predicate, and on six of seven observed turns they disagreed: cards badged 6.2 sat
    /// in the grid under a sentence saying nothing had cleared 6.0. Worse, a relaxed turn drops
    /// nothing, so <c>droppedByFloor</c> is 0 and the below-floor tray is empty — the sub-floor
    /// cards render as ordinary picks with no signal anywhere on the screen that they are under the
    /// user's own bar. So the count says what it knows, and a second clause accounts for the grid.</para>
    ///
    /// <para>When every shown card cleared, the relaxation changed nothing and saying "so it was
    /// relaxed" would send the reader hunting for below-bar cards that do not exist. The honest
    /// fact there is that the bar held.</para>
    /// </summary>
    private static IEnumerable<string> RelaxedClauses(TurnCounts counts, decimal floor)
    {
        var bar = FormatFloor(floor);
        if (counts.AboveFloor < 0)
        {
            // The caller cannot count. Claim nothing about counts — true in every branch.
            yield return FormattableString.Invariant($"your {bar} bar was relaxed to fill the screen");
            yield break;
        }

        // The noun changes with the channel mix, and that is the whole of F4-05's server half. On a
        // turn with an outside channel these counts span BOTH lists — a card under the bar is under
        // it wherever it sits — so calling them "picks" folds cards the same sentence has just
        // called not-matching into the pick count. "21 picks" against "0 picks matched everything
        // you asked" was one screen's footnote.
        var above = counts.AboveFloor;
        var below = Math.Max(0, counts.Shown - above);
        if (below == 0)
        {
            // "Every" takes the singular noun whatever the count is — "every picks cleared" is the
            // same trust leak as the "1 picks" that shipped once already.
            yield return FormattableString.Invariant($"every {Unit(counts, 1)} cleared your {bar} bar");
            yield break;
        }

        yield return above == 0
            ? FormattableString.Invariant($"nothing cleared your {bar} bar, so it was relaxed")
            : FormattableString.Invariant(
                $"only {above} {Unit(counts, above)} cleared your {bar} bar, so it was relaxed");

        // "ON SCREEN under your bar", never "shown below". A relaxed turn stores no tray at all, so
        // the tray clause's own wording put a count against an empty belowFloor array — the reader
        // was told to look below a fence that was not there.
        yield return FormattableString.Invariant($"{below} {Unit(counts, below)} on screen under your {bar} bar");
    }

    /// <summary>
    /// "pick" / "picks" on a turn whose whole screen is picks; "card" / "cards" once an outside
    /// channel is on it, because those are explicitly not picks.
    /// </summary>
    private static string Unit(TurnCounts counts, int of) =>
        counts.Outside > 0
            ? of == 1 ? "card" : "cards"
            : Pick(of);

    /// <summary>
    /// "1 pick", "2 picks". Trivially small and here on purpose: "1 picks" shipped, and a reader who
    /// notices it stops trusting the numbers beside it.
    /// </summary>
    private static string Pick(int count) => count == 1 ? "pick" : "picks";

    /// <summary>The account's floor as it appears in copy — one decimal, invariant, always.</summary>
    internal static string FormatFloor(decimal floor) => floor.ToString("0.0", CultureInfo.InvariantCulture);
}
