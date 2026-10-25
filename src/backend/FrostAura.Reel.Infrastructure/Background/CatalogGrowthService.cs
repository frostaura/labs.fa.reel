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
///
/// <para>
/// Deliberately UNFILTERED, unlike the per-account pool refresh. The rows it writes belong to every
/// tenant, so shaping the sweep by one account's language, year window or banned genres would let
/// that account's preferences decide what the catalogue contains for everybody. The account rules
/// belong in <see cref="CandidateGenerator"/>, which is per-account by construction.
/// </para>
///
/// <para>
/// What it did need was a moving read head. Every pass asked for page 1 of the same
/// popularity-ordered lists, so a weekly sweep spent most of its calls re-fetching the titles it
/// already held. The page now rotates deterministically by day: replayable from the timestamp, and
/// across a rotation span the sweep walks the whole head of every genre instead of standing on it.
/// </para>
/// </summary>
public sealed class CatalogGrowthService(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    ILogger<CatalogGrowthService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromDays(7);
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Pages the day-rotation walks before wrapping. Five deep on a popularity-ordered discover is
    /// ~100 titles per genre per medium — past that TMDB's tail is thin enough that the vote-count
    /// floor rejects most of it anyway.
    /// </summary>
    private const int PageSpan = 5;

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

                // One page for the whole pass, not one per genre: a sweep whose genres sit on
                // different pages is not reproducible from its own timestamp.
                var page = DiscoveryPaging.RotatedPage(AnchorRotation.OffsetFor(DateTime.UtcNow), PageSpan);
                var batches = new List<IReadOnlyList<TmdbListItem>>();
                foreach (var (_, ids) in TmdbGenres.SlugToIds)
                {
                    if (stoppingToken.IsCancellationRequested)
                    {
                        break;
                    }

                    if (ids.Movie is { } movieGenre)
                    {
                        batches.Add(await tmdb.DiscoverAsync(true, movieGenre, region, null, page, stoppingToken));
                    }

                    if (ids.Tv is { } tvGenre)
                    {
                        batches.Add(await tmdb.DiscoverAsync(false, tvGenre, null, null, page, stoppingToken));
                    }
                }

                batches.Add(await tmdb.GetTrendingAsync(true, stoppingToken));
                batches.Add(await tmdb.GetTrendingAsync(false, stoppingToken));
                batches.Add(await tmdb.DiscoverAsync(true, null, region, DateTime.UtcNow.AddDays(-90), page, stoppingToken));

                var items = batches.SelectMany(b => b)
                    .GroupBy(i => (i.IsMovie, i.Id))
                    .Select(g => g.First())
                    .ToList();
                var added = await generator.UpsertCandidatesAsync(items, stoppingToken);
                logger.LogInformation("Catalog growth: {Items} items swept from page {Page}, {Added} new title(s).", items.Count, page, added);

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
                    var enrichmentBudget = scope.ServiceProvider.GetRequiredService<CatalogEnrichmentBudget>();
                    if (await EnrichmentScheduler.EnqueueAsync(db, null, DateTime.UtcNow, stoppingToken, enrichmentBudget))
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
