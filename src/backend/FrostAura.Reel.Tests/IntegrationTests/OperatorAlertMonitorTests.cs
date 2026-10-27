using System.Collections.Concurrent;
using FrostAura.Reel.Application.Llm;
using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Application.Pipeline;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Sync;
using FrostAura.Reel.Domain.Tenancy;
using FrostAura.Reel.Infrastructure.Persistence;
using FrostAura.Reel.Infrastructure.Telemetry;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrostAura.Reel.Tests.IntegrationTests;

/// <summary>
/// Alerts (K-7, E-12; FD-20 default destination): a provider refusal raises one alert per breaker
/// at the destination within one evaluation, repeats hourly while it lasts and resolves when it
/// clears; failed attempts over the hourly threshold raise one; the status API shows them to the
/// Founder tier only. On the runner collection's database, because the failure count is global.
/// </summary>
[Collection("job-runner")]
public sealed class OperatorAlertMonitorTests(JobRunnerDatabaseFixture fixture)
{
    private static readonly DateTime Opened = new(2026, 10, 26, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task A_forced_402_raises_one_alert_at_the_destination_reminds_hourly_and_resolves_when_the_breaker_closes()
    {
        if (!await ResetAsync())
        {
            return;
        }

        var health = new SwitchableProviderHealthReader();
        var sink = new RecordingSink();
        await using var services = Services(health, sink);
        var monitor = services.GetRequiredService<OperatorAlertMonitor>();
        var now = DateTime.UtcNow;

        health.Snapshot = Down(ProviderBreakerReasons.PaymentRequired, since: now.AddSeconds(-30));
        await monitor.EvaluateAsync(now, CancellationToken.None);

        var (raised, kind) = Assert.Single(sink.Deliveries);
        Assert.Equal(OperatorAlertEvent.Raised, kind);
        Assert.Equal(OperatorAlertCodes.ProviderRefused, raised.Code);
        Assert.Equal("payment_required", raised.ReasonCode);
        Assert.Equal(ApiProvider.OpenRouter, raised.Provider);
        Assert.Single(monitor.Current());

        // The breaker's own 15-minute probe cycle reopens it with a new Since: still one alert.
        health.Snapshot = Down(ProviderBreakerReasons.PaymentRequired, since: now.AddMinutes(15));
        await monitor.EvaluateAsync(now.AddMinutes(16), CancellationToken.None);
        Assert.Single(sink.Deliveries);

        await monitor.EvaluateAsync(now.AddMinutes(61), CancellationToken.None);
        Assert.Equal(OperatorAlertEvent.Reminder, sink.Deliveries.Last().Event);
        Assert.Equal(2, sink.Deliveries.Count);

        health.Snapshot = new ProviderHealthSnapshot(ProviderHealthState.Healthy, now, []);
        await monitor.EvaluateAsync(now.AddMinutes(62), CancellationToken.None);
        Assert.Equal(OperatorAlertEvent.Resolved, sink.Deliveries.Last().Event);
        Assert.Empty(monitor.Current());
    }

    [Fact]
    public async Task A_model_purpose_over_the_alarm_line_raises_one_alert_naming_it_and_resolves_when_the_window_clears()
    {
        if (!await ResetAsync())
        {
            return;
        }

        var sink = new RecordingSink();
        using var calls = new FrostAura.Reel.Infrastructure.Llm.LlmCallMetrics();
        await using var services = Services(new SwitchableProviderHealthReader(), sink, llm: calls);
        var monitor = services.GetRequiredService<OperatorAlertMonitor>();
        var now = DateTime.UtcNow;

        // 2 length finishes in a full 50-call window is 4%, above WS-1's 2% line.
        for (var i = 0; i < 48; i++)
        {
            calls.Record("rerank", lengthFinishes: 0, attempts: 1, fallback: false);
        }

        calls.Record("rerank", lengthFinishes: 1, attempts: 2, fallback: false);
        calls.Record("rerank", lengthFinishes: 1, attempts: 2, fallback: false);
        // A healthy purpose beside it raises nothing.
        calls.Record("lens", lengthFinishes: 0, attempts: 1, fallback: false);
        await monitor.EvaluateAsync(now, CancellationToken.None);

        var (raised, kind) = Assert.Single(sink.Deliveries);
        Assert.Equal(OperatorAlertEvent.Raised, kind);
        Assert.Equal(OperatorAlertCodes.LlmAlarm, raised.Code);
        Assert.Equal("rerank", raised.Purpose);
        Assert.Equal("length", raised.ReasonCode);
        Assert.Equal(50, raised.Count);

        // Still alarming a minute later: no second delivery.
        await monitor.EvaluateAsync(now.AddMinutes(1), CancellationToken.None);
        Assert.Single(sink.Deliveries);

        for (var i = 0; i < 50; i++)
        {
            calls.Record("rerank", lengthFinishes: 0, attempts: 1, fallback: false);
        }

        await monitor.EvaluateAsync(now.AddMinutes(2), CancellationToken.None);
        Assert.Equal(OperatorAlertEvent.Resolved, sink.Deliveries.Last().Event);
        Assert.Empty(monitor.Current());
    }

    [Fact]
    public async Task A_purpose_breaker_is_not_an_operator_alert()
    {
        if (!await ResetAsync())
        {
            return;
        }

        var health = new SwitchableProviderHealthReader
        {
            Snapshot = new ProviderHealthSnapshot(ProviderHealthState.Degraded, Opened, [
                new ProviderBreaker(ApiProvider.OpenRouter, ApiUsagePurpose.CrossReference, ProviderBreakerState.Open, ProviderBreakerReasons.TruncationBreaker, Opened, Opened.AddHours(8)),
            ]),
        };
        var sink = new RecordingSink();
        await using var services = Services(health, sink);

        await services.GetRequiredService<OperatorAlertMonitor>().EvaluateAsync(DateTime.UtcNow, CancellationToken.None);

        Assert.Empty(sink.Deliveries);
    }

    [Fact]
    public async Task Failed_attempts_over_the_hourly_threshold_raise_one_alert_counting_retried_attempts_and_terminal_failures()
    {
        if (!await ResetAsync())
        {
            return;
        }

        var sink = new RecordingSink();
        var metrics = new ReelMetrics();
        await using var services = Services(new SwitchableProviderHealthReader(), sink, metrics);
        var monitor = services.GetRequiredService<OperatorAlertMonitor>();
        var now = DateTime.UtcNow;

        // Three terminal failures this hour, one last hour (outside the window).
        await AddFailedAsync(JobKind.CrossReference, now.AddMinutes(-50));
        await AddFailedAsync(JobKind.CrossReference, now.AddMinutes(-20));
        await AddFailedAsync(JobKind.EnrichCatalog, now.AddMinutes(-10));
        await AddFailedAsync(JobKind.EnrichCatalog, now.AddMinutes(-70));
        // Retried attempts the database no longer shows: the runner's memory has them.
        metrics.RememberFailure(now.AddMinutes(-5), JobKind.DeltaSync);

        await monitor.EvaluateAsync(now, CancellationToken.None);
        Assert.Empty(sink.Deliveries); // 4 < 5

        metrics.RememberFailure(now.AddMinutes(-1), JobKind.DeltaSync);
        await monitor.EvaluateAsync(now, CancellationToken.None);

        var (alert, kind) = Assert.Single(sink.Deliveries);
        Assert.Equal(OperatorAlertEvent.Raised, kind);
        Assert.Equal(OperatorAlertCodes.JobFailures, alert.Code);
        Assert.Equal(5, alert.Count);
        Assert.Equal(5, alert.Threshold);
        Assert.Equal(
            [(JobKind.CrossReference, 2), (JobKind.DeltaSync, 2), (JobKind.EnrichCatalog, 1)],
            alert.Kinds!.Select(k => (k.Kind, k.Count)).ToArray());
    }

    [Fact]
    public async Task The_host_local_destination_writes_one_ALERT_line_per_event()
    {
        var lines = new ConcurrentQueue<(string Category, LogLevel Level, string Message)>();
        using var loggers = LoggerFactory.Create(b => b.AddProvider(new CapturingProvider(lines)));
        var sink = new LogOperatorAlertSink(loggers);
        var alert = new OperatorAlert(OperatorAlertCodes.ProviderRefused, Opened, Opened, "payment_required", ApiProvider.OpenRouter, null, null, null);

        await sink.DeliverAsync(alert, OperatorAlertEvent.Raised, CancellationToken.None);
        await sink.DeliverAsync(alert, OperatorAlertEvent.Resolved, CancellationToken.None);

        Assert.Collection(
            lines,
            l =>
            {
                Assert.Equal(LogOperatorAlertSink.Category, l.Category);
                Assert.Equal(LogLevel.Error, l.Level);
                Assert.StartsWith("ALERT raised provider_refused: OpenRouter refuses every call (payment_required)", l.Message, StringComparison.Ordinal);
                Assert.DoesNotContain("(null)", l.Message, StringComparison.Ordinal);
            },
            l =>
            {
                Assert.Equal(LogLevel.Warning, l.Level);
                Assert.StartsWith("ALERT resolved provider_refused", l.Message, StringComparison.Ordinal);
            });

        lines.Clear();
        var failures = new OperatorAlert(OperatorAlertCodes.JobFailures, Opened, Opened, null, null, 6, 5, [new AlertKindCount(JobKind.EnrichCatalog, 6)]);
        await sink.DeliverAsync(failures, OperatorAlertEvent.Reminder, CancellationToken.None);
        var line = Assert.Single(lines);
        Assert.StartsWith("ALERT still active job_failures: 6 failed job attempt(s) in the hour, threshold 5 (EnrichCatalog 6)", line.Message, StringComparison.Ordinal);

        lines.Clear();
        var llm = new OperatorAlert(OperatorAlertCodes.LlmAlarm, Opened, Opened, "length", ApiProvider.OpenRouter, 50, null, null) { Purpose = "rerank" };
        await sink.DeliverAsync(llm, OperatorAlertEvent.Raised, CancellationToken.None);
        Assert.StartsWith(
            "ALERT raised llm_alarm: model purpose rerank is above the length alarm line over its last 50 calls",
            Assert.Single(lines).Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_status_API_shows_operator_alerts_to_the_Founder_tier_only()
    {
        if (!await ResetAsync())
        {
            return;
        }

        var reader = new FixedAlerts([new OperatorAlert(OperatorAlertCodes.ProviderRefused, Opened, Opened, "payment_required", ApiProvider.OpenRouter, null, null, null)]);
        await using var db = fixture.CreateContext();
        var founder = await SeedAccountAsync(db, AccountTier.Founder);
        var free = await SeedAccountAsync(db, AccountTier.Free);
        using var calls = new FrostAura.Reel.Infrastructure.Llm.LlmCallMetrics();
        calls.Record("rerank", lengthFinishes: 1, attempts: 2, fallback: false);
        var query = new SyncStatusQuery(db, new UnmonitoredProviderHealthReader(), reader, calls);

        var founderStatus = await query.GetAsync(founder, 10, DateTime.UtcNow, CancellationToken.None);
        var freeStatus = await query.GetAsync(free, 10, DateTime.UtcNow, CancellationToken.None);

        Assert.Equal("payment_required", Assert.Single(founderStatus.Alerts).ReasonCode);
        Assert.Empty(freeStatus.Alerts);
        // The model-call counters are the operator's too: the same tally the metrics report reads.
        var rerank = Assert.Single(founderStatus.LlmHealth);
        Assert.Equal(("rerank", 1L, 1L), (rerank.Purpose, rerank.Calls, rerank.LengthCalls));
        Assert.Empty(freeStatus.LlmHealth);
    }

    // ── Plumbing ───────────────────────────────────────────────────────────────────────────────

    private ServiceProvider Services(
        IProviderHealthReader health, IOperatorAlertSink sink, ReelMetrics? metrics = null, ILlmCallHealthReader? llm = null)
    {
        var services = new ServiceCollection();
        services.AddScoped<IAccountContext>(_ => new AccountContext(new UnscopedReadPolicy(UnscopedReadMode.Throw)));
        services.AddDbContext<ReelDbContext>(o => o.UseNpgsql(fixture.ConnectionString, npgsql => npgsql.UseVector()));
        services.AddScoped<IReelDbContext>(sp => sp.GetRequiredService<ReelDbContext>());
        services.AddSingleton(health);
        services.AddSingleton(sink);
        if (llm is not null)
        {
            services.AddSingleton(llm);
        }

        services.AddSingleton<ILogger<OperatorAlertMonitor>>(NullLogger<OperatorAlertMonitor>.Instance);
        services.AddSingleton(sp => new OperatorAlertMonitor(
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetServices<IOperatorAlertSink>(),
            sp.GetRequiredService<ILogger<OperatorAlertMonitor>>(),
            metrics));
        return services.BuildServiceProvider(validateScopes: true);
    }

    private static ProviderHealthSnapshot Down(string reason, DateTime since) =>
        new(ProviderHealthState.Down, since, [
            new ProviderBreaker(ApiProvider.OpenRouter, null, ProviderBreakerState.Open, reason, since, since.AddMinutes(15)),
        ]);

    private async Task<bool> ResetAsync()
    {
        if (!fixture.Available)
        {
            return false;
        }

        await using var db = fixture.CreateContext();
        await db.SyncJobs.ExecuteDeleteAsync();
        return true;
    }

    private async Task AddFailedAsync(JobKind kind, DateTime completedAt)
    {
        await using var db = fixture.CreateContext();
        db.SyncJobs.Add(new SyncJob
        {
            Id = Guid.NewGuid(),
            Kind = kind,
            Status = JobStatus.Failed,
            EnqueuedAt = completedAt.AddMinutes(-5),
            StartedAt = completedAt.AddMinutes(-4),
            CompletedAt = completedAt,
            AttemptCount = 3,
            Error = "Timed out.",
        });
        await db.SaveChangesAsync();
    }

    private static async Task<Account> SeedAccountAsync(ReelDbContext db, AccountTier tier)
    {
        var now = DateTime.UtcNow;
        var account = new Account
        {
            Id = Guid.NewGuid(),
            TraktUserSlug = $"alerts-{Guid.NewGuid():N}",
            Tier = tier,
            CreatedAt = now,
            LastSeenAt = now,
            PipelineStage = PipelineStage.FeedReady,
            PipelineStageChangedAt = now,
        };
        db.Accounts.Add(account);
        await db.SaveChangesAsync();
        return account;
    }

    private sealed class RecordingSink : IOperatorAlertSink
    {
        public List<(OperatorAlert Alert, OperatorAlertEvent Event)> Deliveries { get; } = [];

        public Task DeliverAsync(OperatorAlert alert, OperatorAlertEvent kind, CancellationToken ct)
        {
            Deliveries.Add((alert, kind));
            return Task.CompletedTask;
        }
    }

    private sealed class FixedAlerts(IReadOnlyList<OperatorAlert> alerts) : IOperatorAlertReader
    {
        public IReadOnlyList<OperatorAlert> Current() => alerts;
    }

    private sealed class CapturingProvider(ConcurrentQueue<(string Category, LogLevel Level, string Message)> lines) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new Capturing(categoryName, lines);

        public void Dispose()
        {
        }

        private sealed class Capturing(string category, ConcurrentQueue<(string, LogLevel, string)> lines) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                lines.Enqueue((category, logLevel, formatter(state, exception)));
        }
    }
}
