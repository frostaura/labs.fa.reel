namespace FrostAura.Reel.Domain.Feed;

/// <summary>
/// The hero's confidence bar, and whether a given pick actually cleared it.
///
/// Why this is a shared type rather than a private constant in the build job: the hero is allowed to
/// come in under its own bar. The build requires <see cref="Floor"/> for the greedy hero pick, and
/// then — rather than open blank — backfills the remaining slots from the top scored candidates with
/// the floor dropped. On live data that backfill is not a rare edge; it is the founder's home screen
/// every night, with only 31 of 470 scored titles reaching 7.5 and the hero averaging 6.86 under the
/// heading "Tonight's picks". The relaxation was silent, which is precisely the complaint: "the picks
/// are getting worse" is this state, unnamed.
///
/// Naming it costs nothing and it is derivable at serve time, so it needs no column and no migration:
/// a hero pick below the bar came from the backfill by construction, since everything above the bar
/// was taken first. Keep the build and the serve path reading the SAME bar from here — a private copy
/// on either side is how a card comes to be labelled against a threshold the build never applied.
/// </summary>
public static class HeroConfidence
{
    /// <summary>
    /// The predicted rating a title must reach to lead the hero on its own merits. Not a user
    /// setting — it is the product's own bar for "confident enough to be tonight's answer".
    /// </summary>
    public const decimal Floor = 7.5m;

    /// <summary>The pick cleared the bar it was measured against.</summary>
    public const string Confident = "confident";

    /// <summary>The pick is below the bar and is on the hero only because nothing better was there.</summary>
    public const string Reaching = "reaching";

    /// <summary>
    /// The bar this account's hero actually required: the product floor, or the account's own
    /// "for you" minimum when they have set it HIGHER. A lower personal minimum does not lower the
    /// hero bar — it is a floor on what may be shown at all, not a request for a weaker headline.
    /// </summary>
    public static decimal BarFor(decimal? minPredictedRating) =>
        Math.Max(Floor, minPredictedRating ?? 0m);

    /// <summary>
    /// Whether a hero pick cleared <paramref name="bar"/>. Inclusive at the bar: a title predicted
    /// at exactly 7.5 met the stated threshold, and calling it a reach would contradict the same
    /// comparison the build used to select it.
    /// </summary>
    public static string For(decimal predictedRating, decimal bar) =>
        predictedRating >= bar ? Confident : Reaching;
}
