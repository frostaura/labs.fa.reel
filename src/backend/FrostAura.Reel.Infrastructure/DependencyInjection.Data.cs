using FrostAura.Reel.Application.Jobs;
using FrostAura.Reel.Application.Sync;
using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Infrastructure.Adapters;
using FrostAura.Reel.Infrastructure.Background;
using Microsoft.Extensions.DependencyInjection;

namespace FrostAura.Reel.Infrastructure;

public static partial class DependencyInjection
{
    /// <summary>The Trakt adapter, ingestion, sync and the catalogue pipeline: its jobs, budgets and background services.</summary>
    private static IServiceCollection AddReelData(this IServiceCollection services)
    {
        // ── External adapters ──────────────────────────────────────────────────────────────
        services.AddHttpClient<ITraktClient, TraktClient>(client =>
        {
            client.BaseAddress = new Uri("https://api.trakt.tv/");
            // The library snapshots (/sync/watched/shows?extended=full for a 3k-title account) are a
            // single multi-MB response that routinely streams for >30s. Let the resilience pipeline's
            // TotalRequestTimeout govern instead — a finite HttpClient cap would pre-empt it and the
            // request would die with Polly's "didn't complete within '00:00:30'" before the body lands.
            client.Timeout = Timeout.InfiniteTimeSpan;
            client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        }).AddStandardResilienceHandler(o =>
        {
            // Defaults (10s per attempt / 30s total) are far too tight for the un-paginated watched +
            // ratings snapshots. /sync/watched/shows?extended=full for a heavy account streams for
            // minutes; a 90s attempt still timed out mid-download, failed the sync, and it retried in
            // a spiral. Give one attempt real room to finish, inside a generous total that still
            // allows a retry. CircuitBreaker sampling must stay ≥ 2× the attempt timeout (validator).
            o.AttemptTimeout.Timeout = TimeSpan.FromMinutes(4);
            o.TotalRequestTimeout.Timeout = TimeSpan.FromMinutes(10);
            o.CircuitBreaker.SamplingDuration = TimeSpan.FromMinutes(8);
        });

        // ── Shared-work coordination ───────────────────────────────────────────────────────
        services.AddSingleton<Application.Abstractions.ICatalogWorkCoordinator, Concurrency.CatalogWorkCoordinator>();

        services.AddScoped<Application.Ingestion.TitleHydrator>();

        // ── Pipeline: token store, ingestor, job handlers, schedulers ─────────────────────
        services.AddScoped<TraktTokenStore>();
        services.AddScoped<Application.Sync.OutboxDispatcher>();
        services.AddScoped<Application.Sync.ManagedListReconciler>();
        services.AddScoped<Application.Ingestion.TraktLibraryIngestor>();
        services.AddScoped<IJobHandler, FullIngestJobHandler>();
        services.AddScoped<IJobHandler, DeltaSyncJobHandler>();
        services.AddScoped<IJobHandler, HydrateCatalogJobHandler>();
        services.AddScoped<IJobHandler, EnrichCatalogJobHandler>();
        services.AddScoped<IJobHandler, RefreshAvailabilityJobHandler>();
        services.AddScoped<IJobHandler, BuildDigestJobHandler>();
        services.AddScoped<IJobHandler, SeedCatalogJobHandler>();

        // The catalogue's two daily ceilings. Scoped because both read the ledger through the
        // request/job DbContext; the enrichment one also reads the recorder's un-flushed counters,
        // which is why IPendingApiUsage is a singleton resolved into it rather than the reverse.
        services.AddScoped<Application.Jobs.CatalogHydrationBudget>();
        services.AddScoped<Application.Jobs.CatalogEnrichmentBudget>();
        services.AddScoped<Application.Jobs.CatalogSeedProgressQuery>();
        services.AddHostedService<TraktDeltaPollService>();
        services.AddHostedService<CatalogGrowthService>();
        services.AddHostedService<CatalogFollowThroughService>();
        services.AddHostedService<TitleLanguageBackfillService>();
        services.AddHostedService<TokenRefreshService>();
        services.AddHostedService<TraktOutboxDispatcherService>();
        services.AddHostedService<WeeklyDigestService>();
        // WS-4's one-off repairs, run on every boot (idempotent and self-terminating).
        services.AddHostedService<DataRepairStartupService>();

        return services;
    }
}
