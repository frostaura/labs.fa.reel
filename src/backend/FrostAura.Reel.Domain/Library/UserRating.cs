using FrostAura.Reel.Domain.Tenancy;

namespace FrostAura.Reel.Domain.Library;

/// <summary>
/// One Trakt rating (1–10). <see cref="FirstRatedAt"/> — not <see cref="RatedAt"/> — is the
/// leakage-clean time-split axis for the M2 evaluation gate and the retrain drift gate.
/// </summary>
public class UserRating : IAccountScoped
{
    public Guid Id { get; set; }

    public Guid AccountId { get; set; }

    /// <summary>The movie — or, for show/season/episode ratings, the parent show's title.</summary>
    public Guid TitleId { get; set; }

    public RatingSubjectType SubjectType { get; set; }

    /// <summary>0 when the subject is the whole movie/show (part of the uniqueness key).</summary>
    public int SeasonNumber { get; set; }

    /// <summary>0 when the subject is not an episode (part of the uniqueness key).</summary>
    public int EpisodeNumber { get; set; }

    public short Rating { get; set; }

    /// <summary>
    /// Trakt's current opinion of when this was rated. MUTABLE: every ratings walk overwrites it
    /// with whatever Trakt reports, and the in-app rating endpoint sets it to now. Correct for
    /// display and for "what does Trakt think"; wrong as a split axis, because a re-rate moves a row
    /// across the boundary and can carry a label into the train half that was not knowable there.
    /// </summary>
    public DateTime RatedAt { get; set; }

    /// <summary>
    /// When Reel first saw a rating for this row — set once, on insert, and never updated. This is
    /// the split axis for <c>EvalHarness</c> and the drift axis for the nightly retrain gate.
    /// <para>Two alternatives were considered and rejected. <see cref="RatedAt"/> is overwritten on
    /// every Trakt ratings walk, so it is not monotone under a re-rate. An <c>ObservedAt</c> stamped
    /// at ingest would be worse: ~2,000 historical ratings share one onboarding timestamp, the 80%
    /// split index lands inside that tie block, the whole history becomes "train" and the holdout
    /// collapses to zero — the harness would return null and the ledger would stop growing in
    /// silence. <c>FirstRatedAt</c> is backfilled from <c>RatedAt</c>, so it preserves the real
    /// chronology of the user's taste and keeps every historical ledger row comparable.</para>
    /// </summary>
    public DateTime FirstRatedAt { get; set; }

    public DateTime SyncedAt { get; set; }

    public RatingSource Source { get; set; } = RatingSource.Trakt;
}
