using FrostAura.Reel.Application.Jobs;
using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FrostAura.Reel.Infrastructure.Background;

/// <summary>
/// The clock behind <see cref="CatalogFollowThroughScheduler"/>: every
/// <see cref="Interval"/> it asks whether the shared catalogue has un-hydrated or un-enriched rows
/// and daily allowance left, and queues a global job if so. Gate:
/// <c>Background:CatalogFollowThrough</c>.
///
/// <para>
/// A ticker rather than self-re-enqueueing handlers, deliberately. A job that queues its successor
/// unconditionally spins at the runner's 2-second poll once its daily ceiling is spent — claiming a
/// worker slot, doing nothing, queueing itself again — and a job that stops queueing when the
/// ceiling is spent never restarts when the day turns. A tick outside both handlers is the only
/// place that knows the difference.
/// </para>
///
/// <para>
/// <b>It is live from merge, not from the first seed.</b> The catalogue already carries un-hydrated
/// rows from every previous discovery pass, and this will start draining them at
/// <c>HYDRATE_MAX_TITLES_PER_DAY</c> as soon as it is deployed. That is the intended behaviour —
/// those rows are invisible to the ANN arm and to rail similarity until they are hydrated — but it
/// is a behaviour change that does not wait for anyone to enqueue a seed.
/// </para>
/// </summary>
public sealed class CatalogFollowThroughService(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    ILogger<CatalogFollowThroughService> logger) : BackgroundService
{
    /// <summary>
    /// Fifteen minutes. Fast enough that a day's ceiling is actually spent within the day even if
    /// several runs end early; slow enough that the tick itself is two counting queries an hour.
    /// </summary>
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);

    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(3);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.GetValue("Background:CatalogFollowThrough", true))
        {
            logger.LogInformation("Catalogue follow-through disabled via Background:CatalogFollowThrough=false.");
            return;
        }

        try
        {
            await Task.Delay(StartupDelay, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ReelDbContext>();
                var budget = scope.ServiceProvider.GetRequiredService<CatalogHydrationBudget>();
                var extractor = scope.ServiceProvider.GetRequiredService<ITitleAttributeExtractor>();

                var enrichmentBudget = scope.ServiceProvider.GetRequiredService<CatalogEnrichmentBudget>();

                var result = await CatalogFollowThroughScheduler.RunAsync(
                    db, budget, extractor.Version, DateTime.UtcNow, stoppingToken, enrichmentBudget);

                if (result.QueuedHydration || result.QueuedEnrichment)
                {
                    logger.LogInformation(
                        "Catalogue follow-through queued: hydrate={Hydrate} (backlog {Backlog}, allowance {Used}/{Limit}), "
                        + "enrich={Enrich} (missing {MissingEmbedding} embedding / {MissingAttributes} attributes).",
                        result.QueuedHydration,
                        result.HydrationBacklog,
                        result.HydrationAllowance.Used,
                        result.HydrationAllowance.Limit,
                        result.QueuedEnrichment,
                        result.Enrichment.MissingEmbedding,
                        result.Enrichment.MissingAttributes);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Catalogue follow-through tick failed; the next tick continues the schedule.");
            }

            try
            {
                await Task.Delay(Interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
