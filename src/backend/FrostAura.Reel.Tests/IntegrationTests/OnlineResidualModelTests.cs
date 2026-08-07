using FrostAura.Reel.Application.Jobs;
using FrostAura.Reel.Application.Ml;
using FrostAura.Reel.Application.Ranking;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Ml;
using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Domain.Tenancy;
using FrostAura.Reel.Infrastructure.Ml;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrostAura.Reel.Tests.IntegrationTests;

/// <summary>
/// The online residual head on real Postgres: it must be a strict no-op with no events (so the base
/// model is never regressed) and must lift a title's predicted rating after a high-target observe —
/// while persisting an <see cref="OnlineModelState"/> row pinned to the Active artifact. Skips when
/// REEL_TEST_CONNECTION_STRING is unset (no DB in the run), matching the other integration tests.
/// </summary>
[Collection("postgres")]
public class OnlineResidualModelTests(PostgresFixture fixture)
{
    [Fact]
    public void Fresh_head_is_a_strict_no_op()
    {
        // Zero weights + zero bias — the state a never-observed account loads — contribute exactly 0
        // to any vector, so the base score is returned unchanged. This is the no-regression guarantee.
        var vector = new float[FeatureSchema.VectorLength];
        for (var i = 0; i < vector.Length; i++)
        {
            vector[i] = i % 3; // arbitrary non-trivial features
        }

        var residual = OnlineResidualModel.Residual(new float[FeatureSchema.VectorLength], 0d, vector);
        Assert.Equal(0d, residual);
    }

    [Fact]
    public async Task Observing_a_high_target_lifts_the_predicted_rating_and_persists_the_head()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        var titleId = await SeedTitleAsync("Residual Subject");
        var artifactId = await SeedActiveArtifactAsync(accountId);

        var scoped = new AccountContext();
        scoped.SetAccount(accountId);
        await using var db = fixture.CreateContext(scoped);

        var featureBuilder = new FeatureVectorBuilder(db);
        var engine = new FastTreeModelEngine();
        var residualModel = new OnlineResidualModel(
            db, featureBuilder, engine, new MemoryCache(new MemoryCacheOptions()), NullLogger<OnlineResidualModel>.Instance);
        var scorer = new OnDemandScorer(
            db, featureBuilder, engine, residualModel,
            new DislikeSignal(db, new MemoryCache(new MemoryCacheOptions())));
        var now = DateTime.UtcNow;

        // Baseline: no events yet ⇒ residual 0 ⇒ pure base score.
        var before = (await scorer.ScoreAsync(accountId, [titleId], now, CancellationToken.None))[titleId];

        // Teach the head that this title deserves a 10 — a strong positive residual.
        await residualModel.ObserveAsync(accountId, titleId, target: 10d, sampleWeight: 1.0, CancellationToken.None);

        var after = (await scorer.ScoreAsync(accountId, [titleId], now, CancellationToken.None))[titleId];

        Assert.True(after > before, $"predicted rating should rise after a high-target observe ({after} vs {before})");

        var state = await db.OnlineModelStates.FirstOrDefaultAsync(s => s.AccountId == accountId);
        Assert.NotNull(state);
        Assert.Equal(artifactId, state!.BaseArtifactId); // pinned to the Active artifact (auto-reset key)
        Assert.True(state.UpdateCount > 0, "an observe must record at least one update");
    }

    private async Task<Guid> SeedAccountAsync()
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var account = new Account
        {
            Id = Guid.NewGuid(),
            TraktUserSlug = $"residual-{Guid.NewGuid():N}",
            CreatedAt = DateTime.UtcNow,
            LastSeenAt = DateTime.UtcNow,
            PipelineStageChangedAt = DateTime.UtcNow,
        };
        db.Add(account);
        await db.SaveChangesAsync();
        return account.Id;
    }

    private async Task<Guid> SeedTitleAsync(string name)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var title = new Title
        {
            Id = Guid.NewGuid(),
            MediaType = MediaType.Movie,
            TmdbId = Random.Shared.NextInt64(1, long.MaxValue),
            TraktSlug = string.Empty,
            Name = name,
            Language = "en",
            PosterPath = "/p.jpg",
            Genres = ["drama"],
            TmdbPopularity = 10m,
            CreatedAt = DateTime.UtcNow,
        };
        db.Add(title);
        await db.SaveChangesAsync();
        return title.Id;
    }

    /// <summary>
    /// Trains a small FastTree artifact over synthetic full-length vectors (labels clustered near the
    /// mid-range so base predictions leave headroom for the residual to move) and marks it Active.
    /// </summary>
    private async Task<Guid> SeedActiveArtifactAsync(Guid accountId)
    {
        var rng = new Random(1);
        var rows = new List<float[]>();
        var labels = new List<float>();
        for (var i = 0; i < 200; i++)
        {
            var v = new float[FeatureSchema.VectorLength];
            for (var j = 0; j < v.Length; j++)
            {
                v[j] = (float)rng.NextDouble();
            }

            rows.Add(v);
            labels.Add(Math.Clamp(5f + (v[0] - 0.5f), 1f, 10f));
        }

        var trained = new FastTreeModelEngine().Train(
            new TrainingMatrix(rows.ToArray(), labels.ToArray(), FeatureSchema.AllNames()),
            TrainJobHandler.DefaultHyperparams);

        await using var db = fixture.CreateContext(new AccountContext());
        var artifact = new ModelArtifact
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            Version = 1,
            Algo = "FastTree",
            ArtifactBytes = trained.ArtifactBytes,
            // Stamped with the running schema version — an artifact recorded against any other
            // version is deliberately unreadable as Active (ReelDbContext), so "[]" would make
            // this fixture invisible to the very code under test.
            FeatureSchemaJson = System.Text.Json.JsonSerializer.Serialize(
                new { version = FeatureSchema.Version, names = FeatureSchema.AllNames() }),
            TrainingRunId = Guid.NewGuid(),
            Status = ArtifactStatus.Active,
            TrainedAt = DateTime.UtcNow,
        };
        db.ModelArtifacts.Add(artifact);
        await db.SaveChangesAsync();
        return artifact.Id;
    }
}
