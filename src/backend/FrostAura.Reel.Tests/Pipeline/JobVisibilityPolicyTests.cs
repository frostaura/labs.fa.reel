using FrostAura.Reel.Application.Pipeline;
using FrostAura.Reel.Application.Ranking;
using FrostAura.Reel.Domain.Sync;
using FrostAura.Reel.Domain.Tenancy;

namespace FrostAura.Reel.Tests.Pipeline;

/// <summary>
/// The contract test behind "one background set shared with the frontend". The frontend no longer
/// keeps a list of its own: it reads each job's <c>visibility</c> and <c>sseKind</c> from the
/// status payload. What has to hold for that to be safe is pinned here — every job kind has a
/// deliberate classification, and every kind has its own SSE name.
/// </summary>
public class JobVisibilityPolicyTests
{
    /// <summary>
    /// The decision table, one row per <see cref="JobKind"/>, for an account past onboarding and a
    /// job nobody asked for. A new kind fails <see cref="Every_job_kind_has_a_deliberate_row"/> until
    /// someone adds its row here — the policy's fallback arm is a safety net, not a decision.
    /// </summary>
    private static readonly Dictionary<JobKind, JobVisibility> Unprompted = new()
    {
        [JobKind.FullIngest] = JobVisibility.UserVisible,
        [JobKind.DeltaSync] = JobVisibility.Background,
        [JobKind.FullReconcile] = JobVisibility.Background,
        [JobKind.HydrateCatalog] = JobVisibility.Background,
        [JobKind.EnrichCatalog] = JobVisibility.Background,
        [JobKind.Train] = JobVisibility.Background,
        [JobKind.Evaluate] = JobVisibility.Background,
        [JobKind.BuildFeed] = JobVisibility.Background,
        [JobKind.RefreshAvailability] = JobVisibility.Background,
        [JobKind.BuildDigest] = JobVisibility.Background,
        [JobKind.BuildTasteNarrative] = JobVisibility.Background,
        [JobKind.PersonaliseTitles] = JobVisibility.Background,
        [JobKind.SeedCatalog] = JobVisibility.Background,
        [JobKind.CrossReference] = JobVisibility.NamedPass,
    };

    [Fact]
    public void Every_job_kind_has_a_deliberate_row()
    {
        var missing = Enum.GetValues<JobKind>().Where(k => !Unprompted.ContainsKey(k)).ToList();
        Assert.Empty(missing);
    }

    [Fact]
    public void Unprompted_jobs_after_onboarding_classify_as_the_table_says()
    {
        foreach (var (kind, expected) in Unprompted)
        {
            Assert.Equal(expected, JobVisibilityPolicy.Classify(kind, triggerReason: null, PipelineStage.FeedReady));
        }
    }

