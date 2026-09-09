using FrostAura.Reel.Application.Jobs;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Feed;
using FrostAura.Reel.Domain.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace FrostAura.Reel.Tests.IntegrationTests;

/// <summary>
/// The 7-snapshot feed prune, on real Postgres. Retention was previously untested in any form:
/// nothing asserted the depth, nothing asserted that FeedItems die with their snapshot rather
/// than being orphaned, and nothing asserted the prune is account-scoped — which is the one
/// that would be catastrophic and silent, because a rebuild for one account deleting another's
/// history leaves no trace to notice.
/// </summary>
[Collection("postgres")]
public class FeedSnapshotRetentionTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Nine_snapshots_prune_to_the_newest_seven_with_their_items()
    {
        if (!fixture.Available)
        {
            return; // no Postgres available — covered in CI's backend-integration job
        }

        var unscoped = new AccountContext();
        var accountA = await SeedAccountAsync("prune-a");
        var accountB = await SeedAccountAsync("prune-b");

        // A: 9 snapshots, oldest first, 2 items each. B: 3 snapshots — untouched control.
        var aSnapshots = await SeedSnapshotsAsync(accountA, count: 9, itemsEach: 2);
        var bSnapshots = await SeedSnapshotsAsync(accountB, count: 3, itemsEach: 2);

        await using (var db = fixture.CreateContext(unscoped))
        {
            var pruned = await FeedSnapshotRetention.PruneAsync(db, accountA, CancellationToken.None);
            Assert.Equal(2, pruned);
        }

        await using (var verify = fixture.CreateContext(unscoped))
        {
            var survivingA = await verify.FeedSnapshots
                .Where(s => s.AccountId == accountA)
                .OrderBy(s => s.GeneratedAt)
                .Select(s => s.Id)
                .ToListAsync();

            // The newest seven, in order — the two oldest are gone.
            Assert.Equal(FeedSnapshotRetention.KeepSnapshots, survivingA.Count);
            Assert.Equal(aSnapshots.Skip(2).ToList(), survivingA);

            // Their items came with them: 7 × 2.
            Assert.Equal(
                FeedSnapshotRetention.KeepSnapshots * 2,
                await verify.FeedItems.CountAsync(i => i.AccountId == accountA));

            // The evicted snapshots' items are gone too — no orphans left behind.
            var evicted = aSnapshots.Take(2).ToList();
            Assert.False(await verify.FeedItems.AnyAsync(i => evicted.Contains(i.FeedSnapshotId)));

            // Account B is entirely untouched — snapshots AND items.
            Assert.Equal(
                bSnapshots,
                await verify.FeedSnapshots
                    .Where(s => s.AccountId == accountB)
                    .OrderBy(s => s.GeneratedAt)
                    .Select(s => s.Id)
                    .ToListAsync());
            Assert.Equal(6, await verify.FeedItems.CountAsync(i => i.AccountId == accountB));
        }
    }

    [Fact]
    public async Task An_account_under_the_limit_loses_nothing()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync("prune-under");
        var snapshots = await SeedSnapshotsAsync(accountId, count: FeedSnapshotRetention.KeepSnapshots, itemsEach: 1);

        await using var db = fixture.CreateContext(new AccountContext());
        Assert.Equal(0, await FeedSnapshotRetention.PruneAsync(db, accountId, CancellationToken.None));
        Assert.Equal(snapshots.Count, await db.FeedSnapshots.CountAsync(s => s.AccountId == accountId));
    }

    [Fact]
    public async Task The_prune_is_account_scoped_under_a_pinned_context()
    {
        if (!fixture.Available)
        {
            return;
        }

        // The build runs pinned to the account it is building for, so the prune must be correct
        // both ways: it must reach all nine of ITS OWN rows through the tenancy query filter,
        // and it must still leave the other account's history alone.
        var accountA = await SeedAccountAsync("prune-pinned-a");
        var accountB = await SeedAccountAsync("prune-pinned-b");
        var aSnapshots = await SeedSnapshotsAsync(accountA, count: 9, itemsEach: 2);
        var bSnapshots = await SeedSnapshotsAsync(accountB, count: 9, itemsEach: 2);

        var pinnedToA = new AccountContext();
        pinnedToA.SetAccount(accountA);
        await using (var db = fixture.CreateContext(pinnedToA))
        {
            Assert.Equal(2, await FeedSnapshotRetention.PruneAsync(db, accountA, CancellationToken.None));
        }

        await using (var verify = fixture.CreateContext(new AccountContext()))
        {
            Assert.Equal(
                aSnapshots.Skip(2).ToList(),
                await verify.FeedSnapshots.Where(s => s.AccountId == accountA)
                    .OrderBy(s => s.GeneratedAt).Select(s => s.Id).ToListAsync());

            // B still has all nine and all eighteen items.
            Assert.Equal(
                bSnapshots,
                await verify.FeedSnapshots.Where(s => s.AccountId == accountB)
                    .OrderBy(s => s.GeneratedAt).Select(s => s.Id).ToListAsync());
            Assert.Equal(18, await verify.FeedItems.CountAsync(i => i.AccountId == accountB));
        }
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

    /// <summary>Oldest first; exactly one Active (the newest), the rest Superseded per the partial unique index.</summary>
    private async Task<List<Guid>> SeedSnapshotsAsync(Guid accountId, int count, int itemsEach)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var baseTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var ids = new List<Guid>();

        for (var i = 0; i < count; i++)
        {
            var snapshot = new FeedSnapshot
            {
                Id = Guid.NewGuid(),
                AccountId = accountId,
                ModelArtifactId = Guid.NewGuid(),
                GeneratedAt = baseTime.AddDays(i),
                Status = i == count - 1 ? SnapshotStatus.Active : SnapshotStatus.Superseded,
            };
            db.FeedSnapshots.Add(snapshot);
            ids.Add(snapshot.Id);

            for (var rank = 0; rank < itemsEach; rank++)
            {
                db.FeedItems.Add(new FeedItem
                {
                    Id = Guid.NewGuid(),
                    AccountId = accountId,
                    FeedSnapshotId = snapshot.Id,
                    Row = FeedRowKind.Hero,
                    Rank = rank,
                    TitleId = Guid.NewGuid(),
                    PredictedRating = 8m,
                    FinalScore = 8m,
                    WhyThisSentence = "seeded",
                });
            }
        }

        await db.SaveChangesAsync();
        return ids;
    }
}
