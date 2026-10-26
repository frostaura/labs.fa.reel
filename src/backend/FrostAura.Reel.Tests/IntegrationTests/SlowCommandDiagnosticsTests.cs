using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Sync;
using FrostAura.Reel.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
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
    /// <summary>
    /// The other half of F+1: a transaction whose client is parked between statements (the stalled
    /// builds parked on a full console-log queue inside SaveChanges) holds its row locks with no
    /// statement running, so no command timeout ends it. With the session bound the server ends it
    /// and the row is free; without it the row stays locked for as long as the client is parked.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_transaction_parked_between_statements_is_ended_by_the_server_and_frees_its_rows(bool bounded)
    {
        if (!fixture.Available)
        {
            return;
        }

        var jobId = Guid.NewGuid();
        await using (var seed = fixture.CreateContext())
        {
            seed.SyncJobs.Add(new SyncJob { Id = jobId, Kind = JobKind.BuildFeed, Status = JobStatus.Running, EnqueuedAt = DateTime.UtcNow, AttemptCount = 1 });
            await seed.SaveChangesAsync();
        }

        var settings = DatabaseSessionSettings.With(fixture.ConnectionString!, bounded ? TimeSpan.FromMilliseconds(500) : null);
        await using var parked = new NpgsqlConnection(settings.ConnectionString);
        await parked.OpenAsync();
        await using var transaction = await parked.BeginTransactionAsync();
        await using (var write = new NpgsqlCommand("""UPDATE "SyncJobs" SET "ProgressMessage" = 'parked' WHERE "Id" = @id""", parked, transaction))
        {
            write.Parameters.AddWithValue("id", jobId);
            await write.ExecuteNonQueryAsync();
        }

        // The client is parked between statements; nothing is executing on its session.
        await Task.Delay(TimeSpan.FromMilliseconds(1500));

        await using var other = new NpgsqlConnection(fixture.ConnectionString);
        await other.OpenAsync();
        bool acquired;
        try
        {
            await using var take = new NpgsqlCommand("""SELECT 1 FROM "SyncJobs" WHERE "Id" = @id FOR UPDATE NOWAIT""", other);
            take.Parameters.AddWithValue("id", jobId);
            await take.ExecuteScalarAsync();
            acquired = true;
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.LockNotAvailable)
        {
            acquired = false;
        }

        Assert.Equal(bounded, acquired);
        if (bounded)
        {
            // The parked transaction was rolled back with its session: its write never landed.
            await using var read = new NpgsqlCommand("""SELECT "ProgressMessage" FROM "SyncJobs" WHERE "Id" = @id""", other);
            read.Parameters.AddWithValue("id", jobId);
            Assert.NotEqual("parked", await read.ExecuteScalarAsync() as string);
        }
    }

    [Fact]
    public void The_session_bound_defaults_to_a_minute_reads_its_override_and_zero_leaves_the_server_alone()
    {
        const string Raw = "Host=127.0.0.1;Port=5434;Database=x;Username=u;Password=p";
        static IConfiguration Config(string? value) => new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [DatabaseSessionSettings.IdleInTransactionTimeoutKey] = value })
            .Build();

        var defaults = DatabaseSessionSettings.From(Raw, configuration: null);
        Assert.Equal(TimeSpan.FromSeconds(60), defaults.IdleInTransactionTimeout);
        Assert.Contains("idle_in_transaction_session_timeout=60000", new NpgsqlConnectionStringBuilder(defaults.ConnectionString).Options, StringComparison.Ordinal);
        Assert.Equal(TimeSpan.FromSeconds(5), DatabaseSessionSettings.From(Raw, Config("5")).IdleInTransactionTimeout);
        Assert.Equal(TimeSpan.FromSeconds(60), DatabaseSessionSettings.From(Raw, Config("soon")).IdleInTransactionTimeout);

        var off = DatabaseSessionSettings.From(Raw, Config("0"));
        Assert.Null(off.IdleInTransactionTimeout);
        Assert.Equal(Raw, off.ConnectionString);
    }

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
