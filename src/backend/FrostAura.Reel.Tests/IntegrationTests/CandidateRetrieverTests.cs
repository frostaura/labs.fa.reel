using FrostAura.Reel.Application.Ml;
using FrostAura.Reel.Application.Ranking;
using FrostAura.Reel.Application.Search;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Filters;
using FrostAura.Reel.Domain.Library;
using FrostAura.Reel.Domain.Tenancy;
using Pgvector;

namespace FrostAura.Reel.Tests.IntegrationTests;

/// <summary>
/// Hybrid retrieval on real Postgres + pgvector: the ANN arm must lift an unseen, low-popularity
/// but taste-similar title into the pool PAST the popularity head (the whole point of the upgrade),
/// while still honouring eligibility — and a cold-start account with no taste centroid must degrade
/// cleanly to pure popularity. Skips when REEL_TEST_CONNECTION_STRING is unset (no DB in the run).
/// </summary>
[Collection("postgres")]
public class CandidateRetrieverTests(PostgresFixture fixture) : EmbeddedCatalogTestBase(fixture)
{
    /// <summary>
    /// A synthetic genre stamped on every title this instance seeds, with a matching IncludeGenre
    /// allowlist row on its account — so the eligibility lane both arms compose sees THIS test's
    /// rows and nothing else.
    ///
    /// <para>This is the fix for the contamination that had two of the three tests here
    /// quarantined as local-only failures. The catalogue is GLOBAL and shared by every class in the
    /// postgres collection, so a sibling class seeding a popular title displaced the popularity
    /// head, and a sibling seeding an embedding became a nearer ANN neighbour than the deep cut
    /// this file plants. Neither symptom was a defect in the retriever, and neither assertion was
    /// wrong — the POOL was wrong. Narrowing the pool to this test's own rows fixes the cause;
    /// nothing about what is asserted has been weakened. xUnit builds one instance per test, so
    /// the scope is unique per test as well as per class.</para>
    /// </summary>
    private readonly string _scope = $"retrieverscope{Guid.NewGuid():N}";

