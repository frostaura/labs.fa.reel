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
