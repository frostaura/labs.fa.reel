using FrostAura.Reel.Application.Pipeline;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Sync;
using FrostAura.Reel.Domain.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace FrostAura.Reel.Tests.IntegrationTests;

/// <summary>
/// The rollback case on real rows (program §5.0 rule 8): a newer image has written enum members this
/// build has never heard of, and this build still reads every row and still answers the status poll.
/// Before the tolerant converter, EF's string converter threw on the first such row — which, for the
/// account row, failed every authenticated request.
/// </summary>
[Collection("postgres")]
public class PersistedEnumRollbackTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Rows_carrying_members_from_a_newer_build_read_as_their_fallbacks_and_the_status_poll_still_answers()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        await using (var db = fixture.CreateContext(new AccountContext()))
        {
            db.Accounts.Add(new Account
            {
                Id = accountId,
                TraktUserSlug = $"rollback-{Guid.NewGuid():N}",
                CreatedAt = DateTime.UtcNow,
                LastSeenAt = DateTime.UtcNow,
                PipelineStageChangedAt = DateTime.UtcNow,
            });
            db.SyncJobs.Add(new SyncJob
            {
                Id = jobId,
                AccountId = accountId,
                Kind = JobKind.BuildFeed,
                Status = JobStatus.Succeeded,
                EnqueuedAt = DateTime.UtcNow,
                CompletedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();

            // What a newer image would have written.
            await db.Database.ExecuteSqlAsync(
                $"""UPDATE "Accounts" SET "PipelineStage" = 'Hibernating', "Tier" = 'Platinum' WHERE "Id" = {accountId}""");
            await db.Database.ExecuteSqlAsync(
                $"""UPDATE "SyncJobs" SET "Kind" = 'RepairAvailability', "Status" = 'Paused' WHERE "Id" = {jobId}""");
        }

        var scoped = new AccountContext();
        scoped.SetAccount(accountId);
        await using (var db = fixture.CreateContext(scoped))
        {
            var account = await db.Accounts.AsNoTracking().SingleAsync(a => a.Id == accountId);
            Assert.Equal(PipelineStage.Degraded, account.PipelineStage);
            Assert.Equal(AccountTier.Free, account.Tier);

            var job = await db.SyncJobs.AsNoTracking().SingleAsync(j => j.Id == jobId);
            Assert.Equal(JobKind.SeedCatalog, job.Kind);
            Assert.Equal(JobStatus.Cancelled, job.Status);

            var status = await new SyncStatusQuery(db, new UnmonitoredProviderHealthReader())
                .GetAsync(account, retrainThreshold: 10, DateTime.UtcNow, CancellationToken.None);
            Assert.Null(status.ActiveJob);
            Assert.DoesNotContain(status.LastFailures, f => f.Kind == JobKind.SeedCatalog);
        }

        // Reading never wrote the fallback back: the newer image's values are intact for it.
        await using (var db = fixture.CreateContext(new AccountContext()))
        {
            var stored = await db.Database
                .SqlQuery<string>($"""SELECT "PipelineStage" AS "Value" FROM "Accounts" WHERE "Id" = {accountId}""")
                .SingleAsync();
            Assert.Equal("Hibernating", stored);
        }
    }
}
