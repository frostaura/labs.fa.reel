using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Application.Pipeline;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Infrastructure.Background;
using FrostAura.Reel.Infrastructure.Persistence;
using FrostAura.Reel.Infrastructure.Sse;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FrostAura.Reel.Infrastructure;

public static partial class DependencyInjection
{
    /// <summary>Persistence, the tenancy scope, the pipeline event hub and the job runner.</summary>
    private static IServiceCollection AddReelJobs(this IServiceCollection services, string connectionString)
    {
        services.AddScoped<IAccountContext, AccountContext>();

        services.AddDbContext<ReelDbContext>(options =>
            options.UseNpgsql(connectionString, npgsql => npgsql.UseVector()));
        services.AddScoped<IReelDbContext>(sp => sp.GetRequiredService<ReelDbContext>());

        // ── Pipeline: event hub and the job runner ─────────────────────────────────────────
        services.AddSingleton<IPipelineEventHub, PipelineEventHub>();
        services.AddHostedService<JobRunnerService>();

        // ── Status API v2 ───────────────────────────────────────────────────────────────────
        // TryAdd: the provider-health service (WS-1) registers its breaker as the reader and wins
        // whichever partial runs first; until one exists the status API says "Unmonitored".
        services.AddScoped<SyncStatusQuery>();
        services.TryAddSingleton<IProviderHealthReader, UnmonitoredProviderHealthReader>();

        return services;
    }
}
