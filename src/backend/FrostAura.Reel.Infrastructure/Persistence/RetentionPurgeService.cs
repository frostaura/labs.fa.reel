using FrostAura.Reel.Application.Tenancy;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FrostAura.Reel.Infrastructure.Persistence;

/// <summary>
/// Runs <see cref="RetentionPurge"/> once a day: the first pass a few minutes after boot (not during
/// it — boot is when migrations, repairs and the first claims compete for the database), then every
/// 24 hours. Each pass logs what it removed, which is the post-deploy evidence.
///
/// <para>Gate: <c>Background:Retention</c> (default on). A failed pass is logged and retried the
/// next day; retention is never the reason the API is down.</para>
/// </summary>
public sealed class RetentionPurgeService(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    ILogger<RetentionPurgeService> logger) : BackgroundService
{
    public const string GateKey = "Background:Retention";

    public static readonly TimeSpan FirstPassAfter = TimeSpan.FromMinutes(10);

    public static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.GetValue(GateKey, true))
        {
            logger.LogInformation("Retention purge disabled via {Gate}=false.", GateKey);
            return;
        }

        try
        {
            await Task.Delay(FirstPassAfter, stoppingToken);
            using var timer = new PeriodicTimer(Interval);
            do
            {
                await PassAsync(stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutdown.
        }
    }

    /// <summary>One pass. Internal for tests; never throws except on shutdown.</summary>
    internal async Task<RetentionPurgeResult?> PassAsync(CancellationToken ct)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            // Retention is cross-account by definition (J-11).
            using var system = scope.ServiceProvider.GetRequiredService<IAccountContext>().EnterSystemScope();
            var db = scope.ServiceProvider.GetRequiredService<ReelDbContext>();
            var result = await RetentionPurge.RunAsync(db, DateTime.UtcNow, ct);
            logger.LogInformation(
                "Retention purge removed {SyncJobs} job row(s) over {JobDays} days, {TitleScores} superseded score(s) over {ScoreDays} days "
                + "and {RefreshSessions} expired session(s).",
                result.SyncJobs, RetentionPurge.SyncJobRetention.TotalDays, result.TitleScores,
                RetentionPurge.SupersededScoreRetention.TotalDays, result.RefreshSessions);
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Retention purge failed; it runs again in {Hours} hours.", Interval.TotalHours);
            return null;
        }
    }
}
