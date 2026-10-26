using FrostAura.Reel.Domain.Catalog;

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

    /// <summary>
    /// The crowd's half of the hero bar for a movie: the vote-shrunk crowd score a movie must reach
    /// to lead on its own merits. About the upper quartile of the catalogue's shrunk movie scores
    /// (median 6.44, 75th percentile 6.82 on the founder's copy, 2026-10-26).
    /// </summary>
    public const decimal MovieCrowdFloor = 6.8m;

    /// <summary>
    /// The same bar for a show. Per media type because shows' crowd scores run about 0.7 higher
    /// (median 7.16, 75th percentile 7.58): one shared number would wave every show through while
    /// holding movies to their upper quartile — which is how a 91%-movie account's headline fills
    /// with network procedurals.
    /// </summary>
    public const decimal ShowCrowdFloor = 7.5m;

    /// <summary>The crowd floor for <paramref name="mediaType"/>.</summary>
    public static decimal CrowdFloorFor(MediaType mediaType) =>
        mediaType == MediaType.Show ? ShowCrowdFloor : MovieCrowdFloor;

    /// <summary>
    /// Whether a title's crowd backs it as a headline: its SHRUNK crowd score (the badge's own
    /// number, so 10.0 on 2 votes reads 6.6 and fails) is at or above its media type's floor. No
    /// crowd score is not evidence — it does not clear. Applied through the shrunk prior so a thin
    /// vote count cannot carry a title into the hero that a real crowd would not.
    /// </summary>
    public static bool ClearsCrowdFloor(MediaType mediaType, decimal? shrunkCrowdScore) =>
        shrunkCrowdScore is { } score && score >= CrowdFloorFor(mediaType);
}
