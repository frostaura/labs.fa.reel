using System.Reflection;
using FrostAura.Reel.Application.Jobs;
using FrostAura.Reel.Application.Pipeline;
using FrostAura.Reel.Domain.Sync;
using FrostAura.Reel.Infrastructure.Background;

namespace FrostAura.Reel.Tests.Concurrency;

/// <summary>
/// The runner's fairness policy as data (J-5): lanes, caps, deadlines and the breaker pause. The
/// behaviour on a real database is in <c>JobRunnerServiceTests</c>; these pin the table itself so a
/// kind added later cannot slip in without a decision.
/// </summary>
public class JobRunnerPolicyTests
{
    private static readonly JobRunnerOptions Options = JobRunnerOptions.Default;

    [Fact]
    public void Every_job_kind_has_an_explicit_deadline_policy()
    {
        var missing = Enum.GetValues<JobKind>().Where(k => !Options.Kinds.ContainsKey(k)).ToList();
        Assert.Empty(missing);
        Assert.All(Options.Kinds.Values, p => Assert.True(p.Deadline > TimeSpan.Zero));
    }

    [Fact]
    public void The_reserved_lane_is_for_the_work_a_user_waits_on_and_nothing_else()
    {
        Assert.Equal(
            new[] { JobKind.DeltaSync, JobKind.Train, JobKind.BuildFeed }.Order(),
            Options.InteractiveKinds.Order());
        Assert.True(Options.ReservedSlots >= 1);
        Assert.True(Options.GeneralSlots >= Options.KindCaps.Count, "every capped kind can run at once beside the others");
    }

    [Fact]
    public void Enrichment_cross_reference_and_hydration_run_one_at_a_time()
    {
        Assert.Equal(1, Options.KindCaps[JobKind.EnrichCatalog]);
        Assert.Equal(1, Options.KindCaps[JobKind.CrossReference]);
        Assert.Equal(1, Options.KindCaps[JobKind.HydrateCatalog]);
        Assert.DoesNotContain(Options.KindCaps.Keys, Options.InteractiveKinds.Contains);
    }

    [Fact]
    public void The_cross_reference_deadline_sits_above_the_handlers_own_hard_ceiling()
    {
        // The pass boxes itself; the runner's deadline must never be what ends a healthy run.
        var policy = Options.PolicyFor(JobKind.CrossReference);
        Assert.True(policy.Deadline > CrossReferenceJobHandler.HardCeiling);
        Assert.Equal(DeadlineAction.Yield, policy.OnDeadline);
    }

    [Theory]
    [InlineData(JobKind.BuildFeed)]
    [InlineData(JobKind.Train)]
    [InlineData(JobKind.DeltaSync)]
    [InlineData(JobKind.Evaluate)]
    public void Kinds_that_restart_from_nothing_retry_at_their_deadline_instead_of_slicing(JobKind kind) =>
        Assert.Equal(DeadlineAction.Retry, Options.PolicyFor(kind).OnDeadline);

    [Fact]
    public void Every_llm_kind_names_purposes_the_ledger_actually_uses()
    {
        var purposes = typeof(ApiUsagePurpose)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToHashSet(StringComparer.Ordinal);

        Assert.All(Options.LlmKinds.Values.SelectMany(p => p), p => Assert.Contains(p, purposes));
        Assert.DoesNotContain(Options.LlmKinds.Keys, Options.InteractiveKinds.Contains);
    }

    [Fact]
    public void A_provider_that_is_down_pauses_every_llm_kind_and_nothing_else()
    {
        var paused = JobRunnerService.PausedKinds(Snapshot(ProviderHealthState.Down), Options);
        Assert.Equal(Options.LlmKinds.Keys.Select(k => k.ToString()).Order(), paused.Order());
    }

    [Fact]
    public void A_provider_wide_open_breaker_pauses_every_llm_kind()
    {
        var paused = JobRunnerService.PausedKinds(
            Snapshot(ProviderHealthState.Degraded, Breaker(purpose: null, ProviderBreakerState.Open)), Options);
        Assert.Equal(Options.LlmKinds.Count, paused.Count);
    }

    [Fact]
    public void An_open_breaker_on_one_purpose_pauses_only_the_kinds_that_spend_under_it()
    {
        var paused = JobRunnerService.PausedKinds(
            Snapshot(ProviderHealthState.Degraded, Breaker(ApiUsagePurpose.CrossReference, ProviderBreakerState.Open)), Options);
        Assert.Equal([nameof(JobKind.CrossReference)], paused);
    }

    [Fact]
    public void A_degraded_but_closed_breaker_an_unmonitored_reader_or_a_trakt_breaker_pauses_nothing()
    {
        Assert.Empty(JobRunnerService.PausedKinds(
            Snapshot(ProviderHealthState.Degraded, Breaker(ApiUsagePurpose.Lens, ProviderBreakerState.Degraded)), Options));
        Assert.Empty(JobRunnerService.PausedKinds(ProviderHealthSnapshot.Unmonitored, Options));
        Assert.Empty(JobRunnerService.PausedKinds(null, Options));
        Assert.Empty(JobRunnerService.PausedKinds(
            Snapshot(ProviderHealthState.Degraded,
                new ProviderBreaker(ApiProvider.Trakt, null, ProviderBreakerState.Open, "unauthorized", DateTime.UtcNow, null)),
            Options));
    }

    [Fact]
    public void The_claim_puts_every_value_in_a_parameter_never_inside_a_sql_literal()
    {
        // The @p1 bug (J-6): an interpolation inside quotes became the literal text "@p1".
        var sql = JobRunnerService.ClaimNextSql(DateTime.UtcNow, Enum.GetNames<JobKind>(), Options);
        Assert.DoesNotContain("'{", sql.Format, StringComparison.Ordinal);
        Assert.Equal("Stalled with no heartbeat after 3 attempts.", JobRunnerService.StalledMessage(3));
    }

    private static ProviderHealthSnapshot Snapshot(ProviderHealthState state, params ProviderBreaker[] breakers) =>
        new(state, DateTime.UtcNow, breakers);

    private static ProviderBreaker Breaker(string? purpose, ProviderBreakerState state) =>
        new(ApiProvider.OpenRouter, purpose, state, "payment_required", DateTime.UtcNow, null);
}
