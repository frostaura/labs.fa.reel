using FrostAura.Reel.Application.Ranking;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Catalog;
using Microsoft.Extensions.Caching.Memory;

namespace FrostAura.Reel.Tests.IntegrationTests;

/// <summary>
/// <see cref="AnchorRowGate"/> on real pgvector: the floor is the anchor's 50th-nearest comparable
/// neighbour's similarity, a candidate outside that radius is refused unless two shared items carry
/// it in above chance, and a candidate inside it is refused when it shares nothing checkable.
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

    /// <summary>
    /// HREV2-RET-05: the floor is read over COMPARABLE titles. Fifty decoys sit nearer the anchor than
    /// any real neighbour — name twins ("Life as a {anchor}"), titles of the other medium, or a mix —
    /// and set the old floor so high that every real neighbour was refused (House: 33 twins and 35
    /// movies in its top 50 put Grey's Anatomy, 0.429, under a 0.433 floor). With fewer than fifty
    /// comparable titles in reach the floor falls back to the stricter fifty-of-everything.
    /// </summary>
    [Theory]
    [InlineData("twins", 60)]
    [InlineData("other-medium", 60)]
    [InlineData("mixed", 60)]
    [InlineData("mixed", 20)]
    public async Task The_floor_is_the_fiftieth_comparable_neighbour_not_a_name_twin_or_another_medium(string decoys, int comparable)
    {
        if (!Fixture.Available)
        {
            return;
        }

        var plane = Plane.Random();
        var anchorWord = $"Hx{Guid.NewGuid():N}"[..12];
        var anchor = NewTitle("unused", ["hospital"], MediaType.Show, exactName: anchorWord);
        var decoyTitles = Enumerable.Range(0, 50).Select(i => decoys switch
        {
            "twins" => NewTitle("unused", ["hospital"], MediaType.Show, exactName: $"Life as a {anchorWord} {i}"),
            "other-medium" => NewTitle($"film {i}", ["hospital"], MediaType.Movie),
            _ => i % 2 == 0
                ? NewTitle("unused", ["hospital"], i % 4 == 0 ? MediaType.Movie : MediaType.Show, exactName: $"{anchorWord}: The Return {i}")
                : NewTitle($"film {i}", ["hospital"], MediaType.Movie),
        }).ToList();
        var fan = Enumerable.Range(0, comparable).Select(i => NewTitle($"ward {i}", ["hospital"], MediaType.Show)).ToList();

        await using (var db = Fixture.CreateContext(new AccountContext()))
        {
            db.Titles.Add(anchor);
            db.Titles.AddRange(decoyTitles);
            db.Titles.AddRange(fan);
            db.TitleEmbeddings.Add(plane.Embedding(anchor.Id, 0));
            for (var i = 0; i < decoyTitles.Count; i++)
            {
                db.TitleEmbeddings.Add(plane.Embedding(decoyTitles[i].Id, 0.001 + (i * 0.0001)));
            }

            for (var i = 0; i < fan.Count; i++)
            {
                db.TitleEmbeddings.Add(plane.Embedding(fan[i].Id, 0.05 + (i * 0.02)));
            }

            await db.SaveChangesAsync();
        }

        await using var read = Fixture.CreateContext(new AccountContext());
        var floor = await AnchorRowGate.NeighbourFloorAsync(read, cache: null, anchor.Id, CancellationToken.None);
        var admissions = await AnchorRowGate.AdmitAsync(read, cache: null, anchor.Id, [.. fan.Select(t => t.Id)], CancellationToken.None);

        if (comparable >= AnchorRowGate.NeighbourRank)
        {
            Assert.Equal(Math.Cos(0.05 + (49 * 0.02)), floor!.Value, 4);
            Assert.All(fan.Take(50), t => Assert.True(admissions[t.Id].Admitted));
            Assert.All(fan.Skip(50), t => Assert.False(admissions[t.Id].Admitted));
        }
        else
        {
            // Twenty comparable titles cannot state a fifty-neighbour radius, so the floor is the
            // fiftieth of everything — here the last decoy — and the stricter rule holds.
            Assert.Equal(Math.Cos(0.001 + (49 * 0.0001)), floor!.Value, 4);
            Assert.All(fan, t => Assert.False(admissions[t.Id].Admitted));
        }
    }

    /// <summary>
    /// HREV2-RET-05: two shared items carry a candidate in below the neighbour floor, down to the
    /// similarity of a random pair's 90th percentile — and not one shared item, and not below it.
    /// </summary>
    [Fact]
    public async Task Two_shared_items_admit_below_the_floor_but_never_below_chance()
    {
        if (!Fixture.Available)
        {
            return;
        }

        var plane = Plane.Random();
        var anchor = NewTitle("strong anchor", ["safecracker", "heist"]);
        var fan = Enumerable.Range(0, 50).Select(i => NewTitle($"strong fan {i}", ["heist"])).ToList();
        // Outside the fifty radius: cos(1.15) = 0.408 and cos(1.25) = 0.315, either side of 0.364.
        var twoItemsInside = NewTitle("two items above chance", ["heist", "safecracker"]);
        var oneItemInside = NewTitle("one item above chance", ["heist"]);
        var twoItemsBelow = NewTitle("two items below chance", ["heist", "safecracker"]);

        await using (var db = Fixture.CreateContext(new AccountContext()))
        {
            db.Titles.AddRange([anchor, .. fan, twoItemsInside, oneItemInside, twoItemsBelow]);
            db.TitleEmbeddings.Add(plane.Embedding(anchor.Id, 0));
            for (var i = 0; i < fan.Count; i++)
            {
                db.TitleEmbeddings.Add(plane.Embedding(fan[i].Id, 0.01 + (i * 0.02)));
            }

            db.TitleEmbeddings.Add(plane.Embedding(twoItemsInside.Id, 1.15));
            db.TitleEmbeddings.Add(plane.Embedding(oneItemInside.Id, 1.15));
            db.TitleEmbeddings.Add(plane.Embedding(twoItemsBelow.Id, 1.25));
            await db.SaveChangesAsync();
        }

        await using var read = Fixture.CreateContext(new AccountContext());
        var floor = await AnchorRowGate.NeighbourFloorAsync(read, cache: null, anchor.Id, CancellationToken.None);
        var admissions = await AnchorRowGate.AdmitAsync(
            read, cache: null, anchor.Id, [twoItemsInside.Id, oneItemInside.Id, twoItemsBelow.Id], CancellationToken.None);

        Assert.True(admissions[twoItemsInside.Id].Similarity < floor);
        Assert.True(admissions[twoItemsInside.Id].Admitted);
        Assert.Equal(2, admissions[twoItemsInside.Id].Evidence.Count);
        Assert.False(admissions[oneItemInside.Id].Admitted);
        Assert.False(admissions[twoItemsBelow.Id].Admitted);
    }

    /// <summary>
    /// A random plane of the embedding space: two orthonormal random directions, so a title at angle
    /// θ has similarity cos(θ₁ − θ₂) to another on the plane and about zero to every other fixture
    /// in the shared database, whatever axes those use and however many earlier runs left behind.
    /// </summary>
    private sealed class Plane(float[] u, float[] v)
    {
        public static Plane Random()
        {
            var u = Gaussian();
            var v = Gaussian();
            var dot = u.Zip(v, (x, y) => (double)x * y).Sum();
            for (var i = 0; i < v.Length; i++)
            {
                v[i] -= (float)(dot * u[i]);
            }

            Normalise(v);
            return new Plane(u, v);
        }

        public TitleEmbedding Embedding(Guid titleId, double angle) => new()
        {
            TitleId = titleId,
            Embedding = new Pgvector.Vector(u.Zip(v, (x, y) => (float)((Math.Cos(angle) * x) + (Math.Sin(angle) * y))).ToArray()),
            EmbeddingModel = "test",
            SourceTextHash = $"hash-{titleId:N}",
            CreatedAt = DateTime.UtcNow,
        };

        private static float[] Gaussian()
        {
            var vector = new float[1536];
            for (var i = 0; i < vector.Length; i++)
            {
                vector[i] = (float)(Math.Sqrt(-2 * Math.Log(1 - System.Random.Shared.NextDouble()))
                    * Math.Cos(2 * Math.PI * System.Random.Shared.NextDouble()));
            }

            Normalise(vector);
            return vector;
        }

        private static void Normalise(float[] vector)
        {
            var norm = Math.Sqrt(vector.Sum(x => (double)x * x));
            for (var i = 0; i < vector.Length; i++)
            {
                vector[i] = (float)(vector[i] / norm);
            }
        }
    }

    private static Title NewTitle(string name, string[] keywords, MediaType media = MediaType.Movie, string? exactName = null) => new()
    {
        Id = Guid.NewGuid(),
        MediaType = media,
        TmdbId = Random.Shared.NextInt64(1, long.MaxValue),
        TraktSlug = string.Empty,
        Name = exactName ?? $"gate {name} {Guid.NewGuid():N}",
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
