using FrostAura.Reel.Application.Sync;
using FrostAura.Reel.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FrostAura.Reel.Infrastructure.Background;

/// <summary>
/// Runs <see cref="DataRepairs"/> once per boot, after migrations have been applied. This is the
/// route to live for every WS-4 one-off repair: a repair that only ever ran on a copied database
/// fixed the copy. Every repair is idempotent and self-terminating, so running on every boot costs
/// three cheap predicate scans once the backlog is gone and can never re-open a row the fixed code
/// wrote. Each pass logs what it changed, which is the post-deploy evidence.
/// <para>Gate: <c>Background:DataRepairs</c> (default on). A failure is logged and the app keeps
/// serving — a repair must never be the reason the API is down.</para>
/// </summary>
public sealed class DataRepairStartupService(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    ILogger<DataRepairStartupService> logger) : BackgroundService
{
    public const string GateKey = "Background:DataRepairs";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.GetValue(GateKey, true))
        {
            logger.LogInformation("Data repairs disabled via {Gate}=false.", GateKey);
            return;
        }

        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ReelDbContext>();
            var results = await DataRepairs.RunAllAsync(db, DateTime.UtcNow, stoppingToken);
            foreach (var result in results)
            {
                logger.LogInformation("Data repair {Repair}: {Rows} row(s) changed.", result.Name, result.RowsChanged);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Data repairs failed this boot; they are idempotent and run again on the next one.");
        }
    }
}
