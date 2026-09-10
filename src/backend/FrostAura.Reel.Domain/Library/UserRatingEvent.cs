using FrostAura.Reel.Domain.Tenancy;

namespace FrostAura.Reel.Domain.Library;

/// <summary>What happened to a rating, as history rather than as current state.</summary>
public enum RatingChangeKind
{
    /// <summary>The first rating Reel ever saw for this subject.</summary>
    Created,

    /// <summary>A re-rate — <see cref="UserRatingEvent.PreviousRating"/> carries what it was.</summary>
    Changed,

    /// <summary>The rating was removed.</summary>
    Retracted,
}

/// <summary>
/// Append-only rating history. <c>UserRating</c> stays exactly what it is — the current-state
/// projection every existing reader uses — and this is the ledger beside it: never updated, never
/// deleted, one row per change.
///
/// <para><b>The two timestamps are not interchangeable.</b> <see cref="RatedAt"/> is the source's
/// claim about when the user rated; <see cref="ObservedAt"/> is when Reel first saw the change and
/// is monotone by construction. <see cref="ObservedAt"/> exists for exposure→outcome causality —
/// "did this rating arrive after we showed the card?" — and for nothing else.</para>
///
/// <para><b>It is emphatically NOT an evaluation split axis.</b> Roughly two thousand historical
/// ratings share one onboarding ingest timestamp, so an 80% split index lands inside that tie
/// block: the whole history becomes train, the holdout collapses to zero, <c>EvalHarness</c>
/// returns null and the iteration ledger stops growing in silence. The split axis is
/// <c>UserRating.FirstRatedAt</c>, which preserves the real chronology of the user's taste and
/// keeps every historical ledger row comparable. Do not move the split onto this column.</para>
/// </summary>
public class UserRatingEvent : IAccountScoped
{
    public Guid Id { get; set; }

    public Guid AccountId { get; set; }

    public Guid TitleId { get; set; }

    public RatingSubjectType SubjectType { get; set; }

    /// <summary>0 when the subject is the whole movie/show.</summary>
    public int Season { get; set; }

    /// <summary>0 when the subject is not an episode.</summary>
    public int Episode { get; set; }

    /// <summary>The value after the change. On a <see cref="RatingChangeKind.Retracted"/> row this is the value that was removed.</summary>
    public short Rating { get; set; }

    /// <summary>The value before the change; null on a <see cref="RatingChangeKind.Created"/> row.</summary>
    public short? PreviousRating { get; set; }

    /// <summary>The source's claim about when the user rated.</summary>
    public DateTime RatedAt { get; set; }

    /// <summary>When Reel saw it. Monotone; the only timestamp the outcome join may order on.</summary>
    public DateTime ObservedAt { get; set; }

    public RatingSource Source { get; set; } = RatingSource.Trakt;

    public RatingChangeKind ChangeKind { get; set; }
}
