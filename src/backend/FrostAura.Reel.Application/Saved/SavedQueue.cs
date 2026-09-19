using System.Globalization;

namespace FrostAura.Reel.Application.Saved;

/// <summary>How the Saved queue is ordered. <see cref="Tonight"/> is the default.</summary>
public enum SavedSort
{
    /// <summary>Watchable right now, best first, shortest first — the "what do I put on" order.</summary>
    Tonight,

    /// <summary>Purely by the personal prediction, highest first.</summary>
    Predicted,

    /// <summary>Newest save first — the old behaviour, kept because "what did I just add" is a real question.</summary>
    Added,

    /// <summary>Shortest first — the "I have 90 minutes" order.</summary>
    Runtime,
}

/// <summary>
/// One saved title, reduced to the four facts the queue orders on. Deliberately not the response
/// DTO: ordering is pure arithmetic over these, so it is unit-testable without a database, an HTTP
/// context or a poster path.
/// </summary>
/// <param name="TitleId">Tie-breaker of last resort, so every order is total and reproducible.</param>
/// <param name="PredictedRating">Calibrated predicted rating; null only while the on-demand backfill has not reached this title.</param>
/// <param name="RuntimeMinutes">Null when the catalogue has no runtime — treated as "unknown", never as "short".</param>
/// <param name="AvailableNow">On a flatrate provider in the account's region, per the shared availability read.</param>
/// <param name="SavedAt">When the user saved it.</param>
public readonly record struct SavedQueueEntry(
    Guid TitleId,
    decimal? PredictedRating,
    int? RuntimeMinutes,
    bool AvailableNow,
    DateTime SavedAt);

/// <summary>
/// The Saved shelf's ordering rules and its one pinned pick, as pure functions.
///
/// <para>The shelf was a poster grid ordered by save time with a frequently-null prediction; the
/// founder's actual workflow — curate for weeks, then decide on a Tuesday evening — was the least
/// engineered surface in the app. Every rule below exists to answer "what do I put on tonight"
/// without the user re-deriving it from a wall of posters.</para>
///
/// <para><b>Nulls sort last, never first.</b> A missing prediction and a missing runtime are both
/// absence of knowledge, and absence of knowledge must never outrank a real number — a title with no
/// runtime is not a short film, and a title with no score is not a bad one. Both are pushed to the
/// bottom of their key rather than defaulted to a value that would quietly assert something.</para>
/// </summary>
public static class SavedQueue
{
    /// <summary>
    /// The default evening budget in minutes when the account has not set one. 150 is a film plus
    /// the deciding, which is the window the pinned pick is actually competing for.
    /// </summary>
    public const int DefaultEveningBudgetMinutes = 150;

    /// <summary>Floor on a user-set evening budget — below this nothing but a short would qualify.</summary>
    public const int MinEveningBudgetMinutes = 30;

    /// <summary>Ceiling on a user-set evening budget: ten hours is already "no constraint".</summary>
    public const int MaxEveningBudgetMinutes = 600;

    /// <summary>
    /// Parses the <c>?sort=</c> query value. Anything unrecognised — including null and empty —
    /// falls back to <see cref="SavedSort.Tonight"/> rather than 400-ing: a bookmarked URL from an
    /// older build must still render the shelf.
    /// </summary>
    public static SavedSort ParseSort(string? raw) => raw?.Trim().ToLowerInvariant() switch
    {
        "predicted" => SavedSort.Predicted,
        "added" => SavedSort.Added,
        "runtime" => SavedSort.Runtime,
        _ => SavedSort.Tonight,
    };

    /// <summary>Clamps a requested evening budget; null or non-positive clears it back to the default.</summary>
    public static int? NormaliseEveningBudget(int? requested) => requested switch
    {
        null => null,
        <= 0 => null,
        _ => Math.Clamp(requested.Value, MinEveningBudgetMinutes, MaxEveningBudgetMinutes),
    };

    /// <summary>The effective budget: the account's own, or <see cref="DefaultEveningBudgetMinutes"/>.</summary>
    public static int EffectiveEveningBudget(int? configured) => configured ?? DefaultEveningBudgetMinutes;

