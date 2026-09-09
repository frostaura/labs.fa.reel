using FrostAura.Reel.Application.Jobs;
using FrostAura.Reel.Application.Ranking;
using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Domain.Ports.Tmdb;
using FrostAura.Reel.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FrostAura.Reel.Infrastructure.Background;

/// <summary>
/// Weekly thickening of the GLOBAL catalog: sweeps TMDB discover across every genre (movie + TV)
/// plus trending and recent releases, upserting them as candidate titles. Gives the feed
/// candidate pool — and, once enriched, semantic search — a broad, fresh universe beyond what any
/// one user's library or per-build discovery surfaces. Catalog rows are tenant-shared, so this
/// runs once for everyone. Gate: Background:CatalogGrowth.
/// </summary>
public sealed class CatalogGrowthService(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    ILogger<CatalogGrowthService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromDays(7);
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.GetValue("Background:CatalogGrowth", true))
        {
            logger.LogInformation("CatalogGrowth disabled via Background:CatalogGrowth=false.");
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
                var tmdb = scope.ServiceProvider.GetRequiredService<ITmdbClient>();
                var generator = scope.ServiceProvider.GetRequiredService<CandidateGenerator>();
                var region = configuration["GEOIP_DEFAULT_REGION"] ?? "US";

                var batches = new List<IReadOnlyList<TmdbListItem>>();
                foreach (var (_, ids) in TmdbGenres.SlugToIds)
                {
                    if (stoppingToken.IsCancellationRequested)
                    {
                        break;
                    }

                    if (ids.Movie is { } movieGenre)
                    {
                        batches.Add(await tmdb.DiscoverAsync(true, movieGenre, region, null, 1, stoppingToken));
                    }

                    if (ids.Tv is { } tvGenre)
                    {
                        batches.Add(await tmdb.DiscoverAsync(false, tvGenre, null, null, 1, stoppingToken));
                    }
                }

                batches.Add(await tmdb.GetTrendingAsync(true, stoppingToken));
                batches.Add(await tmdb.GetTrendingAsync(false, stoppingToken));
                batches.Add(await tmdb.DiscoverAsync(true, null, region, DateTime.UtcNow.AddDays(-90), 1, stoppingToken));

                var items = batches.SelectMany(b => b)
                    .GroupBy(i => (i.IsMovie, i.Id))
                    .Select(g => g.First())
                    .ToList();
                var added = await generator.UpsertCandidatesAsync(items, stoppingToken);
                logger.LogInformation("Catalog growth: {Items} items swept, {Added} new title(s).", items.Count, added);

                if (added > 0)
                {
                    // Everything this pass just added is unembedded and unattributed, and belongs to
                    // no tenant — so the enrich job that covers it is GLOBAL (null AccountId). It
                    // needs no account: the handler's priority tiers simply fall through to id order
                    // and it publishes no SSE and chains no Train, because there is nobody to tell
                    // and no model to retrain. Postgres treats NULL index keys as distinct, so the
                    // partial unique index cannot dedup global jobs — the scheduler's explicit
                    // in-flight check is the only dedup, exactly as that index's comment says.
                    var db = scope.ServiceProvider.GetRequiredService<ReelDbContext>();
                    if (await EnrichmentScheduler.EnqueueAsync(db, null, DateTime.UtcNow, stoppingToken))
                    {
                        await db.SaveChangesAsync(stoppingToken);
                        logger.LogInformation("Catalog growth: queued a global enrich run for the {Added} new title(s).", added);
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Catalog growth pass failed; next run continues the schedule.");
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
