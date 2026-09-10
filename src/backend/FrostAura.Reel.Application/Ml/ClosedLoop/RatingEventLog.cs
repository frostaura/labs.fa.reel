using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Domain.Library;

namespace FrostAura.Reel.Application.Ml.ClosedLoop;

/// <summary>
/// Emits <see cref="UserRatingEvent"/> rows. Deliberately a plain static helper over the caller's
/// tracked context rather than a service with a save of its own: every emit site already sits
/// inside a transaction that must either commit the rating AND its event, or neither.
///
/// <para><b>Only real changes are emitted.</b> A Trakt ratings walk replays the account's entire
/// snapshot on every sync — two thousand rows, of which perhaps twenty moved. Emitting an event per
/// row walked would produce a history that is almost entirely no-ops and a "learned this week"
/// number in the thousands. The ingestor already partitions added from unchanged; this only ever
/// sees the moved ones.</para>
/// </summary>
public static class RatingEventLog
{
    /// <summary>
    /// Stages one rating-history row. <paramref name="previousRating"/> null means Created;
    /// otherwise Changed. Retraction goes through <see cref="StageRetraction"/> so the change kind
    /// can never be inferred wrongly from a null.
    /// </summary>
    public static void Stage(
        IReelDbContext db,
        Guid accountId,
        Guid titleId,
        RatingSubjectType subjectType,
        int season,
        int episode,
        short rating,
        short? previousRating,
        DateTime ratedAt,
        DateTime observedAt,
        RatingSource source) =>
        db.UserRatingEvents.Add(new UserRatingEvent
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            TitleId = titleId,
            SubjectType = subjectType,
            Season = season,
            Episode = episode,
            Rating = rating,
            PreviousRating = previousRating,
            RatedAt = ratedAt,
            ObservedAt = observedAt,
            Source = source,
            ChangeKind = previousRating is null ? RatingChangeKind.Created : RatingChangeKind.Changed,
        });

    /// <summary>
    /// Stages a retraction. <see cref="UserRatingEvent.Rating"/> carries the value that was removed
    /// and <see cref="UserRatingEvent.PreviousRating"/> repeats it, so a reader scanning the ledger
    /// for "what did they think of this" never has to special-case a zero.
    /// </summary>
    public static void StageRetraction(
        IReelDbContext db,
        Guid accountId,
        Guid titleId,
        RatingSubjectType subjectType,
        int season,
        int episode,
        short retractedRating,
        DateTime ratedAt,
        DateTime observedAt,
        RatingSource source) =>
        db.UserRatingEvents.Add(new UserRatingEvent
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            TitleId = titleId,
            SubjectType = subjectType,
            Season = season,
            Episode = episode,
            Rating = retractedRating,
            PreviousRating = retractedRating,
            RatedAt = ratedAt,
            ObservedAt = observedAt,
            Source = source,
            ChangeKind = RatingChangeKind.Retracted,
        });
}
