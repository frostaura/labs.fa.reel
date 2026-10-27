using System.Net;
using System.Text.Json;
using FrostAura.Reel.Application.Ranking;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Library;
using FrostAura.Reel.Domain.Ml;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Pgvector;

namespace FrostAura.Reel.Tests.IntegrationTests;

/// <summary>
/// Claims register, family <c>narrative-and-dna</c> (R1-45), the Taste DNA pockets:
/// "pockets of taste, found from every title you rated 7 or higher or watched twice" (TastePockets)
/// and "N% of your taste weight" (tastePockets.ts). Both TRUE, pinned on real Postgres + pgvector.
///
/// <para>The fixture plants embedded titles, so it follows <see cref="TasteClusterRetrievalTests"/>:
/// the base class removes the embeddings, and a synthetic language keeps these titles out of every
/// other account's English-only default catalogue while they exist.</para>
/// </summary>
[Collection("postgres")]
public class TastePocketClaimsTests(PostgresFixture fixture, ReelApiFactory api)
    : EmbeddedCatalogTestBase(fixture), IClassFixture<ReelApiFactory>
{
    private readonly string _scope = $"pocketclaims{Guid.NewGuid():N}";

    public override async Task DisposeAsync()
    {
        await base.DisposeAsync();
        if (!Fixture.Available)
        {
            return;
        }

        await using var db = Fixture.CreateContext(new AccountContext());
        var titleIds = await db.Titles.Where(t => t.Language == _scope).Select(t => t.Id).ToListAsync();
        await db.UserRatings.Where(r => titleIds.Contains(r.TitleId)).ExecuteDeleteAsync();
        await db.WatchedTitles.Where(w => titleIds.Contains(w.TitleId)).ExecuteDeleteAsync();
        await db.Titles.Where(t => titleIds.Contains(t.Id)).ExecuteDeleteAsync();
    }

    /// <summary>
    /// Members are exactly the titles rated 7 or higher plus the unrated ones played twice or more:
    /// a 6, an unrated single play and an undated rewatch (no evidence of when) contribute nothing.
    /// The shares the DNA page prints are each pocket's weight over the total weight — not its share
    /// of the titles — and sum to the whole.
    /// </summary>
    [Fact]
    public async Task Pockets_are_found_from_the_titles_rated_seven_or_more_or_watched_twice_and_shares_are_weight()
    {
        if (!Fixture.Available)
        {
            return;
        }

        var accountId = await api.SeedAccountAsync($"pocket-claims-{Guid.NewGuid():N}");
        var included = 0;

        // Two directions, rated at the floor and above it, at different ages so the weights differ.
        for (var i = 0; i < 40; i++)
        {
            await RateAsync(accountId, await SeedTitleAsync($"Pocket A {i}", NearUnit(0, 0.01f * (i % 5))), (short)(7 + (i % 3)), daysAgo: 5 + i);
            included++;
        }

        for (var i = 0; i < 16; i++)
        {
            await RateAsync(accountId, await SeedTitleAsync($"Pocket B {i}", NearUnit(9, 0.01f * (i % 4))), 9, daysAgo: 2000);
            included++;
        }

        // Unrated rewatches, dated: members.
        for (var i = 0; i < 6; i++)
        {
            await WatchAsync(accountId, await SeedTitleAsync($"Rewatch {i}", NearUnit(9, 0.02f)), plays: 2 + (i % 2), dated: true);
            included++;
        }

        // Not members: rated 6, an unrated single play, and an undated rewatch.
        for (var i = 0; i < 5; i++)
        {
            await RateAsync(accountId, await SeedTitleAsync($"Six {i}", NearUnit(0, 0.03f)), 6, daysAgo: 5);
            await WatchAsync(accountId, await SeedTitleAsync($"Once {i}", NearUnit(9, 0.03f)), plays: 1, dated: true);
            await WatchAsync(accountId, await SeedTitleAsync($"Undated {i}", NearUnit(0, 0.04f)), plays: 3, dated: false);
        }

        await using var db = Fixture.CreateContext(new AccountContext());
        var partition = await new TasteClusterProvider(db, new MemoryCache(new MemoryCacheOptions()), NullLogger<TasteClusterProvider>.Instance)
            .GetAsync(accountId, DateTime.UtcNow, CancellationToken.None);

        Assert.Equal(included, partition.Sum(c => c.MemberCount));
        Assert.True(partition.Count >= 2, $"expected at least two pockets, got {partition.Count}");

        using var client = await api.CreateSignedInClientAsync(accountId);
        using var response = await client.GetAsync("/api/taste/");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var clusters = json.RootElement.GetProperty("clusters").EnumerateArray().ToList();

        var totalWeight = partition.Sum(c => c.Weight);
        Assert.Equal(partition.Count, clusters.Count);
        Assert.Equal(1d, clusters.Sum(c => c.GetProperty("share").GetDouble()), 2);
        foreach (var cluster in clusters)
        {
            var source = partition.Single(c => c.Ordinal == cluster.GetProperty("ordinal").GetInt32());
            Assert.Equal(source.MemberCount, cluster.GetProperty("titleCount").GetInt32());
            Assert.Equal(source.Weight / totalWeight, cluster.GetProperty("share").GetDouble(), 2);
        }

        // Weight, not size: pocket B was loved five years ago, so it holds far less weight than titles.
        Assert.Contains(clusters, c => Math.Abs(
            c.GetProperty("share").GetDouble() - ((double)c.GetProperty("titleCount").GetInt32() / included)) > 0.02);
    }

    private async Task<Guid> SeedTitleAsync(string name, float[] embedding)
    {
        await using var db = Fixture.CreateContext(new AccountContext());
        var title = new Title
        {
            Id = Guid.NewGuid(),
            MediaType = MediaType.Movie,
            TmdbId = Random.Shared.NextInt64(1, long.MaxValue),
            TraktSlug = string.Empty,
            Name = name,
            Language = _scope,
            PosterPath = "/p.jpg",
            Genres = [_scope],
            CreatedAt = DateTime.UtcNow.AddDays(-500),
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

    private async Task RateAsync(Guid accountId, Guid titleId, short rating, int daysAgo)
    {
        await using var db = Fixture.CreateContext(new AccountContext());
        db.UserRatings.Add(new UserRating
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            TitleId = titleId,
            SubjectType = RatingSubjectType.Movie,
            Rating = rating,
            RatedAt = DateTime.UtcNow.AddDays(-daysAgo),
            SyncedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private async Task WatchAsync(Guid accountId, Guid titleId, int plays, bool dated)
    {
        await using var db = Fixture.CreateContext(new AccountContext());
        db.WatchedTitles.Add(new WatchedTitle
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            TitleId = titleId,
            Plays = plays,
            IsFullyWatched = true,
            LastWatchedAt = dated ? DateTime.UtcNow.AddDays(-30) : null,
            FirstSyncedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

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
