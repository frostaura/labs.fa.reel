using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Application.Search;
using FrostAura.Reel.Domain.Filters;
using Microsoft.EntityFrameworkCore;

namespace FrostAura.Reel.Application.Saved;

/// <summary>
/// The account's own "for you" bar (its <see cref="FilterKind.MinPredictedRating"/> rule) as WS-4's
/// surfaces read it: the digest at build and at read, and the Saved pin's reason.
///
/// <para>A card is held to the bar <b>as its badge shows it</b>, to one decimal: a card reading
/// "7.5" under a 7.5 bar shows nothing the user ruled out, and a card reading "7.4" does, whatever
/// the unrounded score was. The badge is the stored two decimals rounded to one, half away from zero
/// at both steps — what a <c>numeric(4,2)</c> column and the supply SQL's <c>round()</c> do, and what
/// Ask Reel's gate does. This class used to round half-to-even, so a 7.45 read "7.4" on the digest
/// and was held out of a 7.5 bar here while Ask Reel let it through (R1-05).</para>
///
/// <para>Interim: WS-5's <c>Ranking.ForYouBar</c> becomes the one definition in R1-04 with these
/// same three members (<see cref="ReadAsync"/>, <see cref="Clears"/>, <see cref="Shown"/>) and the
/// same rule. When it lands this class is deleted and its callers drop the <c>Saved.</c> prefix;
/// nothing else changes, and the digest's midpoint tests hold either way.</para>
/// </summary>
public static class ForYouBar
{
    /// <summary>The account's bar, or null when it has set none.</summary>
    public static async Task<decimal?> ReadAsync(IReelDbContext db, Guid accountId, CancellationToken ct) =>
        ContentFilterRules.ParseRating(await db.ContentFilters
            .Where(f => f.AccountId == accountId && f.Kind == FilterKind.MinPredictedRating)
            .Select(f => f.Value)
            .FirstOrDefaultAsync(ct));

    /// <summary>
    /// The number a prediction's badge shows: the stored two decimals, then one, half away from zero
    /// at both steps. Every WS-4 surface sends the client this value, so <c>toFixed(1)</c> cannot
    /// print a different digit from the one the bar was checked against.
    /// </summary>
    public static decimal Shown(decimal predicted) =>
        decimal.Round(decimal.Round(predicted, 2, MidpointRounding.AwayFromZero), 1, MidpointRounding.AwayFromZero);

    /// <summary>True when a prediction, as its badge shows it, reaches <paramref name="bar"/> (always true with no bar).</summary>
    public static bool Clears(decimal predicted, decimal? bar) =>
        bar is not { } floor || floor <= 0m || Shown(predicted) >= floor;
}
