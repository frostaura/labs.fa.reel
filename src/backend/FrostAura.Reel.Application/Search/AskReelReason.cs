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
    /// Builds the turn's reason line.
    /// </summary>
    /// <param name="picks">Cards shown that matched everything asked for.</param>
    /// <param name="outsideShown">Cards shown that trip one of the user's own exclusions, flagged.</param>
    /// <param name="droppedByFloor">Cards moved to the below-floor tray. Zero on a relaxed turn.</param>
    /// <param name="discovered">How many candidates discovery pulled before any ranking.</param>
    /// <param name="widened">Whether discovery had to relax to fill the screen.</param>
    /// <param name="floorRelaxed">Whether the rating floor was dropped for this turn.</param>
    /// <param name="floor">The account's rating floor. Only rendered when it had something to say.</param>
    public static string Build(
        int picks, int outsideShown, int droppedByFloor, int discovered, bool widened, bool floorRelaxed, decimal floor)
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
            clauses.Add(FormattableString.Invariant($"nothing cleared your {floor:0.0} bar, so it was relaxed"));
        }
        else if (droppedByFloor > 0)
        {
            clauses.Add(FormattableString.Invariant($"{droppedByFloor} under your {floor:0.0} bar — shown below"));
        }

        return string.Join(Separator, clauses);
    }

    /// <summary>
    /// "1 pick", "2 picks". Trivially small and here on purpose: "1 picks" shipped, and a reader who
    /// notices it stops trusting the numbers beside it.
    /// </summary>
    private static string Pick(int count) => count == 1 ? "pick" : "picks";

    /// <summary>The account's floor as it appears in copy — one decimal, invariant, always.</summary>
    internal static string FormatFloor(decimal floor) => floor.ToString("0.0", CultureInfo.InvariantCulture);
}
