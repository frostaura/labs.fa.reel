using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Application.Pipeline;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Infrastructure.Background;
using FrostAura.Reel.Infrastructure.Persistence;
using FrostAura.Reel.Infrastructure.Sse;
using FrostAura.Reel.Infrastructure.Telemetry;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace FrostAura.Reel.Infrastructure;

public static partial class DependencyInjection
{
    /// <summary>Persistence, the tenancy scope, the pipeline event hub and the job runner.</summary>
    private static IServiceCollection AddReelJobs(this IServiceCollection services, string connectionString)
    {
        // The fail-closed tenancy policy (J-11), once per process so its once-per-call-site log is too.
        services.TryAddSingleton(sp => UnscopedReadPolicy.FromConfiguration(
            sp.GetService<IConfiguration>(), sp.GetService<ILogger<UnscopedReadPolicy>>()));
        services.AddScoped<IAccountContext, AccountContext>();

        // Names a statement that stalls while it is stalled, with whatever is blocking it (F+1).
        services.TryAddSingleton(sp => new SlowCommandDiagnostics(
            sp.GetRequiredService<ILogger<SlowCommandDiagnostics>>(), connectionString));
        // Every context connection carries the session bounds (idle-in-transaction, F+1).
        services.TryAddSingleton(sp => DatabaseSessionSettings.From(connectionString, sp.GetService<IConfiguration>()));
        services.AddDbContext<ReelDbContext>((sp, options) => options
            .UseNpgsql(sp.GetRequiredService<DatabaseSessionSettings>().ConnectionString, npgsql => npgsql.UseVector())
            .AddInterceptors(sp.GetRequiredService<SlowCommandDiagnostics>()));
        services.AddScoped<IReelDbContext>(sp => sp.GetRequiredService<ReelDbContext>());

        // ── Pipeline: event hub and the job runner ─────────────────────────────────────────
        services.AddSingleton<IPipelineEventHub, PipelineEventHub>();
        services.TryAddSingleton(JobRunnerOptions.Default);
        services.AddHostedService<JobRunnerService>();
        services.AddHostedService<RetentionPurgeService>();

        // ── Status API v2 ───────────────────────────────────────────────────────────────────
        // TryAdd: the provider-health service (WS-1) registers its breaker as the reader and wins
        // whichever partial runs first; until one exists the status API says "Unmonitored".
        services.AddScoped<SyncStatusQuery>();
        services.TryAddSingleton<IProviderHealthReader, UnmonitoredProviderHealthReader>();

        // ── Observability: instruments, the client-error sink, the M5 job metrics ────────────────
        services.TryAddSingleton<ReelMetrics>();
        services.TryAddSingleton<ClientErrorSink>();
        services.AddScoped<JobMetricsQuery>();
        services.AddScoped<AskMetricsQuery>();

        // ── Alerts: evaluated once a minute, delivered to every sink, read by the status API ─────────
        // FD-20 default: the host-local log is the destination until a push or email sink is named.
        services.TryAddSingleton(OperatorAlertOptions.Default);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IOperatorAlertSink, LogOperatorAlertSink>());
        services.AddSingleton<OperatorAlertMonitor>();
        services.AddSingleton<IOperatorAlertReader>(sp => sp.GetRequiredService<OperatorAlertMonitor>());
        services.AddHostedService(sp => sp.GetRequiredService<OperatorAlertMonitor>());

        return services;
    }
}
