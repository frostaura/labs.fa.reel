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

    [Fact]
    public async Task A_fallback_read_on_a_tracked_row_is_never_written_back_unless_code_assigns_the_property()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        await RawAsync(accountId, "Hibernating");

        // A tracked load, a save that changes another column: EF writes modified columns only, so the
        // fallback the row was read with never reaches the database on its own.
        await using (var db = fixture.CreateContext(new AccountContext()))
        {
            var account = await db.Accounts.SingleAsync(a => a.Id == accountId);
            Assert.Equal(PipelineStage.Degraded, account.PipelineStage);
            account.LastSeenAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }

        Assert.Equal("Hibernating", await StoredStageAsync(accountId));

        // The decided rule: the image that is running owns the row. Code that ASSIGNS the stage —
        // its own state machine moving the account on — overwrites the newer image's value, exactly
        // as it would overwrite any value of its own. That is deliberate: the alternative is an
        // account parked forever on a stage the running image cannot name or advance.
        await using (var db = fixture.CreateContext(new AccountContext()))
        {
            var account = await db.Accounts.SingleAsync(a => a.Id == accountId);
            account.PipelineStage = PipelineStage.Training;
            await db.SaveChangesAsync();
        }

        Assert.Equal(nameof(PipelineStage.Training), await StoredStageAsync(accountId));
    }

    [Theory]
    [InlineData("Hibernating", "Active")]
    [InlineData("FeedReady", "Suspended")]
    public async Task A_stage_or_link_from_a_newer_build_is_reported_as_unrecognised_not_as_a_failure_or_a_revoked_link(
        string storedStage, string storedConnection)
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        await using (var db = fixture.CreateContext(new AccountContext()))
        {
            db.TraktConnections.Add(new TraktConnection
            {
                Id = Guid.NewGuid(),
                AccountId = accountId,
                ExpiresAt = DateTime.UtcNow.AddDays(30),
            });
            await db.SaveChangesAsync();
            await db.Database.ExecuteSqlAsync(
                $"""UPDATE "TraktConnections" SET "Status" = {storedConnection} WHERE "AccountId" = {accountId}""");
        }

        await RawAsync(accountId, storedStage);

        var status = await StatusAsync(accountId);
        Assert.Equal(DegradedReason.UnrecognisedState, status.DegradedReason);
    }

    [Fact]
    public async Task A_genuinely_degraded_account_and_a_genuinely_revoked_link_keep_their_own_reasons()
    {
        if (!fixture.Available)
        {
            return;
        }

        // The extra check must not swallow the real thing it sits beside.
        var degraded = await SeedAccountAsync(PipelineStage.Degraded);
        await using (var db = fixture.CreateContext(new AccountContext()))
        {
            db.TraktConnections.Add(new TraktConnection
            {
                Id = Guid.NewGuid(),
                AccountId = degraded,
                ExpiresAt = DateTime.UtcNow.AddDays(30),
                Status = ConnectionStatus.Revoked,
            });
            await db.SaveChangesAsync();
        }

        Assert.Equal(DegradedReason.TraktRevoked, (await StatusAsync(degraded)).DegradedReason);
    }

    private async Task<Guid> SeedAccountAsync(PipelineStage stage = PipelineStage.FeedReady)
    {
        var accountId = Guid.NewGuid();
        await using var db = fixture.CreateContext(new AccountContext());
        db.Accounts.Add(new Account
        {
            Id = accountId,
            TraktUserSlug = $"rollback-{Guid.NewGuid():N}",
            CreatedAt = DateTime.UtcNow,
            LastSeenAt = DateTime.UtcNow,
            PipelineStage = stage,
            PipelineStageChangedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        return accountId;
    }

    private async Task RawAsync(Guid accountId, string stage)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        await db.Database.ExecuteSqlAsync($"""UPDATE "Accounts" SET "PipelineStage" = {stage} WHERE "Id" = {accountId}""");
    }

    private async Task<string> StoredStageAsync(Guid accountId)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        return await db.Database
            .SqlQuery<string>($"""SELECT "PipelineStage" AS "Value" FROM "Accounts" WHERE "Id" = {accountId}""")
            .SingleAsync();
    }

    private async Task<SyncStatusResponse> StatusAsync(Guid accountId)
    {
        var scoped = new AccountContext();
        scoped.SetAccount(accountId);
        await using var db = fixture.CreateContext(scoped);
        var account = await db.Accounts.AsNoTracking().SingleAsync(a => a.Id == accountId);
        return await new SyncStatusQuery(db, new UnmonitoredProviderHealthReader())
            .GetAsync(account, retrainThreshold: 10, DateTime.UtcNow, CancellationToken.None);
    }
}