    /// <summary>
    /// Orders the queue. Every branch ends on <c>SavedAt desc</c> then <c>TitleId</c>, so the order
    /// is total: two titles that tie on every visible fact still land in the same places on every
    /// request, which is what stops the shelf shuffling under a user between refetches.
    /// </summary>
    public static IReadOnlyList<SavedQueueEntry> Order(IEnumerable<SavedQueueEntry> entries, SavedSort sort) => sort switch
    {
        // Available now first, then the best of those, then the shortest of those. The three keys
        // are in the order the question is actually asked: can I watch it, is it good, does it fit.
        SavedSort.Tonight => entries
            .OrderByDescending(e => e.AvailableNow)
            .ThenByDescending(e => e.PredictedRating ?? decimal.MinValue)
            .ThenBy(e => e.RuntimeMinutes ?? int.MaxValue)
            .ThenByDescending(e => e.SavedAt)
            .ThenBy(e => e.TitleId)
            .ToList(),
        SavedSort.Predicted => entries
            .OrderByDescending(e => e.PredictedRating ?? decimal.MinValue)
            .ThenByDescending(e => e.SavedAt)
            .ThenBy(e => e.TitleId)
            .ToList(),
        SavedSort.Runtime => entries
            .OrderBy(e => e.RuntimeMinutes ?? int.MaxValue)
            .ThenByDescending(e => e.PredictedRating ?? decimal.MinValue)
            .ThenByDescending(e => e.SavedAt)
            .ThenBy(e => e.TitleId)
            .ToList(),
        _ => entries
            .OrderByDescending(e => e.SavedAt)
            .ThenBy(e => e.TitleId)
            .ToList(),
    };

    /// <summary>
    /// Every title that could honestly be pinned as "watch tonight", best first: streaming now, with
    /// a real prediction, and with a KNOWN runtime inside the evening budget. Unknown runtime is
    /// excluded on purpose — the pin's whole claim is "this fits your evening", and a title whose
    /// length we do not know cannot make that claim. The client walks this same list when the user
    /// says "not tonight", which is why it is a list and not just a winner.
    /// </summary>
    public static IReadOnlyList<SavedQueueEntry> WatchTonightCandidates(
        IEnumerable<SavedQueueEntry> entries, int eveningBudgetMinutes) => entries
        .Where(e => e.AvailableNow
            && e.PredictedRating is not null
            && e.RuntimeMinutes is not null
            && e.RuntimeMinutes.Value <= eveningBudgetMinutes)
        .OrderByDescending(e => e.PredictedRating!.Value)
        .ThenBy(e => e.RuntimeMinutes!.Value)
        .ThenByDescending(e => e.SavedAt)
        .ThenBy(e => e.TitleId)
        .ToList();

    /// <summary>
    /// The one pinned pick, or null when nothing in the queue can honestly claim the slot. Null is a
    /// real answer here and the surface says so out loud rather than relaxing a rule and pinning
    /// something that is not streaming or does not fit.
    /// </summary>
    public static SavedQueueEntry? PickWatchTonight(IEnumerable<SavedQueueEntry> entries, int eveningBudgetMinutes)
    {
        var candidates = WatchTonightCandidates(entries, eveningBudgetMinutes);
        return candidates.Count == 0 ? null : candidates[0];
    }

    /// <summary>"1h 58m" / "58m" / null. Invariant, because it is a duration, not a locale-shaped number.</summary>
    public static string? FormatRuntime(int? minutes)
    {
        if (minutes is null or <= 0)
        {
            return null;
        }

        var hours = minutes.Value / 60;
        var rest = minutes.Value % 60;
        return hours == 0
            ? string.Format(CultureInfo.InvariantCulture, "{0}m", rest)
            : rest == 0
                ? string.Format(CultureInfo.InvariantCulture, "{0}h", hours)
                : string.Format(CultureInfo.InvariantCulture, "{0}h {1}m", hours, rest);
    }

    /// <summary>
    /// Why this title got pinned, in the user's own terms — the prediction WITH its error band, the
    /// runtime against the budget, and where it is streaming. The band is not decoration: a "8.4"
    /// with a measured ±1.9 beside it is the honest version of the same promise, and the pinned card
    /// is the loudest promise the product makes.
    /// </summary>
    public static string WatchTonightReason(
        decimal predictedRating,
        double? expectedAbsoluteError,
        int runtimeMinutes,
        int eveningBudgetMinutes,
        string? providerName)
    {
        var band = expectedAbsoluteError is { } error
            ? string.Format(CultureInfo.InvariantCulture, " (±{0:0.#})", error)
            : string.Empty;
        var where = string.IsNullOrWhiteSpace(providerName)
            ? "streaming now"
            : string.Format(CultureInfo.InvariantCulture, "on {0}", providerName);
        return string.Format(
            CultureInfo.InvariantCulture,
            "Highest-predicted thing in your queue that's {0} and fits tonight — {1:0.0} for you{2}, {3} against your {4}.",
            where,
            predictedRating,
            band,
            FormatRuntime(runtimeMinutes),
            FormatRuntime(eveningBudgetMinutes));
    }
}
