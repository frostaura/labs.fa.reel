using System.Globalization;

namespace FrostAura.Reel.Application.Ranking;

/// <summary>
/// The account's "for you" bar (<c>MinPredictedRating</c>) on the feed: FD-4's default is that a card
/// below it never reaches a main grid (XM-07). It used to be a soft floor — when fewer than four
/// heroes' worth cleared it, the build relaxed it to the shortlist's 80th percentile and said so —
/// which put cards under the user's own bar into tonight's picks. Now the bar holds at build and at
/// read, and a short page says what the bar held back instead of quietly lowering it.
/// </summary>
public static class ForYouBar
{
    /// <summary>Does a prediction clear the bar? No bar (0) clears everything. The same raw comparison the build uses.</summary>
    public static bool Clears(decimal predicted, decimal bar) => bar <= 0m || predicted >= bar;

    /// <summary>
    /// The build's reason when the bar leaves the page short: <paramref name="clearing"/> of the
    /// shortlist cleared it, and fewer than <paramref name="shortBelow"/> is a short page.
    /// Null when the bar is off or the page is not short.
    /// </summary>
    public static string? ShortReason(decimal bar, int clearing, int shortBelow)
    {
        if (bar <= 0m || clearing >= shortBelow)
        {
            return null;
        }

        // Invariant: a rating on the same 0–10 scale the settings screen shows; a host whose locale
        // writes "9,9" would quote the user a bar they never typed.
        return clearing == 0
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"Nothing cleared your ≥{bar:0.0} “for you” bar tonight, and nothing under it is shown. Lower it in Settings to see more.")
            : string.Create(
                CultureInfo.InvariantCulture,
                $"Only {clearing} title{(clearing == 1 ? string.Empty : "s")} cleared your ≥{bar:0.0} “for you” bar tonight; nothing under it is shown. Lower it in Settings to see more.");
    }

    /// <summary>
    /// The reason a page read after the bar was raised carries: the build had dealt
    /// <paramref name="heldBack"/> card(s) the bar now holds back.
    /// </summary>
    public static string HeldBackOnRead(decimal bar, int heldBack) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"Your ≥{bar:0.0} “for you” bar holds back {heldBack} of tonight's picks; nothing under it is shown. Lower it in Settings to see more.");

    /// <summary>
    /// A reason a build wrote before the bar stopped relaxing ("…relaxed to 7.2 for this build.").
    /// Served after this change it would describe cards the read path no longer shows, so the read
    /// path replaces it.
    /// </summary>
    public static bool IsRetiredRelaxation(string? reason) =>
        reason is not null && reason.Contains("“for you” floor left only", StringComparison.Ordinal)
        && reason.Contains("relaxed to", StringComparison.Ordinal);
}
