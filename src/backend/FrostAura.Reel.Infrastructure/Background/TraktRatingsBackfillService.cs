using FrostAura.Reel.Application.Ingestion;
using FrostAura.Reel.Infrastructure.Eval;
using FrostAura.Reel.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FrostAura.Reel.Infrastructure.Background;

/// <summary>
/// The clock behind <see cref="TraktRatingsBackfill"/>: a batch of <see cref="BatchSize"/> titles
/// every <see cref="Interval"/> until the catalogue is covered, then a look every
/// <see cref="IdleInterval"/> for titles that have arrived or come due for a re-check. Gate:
/// <c>Background:TraktRatingsBackfill</c> (default on).
///
/// <para><b>Pace.</b> 40 calls a minute is 200 per five minutes against the shared Trakt gate's 900,
/// on the lowest-priority lane, so interactive and sync traffic always go first. About 38,000 titles
/// take roughly 16 hours; the rate gate and the halt-on-failure back-off can only make that longer.</para>
///
/// <para><b>It runs only where a live Trakt connection exists</b>
/// (<see cref="TraktRatingsBackfill.HasLiveConnectionAsync"/>) and never under the eval stack's
/// Trakt client id. Lane and eval databases null their tokens, so a merged build booted on a copy
/// makes no Trakt call even if this gate is left on.</para>
/// </summary>
public sealed class TraktRatingsBackfillService(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    ILogger<TraktRatingsBackfillService> logger) : BackgroundService
{
    /// <summary>Titles per batch.</summary>
    public const int BatchSize = 40;

    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    private static readonly TimeSpan IdleInterval = TimeSpan.FromHours(6);

    private static readonly TimeSpan HaltBackoff = TimeSpan.FromMinutes(30);

    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(5);

    /// <summary>Batches between progress lines in the log, so a 16-hour back-fill is visible without a line a minute.</summary>
    private const int LogEvery = 30;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.GetValue("Background:TraktRatingsBackfill", true))
        {
            logger.LogInformation("Trakt ratings back-fill disabled via Background:TraktRatingsBackfill=false.");
            return;
        }

        var clientId = configuration["TRAKT_CLIENT_ID"]?.Trim();
        if (string.IsNullOrEmpty(clientId)
            || string.Equals(clientId, OfflineTraktClient.EvalClientId, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogInformation("Trakt ratings back-fill off: no live Trakt client id on this stack.");
            return;
        }

        if (!await DelayAsync(StartupDelay, stoppingToken))
        {
            return;
        }

        var batches = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            var next = Interval;
            try
            {
                using var scope = scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ReelDbContext>();
                if (!await TraktRatingsBackfill.HasLiveConnectionAsync(db, stoppingToken))
                {
                    next = IdleInterval;
                }
                else
                {
                    var backfill = scope.ServiceProvider.GetRequiredService<TraktRatingsBackfill>();
                    var result = await backfill.RunBatchAsync(BatchSize, DateTime.UtcNow, stoppingToken);
                    batches++;
                    next = result.Halted ? HaltBackoff : result.Asked == 0 && result.Failed == 0 ? IdleInterval : Interval;

                    if (result.Halted || result.Asked == 0 || batches % LogEvery == 1)
                    {
                        var (scored, withRating) = await TraktRatingsBackfill.CoverageAsync(db, stoppingToken);
                        logger.LogInformation(
                            "Trakt ratings back-fill: batch asked {Asked}, found {Found}, rated {Rated}, failed {Failed}{Halted}; "
                            + "scored titles with a Trakt rating {WithRating}/{Scored}.",
                            result.Asked, result.Found, result.Rated, result.Failed, result.Halted ? " (halted)" : string.Empty,
                            withRating, scored);
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Trakt ratings back-fill tick failed; the next tick continues.");
                next = HaltBackoff;
            }

            if (!await DelayAsync(next, stoppingToken))
            {
                return;
            }
        }
    }

    private static async Task<bool> DelayAsync(TimeSpan delay, CancellationToken ct)
    {
        try
        {
            await Task.Delay(delay, ct);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
