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
/// <para><b>Before every other hosted service, not beside them.</b> The repairs run in
/// <see cref="StartingAsync"/>, which the host awaits for every lifecycle service before it calls
/// any service's <c>StartAsync</c> — so the job runner cannot pick up a pending enrich job and touch
/// a pre-fix attribute row while its repair is still waiting to run (review defect 7). Migrations
/// are applied in <c>Program</c> before the host starts at all, so the schema is current by then.</para>
/// <para>Gate: <c>Background:DataRepairs</c> (default on). A failure is logged and the app keeps
/// serving — a repair must never be the reason the API is down.</para>
/// </summary>
public sealed class DataRepairStartupService(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    ILogger<DataRepairStartupService> logger) : IHostedLifecycleService
{
    public const string GateKey = "Background:DataRepairs";

    /// <summary>What the last boot's pass changed; empty when the gate is off or the pass failed.</summary>
    public IReadOnlyList<DataRepairResult> LastResults { get; private set; } = [];

    public async Task StartingAsync(CancellationToken cancellationToken)
    {
        if (!configuration.GetValue(GateKey, true))
        {
            logger.LogInformation("Data repairs disabled via {Gate}=false.", GateKey);
            return;
        }

        try
        {
            using var scope = scopeFactory.CreateSystemScope();
            var db = scope.ServiceProvider.GetRequiredService<ReelDbContext>();
            LastResults = await DataRepairs.RunAllAsync(db, DateTime.UtcNow, cancellationToken);
            foreach (var result in LastResults)
            {
                logger.LogInformation("Data repair {Repair}: {Rows} row(s) changed.", result.Name, result.RowsChanged);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Data repairs failed this boot; they are idempotent and run again on the next one.");
        }
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
