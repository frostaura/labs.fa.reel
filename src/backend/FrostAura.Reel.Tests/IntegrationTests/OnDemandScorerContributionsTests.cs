using System.Text.Json;
using FrostAura.Reel.Api.Endpoints;
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
/// IREV3S-05 on real Postgres: a title first scored on demand (from Ask, typeahead or search) must
/// carry the contributions behind its number, because its title page's "why X for you" waterfall
/// reads them. Before, the scorer wrote numbers only, and Sexy Beast's page explained a 6.2 with a
/// −0.6 demotion and a +0.0 prior. Also pinned: the title page rescores a row stored without
/// contributions, and leaves a row that has them alone. Skips when REEL_TEST_CONNECTION_STRING is unset.
/// </summary>
[Collection("postgres")]
public class OnDemandScorerContributionsTests(PostgresFixture fixture)
{
    [Fact]
    public async Task An_on_demand_score_persists_the_contributions_behind_its_number()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        var artifact = await SeedArtifactAsync(accountId, [1, 2, 3]);
        var title = await SeedTitleAsync("heist");
        var engine = new ScriptedEngine([("simTopLovedMean", 0.41f), ("traktRating", -0.2f), ("decadeAffinity", 0.12f)]);

        await using var db = Context(accountId);
        var scored = await Scorer(db, engine).ScoreAsync(accountId, [title], DateTime.UtcNow, CancellationToken.None);

