using FrostAura.Reel.Application.Ranking;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Feed;
using FrostAura.Reel.Domain.Sync;
using FrostAura.Reel.Domain.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace FrostAura.Reel.Tests.IntegrationTests;

/// <summary>
/// WS-5 task 3, the deploy-time purge: an Active snapshot carrying a why-line this build refuses,
/// an anchored card with no stored evidence, a row with no stored why-source, or a caption on a
/// failed non-anchored pass gets exactly one forced Fresh build; a clean snapshot gets none, even
/// when the catalogue moves under it; a build already waiting is re-aimed rather than joined; and
/// the purge is off on a host whose background services are off.
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
        var hedged = await SeedAsync(heroWhy: "Sharp legal drama, though the series format is a weaker fit for you.");
        var unexplained = await SeedAsync(heroWhy: "You rate Tony Gilroy 7.8 on average.", storedEvidence: false);
        var clean = await SeedAsync(heroWhy: "You rate Tony Gilroy 7.8 on average.");
        var blank = await SeedAsync(heroWhy: string.Empty); // the generic-once blank

        await using var db = fixture.CreateContext(new AccountContext());
        var findings = (await UntrustedSnapshotSweep.FindAsync(db, CancellationToken.None))
            .ToDictionary(f => f.AccountId);

        Assert.Equal(1, findings[stub].RefusedWhys);
        Assert.Equal(1, findings[era].RefusedWhys);
        Assert.Equal(1, findings[article].RefusedWhys);
        Assert.Equal(1, findings[hedged].RefusedWhys);
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
    public async Task A_snapshot_written_before_provenance_was_stored_is_rebuilt_once()
    {
        if (!fixture.Available)
        {
            return;
        }

        // Every row a pre-deploy image wrote: no why-source, no stored evidence. Its fallback and
        // stub captions cannot be told from real ones, so the account gets its one Fresh build.
        var legacy = await SeedAsync(heroWhy: "Close in feel to the titles you love.", whySource: null, storedEvidence: null);

        await using var db = fixture.CreateContext(new AccountContext());
        var finding = (await UntrustedSnapshotSweep.FindAsync(db, CancellationToken.None)).Single(f => f.AccountId == legacy);
        Assert.Equal(2, finding.UnattributedCards);
        Assert.Equal(1, finding.UnexplainedAnchoredCards);
    }

    [Fact]
    public async Task A_non_anchored_caption_on_a_failed_model_pass_is_rebuilt_and_its_honest_blank_is_not()
    {
        if (!fixture.Available)
        {
            return;
        }

        var captioned = await SeedAsync(heroWhy: "Close in feel to the titles you love.", heroWhySource: "Fallback");
        var blanked = await SeedAsync(heroWhy: string.Empty, heroWhySource: "Fallback");

        await using var db = fixture.CreateContext(new AccountContext());
        var findings = (await UntrustedSnapshotSweep.FindAsync(db, CancellationToken.None)).ToDictionary(f => f.AccountId);
        Assert.Equal(1, findings[captioned].FallbackCaptions);
        Assert.False(findings.ContainsKey(blanked));
    }

    [Fact]
    public async Task Catalogue_drift_under_a_kept_card_never_triggers_another_paid_build()
    {
        if (!fixture.Available)
        {
            return;
        }

        // The card was admitted on a shared director and that evidence is stored on the row. The
        // catalogue then changes underneath it (a credit re-sync drops the shared director): the
        // earlier sweep re-read the facts, found nothing shared, and forced a Fresh build on every
        // restart. Stored evidence does not drift.
        var accountId = await SeedAsync(heroWhy: "You rate Tony Gilroy 7.8 on average.");
        await using (var drift = fixture.CreateContext(new AccountContext()))
        {
            var memberId = await drift.FeedItems
                .Where(i => i.AccountId == accountId && i.Row == FeedRowKind.BecauseYouLoved)
                .Select(i => i.TitleId)
                .SingleAsync();
            drift.TitleCredits.RemoveRange(await drift.TitleCredits.Where(c => c.TitleId == memberId).ToListAsync());
            await drift.SaveChangesAsync();
        }

        await using var db = fixture.CreateContext(new AccountContext());
        Assert.DoesNotContain(await UntrustedSnapshotSweep.FindAsync(db, CancellationToken.None), f => f.AccountId == accountId);
    }

    [Theory]
    [InlineData(null, null, true)] // production default: on
    [InlineData(null, "false", false)] // a slot with background services off: off
    [InlineData(null, "true", true)]
    [InlineData("true", "false", true)] // an eval stack that wants the purge measured: explicit on
    [InlineData("false", "true", false)] // explicit off wins
    public void The_purge_follows_the_hosts_background_switch_unless_set(string? trustPurge, string? reconcile, bool expected)
    {
        var settings = new Dictionary<string, string?>();
        if (trustPurge is not null)
        {
            settings["Background:TrustPurge"] = trustPurge;
        }

        if (reconcile is not null)
        {
            settings["Background:NightlyReconcile"] = reconcile;
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        Assert.Equal(expected, UntrustedSnapshotSweep.Enabled(configuration));
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
    /// because-you-loved card admitted on the anchor's director, with what each row stores.
    /// </summary>
    /// <param name="heroWhy">The hero card's persisted line.</param>
    /// <param name="storedEvidence">True: the anchored card stores the shared director it was admitted on;
    /// false: it stores none; null: no column value (a pre-deploy row).</param>
    /// <param name="whySource">The why-source stored on every row; null for a pre-deploy row.</param>
    /// <param name="heroWhySource">Overrides the hero's stored why-source.</param>
    private async Task<Guid> SeedAsync(
        string heroWhy, bool? storedEvidence = true, string? whySource = "Llm", string? heroWhySource = null)
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
        db.Persons.Add(director);

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
            PersonId = director.Id,
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
            WhySource = heroWhySource ?? whySource,
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
            WhySource = whySource,
            EvidenceJson = storedEvidence switch
            {
                true => AnchorRowGate.Serialize([new SharedEvidence("person", director.Name, "director")]),
                false => "[]",
                null => null,
            },
        });

        await db.SaveChangesAsync();
        return account.Id;
    }
}
