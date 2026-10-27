using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FrostAura.Reel.Application.Ml;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Ml;
using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Domain.Tenancy;
using FrostAura.Reel.Infrastructure.Ml;
using FrostAura.Reel.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FrostAura.Reel.Tests.IntegrationTests;

/// <summary>
/// HREV2-RET-05 through the endpoint: a short "more like this" page carries <c>anchoredSupply</c> —
/// what the anchored gate held back and what sat under the for-you bar — so the empty rail stops
/// saying "nothing left that your conditions allow" when the gate, not the conditions, emptied it.
/// A genre rail keeps its own <c>supply</c> and carries no anchored count.
/// </summary>
[Collection("postgres")]
public class AnchoredRailSupplyTests(PostgresFixture fixture, ReelApiFactory api) : IClassFixture<ReelApiFactory>
{
    [Fact]
    public async Task An_empty_similar_rail_says_the_gate_held_back_its_neighbours()
    {
        if (!fixture.Available)
        {
            return;
        }

        var (accountId, client) = await api.CreateSignedInAccountAsync($"anchored-{Guid.NewGuid():N}"[..24]);
        var genre = $"anchored-{Guid.NewGuid():N}";
        var axis = Random.Shared.Next(300, 590);
        Guid anchorId;
        using (var scope = api.Services.CreateScope())
        {
            using var system = scope.ServiceProvider.GetRequiredService<IAccountContext>().EnterSystemScope();
            var db = scope.ServiceProvider.GetRequiredService<ReelDbContext>();
            (await db.Accounts.SingleAsync(a => a.Id == accountId)).Tier = AccountTier.Paid;
            var titles = Enumerable.Range(0, 11).Select(i => NewTitle(genre, i == 0 ? ["heist"] : ["courtroom"], i)).ToList();
            anchorId = titles[0].Id;
            db.Titles.AddRange(titles);
            db.TitleEmbeddings.AddRange(titles.Select((t, i) => Embedding(t.Id, axis, i)));
            db.ModelArtifacts.Add(Artifact(accountId));
            await db.SaveChangesAsync();
        }

        using var similar = await client.PostAsJsonAsync(
            "/api/feed/more", new { row = "similar", anchorTitleId = anchorId, excludeTitleIds = Array.Empty<Guid>(), limit = 12 });
        Assert.Equal(HttpStatusCode.OK, similar.StatusCode);
        using (var doc = JsonDocument.Parse(await similar.Content.ReadAsStringAsync()))
        {
            var root = doc.RootElement;
            Assert.Equal(0, root.GetProperty("items").GetArrayLength());
            Assert.True(root.GetProperty("exhausted").GetBoolean());
            Assert.Equal(JsonValueKind.Null, root.GetProperty("supply").ValueKind);
            var anchored = root.GetProperty("anchoredSupply");
            Assert.True(anchored.GetProperty("gateHeldBack").GetInt32() > 0);
            Assert.Equal(10, anchored.GetProperty("gateHeldBack").GetInt32() + anchored.GetProperty("belowBar").GetInt32());
        }

        using var genreRail = await client.PostAsJsonAsync(
            "/api/feed/more", new { row = "genre", genre, excludeTitleIds = Array.Empty<Guid>(), limit = 30 });
        Assert.Equal(HttpStatusCode.OK, genreRail.StatusCode);
        using (var doc = JsonDocument.Parse(await genreRail.Content.ReadAsStringAsync()))
        {
            Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("anchoredSupply").ValueKind);
        }
    }

    private static Title NewTitle(string genre, string[] keywords, int i) => new()
    {
        Id = Guid.NewGuid(),
        MediaType = MediaType.Movie,
        TmdbId = Random.Shared.NextInt64(1, long.MaxValue),
        TraktSlug = string.Empty,
        Name = $"anchored {genre} {i} {Guid.NewGuid():N}",
        Language = "en",
        PosterPath = "/p.jpg",
        Genres = [genre],
        Keywords = keywords,
        // Low popularity: never perturbs the shared catalogue's popularity head.
        TmdbPopularity = 1.5m - (i * 0.001m),
        ReleasedAt = DateTime.UtcNow.AddYears(-2),
        CreatedAt = DateTime.UtcNow,
    };

    private static TitleEmbedding Embedding(Guid titleId, int axis, int i)
    {
        var vector = new float[1536];
        vector[axis] = 1f;
        vector[axis + 1] = 0.001f * i; // a hair apart, so ordering is stable
        return new TitleEmbedding
        {
            TitleId = titleId,
            Embedding = new Pgvector.Vector(vector),
            EmbeddingModel = "test",
            SourceTextHash = $"hash-{titleId:N}",
            CreatedAt = DateTime.UtcNow,
        };
    }

    /// <summary>A small real model over random rows, so every card clears scoring the way a live one does.</summary>
    private static ModelArtifact Artifact(Guid accountId)
    {
        var rng = new Random(29);
        var names = FeatureSchema.AllNames();
        var rows = new float[240][];
        var labels = new float[240];
        for (var i = 0; i < rows.Length; i++)
        {
            rows[i] = [.. Enumerable.Range(0, names.Length).Select(_ => (float)rng.NextDouble())];
            labels[i] = Math.Clamp(3f + (5f * rows[i][0]), 1f, 10f);
        }

        var trained = new FastTreeModelEngine().Train(
            new TrainingMatrix(rows, labels, names), """{"Trees":40,"Leaves":8,"MinExamplesPerLeaf":5,"LearningRate":0.2}""");
        return new ModelArtifact
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            Version = 1,
            Algo = "FastTree",
            ArtifactBytes = trained.ArtifactBytes,
            FeatureSchemaJson = JsonSerializer.Serialize(new { version = FeatureSchema.Version, names }),
            TrainingRunId = Guid.NewGuid(),
            Status = ArtifactStatus.Active,
            TrainedAt = DateTime.UtcNow,
        };
    }
}
