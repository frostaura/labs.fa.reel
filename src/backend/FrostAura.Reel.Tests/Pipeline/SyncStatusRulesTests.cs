using FrostAura.Reel.Application.Pipeline;
using FrostAura.Reel.Domain.Sync;
using FrostAura.Reel.Domain.Tenancy;

namespace FrostAura.Reel.Tests.Pipeline;

/// <summary>The pure rules behind the status payload: failure reason codes, the degraded-reason
/// order, and when a job summary may show progress.</summary>
public class SyncStatusRulesTests
{
    [Theory]
    [InlineData("Stalled with no heartbeat after 3 attempts.", JobFailureReason.Stalled)]
    [InlineData("Stalled with no heartbeat after @p1 attempts.", JobFailureReason.Stalled)]
    [InlineData("Response status code does not indicate success: 402 (Payment Required).", JobFailureReason.PaymentRequired)]
    [InlineData("OpenRouter returned 402.", JobFailureReason.PaymentRequired)]
    [InlineData("Response status code does not indicate success: 401 (Unauthorized).", JobFailureReason.Unauthorized)]
    [InlineData("HTTP 403", JobFailureReason.Forbidden)]
    [InlineData("Response status code does not indicate success: 429 (Too Many Requests).", JobFailureReason.RateLimited)]
    [InlineData("judge answered none of 3 batches", JobFailureReason.NoAnswer)]
    [InlineData("The re-rank family ceiling is spent for today.", JobFailureReason.BudgetSpent)]
    [InlineData("The request was canceled due to the configured HttpClient.Timeout of 100 seconds elapsing.", JobFailureReason.TimedOut)]
    [InlineData("A task was canceled.", JobFailureReason.TimedOut)]
    [InlineData("Object reference not set to an instance of an object.", JobFailureReason.Error)]
    [InlineData("", JobFailureReason.Error)]
    [InlineData(null, JobFailureReason.Error)]
    public void Error_text_maps_to_a_stable_code(string? error, string expected) =>
        Assert.Equal(expected, JobFailureReason.Classify(error));

    /// <summary>Status-code look-alikes inside ids, versions and counts are not status codes.</summary>
    [Theory]
    [InlineData("Title 4021 not found")]
    [InlineData("Job 6f3e-401a-9b2c failed")]
    [InlineData("model v1.402 rejected the schema")]
    [InlineData("processed 4030 rows then failed")]
    [InlineData("account e403f2 missing")]
    public void A_number_that_merely_contains_a_status_code_is_a_generic_error(string error) =>
        Assert.Equal(JobFailureReason.Error, JobFailureReason.Classify(error));

    [Fact]
    public void Every_code_is_lower_snake_case_and_listed()
    {
        Assert.All(JobFailureReason.All, c => Assert.Matches("^[a-z]+(_[a-z]+)*$", c));
        Assert.Equal(JobFailureReason.All.Count, JobFailureReason.All.Distinct().Count());
    }

    private static readonly ProviderHealthSnapshot Down =
        new(ProviderHealthState.Down, DateTime.UtcNow,
            [new ProviderBreaker(ApiProvider.OpenRouter, null, ProviderBreakerState.Open, "payment_required", DateTime.UtcNow, null)]);

    [Theory]
    // The link outranks everything: it is the one problem only the user can fix.
    [InlineData(ConnectionStatus.Revoked, 3, PipelineStage.NotEnoughRatings, true, DegradedReason.TraktRevoked)]
    [InlineData(ConnectionStatus.RefreshFailed, 3, PipelineStage.Degraded, true, DegradedReason.TraktRefreshFailed)]
    [InlineData(ConnectionStatus.Active, 3, PipelineStage.NotEnoughRatings, true, DegradedReason.TraktWritesDeadLettered)]
    [InlineData(ConnectionStatus.Active, 0, PipelineStage.NotEnoughRatings, true, DegradedReason.NotEnoughRatings)]
    [InlineData(ConnectionStatus.Active, 0, PipelineStage.FeedReady, true, DegradedReason.ProviderDown)]
    [InlineData(ConnectionStatus.Active, 0, PipelineStage.Degraded, false, DegradedReason.PipelineFailed)]
    public void The_degraded_reason_is_the_most_actionable_cause(
        ConnectionStatus connection, int deadLetters, PipelineStage stage, bool providerDown, DegradedReason expected)
    {
        var health = providerDown ? Down : ProviderHealthSnapshot.Unmonitored;
        Assert.Equal(expected, SyncStatusQuery.ResolveDegradedReason(connection, deadLetters, stage, health));
    }

