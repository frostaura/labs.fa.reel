namespace FrostAura.Reel.Application.Ranking;

/// <summary>One card whose number moved: what the client patches in place, and nothing else.</summary>
public readonly record struct FeedScoreUpdate(Guid TitleId, decimal PredictedRating);

/// <summary>
/// The rule that separates "tonight's picks are unchanged" from "tonight's picks are unchanged and
/// the model now thinks differently about them".
///
/// <para>Preserve-mode no-op suppression compares the assembled feed to the Active one on
/// <c>(Row, AnchorTitleId, Rank, TitleId)</c> only — scores are deliberately excluded, because a
/// score move must not re-deal the page. The unintended consequence was that a build which changed
/// every prediction and no item wrote nothing at all: the recalibration happened and was discarded
/// one line before the screen, which is exactly the founder's "the sync happens instantly and
/// nothing changes". Worse, it was INCONSISTENT — <c>TitleScores</c> are persisted before assembly,
/// so Saved, the live feed refill, the paginator and the digest all showed the fresh number while
/// the hero showed the stale one. The same title could read 6.9 in one place and 5.6 in another.</para>
///
/// <para>So the two decisions are split. Item identity still governs whether a SNAPSHOT is minted
/// (the stable-"tonight" contract, untouched). Score drift governs whether the existing snapshot's
/// rows are UPDATED IN PLACE — same snapshot id, same <c>GeneratedAt</c>, so the client's
/// generatedAt-keyed rows never remount and the page the user is reading does not move under
/// them.</para>
/// </summary>
public static class FeedScoreDrift
{
    /// <summary>
    /// How far a displayed rating must move to be worth a write and an SSE frame. Displayed ratings
    /// are rounded to 0.1, so anything smaller than this is at most a couple of last-digit flickers
    /// — noise the user would read as the page being unstable rather than as the model learning.
    /// </summary>
    public const decimal Threshold = 0.3m;

    /// <summary>True when the displayed rating moved by at least <see cref="Threshold"/>.</summary>
    public static bool RatingMoved(decimal previous, decimal current) =>
        Math.Abs(current - previous) >= Threshold;

    /// <summary>
    /// True when the per-feature contribution list changed. A card can keep its number and still be
    /// justified differently — the tap-through breakdown is what the user checks when they disagree
    /// — so a contributions change is a reason to write even when the rating held.
    /// </summary>
    public static bool ExplanationChanged(string? previous, string? current) =>
        !string.Equals(previous ?? string.Empty, current ?? string.Empty, StringComparison.Ordinal);

    /// <summary>
    /// True when either half moved — the write condition. Kept as one predicate so the handler and
    /// its tests cannot disagree about what "the scores changed" means.
    /// </summary>
    public static bool Changed(
        decimal previousRating, decimal currentRating, string? previousExplanation, string? currentExplanation) =>
        RatingMoved(previousRating, currentRating) || ExplanationChanged(previousExplanation, currentExplanation);

    /// <summary>
    /// The <c>feed-scores-updated</c> frame body: the snapshot's UNCHANGED <c>generatedAt</c> (the
    /// client keys rows on it and must be able to prove the frame belongs to the page it is looking
    /// at) plus only the cards whose rating moved. Contribution-only changes are written but not
    /// broadcast — there is no number on screen for them to patch.
    /// </summary>
    public static IReadOnlyDictionary<string, object?> Payload(
        DateTime generatedAtUtc, IReadOnlyList<FeedScoreUpdate> changed) =>
        new Dictionary<string, object?>
        {
            ["generatedAt"] = generatedAtUtc,
            ["changed"] = changed
                .Select(c => new Dictionary<string, object?>
                {
                    ["titleId"] = c.TitleId,
                    ["predictedRating"] = c.PredictedRating,
                })
                .ToList(),
        };
}
