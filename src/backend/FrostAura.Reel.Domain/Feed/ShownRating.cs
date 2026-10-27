namespace FrostAura.Reel.Domain.Feed;

/// <summary>
/// A "for you" prediction as the user reads it: the stored two decimals, shown at one.
///
/// <para><b>Why one definition.</b> Every bar in Reel — the account's own "for you" bar, the hero's
/// 7.5 — is a promise about the number on the card, so it has to be held to that number and not to
/// the unrounded score behind it. Before this type the feed build and its read path compared the raw
/// score (a 7.46 was held out of a 7.5 bar while its badge read "7.5"), the Saved and digest paths
/// rounded half-to-even (a 7.35 passed a 7.4 bar while the badge read "7.3"), and Ask Reel and the
/// supply truth rounded half away from zero. Three rules, three answers at every midpoint.</para>
///
/// <para><b>The rule.</b> Half away from zero at both steps, because that is what Postgres does when a
/// value lands in a <c>numeric(4,2)</c> column and what <c>round()</c> does in the supply SQL, so the
/// database, the checks and the server agree on every value. The browser does NOT: <c>toFixed(1)</c>
/// rounds the binary double, and 7.35 is stored as 7.3499…, so it prints "7.3". That is why every
/// surface sends the client <see cref="Of"/> — an exact one-decimal value <c>toFixed(1)</c> cannot
/// move — rather than the stored two decimals.</para>
/// </summary>
public static class ShownRating
{
    /// <summary>
    /// The prediction at its stored resolution (2 dp), rounded the way a <c>numeric(4,2)</c> column
    /// rounds it. Idempotent on a value that is already two decimals.
    /// </summary>
    public static decimal Stored(decimal predicted) =>
        decimal.Round(predicted, 2, MidpointRounding.AwayFromZero);

    /// <summary>
    /// The number the badge shows: the stored value at one decimal. Rounding the stored value rather
    /// than the raw score keeps a build-time decision (on the raw score) and a read-time decision
    /// (on the stored row) identical: a raw 7.346 is stored as 7.35 and shown as 7.4 on both sides.
    /// </summary>
    public static decimal Of(decimal predicted) =>
        decimal.Round(Stored(predicted), 1, MidpointRounding.AwayFromZero);

    /// <summary>
    /// Whether a prediction, as its badge shows it, reaches <paramref name="bar"/>. Inclusive: a card
    /// reading "7.5" met a 7.5 bar, whatever the unrounded score was.
    /// </summary>
    public static bool Reaches(decimal predicted, decimal bar) => Of(predicted) >= bar;

    /// <summary>
    /// The smallest STORED (2 dp) value that <see cref="Reaches"/> <paramref name="bar"/>, for a SQL
    /// filter on a stored column: <c>stored &gt;= LowestStored(bar)</c> is exactly
    /// <c>Reaches(stored, bar)</c> for every two-decimal value. The bar is first lifted to the
    /// one-decimal value a shown number must reach (a 7.45 bar needs a "7.5"), and a shown number of
    /// x.y starts at x.y − 0.05.
    /// </summary>
    public static decimal LowestStored(decimal bar) =>
        (decimal.Ceiling(bar * 10m) / 10m) - 0.05m;
}
