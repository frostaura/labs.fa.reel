using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.CrossReference;
using FrostAura.Reel.Domain.Feed;
using FrostAura.Reel.Domain.Filters;
using FrostAura.Reel.Domain.Lens;
using FrostAura.Reel.Domain.Library;
using FrostAura.Reel.Domain.Ml;
using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Domain.Ports.Tmdb;
using FrostAura.Reel.Domain.Providers;
using FrostAura.Reel.Domain.Sync;
using FrostAura.Reel.Domain.Tenancy;

namespace FrostAura.Reel.Application.Persistence;

/// <summary>
/// What a stored enum name this build does not define reads as (program §5.0 rule 8). The schema
/// steward owns this table; a persisted enum without an entry fails
/// <c>PersistedEnumFallbackTests</c>, so a lane that adds one has to decide its fallback on purpose.
///
/// <para>The rule for choosing: an unknown value was written by a NEWER image, so this one must do
/// the least harmful thing with the row — never spend money, never call Trakt, never leak an
/// excluded title, never claim a job it cannot run. Where no member is inert, the fallback is
/// <see cref="Unrecognised{TEnum}"/>, a value outside the enum that every exhaustive switch sends to
/// its default branch (which throws or skips) instead of doing a known-but-wrong thing.</para>
/// </summary>
public static class PersistedEnumFallbacks
{
    private static readonly Dictionary<Type, Enum> Table = new()
    {
        // Least privilege.
        [typeof(AccountTier)] = AccountTier.Free,

        // "Something is off" — the honest reading, and the status API explains Degraded.
        [typeof(PipelineStage)] = PipelineStage.Degraded,

        // An unknown filter over-excludes rather than leaks: exclusions are airtight by spec.
        [typeof(FilterKind)] = FilterKind.ExcludeKeyword,

        // Never a demotion or a keep the judge did not make.
        [typeof(CrossReferenceVerdict)] = CrossReferenceVerdict.Unsure,
        [typeof(CrossReferenceRound)] = CrossReferenceRound.First,

        [typeof(LinkKind)] = LinkKind.DirectSearch,

        // Failed spends nothing: Pending would re-bill the extraction, Done would claim data.
        [typeof(AttributeExtractionStatus)] = AttributeExtractionStatus.Failed,

        // The role no person query, affinity or lens anchor is built on.
        [typeof(CreditRole)] = CreditRole.Writer,

        // Not "on your services": an unknown offer never counts as streaming now.
        [typeof(AvailabilityKind)] = AvailabilityKind.Buy,

        [typeof(MediaType)] = MediaType.Movie,
        [typeof(RatingChangeKind)] = RatingChangeKind.Changed,

        // Episode ratings feed no model, no exclusion and no anchor.
        [typeof(RatingSubjectType)] = RatingSubjectType.Episode,

        // An unknown reaction hides the title — the exclusion-safe reading.
        [typeof(ReactionKind)] = ReactionKind.NotInterested,
        [typeof(ReactionReason)] = ReactionReason.NotForMe,

        // Trakt-sourced rows are never written back to Trakt.
        [typeof(RatingSource)] = RatingSource.Trakt,

        // Not shown and not regenerated until its cache entry ages out.
        [typeof(LensStatus)] = LensStatus.Failed,

        // Never served as the active model, never counted as a failed training.
        [typeof(ArtifactStatus)] = ArtifactStatus.Superseded,
        [typeof(TrainingRunStatus)] = TrainingRunStatus.Failed,
        [typeof(SnapshotStatus)] = SnapshotStatus.Superseded,

        [typeof(FeedRowKind)] = FeedRowKind.NewForYou,
        [typeof(FeedBuildOutcome)] = FeedBuildOutcome.Skipped,

        // Background, global, never the pill. The runner claims only kinds it has a handler for,
        // so an unknown kind is never executed as this one.
        [typeof(JobKind)] = JobKind.SeedCatalog,

        // Terminal and inert: neither claimed nor shown as running.
        [typeof(JobStatus)] = JobStatus.Cancelled,

        // Budgets count it against the paid provider: over-counting spends less, never more.
        [typeof(ApiProvider)] = ApiProvider.OpenRouter,

        // No Trakt call is made for a revoked link — a refresh would rotate the single-use token.
        [typeof(ConnectionStatus)] = ConnectionStatus.Revoked,

        // Every member is a different Trakt write: the dispatcher's default branch fails the entry
        // towards dead-letter instead of sending a write nobody asked for.
        [typeof(OutboxKind)] = Unrecognised<OutboxKind>(),

        // Not retried, not shown as pending.
        [typeof(OutboxStatus)] = OutboxStatus.DeadLetter,

        [typeof(ExposureSurface)] = ExposureSurface.FeedPage,

        // Not "watched", so the title is not treated as seen on the strength of a value we cannot read.
        [typeof(ListRemovalReason)] = ListRemovalReason.UserRemoved,

        [typeof(PersonRole)] = PersonRole.Any,
        [typeof(PersonCreditRole)] = PersonCreditRole.Any,
        [typeof(RatePriority)] = RatePriority.Backfill,
        [typeof(WebPushOutcome)] = WebPushOutcome.Failure,

        // The why-source lanes (WS-1): an unreadable source is never presented as model output.
        [typeof(AgentFailureKind)] = AgentFailureKind.Unknown,
        [typeof(ResultSource)] = ResultSource.Fallback,
        [typeof(Satisfies)] = Satisfies.Partial,
    };

    /// <summary>
    /// A value outside <typeparamref name="TEnum"/>'s members, for enums with no inert member.
    /// <c>Enum.IsDefined</c> is false for it and it is never written: a row read with it is only
    /// ever re-saved if code assigns the property, which nothing does with a value it cannot name.
    /// </summary>
    public static TEnum Unrecognised<TEnum>()
        where TEnum : struct, Enum =>
        (TEnum)Enum.ToObject(typeof(TEnum), -1);

    /// <summary>Whether the steward has decided a fallback for this enum.</summary>
    public static bool IsDeclared(Type enumType) => Table.ContainsKey(enumType);

    /// <summary>
    /// The fallback for <typeparamref name="TEnum"/>. An enum nobody declared yet reads as
    /// <see cref="Unrecognised{TEnum}"/> — inert at runtime, and a failing test until declared.
    /// </summary>
    public static TEnum For<TEnum>()
        where TEnum : struct, Enum =>
        Table.TryGetValue(typeof(TEnum), out var fallback) ? (TEnum)fallback : Unrecognised<TEnum>();

    /// <summary>The declared fallback as an object, for tests that walk the model.</summary>
    public static object? Declared(Type enumType) => Table.GetValueOrDefault(enumType);
}