    [Fact]
    public async Task Ann_arm_surfaces_a_similar_unseen_title_past_the_popularity_head()
    {
        if (!Fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();

        // Loved anchor: rated 9 (⇒ excluded from discovery) with an embedding, so BuildTasteState
        // derives a LovedCentroid pointing at dim 0.
        var lovedId = await SeedTitleAsync("Loved Anchor", popularity: 20m, UnitVector(0));
        await SeedLovedRatingAsync(accountId, lovedId);

        // Unseen, LOW popularity, embedding ≈ the centroid ⇒ only the ANN arm can reach it.
        var similarId = await SeedTitleAsync("Similar Deep Cut", popularity: 1m, NearUnit(0, 0.05f));

        // Unseen, HIGH popularity, embedding orthogonal to the centroid ⇒ reached by the pop arm.
        var popularId = await SeedTitleAsync("Popular But Off-Taste", popularity: 999m, UnitVector(1));

        // Watched + embedding near the centroid ⇒ must NEVER appear (eligibility), proving the ANN
        // arm can't leak seen titles even when they're the nearest neighbours.
        var seenId = await SeedTitleAsync("Seen Neighbour", popularity: 5m, NearUnit(0, 0.1f));
        await SeedWatchedAsync(accountId, seenId);

        var result = await RetrieveAsync(accountId, popCount: 1, annCount: 10);
        var ids = result.Select(t => t.Id).ToHashSet();

        // popCount = 1 means the popularity arm alone yields only the single most-popular title
        // (popularId); the similar deep cut can therefore only be present via the ANN arm.
        Assert.Contains(similarId, ids);
        Assert.Contains(popularId, ids);
        Assert.DoesNotContain(lovedId, ids);  // rated ⇒ seen ⇒ excluded
        Assert.DoesNotContain(seenId, ids);   // watched ⇒ excluded
    }

    [Fact]
    public async Task Cold_start_with_no_loved_centroid_falls_back_to_pure_popularity()
    {
        if (!Fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync(); // no ratings ⇒ null LovedCentroid ⇒ ANN arm skipped
        var a = await SeedTitleAsync("Cold A", popularity: 50m, UnitVector(0));
        var b = await SeedTitleAsync("Cold B", popularity: 40m, UnitVector(1));

        var result = await RetrieveAsync(accountId, popCount: 5, annCount: 10);
        var ids = result.Select(t => t.Id).ToHashSet();

        Assert.Contains(a, ids);
        Assert.Contains(b, ids);
    }

    [Fact]
    public async Task RetrieveSimilar_surfaces_unseen_neighbours_and_excludes_the_anchor_and_seen_titles()
    {
        if (!Fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();

        // The VIEWED title — embedding along dim 0. It is the kNN centre; the lane must exclude it so
        // "more like this" never lists the very title being viewed.
        var anchorEmbedding = UnitVector(0);
        var anchorId = await SeedTitleAsync("Viewed Anchor", popularity: 50m, anchorEmbedding);

        // Unseen, embedding ≈ the anchor ⇒ the semantic neighbour the rail exists to surface.
        var similarId = await SeedTitleAsync("Close Neighbour", popularity: 3m, NearUnit(0, 0.05f));

        // Unseen but embedding orthogonal to the anchor ⇒ far in feel; despite huge popularity it must
        // rank BEHIND the neighbour, proving the acquisition is semantic kNN, not a popularity sort.
        var farId = await SeedTitleAsync("Off-Feel But Popular", popularity: 999m, UnitVector(1));

        // Watched neighbour near the anchor ⇒ eligibility must keep it out even though it is a top hit,
        // proving the similarity arm can't leak an already-seen title.
        var seenId = await SeedTitleAsync("Seen Neighbour", popularity: 5m, NearUnit(0, 0.1f));
        await SeedWatchedAsync(accountId, seenId);

        var result = await RetrieveSimilarAsync(accountId, anchorId, anchorEmbedding, count: 10);
        var ids = result.Select(t => t.Id).ToList();

        Assert.Contains(similarId, ids);
        Assert.DoesNotContain(anchorId, ids); // the viewed title itself is never "more like this"
        Assert.DoesNotContain(seenId, ids);   // watched ⇒ excluded by eligibility
        // Semantic order: the close neighbour outranks the far, far-more-popular title.
        Assert.True(ids.IndexOf(similarId) < ids.IndexOf(farId));
    }

    private async Task<IReadOnlyList<Title>> RetrieveSimilarAsync(Guid accountId, Guid anchorId, float[] anchorEmbedding, int count)
    {
        await using var db = Fixture.CreateContext(new AccountContext());
        var retriever = new CandidateRetriever(db, new EligibilityQueryBuilder(db));
        return await retriever.RetrieveSimilarAsync(
            accountId,
            new Vector(anchorEmbedding),
            lane: q => q.Where(t => t.TmdbId != null && t.PosterPath != null && t.Id != anchorId),
            count,
            maturityCeiling: null,
            CancellationToken.None);
    }

    private async Task<IReadOnlyList<Title>> RetrieveAsync(Guid accountId, int popCount, int annCount)
    {
        await using var db = Fixture.CreateContext(new AccountContext());
        var taste = await new FeatureVectorBuilder(db).BuildTasteStateAsync(accountId, DateTime.UtcNow, CancellationToken.None);
        var retriever = new CandidateRetriever(db, new EligibilityQueryBuilder(db));
        return await retriever.RetrieveAsync(
            accountId,
            taste,
            lane: q => q.Where(t => t.TmdbId != null && t.PosterPath != null),
            popCount,
            annCount,
            maturityCeiling: null,
            CancellationToken.None);
    }

    private async Task<Guid> SeedAccountAsync()
    {
        await using var db = Fixture.CreateContext(new AccountContext());
        var account = new Account
        {
            Id = Guid.NewGuid(),
            TraktUserSlug = $"retriever-{Guid.NewGuid():N}",
            CreatedAt = DateTime.UtcNow,
            LastSeenAt = DateTime.UtcNow,
            PipelineStageChangedAt = DateTime.UtcNow,
        };
        db.Add(account);
        db.ContentFilters.Add(new ContentFilter
        {
            Id = Guid.NewGuid(),
            AccountId = account.Id,
            Kind = FilterKind.IncludeGenre,
            Value = _scope,
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        return account.Id;
    }

    private async Task<Guid> SeedTitleAsync(string name, decimal popularity, float[] embedding)
    {
        await using var db = Fixture.CreateContext(new AccountContext());
        var title = new Title
        {
            Id = Guid.NewGuid(),
            MediaType = MediaType.Movie,
            TmdbId = Random.Shared.NextInt64(1, long.MaxValue),
            TraktSlug = string.Empty,
            Name = name,
            Language = "en", // eligibility defaults to English-only when no language filter is configured
            PosterPath = "/p.jpg",
            Genres = ["drama", _scope],
            TmdbPopularity = popularity,
            CreatedAt = DateTime.UtcNow,
        };
        db.Add(title);
        db.TitleEmbeddings.Add(new TitleEmbedding
        {
            TitleId = title.Id,
            Embedding = new Vector(embedding),
            EmbeddingModel = "test",
            SourceTextHash = Guid.NewGuid().ToString("N"),
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        return title.Id;
    }

    private async Task SeedLovedRatingAsync(Guid accountId, Guid titleId)
    {
        await using var db = Fixture.CreateContext(new AccountContext());
        db.UserRatings.Add(new UserRating
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            TitleId = titleId,
            SubjectType = RatingSubjectType.Movie,
            Rating = 9,
            // All-time loved but older than a year, so RecentCentroid stays null and the drift
            // pull is skipped — keeps this test's ANN behaviour to the single LovedCentroid.
            RatedAt = DateTime.UtcNow.AddDays(-400),
            SyncedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private async Task SeedWatchedAsync(Guid accountId, Guid titleId)
    {
        await using var db = Fixture.CreateContext(new AccountContext());
        db.WatchedTitles.Add(new WatchedTitle
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            TitleId = titleId,
            Plays = 1,
            IsFullyWatched = true,
            LastWatchedAt = DateTime.UtcNow,
            FirstSyncedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    /// <summary>A unit 1536-vector pointing along one axis — a clean, well-separated taste direction.</summary>
    private static float[] UnitVector(int axis)
    {
        var v = new float[1536];
        v[axis] = 1f;
        return v;
    }

    /// <summary>A normalized vector close to <paramref name="axis"/> with a small off-axis nudge.</summary>
    private static float[] NearUnit(int axis, float nudge)
    {
        var v = new float[1536];
        v[axis] = 1f;
        v[axis + 1] = nudge;
        var norm = (float)Math.Sqrt(v.Sum(x => (double)x * x));
        for (var i = 0; i < v.Length; i++)
        {
            v[i] /= norm;
        }

        return v;
    }
}
