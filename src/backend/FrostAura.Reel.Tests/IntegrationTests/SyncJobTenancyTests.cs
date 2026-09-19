using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Sync;
using FrostAura.Reel.Domain.Tenancy;
using FrostAura.Reel.Infrastructure.Background;
using Microsoft.EntityFrameworkCore;

namespace FrostAura.Reel.Tests.IntegrationTests;

/// <summary>
/// SyncJob's tenancy filter on real Postgres. The structural test in Persistence/TenancyModelTests
/// asserts the filter is DECLARED; this asserts it does the right thing, which for this entity is
/// two things at once — one account can never see another's jobs, and everybody can still see the
/// global (null-account) ones a catalog refresh enqueues.
///
/// Assertions are pinned to this test's own job ids rather than to counts, because the whole
/// integration collection shares one database and sibling classes enqueue jobs of their own.
/// </summary>
[Collection("postgres")]
public class SyncJobTenancyTests(PostgresFixture fixture)
{
    [Fact]
    public async Task An_account_sees_its_own_jobs_and_global_jobs_but_never_another_accounts()
    {
        if (!fixture.Available)
        {
            return; // no Postgres available — covered in CI's backend-integration job
        }

        var unscoped = new AccountContext();
        var accountA = await SeedAccountAsync("syncjob-a");
        var accountB = await SeedAccountAsync("syncjob-b");

        Guid jobA, jobB, globalJob;
        await using (var db = fixture.CreateContext(unscoped))
        {
            jobA = AddJob(db, accountA, JobKind.DeltaSync);
            jobB = AddJob(db, accountB, JobKind.DeltaSync);
            globalJob = AddJob(db, accountId: null, JobKind.HydrateCatalog);
            await db.SaveChangesAsync();
        }

        var seeded = new[] { jobA, jobB, globalJob };

        // Pinned to A: own job + the global one. B's job is invisible even when asked for by id.
        var pinnedToA = new AccountContext();
        pinnedToA.SetAccount(accountA);
        await using (var db = fixture.CreateContext(pinnedToA))
        {
            var visible = await db.SyncJobs.Where(j => seeded.Contains(j.Id)).Select(j => j.Id).ToListAsync();
            Assert.Equal([jobA, globalJob], visible.OrderBy(id => Array.IndexOf(seeded, id)).ToList());
            Assert.False(await db.SyncJobs.AnyAsync(j => j.Id == jobB));
            Assert.Null(await db.SyncJobs.FirstOrDefaultAsync(j => j.Id == jobB));
        }

        // Pinned to B: the mirror image.
        var pinnedToB = new AccountContext();
        pinnedToB.SetAccount(accountB);
        await using (var db = fixture.CreateContext(pinnedToB))
        {
            var visible = await db.SyncJobs.Where(j => seeded.Contains(j.Id)).Select(j => j.Id).ToListAsync();
            Assert.Equal([jobB, globalJob], visible.OrderBy(id => Array.IndexOf(seeded, id)).ToList());
            Assert.False(await db.SyncJobs.AnyAsync(j => j.Id == jobA));
        }

        // Unscoped (JobRunnerService claims and loads jobs before pinning; the background
        // schedulers never pin at all) must still see everything, or the queue stops draining.
        await using (var db = fixture.CreateContext(unscoped))
        {
            var visible = await db.SyncJobs.Where(j => seeded.Contains(j.Id)).Select(j => j.Id).ToListAsync();
            Assert.Equal(3, visible.Count);
            Assert.Equal(jobA, (await db.SyncJobs.FirstAsync(j => j.Id == jobA)).Id);
        }
    }

