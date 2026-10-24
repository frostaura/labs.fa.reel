using System.Text.Json.Serialization;
using FrostAura.Reel.Application.Ranking;
using FrostAura.Reel.Domain.Sync;
using FrostAura.Reel.Domain.Tenancy;

namespace FrostAura.Reel.Application.Pipeline;

/// <summary>How the header status treats a job.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<JobVisibility>))]
public enum JobVisibility
{
    /// <summary>
    /// Maintenance nobody asked for and nobody waits on — Trakt polls, catalogue enrichment,
    /// hydration, lens copy, digests. Never the status API's <c>activeJob</c>; a failure still
    /// reaches <c>lastFailures</c>, because health outranks progress.
    /// </summary>
    Background,

    /// <summary>Work the user started or is waiting on. Eligible to be <c>activeJob</c>.</summary>
    UserVisible,

    /// <summary>
    /// The cross-reference pass: shown by name ("Cross-referencing · first pass") with no
    /// denominator, so <c>progressPct</c> is always null. Eligible to be <c>activeJob</c> only when
    /// no <see cref="UserVisible"/> job is in flight, and excluded from the idle-pill metric shared
    /// by WS-6 and WS-9 (it is bounded by its own per-kind deadline instead).
    /// </summary>
    NamedPass,
}

/// <summary>
/// THE one classification of job kinds for the header status — the background set the frontend
/// and backend used to keep as two hand-mirrored lists (<c>IsBackground</c> in SyncEndpoints and
/// <c>BACKGROUND_KINDS</c> in syncCopy.ts) that had drifted apart, which is how catalogue and LLM
/// housekeeping came to own the pill (H-1).
///
/// <para>The frontend no longer keeps a copy: every job summary on <c>/api/sync/status</c>
/// carries its <c>visibility</c> and <c>sseKind</c>, computed here. A live SSE frame is applied
/// to the pill only when its kind matches <c>activeJob.sseKind</c>. The contract test pins that
/// every <see cref="JobKind"/> is classified and has a distinct SSE kind.</para>
/// </summary>
public static class JobVisibilityPolicy
{
    /// <summary>TriggerReason stamped by <c>POST /api/sync/now</c> — a sync the user asked for.</summary>
    public const string SyncNowReason = "user-sync-now";

    /// <summary>TriggerReason the narrative endpoint stamps when the user asks for a rewrite.</summary>
    public const string NarrativeRegenerateReason = "user-regenerate";

    /// <summary>The BuildFeed trigger reasons that are a user action the page visibly answers.</summary>
    private static readonly HashSet<string> UserBuildReasons =
    [
        FeedBuildPolicy.ManualRefreshReason,
        FeedBuildPolicy.FiltersChangedReason,
    ];

    /// <summary>
    /// Classifies a job for the header. <paramref name="stage"/> is the account's stored pipeline
    /// stage: until the first feed exists, the onboarding chain (Train, Evaluate, the first
    /// BuildFeed) is exactly what the user is waiting on; afterwards a retrain is maintenance.
    /// </summary>
    public static JobVisibility Classify(JobKind kind, string? triggerReason, PipelineStage stage)
    {
        // NotEnoughRatings is still onboarding: the Train that follows the next few ratings is the
        // one thing that account is waiting for.
        var onboarding = stage is PipelineStage.Linked or PipelineStage.Ingesting or PipelineStage.Extracting
            or PipelineStage.Training or PipelineStage.Evaluated or PipelineStage.NotEnoughRatings;

        return kind switch
        {
            JobKind.FullIngest => JobVisibility.UserVisible,
            JobKind.DeltaSync => triggerReason == SyncNowReason ? JobVisibility.UserVisible : JobVisibility.Background,
            JobKind.Train or JobKind.Evaluate => onboarding ? JobVisibility.UserVisible : JobVisibility.Background,
            JobKind.BuildFeed => onboarding || (triggerReason is not null && UserBuildReasons.Contains(triggerReason))
                ? JobVisibility.UserVisible
                : JobVisibility.Background,
            JobKind.BuildTasteNarrative => triggerReason == NarrativeRegenerateReason
                ? JobVisibility.UserVisible
                : JobVisibility.Background,
            JobKind.CrossReference => JobVisibility.NamedPass,
            JobKind.FullReconcile or JobKind.HydrateCatalog or JobKind.EnrichCatalog or JobKind.RefreshAvailability
                or JobKind.BuildDigest or JobKind.PersonaliseTitles or JobKind.SeedCatalog => JobVisibility.Background,

            // A kind added after this policy was written is maintenance until someone decides
            // otherwise: the failure mode of a wrong guess is a quiet pill, not a lying one.
            _ => JobVisibility.Background,
        };
    }

    /// <summary>
    /// The short kind the SSE <c>job-progress</c>/<c>job-completed</c> frames carry for this job
    /// kind, and the key the frontend's per-kind copy is looked up by. Kinds that publish no frames
    /// today still get a distinct name, so a handler that starts publishing needs no mapping change.
    /// </summary>
    public static string SseKind(JobKind kind) => kind switch
    {
        JobKind.FullIngest => "ingest",
        JobKind.DeltaSync => "delta",
        JobKind.FullReconcile => "reconcile",
        JobKind.HydrateCatalog => "hydrate",
        JobKind.EnrichCatalog => "enrich",
        JobKind.Train => "train",
        JobKind.Evaluate => "evaluate",
        JobKind.BuildFeed => "feed",
        JobKind.RefreshAvailability => "availability",
        JobKind.BuildDigest => "digest",
        JobKind.BuildTasteNarrative => "narrative",
        JobKind.PersonaliseTitles => "lens",
        JobKind.SeedCatalog => "seed",
        JobKind.CrossReference => "xref",
        _ => kind.ToString().ToLowerInvariant(),
    };
}
