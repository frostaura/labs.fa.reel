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
        int aboveFloor = AboveFloorUnknown)
    {

        if (picks + outsideShown == 0)
        {
            return Empty;
        }

        var clauses = new List<string>(4);
        if (outsideShown > 0)
        {
            clauses.Add(FormattableString.Invariant($"{picks} {Pick(picks)} matched everything you asked"));
            clauses.Add(FormattableString.Invariant($"{outsideShown} closest-but-outside shown separately"));
        }
        else
        {
            clauses.Add(FormattableString.Invariant($"{picks} {Pick(picks)} ranked for you, from {discovered} pulled"));
        }

        if (widened)
        {
            clauses.Add("widened to find enough");
        }

        // The floor speaks in BOTH branches. This is the clause the old nested string could not
        // reach: a turn that dropped cards for failing the user's own bar said nothing about them.
        if (floorRelaxed)
        {
            clauses.AddRange(RelaxedClauses(picks + outsideShown, aboveFloor, floor));
        }
        else if (droppedByFloor > 0)
        {
            clauses.Add(FormattableString.Invariant($"{droppedByFloor} under your {FormatFloor(floor)} bar — shown below"));
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
    private static IEnumerable<string> RelaxedClauses(int shown, int aboveFloor, decimal floor)
    {
        var bar = FormatFloor(floor);
        if (aboveFloor < 0)
        {
            // The caller cannot count. Claim nothing about counts — true in every branch.
            yield return FormattableString.Invariant($"your {bar} bar was relaxed to fill the screen");
            yield break;
        }

        var below = Math.Max(0, shown - aboveFloor);
        if (below == 0)
        {
            yield return FormattableString.Invariant($"every pick cleared your {bar} bar");
            yield break;
        }

        yield return aboveFloor == 0
            ? FormattableString.Invariant($"nothing cleared your {bar} bar, so it was relaxed")
            : FormattableString.Invariant($"only {aboveFloor} {Pick(aboveFloor)} cleared your {bar} bar, so it was relaxed");
        yield return FormattableString.Invariant($"{below} {Pick(below)} shown below your {bar} bar");
    }

    /// <summary>
    /// "1 pick", "2 picks". Trivially small and here on purpose: "1 picks" shipped, and a reader who
    /// notices it stops trusting the numbers beside it.
    /// </summary>
    private static string Pick(int count) => count == 1 ? "pick" : "picks";

    /// <summary>The account's floor as it appears in copy — one decimal, invariant, always.</summary>
    internal static string FormatFloor(decimal floor) => floor.ToString("0.0", CultureInfo.InvariantCulture);
}
