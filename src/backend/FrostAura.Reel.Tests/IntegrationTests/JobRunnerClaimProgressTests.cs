using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Sync;
using FrostAura.Reel.Domain.Tenancy;
using FrostAura.Reel.Infrastructure.Background;
using Microsoft.EntityFrameworkCore;

namespace FrostAura.Reel.Tests.IntegrationTests;

/// <summary>
/// The runner's claim starts every attempt from no progress. A retry used to inherit the failed
/// attempt's "100% — nothing cross-referenced this run" and report it as live work for as long as
/// the retry ran (H-1). Driven through the real runner's claim on Postgres.
/// </summary>
[Collection("postgres")]
public class JobRunnerClaimProgressTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Claiming_a_retry_clears_the_previous_attempts_progress_but_keeps_its_error_and_cursor()
    {
        if (!fixture.Available)
        {
            return;
        }

        var unscoped = new AccountContext();
        var accountId = await SeedAccountAsync();
        var jobId = Guid.NewGuid();
        await using (var db = fixture.CreateContext(unscoped))
        {
            db.SyncJobs.Add(new SyncJob
            {
                Id = jobId,
                AccountId = accountId,
                Kind = JobKind.FullIngest,
                Status = JobStatus.Pending,
                // Priority below anything a sibling test enqueues, so this row is claimed first.
                Priority = short.MinValue,
                EnqueuedAt = DateTime.UtcNow.AddMinutes(-3),
                StartedAt = DateTime.UtcNow.AddMinutes(-3),
                AttemptCount = 1,
                ProgressPct = 100,
                ProgressMessage = "nothing cross-referenced this run",
                CursorJson = """{"phase":"shows","page":3}""",
                Error = "judge answered none of 3 batches",
            });
            await db.SaveChangesAsync();
        }

        await using (var harness = JobRunnerHarness.Create(fixture.ConnectionString!, JobRunnerOptions.Default))
        await using (var claimer = fixture.CreateContext(unscoped))
        {
            Guid? claimed = null;
            for (var i = 0; i < 50 && claimed != jobId; i++)
            {
                claimed = (await harness.Runner.ClaimNextAsync(Enum.GetNames<JobKind>(), CancellationToken.None))?.Id;
                if (claimed is null)
                {
                    break;
                }

                if (claimed != jobId)
                {
                    // A sibling's leftover row: release it and keep going.
                    await claimer.SyncJobs.Where(j => j.Id == claimed.Value)
                        .ExecuteUpdateAsync(u => u.SetProperty(j => j.Status, JobStatus.Succeeded));
                }
            }

            Assert.Equal(jobId, claimed);
        }

        await using (var db = fixture.CreateContext(unscoped))
        {
            var job = await db.SyncJobs.AsNoTracking().FirstAsync(j => j.Id == jobId);
            Assert.Equal(JobStatus.Running, job.Status);
            Assert.Equal(2, job.AttemptCount);
            Assert.Null(job.ProgressPct);
            Assert.Null(job.ProgressMessage);

            // What the retry needs to resume and what the status API reads for the reason survive.
            using var cursor = System.Text.Json.JsonDocument.Parse(job.CursorJson!);
            Assert.Equal("shows", cursor.RootElement.GetProperty("phase").GetString());
            Assert.Equal(3, cursor.RootElement.GetProperty("page").GetInt32());
            Assert.Equal("judge answered none of 3 batches", job.Error);
        }
    }

    private async Task<Guid> SeedAccountAsync()
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var account = new Account
        {
            Id = Guid.NewGuid(),
            TraktUserSlug = $"claim-progress-{Guid.NewGuid():N}",
            CreatedAt = DateTime.UtcNow,
            LastSeenAt = DateTime.UtcNow,
            PipelineStageChangedAt = DateTime.UtcNow,
        };
        db.Accounts.Add(account);
        await db.SaveChangesAsync();
        return account.Id;
    }
}
