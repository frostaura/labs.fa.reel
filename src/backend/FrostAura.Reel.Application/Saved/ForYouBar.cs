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
/// the unrounded score was. That is the same reading the exclusion matrix and the supply truth use,
/// so the surface, the badge and the check can never disagree.</para>
/// </summary>
public static class ForYouBar
{
    /// <summary>The account's bar, or null when it has set none.</summary>
    public static async Task<decimal?> ReadAsync(IReelDbContext db, Guid accountId, CancellationToken ct) =>
        ContentFilterRules.ParseRating(await db.ContentFilters
            .Where(f => f.AccountId == accountId && f.Kind == FilterKind.MinPredictedRating)
            .Select(f => f.Value)
            .FirstOrDefaultAsync(ct));

    /// <summary>True when a prediction, as its badge shows it, reaches <paramref name="bar"/> (always true with no bar).</summary>
    public static bool Clears(decimal predicted, decimal? bar) =>
        bar is not { } floor || Math.Round(predicted, 1) >= floor;
}
