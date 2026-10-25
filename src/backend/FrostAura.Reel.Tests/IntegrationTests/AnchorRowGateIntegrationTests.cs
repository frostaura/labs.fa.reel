using FrostAura.Reel.Application.Ranking;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Catalog;
using Microsoft.Extensions.Caching.Memory;

namespace FrostAura.Reel.Tests.IntegrationTests;

/// <summary>
/// <see cref="AnchorRowGate"/> on real pgvector: the floor is the anchor's 50th-nearest-neighbour
/// similarity across the catalogue, a candidate outside that radius is refused however much it
/// shares, and a candidate inside it is refused when it shares nothing checkable.
/// </summary>
[Collection("postgres")]
public class AnchorRowGateIntegrationTests(PostgresFixture fixture) : EmbeddedCatalogTestBase(fixture)
{
    [Fact]
    public async Task Only_a_neighbour_inside_the_fifty_radius_with_shared_evidence_is_admitted()
    {
        if (!Fixture.Available)
        {
            return;
        }

        // A private plane of the 1536-d space (axes 600–899 are used by no other fixture): the
        // anchor on axis a, sixty titles fanned out from it towards axis b, so similarity falls
        // strictly with the index and the anchor's 50 nearest titles are exactly titles 0–49.
        var a = Random.Shared.Next(600, 750);
        var b = a + 150;
        var anchor = NewTitle("anchor", ["heist"]);
        var fan = Enumerable.Range(0, 60)
            .Select(i => NewTitle(
                $"fan {i}",
                // Title 10 sits well inside the radius but shares nothing; every other title shares
                // the anchor's story keyword.
                i == 10 ? ["courtroom"] : ["heist"]))
            .ToList();

        await using (var db = Fixture.CreateContext(new AccountContext()))
        {
            db.Titles.Add(anchor);
            db.Titles.AddRange(fan);
            db.TitleEmbeddings.Add(Embedding(anchor.Id, a, b, angle: 0));
            for (var i = 0; i < fan.Count; i++)
            {
                db.TitleEmbeddings.Add(Embedding(fan[i].Id, a, b, angle: 0.01 + (i * 0.02)));
            }

            await db.SaveChangesAsync();
        }

        await using var read = Fixture.CreateContext(new AccountContext());
        using var cache = new MemoryCache(new MemoryCacheOptions());

        var floor = await AnchorRowGate.NeighbourFloorAsync(read, cache, anchor.Id, CancellationToken.None);
        Assert.NotNull(floor);
        Assert.Equal(Math.Cos(0.01 + (49 * 0.02)), floor!.Value, 4);

        var admissions = await AnchorRowGate.AdmitAsync(
            read, cache, anchor.Id, fan.Select(t => t.Id).ToList(), CancellationToken.None);

        for (var i = 0; i < fan.Count; i++)
        {
            var admission = admissions[fan[i].Id];
            var expected = i < 50 && i != 10;
            Assert.True(expected == admission.Admitted, $"title {i}: expected admitted={expected}");
            if (admission.Admitted)
            {
                Assert.Equal([new SharedEvidence("keyword", "heist")], admission.Evidence);
                Assert.True(admission.Similarity >= floor);
            }
        }

        Assert.Empty(admissions[fan[10].Id].Evidence);
        Assert.True(admissions[fan[55].Id].Similarity < floor); // outside the radius, evidence or not
    }

    [Fact]
    public async Task An_anchor_without_an_embedding_admits_nothing()
    {
        if (!Fixture.Available)
        {
            return;
        }

        var anchor = NewTitle("unembedded anchor", ["heist"]);
        var twin = NewTitle("twin", ["heist"]);
        await using (var db = Fixture.CreateContext(new AccountContext()))
        {
            db.Titles.AddRange(anchor, twin);
            db.TitleEmbeddings.Add(Embedding(twin.Id, 595, 596, angle: 0)); // outside the 600–899 plane above
            await db.SaveChangesAsync();
        }

        await using var read = Fixture.CreateContext(new AccountContext());
        Assert.Null(await AnchorRowGate.NeighbourFloorAsync(read, cache: null, anchor.Id, CancellationToken.None));

        var admissions = await AnchorRowGate.AdmitAsync(read, cache: null, anchor.Id, [twin.Id], CancellationToken.None);
        Assert.False(admissions[twin.Id].Admitted);
        Assert.NotEmpty(admissions[twin.Id].Evidence); // the evidence exists; the radius cannot be stated
    }

    private static Title NewTitle(string name, string[] keywords) => new()
    {
        Id = Guid.NewGuid(),
        MediaType = MediaType.Movie,
        TmdbId = Random.Shared.NextInt64(1, long.MaxValue),
        TraktSlug = string.Empty,
        Name = $"gate {name} {Guid.NewGuid():N}",
        Keywords = keywords,
        Year = 2015,
        // Kept off the popularity head so this never perturbs the collection's other fixtures.
        TmdbPopularity = 0.5m,
        CreatedAt = DateTime.UtcNow,
    };

    private static TitleEmbedding Embedding(Guid titleId, int axisA, int axisB, double angle)
    {
        var vector = new float[1536];
        vector[axisA] = (float)Math.Cos(angle);
        vector[axisB] = (float)Math.Sin(angle);
        return new TitleEmbedding
        {
            TitleId = titleId,
            Embedding = new Pgvector.Vector(vector),
            EmbeddingModel = "test",
            SourceTextHash = $"hash-{titleId:N}",
            CreatedAt = DateTime.UtcNow,
        };
    }
}