        Assert.True(scored.ContainsKey(title));
        var stored = await StoredAsync(accountId, title, artifact);
        Assert.Equal(
            [("simTopLovedMean", 0.41m), ("traktRating", -0.2m), ("decadeAffinity", 0.12m)],
            Parse(stored.ContributionsJson));
        Assert.Equal(scored[title], stored.PredictedRating);
    }

    [Fact]
    public async Task A_rescore_replaces_a_stored_rows_reasons_with_the_ones_behind_the_new_number()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        var artifact = await SeedArtifactAsync(accountId, [1, 2, 3]);
        var title = await SeedTitleAsync("drama");
        await using (var seed = Context(accountId))
        {
            await TitleScoreWriter.UpsertAsync(
                seed, accountId, artifact,
                [new TitleScoreWriter.Score(title, 8.3m, """[{"feature": "directorAffinity", "value": 0.9}]""")],
                DateTime.UtcNow.AddHours(-1), CancellationToken.None);
        }

        await using var db = Context(accountId);
        var engine = new ScriptedEngine([("writerAffinity", 0.27f)]);
        var scored = await Scorer(db, engine).ScoreAsync(accountId, [title], DateTime.UtcNow, CancellationToken.None);

        var stored = await StoredAsync(accountId, title, artifact);
        Assert.Equal(scored[title], stored.PredictedRating);
        Assert.Equal([("writerAffinity", 0.27m)], Parse(stored.ContributionsJson));
    }

    [Fact]
    public async Task The_title_page_rescores_a_row_stored_without_contributions()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        var artifact = await SeedArtifactAsync(accountId, [4, 5, 6]);
        var title = await SeedTitleAsync("crime");
        await using (var seed = Context(accountId))
        {
            // The legacy shape: a number-only on-demand score from before the fix.
            await TitleScoreWriter.UpsertAsync(
                seed, accountId, artifact,
                [new TitleScoreWriter.Score(title, 6.2m, "[]")],
                DateTime.UtcNow.AddHours(-1), CancellationToken.None);
        }

        await using var db = Context(accountId);
        var engine = new ScriptedEngine([("userGenreAffinity", 0.55f), ("simLovedCentroid", 0.18f)]);
        var scorer = Scorer(db, engine);

        var resolved = await TitleEndpoints.ResolveScoreAsync(db, scorer, accountId, title, CancellationToken.None);

        Assert.NotNull(resolved);
        Assert.Equal(1, engine.Calls);
        Assert.Equal([("userGenreAffinity", 0.55m), ("simLovedCentroid", 0.18m)], Parse(resolved!.ContributionsJson));

        // Explained now, so a second open reads the row instead of scoring again.
        var again = await TitleEndpoints.ResolveScoreAsync(db, scorer, accountId, title, CancellationToken.None);
        Assert.Equal(1, engine.Calls);
        Assert.Equal(resolved.Id, again!.Id);
    }

    [Fact]
    public async Task The_title_page_leaves_an_explained_row_alone()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        var artifact = await SeedArtifactAsync(accountId, [7, 8, 9]);
        var title = await SeedTitleAsync("comedy");
        await using (var seed = Context(accountId))
        {
            await TitleScoreWriter.UpsertAsync(
                seed, accountId, artifact,
                [new TitleScoreWriter.Score(title, 7.4m, """[{"feature": "castAffinity", "value": 0.6}]""")],
                DateTime.UtcNow.AddHours(-1), CancellationToken.None);
        }

        await using var db = Context(accountId);
        var engine = new ScriptedEngine([("traktRating", 0.3f)]);

        var resolved = await TitleEndpoints.ResolveScoreAsync(db, Scorer(db, engine), accountId, title, CancellationToken.None);

        Assert.Equal(0, engine.Calls);
        Assert.Equal(7.4m, resolved!.PredictedRating);
        Assert.Equal([("castAffinity", 0.6m)], Parse(resolved.ContributionsJson));
    }

    [Fact]
    public async Task The_real_engine_explains_an_on_demand_score_in_schema_features()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        var artifact = await SeedTrainedArtifactAsync(accountId);
        var title = await SeedTitleAsync("thriller");

        await using var db = Context(accountId);
        await Scorer(db, new FastTreeModelEngine()).ScoreAsync(accountId, [title], DateTime.UtcNow, CancellationToken.None);

        var contributions = Parse((await StoredAsync(accountId, title, artifact)).ContributionsJson);
        Assert.InRange(contributions.Count, 1, 5); // the build's top-5 shape
        Assert.All(contributions, c => Assert.Contains(c.Feature, FeatureSchema.AllNames()));
    }

    [Fact]
    public async Task The_waterfall_walks_from_the_models_start_to_the_number_on_the_page()
    {
        if (!fixture.Available)
        {
            return;
        }

        // F-25: additive steps in rating points that land on the prediction, from a start every title
        // shares — the property the page's walk is drawn on, and the one the normalised bars lack.
        var accountId = await SeedAccountAsync();
        await SeedTrainedArtifactAsync(accountId);
        var first = await SeedTitleAsync("thriller");
        var second = await SeedTitleAsync("comedy");

        await using var db = Context(accountId);
        var scorer = Scorer(db, new FastTreeModelEngine());
        var now = DateTime.UtcNow;
        var scored = await scorer.ScoreAsync(accountId, [first, second], now, CancellationToken.None);
        var taste = await new FeatureVectorBuilder(db).BuildTasteStateAsync(accountId, now, CancellationToken.None);

        var walks = new List<PredictionWaterfall>();
        foreach (var title in new[] { first, second })
        {
            var walk = await scorer.WaterfallAsync(accountId, title, scored[title], taste, now, CancellationToken.None);
            Assert.NotNull(walk);
            Assert.Equal(PredictionWaterfall.StartLabel, walk!.BaselineLabel);
            Assert.InRange(walk.Steps.Count, 1, OnDemandScorer.WaterfallFeatureSteps + 4);
            Assert.InRange(walk.Baseline + walk.Steps.Sum(s => s.Value), scored[title] - 0.005m, scored[title] + 0.005m);
            walks.Add(walk);
        }

        // The start is the ensemble's, not the title's: identical for both titles (no head, no
        // calibration pairs and no dislikes on this account, so nothing else moves it), and near the
        // mean of the labels it was trained on (3 + 8 x U(0,1) mean, about 7).
        // Within the roundings: each walk's start absorbs its own 2dp number and 3dp steps.
        Assert.InRange(Math.Abs(walks[0].Baseline - walks[1].Baseline), 0m, 0.025m);
        Assert.InRange(walks[0].Baseline, 5.5m, 8.5m);

        // A walk that cannot land on the shown number is not offered.
        Assert.Null(await scorer.WaterfallAsync(accountId, first, scored[first] + 1m, taste, now, CancellationToken.None));
    }

    [Fact]
    public void The_engines_unnormalised_contributions_sum_to_the_score_less_one_shared_start()
    {
        // The additivity the waterfall is built on, at float precision and with no rounding in the way.
        var engine = new FastTreeModelEngine();
        var bytes = TrainedBytes();
        var key = Guid.NewGuid();
        var rng = new Random(5);
        var starts = Enumerable.Range(0, 5).Select(_ =>
        {
            var v = new float[FeatureSchema.VectorLength];
            for (var j = 0; j < v.Length; j++)
            {
                v[j] = (float)rng.NextDouble();
            }

            var row = engine.ExplainCached(key, bytes, v, FeatureSchema.AllNames())!;
            Assert.Equal(engine.ScoreCached(key, bytes, [v])[0], row.Score, 4);
            return row.Score - row.TopContributions.Sum(c => c.Contribution);
        }).ToList();

        Assert.InRange(starts.Max() - starts.Min(), 0f, 1e-3f);
    }

    [Fact]
    public void The_card_bars_are_the_walk_scaled_to_its_largest_push()
    {
        // One decomposition on the title page: a bar may not credit a feature the walk beside it
        // takes points away for (Men of Honor, 2026-10-26: "+0.1 your contrarian lens" over "-0.1").
        var engine = new FastTreeModelEngine();
        var bytes = TrainedBytes();
        var key = Guid.NewGuid();
        var rng = new Random(9);
        var v = new float[FeatureSchema.VectorLength];
        for (var j = 0; j < v.Length; j++)
        {
            v[j] = (float)rng.NextDouble();
        }

        var walk = engine.ExplainCached(key, bytes, v, FeatureSchema.AllNames())!.TopContributions;
        var bars = engine.ScoreWithContributionsCached(key, bytes, [v], FeatureSchema.AllNames(), topK: 5)[0].TopContributions;
        var largest = Math.Abs(walk[0].Contribution);

        Assert.Equal(walk.Take(5).Select(c => c.Feature), bars.Select(c => c.Feature));
        Assert.All(bars.Zip(walk), pair => Assert.Equal(pair.Second.Contribution / largest, pair.First.Contribution, 4));
        Assert.Equal(1f, Math.Abs(bars[0].Contribution), 4);
    }

    // ── Harness ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// An engine that answers every row with a fixed score and a scripted contribution list, and
    /// counts the explainable scoring calls — so a test can tell a rescore from a read.
    /// </summary>
    private sealed class ScriptedEngine(IReadOnlyList<(string Feature, float Contribution)> contributions) : IModelEngine
    {
        public int Calls { get; private set; }

        public TrainedModel Train(TrainingMatrix matrix, string hyperparamsJson) => throw new NotSupportedException();

        public float[] Score(byte[] artifactBytes, float[][] features) => [.. features.Select(_ => 6.8f)];

        public float[] ScoreCached(Guid modelKey, byte[] artifactBytes, float[][] features) => Score(artifactBytes, features);

        public ScoredRow[] ScoreWithContributions(byte[] artifactBytes, float[][] features, string[] featureNames, int topK = 5)
        {
            Calls++;
            return [.. features.Select(_ => new ScoredRow(6.8f, contributions))];
        }

        public ScoredRow[] ScoreWithContributionsCached(
            Guid modelKey, byte[] artifactBytes, float[][] features, string[] featureNames, int topK = 5) =>
            ScoreWithContributions(artifactBytes, features, featureNames, topK);
    }

    private Infrastructure.Persistence.ReelDbContext Context(Guid accountId)
    {
        var scoped = new AccountContext();
        scoped.SetAccount(accountId);
        return fixture.CreateContext(scoped);
    }

    private static OnDemandScorer Scorer(Infrastructure.Persistence.ReelDbContext db, IModelEngine engine)
    {
        var featureBuilder = new FeatureVectorBuilder(db);
        return new OnDemandScorer(
            db, featureBuilder, engine,
            new OnlineResidualModel(db, featureBuilder, engine, new MemoryCache(new MemoryCacheOptions()), NullLogger<OnlineResidualModel>.Instance),
            new DislikeSignal(db, new MemoryCache(new MemoryCacheOptions())),
            new RatingCalibrationService(db, new MemoryCache(new MemoryCacheOptions())));
    }

    private async Task<TitleScore> StoredAsync(Guid accountId, Guid titleId, Guid artifactId)
    {
        await using var db = Context(accountId);
        return await db.TitleScores.AsNoTracking()
            .SingleAsync(s => s.AccountId == accountId && s.TitleId == titleId && s.ModelArtifactId == artifactId);
    }

    private static List<(string Feature, decimal Value)> Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return [.. doc.RootElement.EnumerateArray()
            .Select(e => (e.GetProperty("feature").GetString()!, Math.Round(e.GetProperty("value").GetDecimal(), 3)))];
    }

    private async Task<Guid> SeedAccountAsync()
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var account = new Account
        {
            Id = Guid.NewGuid(),
            TraktUserSlug = $"ondemand-why-{Guid.NewGuid():N}",
            CreatedAt = DateTime.UtcNow,
            LastSeenAt = DateTime.UtcNow,
            PipelineStageChangedAt = DateTime.UtcNow,
        };
        db.Add(account);
        await db.SaveChangesAsync();
        return account.Id;
    }

    private async Task<Guid> SeedTitleAsync(string genre)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var title = new Title
        {
            Id = Guid.NewGuid(),
            MediaType = MediaType.Movie,
            TmdbId = Random.Shared.NextInt64(1, long.MaxValue),
            TraktSlug = string.Empty,
            Name = $"on-demand {genre} {Guid.NewGuid():N}",
            Language = "en",
            PosterPath = "/p.jpg",
            Genres = [genre],
            Year = 2000,
            ReleasedAt = new DateTime(2000, 9, 1, 0, 0, 0, DateTimeKind.Utc),
            RuntimeMinutes = 89,
            TmdbPopularity = 4m,
            TraktRating = 7.2m,
            TraktVotes = 900,
            CreatedAt = DateTime.UtcNow,
        };
        db.Add(title);
        await db.SaveChangesAsync();
        return title.Id;
    }

    /// <summary>An Active artifact whose bytes the scripted engine never reads.</summary>
    private Task<Guid> SeedArtifactAsync(Guid accountId, byte[] bytes) => AddArtifactAsync(accountId, bytes);

    /// <summary>
    /// A small real FastTree whose label leans on EVERY feature, so a title vector away from the
    /// training mean gets non-zero contributions whichever features its catalogue row fills.
    /// </summary>
    private Task<Guid> SeedTrainedArtifactAsync(Guid accountId) => AddArtifactAsync(accountId, TrainedBytes());

    private static byte[] TrainedBytes()
    {
        var rng = new Random(11);
        var rows = new List<float[]>();
        var labels = new List<float>();
        for (var i = 0; i < 300; i++)
        {
            var v = new float[FeatureSchema.VectorLength];
            for (var j = 0; j < v.Length; j++)
            {
                v[j] = (float)rng.NextDouble();
            }

            rows.Add(v);
            labels.Add(Math.Clamp(3f + (8f * v.Average()), 1f, 10f));
        }

        var trained = new FastTreeModelEngine().Train(
            new TrainingMatrix([.. rows], [.. labels], FeatureSchema.AllNames()),
            TrainJobHandler.DefaultHyperparams);
        return trained.ArtifactBytes;
    }

    private async Task<Guid> AddArtifactAsync(Guid accountId, byte[] bytes)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var artifact = new ModelArtifact
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            Version = 1,
            Algo = "FastTree",
            ArtifactBytes = bytes,
            FeatureSchemaJson = JsonSerializer.Serialize(new { version = FeatureSchema.Version, names = FeatureSchema.AllNames() }),
            TrainingRunId = Guid.NewGuid(),
            Status = ArtifactStatus.Active,
            TrainedAt = DateTime.UtcNow,
        };
        db.ModelArtifacts.Add(artifact);
        await db.SaveChangesAsync();
        return artifact.Id;
    }
}