    [Theory]
    [InlineData(ConnectionStatus.Revoked, PipelineStage.Degraded)]
    [InlineData(ConnectionStatus.Active, PipelineStage.Degraded)]
    [InlineData(ConnectionStatus.Revoked, PipelineStage.FeedReady)]
    public void A_state_written_by_a_newer_build_outranks_every_reason_read_off_its_fallback(ConnectionStatus connection, PipelineStage stage) =>
        Assert.Equal(DegradedReason.UnrecognisedState, SyncStatusQuery.ResolveDegradedReason(connection, 0, stage, Down, unrecognisedState: true));

    [Theory]
    [InlineData(PipelineStage.FeedReady, ProviderHealthState.Unmonitored)]
    [InlineData(PipelineStage.FeedReady, ProviderHealthState.Healthy)]
    [InlineData(PipelineStage.FeedReady, ProviderHealthState.Degraded)]
    [InlineData(PipelineStage.Training, ProviderHealthState.Unmonitored)]
    public void A_healthy_account_has_no_degraded_reason(PipelineStage stage, ProviderHealthState state) =>
        Assert.Null(SyncStatusQuery.ResolveDegradedReason(
            ConnectionStatus.Active, 0, stage, new ProviderHealthSnapshot(state, DateTime.UtcNow, [])));

    [Fact]
    public void The_default_provider_reader_says_unmonitored_never_healthy()
    {
        var snapshot = new UnmonitoredProviderHealthReader().Current();
        Assert.Equal(ProviderHealthState.Unmonitored, snapshot.State);
        Assert.Empty(snapshot.Breakers);
        Assert.Null(snapshot.CheckedAt);
    }

    [Theory]
    // status, attempts, pct, expected pct, expected retrying
    [InlineData(JobStatus.Pending, 0, null, null, false)]
    [InlineData(JobStatus.Pending, 1, 100, null, true)]
    [InlineData(JobStatus.Pending, 2, 55, null, true)]
    [InlineData(JobStatus.Running, 1, 55, 55, false)]
    [InlineData(JobStatus.Running, 1, 100, 100, false)]
    [InlineData(JobStatus.Running, 2, 100, null, true)]
    [InlineData(JobStatus.Running, 3, 20, 20, true)]
    [InlineData(JobStatus.Succeeded, 1, 100, 100, false)]
    [InlineData(JobStatus.Failed, 3, 80, 80, false)]
    public void Progress_is_shown_only_when_it_is_this_attempts_own(
        JobStatus status, int attempts, int? pct, int? expectedPct, bool expectedRetrying)
    {
        var job = new SyncJob
        {
            Id = Guid.NewGuid(),
            AccountId = Guid.NewGuid(),
            Kind = JobKind.FullIngest,
            Status = status,
            AttemptCount = attempts,
            ProgressPct = pct,
            ProgressMessage = pct is null ? null : "working",
            EnqueuedAt = DateTime.UtcNow,
            Error = attempts > 1 || status == JobStatus.Pending && attempts > 0 ? "429 Too Many Requests" : null,
        };

        var summary = SyncStatusQuery.Summarise(job, PipelineStage.Ingesting);

        Assert.Equal(expectedPct, summary.ProgressPct is { } p ? (int)p : null);
        Assert.Equal(expectedRetrying, summary.Retrying);
        Assert.Equal(expectedRetrying ? JobFailureReason.RateLimited : null, summary.RetryReasonCode);
        Assert.Equal(JobRunnerLimits.MaxAttempts, summary.MaxAttempts);
        if (status == JobStatus.Pending)
        {
            Assert.Null(summary.ProgressMessage);
        }
    }

    [Fact]
    public void The_named_pass_never_carries_a_percentage()
    {
        var job = new SyncJob
        {
            Id = Guid.NewGuid(),
            AccountId = Guid.NewGuid(),
            Kind = JobKind.CrossReference,
            Status = JobStatus.Running,
            AttemptCount = 1,
            ProgressPct = 37,
            ProgressMessage = "Cross-referencing · first pass",
            EnqueuedAt = DateTime.UtcNow,
        };

        var summary = SyncStatusQuery.Summarise(job, PipelineStage.FeedReady);

        Assert.Equal(JobVisibility.NamedPass, summary.Visibility);
        Assert.Null(summary.ProgressPct);
        Assert.Equal("Cross-referencing · first pass", summary.ProgressMessage);
    }
}
