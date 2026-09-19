using FrostAura.Reel.Application.Jobs;
using FrostAura.Reel.Application.Ml;
using FrostAura.Reel.Application.Ranking;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Library;
using FrostAura.Reel.Domain.Ml;
using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Domain.Tenancy;
using FrostAura.Reel.Infrastructure.Ml;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Pgvector;

namespace FrostAura.Reel.Tests.IntegrationTests;

/// <summary>
/// Phase 8b end-to-end on real Postgres: a dislike WITH ITS REASON must move scores, not just
/// remove one title. Genre-reasoned rejections demote same-genre candidates through
/// <see cref="OnDemandScorer"/>; seen-enough rejections demote near-identical plots hard; an
/// unrelated title's score does not move at all. Plus the schema-v3 features: populated from
/// live reactions, strictly as-of aware. Skips when REEL_TEST_CONNECTION_STRING is unset.
/// </summary>
[Collection("postgres")]
public class DislikeSteeringTests(PostgresFixture fixture) : EmbeddedCatalogTestBase(fixture)
{
    private const int EmbeddingDims = 1536;

    [Fact]
    public async Task Genre_reasoned_dislikes_demote_same_genre_candidates_and_leave_others_untouched()
    {
        if (!Fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        await SeedActiveArtifactAsync(accountId);
        var candidateHorror = await SeedTitleAsync("candidate horror", ["horror"]);
        var controlDrama = await SeedTitleAsync("control drama", ["drama"]);
        var dislikedA = await SeedTitleAsync("declined horror a", ["horror"]);
        var dislikedB = await SeedTitleAsync("declined horror b", ["horror"]);

        var (scorer, dislikes, db) = CreateScorer(accountId);
        await using var _ = db;
        var now = DateTime.UtcNow;

        var before = await scorer.ScoreAsync(accountId, [candidateHorror, controlDrama], now, CancellationToken.None);

        // Two fresh "wrong genre" rejections → genre weight 2 → the 0.6-capped genre demotion.
        await AddReactionAsync(accountId, dislikedA, ReactionReason.Genre);
        await AddReactionAsync(accountId, dislikedB, ReactionReason.Genre);
        dislikes.Invalidate(accountId);

        var after = await scorer.ScoreAsync(accountId, [candidateHorror, controlDrama], now, CancellationToken.None);

        // The same-genre candidate drops by the capped genre component (0.6, minus at most a
        // sliver of base-model drift from the v3 overlap feature changing).
        Assert.True(
            before[candidateHorror] - after[candidateHorror] >= 0.3m,
            $"horror candidate should drop ≥0.3 ({before[candidateHorror]} → {after[candidateHorror]})");

        // The control shares no genre and has no embedding: its vector and demotion are both
        // unchanged, so its score is EXACTLY what it was.
        Assert.Equal(before[controlDrama], after[controlDrama]);
    }

    [Fact]
    public async Task Seen_enough_dislikes_demote_near_identical_plots_hard()
    {
        if (!Fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        await SeedActiveArtifactAsync(accountId);
        var disliked = await SeedTitleAsync("seen enough of these", ["drama"]);
        var similar = await SeedTitleAsync("near identical plot", ["comedy"]);
        var unrelated = await SeedTitleAsync("orthogonal plot", ["comedy"]);
        await SeedEmbeddingAsync(disliked, axis: 0);
        await SeedEmbeddingAsync(similar, axis: 0);   // identical direction → cosine 1
        await SeedEmbeddingAsync(unrelated, axis: 1); // orthogonal → cosine 0

        var (scorer, dislikes, db) = CreateScorer(accountId);
        await using var _ = db;
        var now = DateTime.UtcNow;

        var before = await scorer.ScoreAsync(accountId, [similar, unrelated], now, CancellationToken.None);

        await AddReactionAsync(accountId, disliked, ReactionReason.SeenEnough);
        dislikes.Invalidate(accountId);

        var after = await scorer.ScoreAsync(accountId, [similar, unrelated], now, CancellationToken.None);

        // cos 1 × 0.9 (seen-enough) + cos 1 × 0.3 (centroid) = 1.2 demotion; allow base drift from
        // the v3 centroid-similarity feature changing under the model.
        Assert.True(
            before[similar] - after[similar] >= 0.8m,
            $"near-identical candidate should drop ≥0.8 ({before[similar]} → {after[similar]})");

        // Orthogonal plot: zero similarity, zero genre overlap — identical vector, zero demotion.
        Assert.Equal(before[unrelated], after[unrelated]);
    }

    [Fact]
    public async Task Schema_v3_features_populate_from_reactions_and_respect_as_of()
    {
        if (!Fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        var candidateHorror = await SeedTitleAsync("v3 horror candidate", ["horror"]);
        var candidateDrama = await SeedTitleAsync("v3 drama candidate", ["drama"]);
        var disliked = await SeedTitleAsync("v3 declined horror", ["horror"]);
        await SeedEmbeddingAsync(disliked, axis: 0);
        await SeedEmbeddingAsync(candidateHorror, axis: 0);

        var reactedAt = DateTime.UtcNow.AddDays(-1);
        await AddReactionAsync(accountId, disliked, ReactionReason.Genre, reactedAt);

        var scoped = new AccountContext();
        scoped.SetAccount(accountId);
        await using var db = Fixture.CreateContext(scoped);
        var builder = new FeatureVectorBuilder(db);

        var overlapIndex = Array.IndexOf(FeatureSchema.Scalars, "dislikedGenreOverlap");
        var centroidIndex = Array.IndexOf(FeatureSchema.Scalars, "simNotInterestedCentroid");
        Assert.True(overlapIndex >= 0 && centroidIndex >= 0, "v3 scalars must exist in the schema");

        // As-of AFTER the reaction: both features light up — the overlap carries the ~1.0 recency
        // weight of a day-old dislike, the centroid similarity is cosine 1 to the identical vector.
        var asOfAfter = reactedAt.AddHours(2);
        var tasteAfter = await builder.BuildTasteStateAsync(accountId, asOfAfter, CancellationToken.None);
        var vectorsAfter = await builder.BuildAsync(
            tasteAfter, [candidateHorror, candidateDrama], asOfAfter, CancellationToken.None);

        Assert.InRange(vectorsAfter[candidateHorror][overlapIndex], 0.9f, 1.01f);
        Assert.InRange(vectorsAfter[candidateHorror][centroidIndex], 0.99f, 1.01f);
        // No genre overlap and no embedding for the drama candidate → both neutral 0.
        Assert.Equal(0f, vectorsAfter[candidateDrama][overlapIndex]);
        Assert.Equal(0f, vectorsAfter[candidateDrama][centroidIndex]);

        // As-of BEFORE the reaction: the reaction does not exist yet — training at splitAt must
        // not see the future, exactly like every other label source.
        var asOfBefore = reactedAt.AddHours(-2);
        var tasteBefore = await builder.BuildTasteStateAsync(accountId, asOfBefore, CancellationToken.None);
        var vectorsBefore = await builder.BuildAsync(
            tasteBefore, [candidateHorror], asOfBefore, CancellationToken.None);

        Assert.Equal(0f, vectorsBefore[candidateHorror][overlapIndex]);
        Assert.Equal(0f, vectorsBefore[candidateHorror][centroidIndex]);
    }

    // ── Harness ─────────────────────────────────────────────────────────────────────────────

    private (OnDemandScorer Scorer, DislikeSignal Dislikes, Infrastructure.Persistence.ReelDbContext Db) CreateScorer(Guid accountId)
    {
        var scoped = new AccountContext();
        scoped.SetAccount(accountId);
        var db = Fixture.CreateContext(scoped);
        var featureBuilder = new FeatureVectorBuilder(db);
        var engine = new FastTreeModelEngine();
        var residualModel = new OnlineResidualModel(
            db, featureBuilder, engine, new MemoryCache(new MemoryCacheOptions()), NullLogger<OnlineResidualModel>.Instance);
        var dislikes = new DislikeSignal(db, new MemoryCache(new MemoryCacheOptions()));
        var calibration = new RatingCalibrationService(db, new MemoryCache(new MemoryCacheOptions()));
        return (new OnDemandScorer(db, featureBuilder, engine, residualModel, dislikes, calibration), dislikes, db);
    }

    private async Task<Guid> SeedAccountAsync()
    {
        await using var db = Fixture.CreateContext(new AccountContext());
        var account = new Account
        {
            Id = Guid.NewGuid(),
            TraktUserSlug = $"dislike-{Guid.NewGuid():N}",
            CreatedAt = DateTime.UtcNow,
            LastSeenAt = DateTime.UtcNow,
            PipelineStageChangedAt = DateTime.UtcNow,
        };
        db.Add(account);
        await db.SaveChangesAsync();
        return account.Id;
    }

    private async Task<Guid> SeedTitleAsync(string name, string[] genres)
    {
        await using var db = Fixture.CreateContext(new AccountContext());
        var title = new Title
        {
            Id = Guid.NewGuid(),
            MediaType = MediaType.Movie,
            TmdbId = Random.Shared.NextInt64(1, long.MaxValue),
            TraktSlug = string.Empty,
            Name = $"{name} {Guid.NewGuid():N}",
            Language = "en",
            PosterPath = "/p.jpg",
            Genres = genres,
            Year = 2015,
            RuntimeMinutes = 110,
            TmdbPopularity = 3m,
            CreatedAt = DateTime.UtcNow,
        };
        db.Add(title);
        await db.SaveChangesAsync();
        return title.Id;
    }

    /// <summary>A unit embedding along one axis — pairs are either identical (cos 1) or orthogonal (cos 0).</summary>
    private async Task SeedEmbeddingAsync(Guid titleId, int axis)
    {
        await using var db = Fixture.CreateContext(new AccountContext());
        var vector = new float[EmbeddingDims];
        vector[axis] = 1f;
        db.TitleEmbeddings.Add(new TitleEmbedding
        {
            TitleId = titleId,
            Embedding = new Vector(vector),
            EmbeddingModel = "test",
            SourceTextHash = $"hash-{titleId:N}",
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private async Task AddReactionAsync(
        Guid accountId, Guid titleId, ReactionReason reason, DateTime? createdAt = null)
    {
        await using var db = Fixture.CreateContext(new AccountContext());
        db.UserTitleReactions.Add(new UserTitleReaction
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            TitleId = titleId,
            Kind = ReactionKind.NotInterested,
            Reason = reason,
            CreatedAt = createdAt ?? DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    /// <summary>Same shape as the other suites: a small FastTree over the live schema, Active.</summary>
    private async Task SeedActiveArtifactAsync(Guid accountId)
    {
        var rng = new Random(7);
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

        await using var db = Fixture.CreateContext(new AccountContext());
        db.ModelArtifacts.Add(new ModelArtifact
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            Version = 1,
            Algo = "FastTree",
            ArtifactBytes = trained.ArtifactBytes,
            FeatureSchemaJson = System.Text.Json.JsonSerializer.Serialize(
                new { version = FeatureSchema.Version, names = FeatureSchema.AllNames() }),
            TrainingRunId = Guid.NewGuid(),
            Status = ArtifactStatus.Active,
            TrainedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }
}
