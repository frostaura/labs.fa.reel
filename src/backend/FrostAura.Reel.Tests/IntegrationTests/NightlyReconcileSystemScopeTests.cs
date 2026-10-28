using FrostAura.Reel.Application.Ml;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Ml;
using FrostAura.Reel.Domain.Sync;
using FrostAura.Reel.Domain.Tenancy;
using FrostAura.Reel.Infrastructure.Background;
using FrostAura.Reel.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FrostAura.Reel.Tests.IntegrationTests;

/// <summary>
/// WS-5 task 15 (J-11): the nightly reconcile and its start-up sweeps read every account on purpose,
/// so they say so with <see cref="IAccountContext.EnterSystemScope"/>. Under
/// <see cref="UnscopedReadMode.Throw"/> — where tenancy is heading — a sweep that forgot would throw
/// inside its own catch and log an error on every boot, leaving stale artifacts Active and untrusted
/// snapshots served.
/// </summary>
[Collection("postgres")]
public class NightlyReconcileSystemScopeTests(PostgresFixture fixture)
{
    [Fact]
    public async Task The_start_up_sweeps_run_clean_when_tenancy_fails_closed()
    {
        if (!fixture.Available)
        {
            return;
        }

        var services = new ServiceCollection();
        services.AddScoped<IAccountContext>(_ => new AccountContext(new UnscopedReadPolicy(UnscopedReadMode.Throw)));
        services.AddScoped<ReelDbContext>(sp => fixture.CreateContext(sp.GetRequiredService<IAccountContext>()));
        await using var provider = services.BuildServiceProvider();

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                // The two start-up sweeps run; the 03:00 loop does not.
                ["Background:NightlyReconcile"] = "false",
                ["Background:TrustPurge"] = "true",
            })
            .Build();
        var log = new RecordingLogger<NightlyReconcileService>();
        var service = new NightlyReconcileService(provider.GetRequiredService<IServiceScopeFactory>(), configuration, log);

        await service.StartAsync(CancellationToken.None);
        await service.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.DoesNotContain(log.Lines, l => l.Level >= LogLevel.Error);
        Assert.Contains(log.Lines, l => l.Message.Contains("NightlyReconcile disabled", StringComparison.Ordinal));
    }

    /// <summary>
    /// HREV2-RET-02: an artifact trained with leave-one-out pools is retrained once on deploy, behind
    /// the same switch as the trust purge (a retrain chains a paid Fresh build), and keeps serving
    /// until its replacement lands — it is not superseded into a model-less gap.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_start_up_sweep_queues_one_retrain_for_an_artifact_on_an_older_recipe(bool paidSweepsOn)
    {
        if (!fixture.Available)
        {
            return;
        }

        var legacy = await SeedAccountWithActiveAsync("""{"version": 5}""");
        var current = await SeedAccountWithActiveAsync($$"""{"version": 5, "pools": "{{TrainingRecipe.Current}}"}""");

        var services = new ServiceCollection();
        services.AddScoped<IAccountContext>(_ => new AccountContext(new UnscopedReadPolicy(UnscopedReadMode.Throw)));
        services.AddScoped<ReelDbContext>(sp => fixture.CreateContext(sp.GetRequiredService<IAccountContext>()));
        await using var provider = services.BuildServiceProvider();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Background:NightlyReconcile"] = "false",
                ["Background:TrustPurge"] = paidSweepsOn ? "true" : "false",
            })
            .Build();
        var log = new RecordingLogger<NightlyReconcileService>();

        // Twice: a second boot must not stack a second retrain on the one already queued.
        for (var boot = 0; boot < 2; boot++)
        {
            var service = new NightlyReconcileService(provider.GetRequiredService<IServiceScopeFactory>(), configuration, log);
            await service.StartAsync(CancellationToken.None);
            await service.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(30));
        }

        Assert.DoesNotContain(log.Lines, l => l.Level >= LogLevel.Error);
        await using var db = fixture.CreateContext(new AccountContext());
        var trains = await db.SyncJobs
            .Where(j => (j.AccountId == legacy || j.AccountId == current) && j.Kind == JobKind.Train)
            .ToListAsync();
        if (paidSweepsOn)
        {
            var queued = Assert.Single(trains);
            Assert.Equal(legacy, queued.AccountId);
            Assert.Equal(NightlyReconcileService.RecipeRetrainReason, queued.TriggerReason);
        }
        else
        {
            Assert.Empty(trains);
        }

        // Still the account's model until the retrain replaces it.
        Assert.Equal(2, await db.ModelArtifacts.CountAsync(
            a => (a.AccountId == legacy || a.AccountId == current) && a.Status == ArtifactStatus.Active));
    }

    /// <summary>
    /// HREV2-RET-02, the stored half: the scores a legacy-recipe artifact was misserved with — 2020s
    /// titles near 2, Boss Level (rated 7) at 2.0 — are dropped on start-up whatever the paid switch
    /// says, because with it off the retrain that would supersede them never comes. Only rows written
    /// since the misserving began; only the Active legacy artifact's; and only once, so a row this
    /// build scores survives the next restart.
    /// </summary>
    [Theory]
    [InlineData("""{"version": 5}""", false)]
    [InlineData("""{"version": 5, "pools": "leave-one-out"}""", true)]
    public async Task The_start_up_sweep_drops_the_scores_a_legacy_artifact_was_misserved_with_once(
        string legacyJson, bool paidSweepsOn)
    {
        if (!fixture.Available)
        {
            return;
        }

        var legacy = await SeedAccountWithActiveAsync(legacyJson);
        var current = await SeedAccountWithActiveAsync($$"""{"version": 5, "pools": "{{TrainingRecipe.Current}}"}""");
        var titles = await SeedTitlesAsync(5);
        var since = RecipeScoreRepair.MisservedSince;
        var now = DateTime.UtcNow;

        Guid legacyArtifact, currentArtifact, supersededArtifact;
        await using (var db = fixture.CreateContext(new AccountContext()))
        {
            legacyArtifact = await db.ModelArtifacts.Where(a => a.AccountId == legacy).Select(a => a.Id).SingleAsync();
            currentArtifact = await db.ModelArtifacts.Where(a => a.AccountId == current).Select(a => a.Id).SingleAsync();
            supersededArtifact = Guid.NewGuid();
            db.ModelArtifacts.Add(new ModelArtifact
            {
                Id = supersededArtifact,
                AccountId = legacy,
                Version = 0,
                ArtifactBytes = [1],
                FeatureSchemaJson = """{"version": 5}""",
                TrainingRunId = Guid.NewGuid(),
                Status = ArtifactStatus.Superseded,
                TrainedAt = since.AddDays(-30),
            });
            db.TitleScores.AddRange(
                Score(legacy, legacyArtifact, titles[0], since.AddDays(-3)),      // pre-rollup history: kept
                Score(legacy, legacyArtifact, titles[1], since.AddMinutes(5)),     // misserved: dropped
                Score(legacy, legacyArtifact, titles[2], now.AddMinutes(-1)),      // misserved: dropped
                Score(legacy, supersededArtifact, titles[3], since.AddHours(1)),   // never served: kept
                Score(current, currentArtifact, titles[4], now.AddMinutes(-2)));   // current recipe: kept
            await db.SaveChangesAsync();
        }

        var services = new ServiceCollection();
        services.AddScoped<IAccountContext>(_ => new AccountContext(new UnscopedReadPolicy(UnscopedReadMode.Throw)));
        services.AddScoped<ReelDbContext>(sp => fixture.CreateContext(sp.GetRequiredService<IAccountContext>()));
        await using var provider = services.BuildServiceProvider();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Background:NightlyReconcile"] = "false",
                ["Background:TrustPurge"] = paidSweepsOn ? "true" : "false",
            })
            .Build();
        var log = new RecordingLogger<NightlyReconcileService>();

        async Task BootAsync()
        {
            var service = new NightlyReconcileService(provider.GetRequiredService<IServiceScopeFactory>(), configuration, log);
            await service.StartAsync(CancellationToken.None);
            await service.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(30));
        }

        async Task<HashSet<(Guid Artifact, Guid Title)>> RowsAsync()
        {
            await using var db = fixture.CreateContext(new AccountContext());
            return [.. (await db.TitleScores
                    .Where(s => s.AccountId == legacy || s.AccountId == current)
                    .Select(s => new { s.ModelArtifactId, s.TitleId })
                    .ToListAsync())
                .Select(s => (s.ModelArtifactId, s.TitleId))];
        }

        await BootAsync();
        Assert.Equal(
            [(legacyArtifact, titles[0]), (supersededArtifact, titles[3]), (currentArtifact, titles[4])],
            (await RowsAsync()).OrderBy(r => Array.IndexOf(titles, r.Title)).ToList());
        await using (var db = fixture.CreateContext(new AccountContext()))
        {
            Assert.Equal(RecipeScoreRepair.Revision, RecipeScoreRepair.RevisionOf(
                await db.ModelArtifacts.Where(a => a.Id == legacyArtifact).Select(a => a.FeatureSchemaJson).SingleAsync()));
            Assert.Equal(0, RecipeScoreRepair.RevisionOf(
                await db.ModelArtifacts.Where(a => a.Id == currentArtifact).Select(a => a.FeatureSchemaJson).SingleAsync()));

            // This build rescores a title; the next restart must keep it.
            db.TitleScores.Add(Score(legacy, legacyArtifact, titles[1], DateTime.UtcNow));
            await db.SaveChangesAsync();
        }

        await BootAsync();
        Assert.Contains((legacyArtifact, titles[1]), await RowsAsync());
        Assert.Equal(4, (await RowsAsync()).Count);
        Assert.DoesNotContain(log.Lines, l => l.Level >= LogLevel.Error);
    }

    private static TitleScore Score(Guid accountId, Guid artifactId, Guid titleId, DateTime scoredAt) => new()
    {
        Id = Guid.NewGuid(),
        AccountId = accountId,
        TitleId = titleId,
        ModelArtifactId = artifactId,
        PredictedRating = 2.1m,
        ContributionsJson = "[]",
        ScoredAt = scoredAt,
    };

    private async Task<Guid[]> SeedTitlesAsync(int count)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var titles = Enumerable.Range(0, count).Select(i => new Domain.Catalog.Title
        {
            Id = Guid.NewGuid(),
            MediaType = Domain.Catalog.MediaType.Movie,
            TmdbId = Random.Shared.NextInt64(1, long.MaxValue),
            TraktSlug = string.Empty,
            Name = $"Repair {i} {Guid.NewGuid():N}",
            Year = 2024,
            CreatedAt = DateTime.UtcNow,
        }).ToList();
        db.Titles.AddRange(titles);
        await db.SaveChangesAsync();
        return [.. titles.Select(t => t.Id)];
    }

    private async Task<Guid> SeedAccountWithActiveAsync(string featureSchemaJson)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var now = DateTime.UtcNow;
        var account = new Account
        {
            Id = Guid.NewGuid(),
            TraktUserSlug = $"recipe-sweep-{Guid.NewGuid():N}",
            CreatedAt = now,
            LastSeenAt = now,
            PipelineStageChangedAt = now,
            PipelineStage = PipelineStage.FeedReady,
        };
        db.Accounts.Add(account);
        db.ModelArtifacts.Add(new ModelArtifact
        {
            Id = Guid.NewGuid(),
            AccountId = account.Id,
            Version = 1,
            ArtifactBytes = [1],
            FeatureSchemaJson = featureSchemaJson,
            TrainingRunId = Guid.NewGuid(),
            Status = ArtifactStatus.Active,
            TrainedAt = now,
        });
        await db.SaveChangesAsync();
        return account.Id;
    }
}
