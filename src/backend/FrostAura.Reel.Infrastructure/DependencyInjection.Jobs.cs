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

        services.AddDbContext<ReelDbContext>(options =>
            options.UseNpgsql(connectionString, npgsql => npgsql.UseVector()));
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

        return services;
    }
}
