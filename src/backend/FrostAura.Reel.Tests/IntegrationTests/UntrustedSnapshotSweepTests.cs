using FrostAura.Reel.Application.Ranking;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Feed;
using FrostAura.Reel.Domain.Sync;
using FrostAura.Reel.Domain.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace FrostAura.Reel.Tests.IntegrationTests;

/// <summary>
/// WS-5 task 3, the deploy-time purge: an Active snapshot carrying a why-line this build refuses,
/// or an anchored card with nothing shared with its anchor, gets exactly one forced Fresh build; a
/// clean snapshot gets none; and a build already waiting is re-aimed rather than joined.
/// </summary>
[Collection("postgres")]
public class UntrustedSnapshotSweepTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Only_snapshots_carrying_refused_lines_or_unexplained_anchored_cards_are_rebuilt()
    {
        if (!fixture.Available)
        {
            return;
        }

        var stub = await SeedAsync(heroWhy: "A action pick aligned with your taste.");
        var era = await SeedAsync(heroWhy: "From the 2010s, a decade you rate highly.");
        var article = await SeedAsync(heroWhy: "A adventure for a quiet night.");
        var unexplained = await SeedAsync(heroWhy: "You rate Tony Gilroy 7.8 on average.", memberShares: false);
        var clean = await SeedAsync(heroWhy: "You rate Tony Gilroy 7.8 on average.", memberShares: true);
        var blank = await SeedAsync(heroWhy: string.Empty, memberShares: true); // the generic-once blank

        await using var db = fixture.CreateContext(new AccountContext());
        var findings = (await UntrustedSnapshotSweep.FindAsync(db, CancellationToken.None))
            .ToDictionary(f => f.AccountId);

        Assert.Equal(1, findings[stub].RefusedWhys);
        Assert.Equal(1, findings[era].RefusedWhys);
        Assert.Equal(1, findings[article].RefusedWhys);
        Assert.Equal(0, findings[unexplained].RefusedWhys);
        Assert.Equal(1, findings[unexplained].UnexplainedAnchoredCards);
        Assert.False(findings.ContainsKey(clean));
        Assert.False(findings.ContainsKey(blank));

        var mine = findings.Values.Where(f => f.AccountId == stub || f.AccountId == unexplained).ToList();
        Assert.Equal(2, await UntrustedSnapshotSweep.StageRebuildsAsync(db, mine, DateTime.UtcNow, CancellationToken.None));
        await db.SaveChangesAsync();

        await using var verify = fixture.CreateContext(new AccountContext());
        foreach (var accountId in new[] { stub, unexplained })
        {
            var job = await verify.SyncJobs.SingleAsync(j => j.AccountId == accountId && j.Kind == JobKind.BuildFeed);
            Assert.Equal(FeedBuildPolicy.TrustPurgeReason, job.TriggerReason);
        }

        Assert.False(await verify.SyncJobs.AnyAsync(j => j.AccountId == clean));
    }

    [Fact]
    public async Task A_pending_build_is_re_aimed_at_the_purge_instead_of_joined()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAsync(heroWhy: "A action pick aligned with your taste.");
        await using (var seed = fixture.CreateContext(new AccountContext()))
        {
            seed.SyncJobs.Add(new SyncJob
            {
                Id = Guid.NewGuid(),
                AccountId = accountId,
                Kind = JobKind.BuildFeed,
                EnqueuedAt = DateTime.UtcNow,
                TriggerReason = "delta-sync",
            });
            await seed.SaveChangesAsync();
        }

        await using (var db = fixture.CreateContext(new AccountContext()))
        {
            var findings = (await UntrustedSnapshotSweep.FindAsync(db, CancellationToken.None))
                .Where(f => f.AccountId == accountId).ToList();
            Assert.Equal(1, await UntrustedSnapshotSweep.StageRebuildsAsync(db, findings, DateTime.UtcNow, CancellationToken.None));
            await db.SaveChangesAsync();
        }

        await using var verify = fixture.CreateContext(new AccountContext());
        var job = await verify.SyncJobs.SingleAsync(j => j.AccountId == accountId && j.Kind == JobKind.BuildFeed);
        Assert.Equal(FeedBuildPolicy.TrustPurgeReason, job.TriggerReason);
    }

    /// <summary>
    /// One account, an Active snapshot with a hero card carrying <paramref name="heroWhy"/> and one
    /// because-you-loved card whose credits do — or do not — share the anchor's director.
    /// </summary>
    private async Task<Guid> SeedAsync(string heroWhy, bool memberShares = true)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var account = new Account
        {
            Id = Guid.NewGuid(),
            TraktUserSlug = $"purge-{Guid.NewGuid():N}",
            CreatedAt = DateTime.UtcNow,
            LastSeenAt = DateTime.UtcNow,
            PipelineStageChangedAt = DateTime.UtcNow,
        };
        db.Accounts.Add(account);

        var director = new Person { Id = Guid.NewGuid(), TmdbId = Random.Shared.NextInt64(1, long.MaxValue), Name = "Shared Director" };
        var stranger = new Person { Id = Guid.NewGuid(), TmdbId = Random.Shared.NextInt64(1, long.MaxValue), Name = "Somebody Else" };
        db.Persons.AddRange(director, stranger);

        Title NewTitle(string name) => new()
        {
            Id = Guid.NewGuid(),
            MediaType = MediaType.Movie,
            TraktId = Random.Shared.NextInt64(1, long.MaxValue),
            TraktSlug = $"purge-{name}-{Guid.NewGuid():N}",
            Name = name,
            Year = 2016,
            CreatedAt = DateTime.UtcNow,
        };
        var hero = NewTitle("hero");
        var anchor = NewTitle("anchor");
        var member = NewTitle("member");
        db.Titles.AddRange(hero, anchor, member);
        db.TitleCredits.Add(new TitleCredit { Id = Guid.NewGuid(), TitleId = anchor.Id, PersonId = director.Id, Role = CreditRole.Director });
        db.TitleCredits.Add(new TitleCredit
        {
            Id = Guid.NewGuid(),
            TitleId = member.Id,
            PersonId = memberShares ? director.Id : stranger.Id,
            Role = CreditRole.Director,
        });

        var snapshot = new FeedSnapshot
        {
            Id = Guid.NewGuid(),
            AccountId = account.Id,
            ModelArtifactId = Guid.NewGuid(),
            GeneratedAt = DateTime.UtcNow,
            Status = SnapshotStatus.Active,
        };
        db.FeedSnapshots.Add(snapshot);
        db.FeedItems.Add(new FeedItem
        {
            Id = Guid.NewGuid(),
            AccountId = account.Id,
            FeedSnapshotId = snapshot.Id,
            Row = FeedRowKind.Hero,
            Rank = 0,
            TitleId = hero.Id,
            PredictedRating = 7.4m,
            WhyThisSentence = heroWhy,
        });
        db.FeedItems.Add(new FeedItem
        {
            Id = Guid.NewGuid(),
            AccountId = account.Id,
            FeedSnapshotId = snapshot.Id,
            Row = FeedRowKind.BecauseYouLoved,
            AnchorTitleId = anchor.Id,
            Rank = 0,
            TitleId = member.Id,
            PredictedRating = 7.1m,
            WhyThisSentence = "Same director as anchor: Shared Director.",
        });

        await db.SaveChangesAsync();
        return account.Id;
    }
}