    [Fact]
    public void Every_job_kind_has_its_own_sse_kind()
    {
        var names = Enum.GetValues<JobKind>().Select(JobVisibilityPolicy.SseKind).ToList();
        Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());
        Assert.All(names, n => Assert.Matches("^[a-z]+$", n));
    }

    /// <summary>
    /// The names the handlers already publish on their SSE frames, and the keys the frontend's
    /// per-kind copy is keyed by. Renaming one silently orphans that copy.
    /// </summary>
    [Theory]
    [InlineData(JobKind.FullIngest, "ingest")]
    [InlineData(JobKind.DeltaSync, "delta")]
    [InlineData(JobKind.FullReconcile, "reconcile")]
    [InlineData(JobKind.HydrateCatalog, "hydrate")]
    [InlineData(JobKind.EnrichCatalog, "enrich")]
    [InlineData(JobKind.Train, "train")]
    [InlineData(JobKind.Evaluate, "evaluate")]
    [InlineData(JobKind.BuildFeed, "feed")]
    [InlineData(JobKind.BuildTasteNarrative, "narrative")]
    [InlineData(JobKind.PersonaliseTitles, "lens")]
    [InlineData(JobKind.CrossReference, "xref")]
    public void Sse_kinds_match_the_names_the_handlers_publish(JobKind kind, string sseKind) =>
        Assert.Equal(sseKind, JobVisibilityPolicy.SseKind(kind));

    [Fact]
    public void Only_the_users_own_sync_now_is_a_visible_delta_sync()
    {
        Assert.Equal(JobVisibility.UserVisible,
            JobVisibilityPolicy.Classify(JobKind.DeltaSync, JobVisibilityPolicy.SyncNowReason, PipelineStage.FeedReady));

        foreach (var trigger in new string?[] { null, "delta-poll", "relink", "user-sync" })
        {
            Assert.Equal(JobVisibility.Background,
                JobVisibilityPolicy.Classify(JobKind.DeltaSync, trigger, PipelineStage.FeedReady));
        }
    }

    [Theory]
    [InlineData(FeedBuildPolicy.ManualRefreshReason, JobVisibility.UserVisible)]
    [InlineData(FeedBuildPolicy.FiltersChangedReason, JobVisibility.UserVisible)]
    [InlineData("delta-sync", JobVisibility.Background)]
    [InlineData("not interested", JobVisibility.Background)]
    [InlineData("model-trained", JobVisibility.Background)]
    [InlineData(null, JobVisibility.Background)]
    public void A_feed_build_is_visible_only_when_the_user_asked_the_page_to_change(string? trigger, JobVisibility expected) =>
        Assert.Equal(expected, JobVisibilityPolicy.Classify(JobKind.BuildFeed, trigger, PipelineStage.FeedReady));

    [Theory]
    [InlineData(PipelineStage.Linked, JobVisibility.UserVisible)]
    [InlineData(PipelineStage.Ingesting, JobVisibility.UserVisible)]
    [InlineData(PipelineStage.Extracting, JobVisibility.UserVisible)]
    [InlineData(PipelineStage.Training, JobVisibility.UserVisible)]
    [InlineData(PipelineStage.Evaluated, JobVisibility.UserVisible)]
    [InlineData(PipelineStage.NotEnoughRatings, JobVisibility.UserVisible)]
    [InlineData(PipelineStage.FeedReady, JobVisibility.Background)]
    [InlineData(PipelineStage.Degraded, JobVisibility.Background)]
    public void The_onboarding_chain_is_visible_until_the_first_feed_exists(PipelineStage stage, JobVisibility expected)
    {
        Assert.Equal(expected, JobVisibilityPolicy.Classify(JobKind.Train, "model-trained", stage));
        Assert.Equal(expected, JobVisibilityPolicy.Classify(JobKind.Evaluate, "model-trained", stage));
        Assert.Equal(expected, JobVisibilityPolicy.Classify(JobKind.BuildFeed, "model-trained", stage));

        // Maintenance stays maintenance however early the account is.
        Assert.Equal(JobVisibility.Background, JobVisibilityPolicy.Classify(JobKind.EnrichCatalog, null, stage));
        Assert.Equal(JobVisibility.Background, JobVisibilityPolicy.Classify(JobKind.HydrateCatalog, null, stage));
    }

    [Fact]
    public void A_narrative_rewrite_is_visible_only_when_the_user_asked_for_it()
    {
        Assert.Equal(JobVisibility.UserVisible, JobVisibilityPolicy.Classify(
            JobKind.BuildTasteNarrative, JobVisibilityPolicy.NarrativeRegenerateReason, PipelineStage.FeedReady));
        Assert.Equal(JobVisibility.Background, JobVisibilityPolicy.Classify(
            JobKind.BuildTasteNarrative, "weekly-narrative", PipelineStage.FeedReady));
    }

    [Fact]
    public void A_kind_unknown_to_this_build_falls_back_to_background()
    {
        var unknown = (JobKind)Enum.GetValues<JobKind>().Cast<int>().Max() + 1;
        Assert.Equal(JobVisibility.Background, JobVisibilityPolicy.Classify(unknown, null, PipelineStage.Linked));
        Assert.False(string.IsNullOrEmpty(JobVisibilityPolicy.SseKind(unknown)));
    }
}
