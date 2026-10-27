using FrostAura.Reel.Domain.Feed;
using FrostAura.Reel.Domain.Sync;
using FrostAura.Reel.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FrostAura.Reel.Infrastructure.Background;

/// <summary>
/// Schedules the weekly digest: every ~15 minutes it checks whether it is Friday at or after
/// 16:00 UTC and, if so, enqueues one BuildDigest job per account that has neither a digest
/// for the current ISO week nor an in-flight build. Idempotent by construction — the digest
/// row's (AccountId, WeekStart) uniqueness plus the in-flight job guard mean repeated Friday
/// ticks converge instead of duplicating. Gate: Background:Digest.
/// </summary>
public sealed class WeeklyDigestService(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    ILogger<WeeklyDigestService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.GetValue("Background:Digest", true))
        {
            logger.LogInformation("Weekly digest disabled via Background:Digest=false.");
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(15));
        while (await WaitAsync(timer, stoppingToken))
        {
            try
            {
                var now = DateTime.UtcNow;
                if (now.DayOfWeek != DayOfWeek.Friday || now.Hour < 16)
                {
                    continue;
                }

                await ScheduleAsync(now, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Weekly digest scheduling pass failed; continuing.");
            }
        }
    }

    /// <summary>
    /// One scheduling pass for the week containing <paramref name="now"/>: queues a BuildDigest for
    /// every account with neither this week's digest nor a build in flight, and returns how many it
    /// queued. It reads every account's digests and jobs, so it runs in system scope (J-11).
    /// </summary>
    internal async Task<int> ScheduleAsync(DateTime now, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateSystemScope();
        var db = scope.ServiceProvider.GetRequiredService<ReelDbContext>();
        var weekStart = DigestWeek.MondayOf(now);
        var queued = 0;

        var accountIds = await db.Accounts.Select(a => a.Id).ToListAsync(ct);
        foreach (var accountId in accountIds)
        {
            var hasDigest = await db.AccountDigests.AnyAsync(
                d => d.AccountId == accountId && d.WeekStart == weekStart, ct);
            if (hasDigest)
            {
                continue;
            }

            var inFlight = await db.SyncJobs.AnyAsync(
                j => j.AccountId == accountId
                    && j.Kind == JobKind.BuildDigest
                    && (j.Status == JobStatus.Pending || j.Status == JobStatus.Running),
                ct);
            if (inFlight)
            {
                continue;
            }

            db.SyncJobs.Add(new SyncJob
            {
                Id = Guid.NewGuid(),
                AccountId = accountId,
                Kind = JobKind.BuildDigest,
                Priority = 2,
                EnqueuedAt = now,
                TriggerReason = "weekly-digest",
            });
            queued++;
        }

        await db.SaveChangesAsync(ct);
        return queued;
    }

    private static async ValueTask<bool> WaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try
        {
            return await timer.WaitForNextTickAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