    [Fact]
    public async Task A_pinned_read_of_the_global_queue_still_finds_the_catalog_job()
    {
        if (!fixture.Available)
        {
            return;
        }

        // The regression this guards: a filter written as the generic `AccountId == pinned`
        // would compile, pass the structural test, and silently hide every global job from any
        // request-scoped read — the in-flight checks that stop duplicate catalog refreshes would
        // start returning false and enqueue a duplicate on every call.
        var accountId = await SeedAccountAsync("syncjob-global");
        Guid globalJob;
        await using (var db = fixture.CreateContext(new AccountContext()))
        {
            globalJob = AddJob(db, accountId: null, JobKind.RefreshAvailability);
            await db.SaveChangesAsync();
        }

        var pinned = new AccountContext();
        pinned.SetAccount(accountId);
        await using (var db = fixture.CreateContext(pinned))
        {
            var job = await db.SyncJobs.FirstOrDefaultAsync(j => j.Id == globalJob);
            Assert.NotNull(job);
            Assert.Null(job.AccountId);
        }
    }

    private static Guid AddJob(DbContext db, Guid? accountId, JobKind kind)
    {
        var job = new SyncJob
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            Kind = kind,
            Status = JobStatus.Pending,
            Priority = 5,
            EnqueuedAt = DateTime.UtcNow,
            TriggerReason = "syncjob-tenancy-test",
        };
        db.Add(job);
        return job.Id;
    }

    [Fact]
    public async Task A_build_is_not_claimed_while_the_accounts_delta_sync_is_pending_or_running()
    {
        if (!fixture.Available)
        {
            return;
        }

        var unscoped = new AccountContext();
        var account = await SeedAccountAsync("claim-order-a");
        var other = await SeedAccountAsync("claim-order-b");
        Guid sync, build, otherBuild;
        await using (var db = fixture.CreateContext(unscoped))
        {
            // Enqueued in the order the refresh control enqueues them, a tick apart.
            sync = AddJob(db, account, JobKind.DeltaSync);
            build = AddJob(db, account, JobKind.BuildFeed);
            otherBuild = AddJob(db, other, JobKind.BuildFeed);
            await db.SaveChangesAsync();
        }

        await using var claimer = fixture.CreateContext(unscoped);
        var mine = new[] { sync, build, otherBuild };
        async Task<Guid?> ClaimAsync()
        {
            var ids = await claimer.Database.SqlQuery<Guid>(JobRunnerService.ClaimNextSql(DateTime.UtcNow)).ToListAsync();
            return ids.Count == 0 ? null : ids[0];
        }

        // Claims that land on rows other tests may have left behind are released, not counted.
        async Task<Guid?> ClaimMineAsync()
        {
            for (var i = 0; i < 50; i++)
            {
                var id = await ClaimAsync();
                if (id is null || mine.Contains(id.Value)) return id;
                await claimer.SyncJobs.Where(j => j.Id == id.Value)
                    .ExecuteUpdateAsync(u => u.SetProperty(j => j.Status, JobStatus.Succeeded));
            }
            return null;
        }

        // The sync goes first even though the build is the same priority and only a tick younger;
        // the other account's build is not held by this account's sync.
        Assert.Equal(sync, await ClaimMineAsync());
        Assert.Equal(otherBuild, await ClaimMineAsync());
        // With the sync running, this account's build stays on the shelf.
        Assert.Null(await ClaimMineAsync());

        await claimer.SyncJobs.Where(j => j.Id == sync)
            .ExecuteUpdateAsync(u => u.SetProperty(j => j.Status, JobStatus.Succeeded));
        Assert.Equal(build, await ClaimMineAsync());
    }

    private async Task<Guid> SeedAccountAsync(string prefix)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var account = new Account
        {
            Id = Guid.NewGuid(),
            TraktUserSlug = $"{prefix}-{Guid.NewGuid():N}",
            CreatedAt = DateTime.UtcNow,
            LastSeenAt = DateTime.UtcNow,
            PipelineStageChangedAt = DateTime.UtcNow,
        };
        db.Accounts.Add(account);
        await db.SaveChangesAsync();
        return account.Id;
    }
}
