using System.Globalization;

namespace FrostAura.Reel.Application.Search;

/// <summary>
/// One turn's counts, as one value.
///
/// <para>Every clause of the reason line is derived from these and from nothing else, which is the
/// point: the clauses used to take seven loose arguments and compose independently, so a starved
/// turn could say "0 picks matched" and "only 1 pick cleared" and "1 pick shown below" about the
/// same two cards. Numbers that describe one screen have to travel together or they drift.</para>
/// </summary>
/// <param name="Picks">Cards in the grid that the turn stands behind as matching the request.</param>
/// <param name="Outside">
/// Cards on screen that trip one of the user's own negations, flagged and shown separately. These
/// are explicitly NOT picks, and no clause may count them as such.
/// </param>
/// <param name="Tray">Cards the account's rating floor put under the "under your bar" fence.</param>
/// <param name="NearMiss">Cards the judge called a partial match, in the collapsed near-miss tray.</param>
public readonly record struct TurnCounts(int Picks, int Outside, int Tray, int NearMiss = 0)
{
    /// <summary>Everything the turn put in the grid — both channels, never a tray.</summary>
    public int Shown => Picks + Outside;
}

/// <summary>
/// The one-line account a turn gives of itself, under the results — in the user's language.
///
/// <para><b>No pipeline counts.</b> "24 picks ranked for you, from 60 pulled · widened to find
/// enough · only 4 picks cleared your 6.0 bar, so it was relaxed" told the user about a retrieval
/// stage they never see and a relaxation that ignored their own bar for 20 of 24 cards (A-11). The
/// floor no longer relaxes, discovery's size is the founder's debug block's business, and every
/// clause now names something on the screen: the picks, the cards outside the request, the tray
/// under the bar, and the near misses tucked away below.</para>
///
/// <para>Every number is <see cref="FormattableString.Invariant"/>: this string is compared verbatim
/// by tests and must not acquire a comma decimal separator on a differently-configured host.</para>
/// </summary>
public static class AskReelReason
{
    /// <summary>What separates the clauses. A middot reads as a list; a semicolon read as a sentence.</summary>
    public const string Separator = " · ";

    /// <summary>The honest answer when nothing reached the grid.</summary>
    public const string Empty = "no picks matched what you asked";

    /// <summary>Builds the turn's reason line from ONE set of counts.</summary>
    /// <param name="counts">What is on the screen.</param>
    /// <param name="floor">The account's For-you bar; quoted only when the tray has something in it.</param>
    public static string Build(TurnCounts counts, decimal floor)
    {
        var clauses = new List<string>(4);
        if (counts.Shown == 0)
        {
            clauses.Add(Empty);
        }
        else if (counts.Outside > 0)
        {
            clauses.Add(FormattableString.Invariant($"{counts.Picks} {Pick(counts.Picks)} match what you asked"));
            clauses.Add(FormattableString.Invariant($"{counts.Outside} close but outside what you asked, shown separately"));
        }
        else
        {
            clauses.Add(FormattableString.Invariant($"{counts.Picks} {Pick(counts.Picks)} for you"));
        }

        if (counts.Tray > 0)
        {
            clauses.Add(FormattableString.Invariant($"{counts.Tray} under your {FormatFloor(floor)} bar — shown below"));
        }

        if (counts.NearMiss > 0)
        {
            clauses.Add(FormattableString.Invariant($"{counts.NearMiss} close but not quite — tucked away below"));
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
