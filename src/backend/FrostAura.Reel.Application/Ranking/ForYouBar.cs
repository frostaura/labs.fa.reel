using System.Globalization;
using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Application.Search;
using FrostAura.Reel.Domain.Feed;
using FrostAura.Reel.Domain.Filters;
using Microsoft.EntityFrameworkCore;

namespace FrostAura.Reel.Application.Ranking;

/// <summary>
/// The account's "for you" bar (<c>MinPredictedRating</c>) — the one definition every surface holds
/// a card to: the feed build, its read path and its paged rails, the digest, the Saved pin, Ask
/// Reel's gate and the supply truth. FD-4's default is that a card below it never reaches a main
/// grid (XM-07). It used to be a soft floor — when fewer than four heroes' worth cleared it, the
/// build relaxed it to the shortlist's 80th percentile and said so — which put cards under the
/// user's own bar into tonight's picks. Now the bar holds at build and at read, and a short page says
/// what the bar held back instead of quietly lowering it.
///
/// <para><b>Held to the badge, at one decimal</b> (<see cref="Shown"/>). A card reading "7.5" under
/// a 7.5 bar shows nothing the user ruled out, and a card reading "7.3" under a 7.4 bar does, whatever
/// the unrounded score was. There used to be three rules (raw here, half-to-even in the Saved copy of
/// this class, half away from zero in Ask Reel and the supply SQL) and they disagreed at every
/// midpoint; see <see cref="ShownRating"/> for the rule and why the client is sent the shown value.</para>
/// </summary>
public static class ForYouBar
{
    /// <summary>The account's bar, or null when it has set none. Clamped to the 0–10 scale.</summary>
    public static async Task<decimal?> ReadAsync(IReelDbContext db, Guid accountId, CancellationToken ct) =>
        ContentFilterRules.ParseRating(await db.ContentFilters
            .Where(f => f.AccountId == accountId && f.Kind == FilterKind.MinPredictedRating)
            .Select(f => f.Value)
            .FirstOrDefaultAsync(ct));

    /// <summary>
    /// The number a prediction's badge shows (one decimal, half away from zero, from the stored two).
    /// Every surface sends the client this value, so <c>toFixed(1)</c> cannot print a different digit.
    /// </summary>
    public static decimal Shown(decimal predicted) => ShownRating.Of(predicted);

    /// <summary>Does a prediction, as its badge shows it, clear the bar? No bar (0 or less) clears everything.</summary>
    public static bool Clears(decimal predicted, decimal bar) => bar <= 0m || ShownRating.Reaches(predicted, bar);

    /// <summary><inheritdoc cref="Clears(decimal, decimal)" path="/summary"/> Null is no bar.</summary>
    public static bool Clears(decimal predicted, decimal? bar) => bar is not { } floor || Clears(predicted, floor);

    /// <summary>
    /// The smallest stored (2 dp) prediction that <see cref="Clears(decimal, decimal)"/> a positive
    /// <paramref name="bar"/>, for a SQL filter on a stored <c>PredictedRating</c> column, so a query
    /// and the in-memory check can never disagree. Callers keep their own <c>bar &lt;= 0</c> branch.
    /// </summary>
    public static decimal LowestStoredClearing(decimal bar) => ShownRating.LowestStored(bar);

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
