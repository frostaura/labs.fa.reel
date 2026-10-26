using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Sync;
using FrostAura.Reel.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FrostAura.Reel.Tests.IntegrationTests;

/// <summary>
/// F+1: a statement that stalls is named while it stalls, with the job it ran for and the session
/// blocking it — the evidence the BuildFeed tail never left behind. Reproduced with a real lock
/// wait: a second session holds the job row while EF tries to update it.
/// </summary>
[Collection("job-runner")]
public sealed class SlowCommandDiagnosticsTests(JobRunnerDatabaseFixture fixture)
{
    [Fact]
    public async Task A_statement_waiting_on_a_lock_is_reported_with_its_job_and_its_blocker_while_it_waits_and_when_it_ends()
    {
        if (!fixture.Available)
        {
            return;
        }

        var log = new RecordingLogger<SlowCommandDiagnostics>();
        using var diagnostics = new SlowCommandDiagnostics(
            log, fixture.ConnectionString, threshold: TimeSpan.FromMilliseconds(300), sweepInterval: TimeSpan.FromHours(1));
        var jobId = Guid.NewGuid();
        await using (var seed = fixture.CreateContext())
        {
            seed.SyncJobs.Add(new SyncJob { Id = jobId, Kind = JobKind.BuildFeed, Status = JobStatus.Succeeded, EnqueuedAt = DateTime.UtcNow, AttemptCount = 1 });
            await seed.SaveChangesAsync();
        }

        // The blocker: an open transaction holding the row, the shape a long transaction elsewhere takes.
        await using var blocker = new NpgsqlConnection(fixture.ConnectionString);
        await blocker.OpenAsync();
        await using var transaction = await blocker.BeginTransactionAsync();
        await using (var hold = new NpgsqlCommand("""SELECT 1 FROM "SyncJobs" WHERE "Id" = @id FOR UPDATE /* the-blocker */""", blocker, transaction))
        {
            hold.Parameters.AddWithValue("id", jobId);
            await hold.ExecuteScalarAsync();
        }

        var options = new DbContextOptionsBuilder<ReelDbContext>()
            .UseNpgsql(fixture.ConnectionString, npgsql => npgsql.UseVector())
            .AddInterceptors(diagnostics)
            .Options;
        var tenancy = new AccountContext();
        var account = Guid.NewGuid();
        tenancy.SetAccount(account);
        await using var db = new ReelDbContext(options, tenancy);

        Task<int> stalled;
        using (JobRunContext.Enter(jobId, JobKind.BuildFeed, 2))
        {
            stalled = db.SyncJobs.IgnoreQueryFilters().Where(j => j.Id == jobId)
                .ExecuteUpdateAsync(s => s.SetProperty(j => j.ProgressMessage, "waited"));
        }

        await WaitUntilAsync(() => diagnostics.SweepAndCountAsync(log), TimeSpan.FromSeconds(10));
        var waiting = Assert.Single(log.Lines, l => l.Message.Contains("still running", StringComparison.Ordinal)).Message;
        Assert.Contains($"for job {jobId} (BuildFeed, attempt 2, account {account})", waiting, StringComparison.Ordinal);
        Assert.Contains("waiting on Lock/", waiting, StringComparison.Ordinal);
        Assert.Contains("transaction open", waiting, StringComparison.Ordinal);
        Assert.Contains("the-blocker", waiting, StringComparison.Ordinal);
        Assert.Contains("UPDATE \"SyncJobs\"", waiting, StringComparison.Ordinal);

        // Reported once, not on every sweep.
        await diagnostics.SweepAsync(DateTime.UtcNow);
        Assert.Single(log.Lines, l => l.Message.Contains("still running", StringComparison.Ordinal));

        await transaction.RollbackAsync();
        Assert.Equal(1, await stalled.WaitAsync(TimeSpan.FromSeconds(10)));
        var finished = Assert.Single(log.Lines, l => l.Message.Contains("finished after", StringComparison.Ordinal)).Message;
        Assert.Contains($"for job {jobId}", finished, StringComparison.Ordinal);

        // A quick statement leaves no trace.
        var before = log.Lines.Count;
        await db.SyncJobs.IgnoreQueryFilters().CountAsync(j => j.Id == jobId);
        Assert.Equal(before, log.Lines.Count);

        await db.SyncJobs.IgnoreQueryFilters().Where(j => j.Id == jobId).ExecuteDeleteAsync();
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "the slow statement was never reported");
            await Task.Delay(100);
        }
    }
}

internal static class SlowCommandDiagnosticsTestExtensions
{
    /// <summary>One sweep, then whether a "still running" line has been written.</summary>
    public static async Task<bool> SweepAndCountAsync(this SlowCommandDiagnostics diagnostics, RecordingLogger<SlowCommandDiagnostics> log)
    {
        await diagnostics.SweepAsync(DateTime.UtcNow);
        return log.Lines.Any(l => l.Message.Contains("still running", StringComparison.Ordinal));
    }
}
