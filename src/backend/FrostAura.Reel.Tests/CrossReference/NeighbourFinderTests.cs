using FrostAura.Reel.Application.CrossReference;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Library;
using FrostAura.Reel.Domain.Tenancy;
using FrostAura.Reel.Tests.IntegrationTests;
using Pgvector;

namespace FrostAura.Reel.Tests.CrossReference;

/// <summary>
/// The LATERAL kNN against real pgvector. Raw SQL, so none of EF's guarantees apply and every one
/// of them has to be asserted here instead — including the tenancy filter, which for this query
/// exists only because the predicate is written out in the query text.
/// </summary>
[Collection("postgres")]
public class NeighbourFinderTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Neighbours_come_back_in_descending_similarity_with_the_viewers_own_rating()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        var candidateId = await SeedTitleAsync("Candidate", UnitVector(0));
        var nearId = await SeedRatedAsync(accountId, "Near", NearUnit(0, 0.05f), 9);
        var midId = await SeedRatedAsync(accountId, "Mid", NearUnit(0, 0.40f), 6);
        var farId = await SeedRatedAsync(accountId, "Far", UnitVector(1), 2);

        var result = await FindAsync(accountId, [candidateId], new NeighbourFinder.Options(K: 3));
        var neighbours = result[candidateId];

        Assert.Equal([nearId, midId, farId], neighbours.Select(n => n.TitleId));
        Assert.Equal(9, neighbours[0].Rating);
        Assert.True(neighbours[0].Similarity > neighbours[1].Similarity);
        Assert.InRange(neighbours[0].Similarity, 0.9d, 1.0d);
    }

    [Fact]
    public async Task A_candidate_never_appears_as_its_own_neighbour()
    {
        if (!fixture.Available)
        {
            return;
        }

        // The candidate pool and the rated pool overlap in the backtest: a holdout title is rated
        // (later) AND judged. Without the self-exclusion it would score cosine 1.0 against itself
        // and the judge would be shown the answer.
        var accountId = await SeedAccountAsync();
        var candidateId = await SeedRatedAsync(accountId, "Rated Candidate", UnitVector(0), 10);
        var otherId = await SeedRatedAsync(accountId, "Other", NearUnit(0, 0.3f), 7);

        var result = await FindAsync(accountId, [candidateId], new NeighbourFinder.Options(K: 3));

        Assert.DoesNotContain(candidateId, result[candidateId].Select(n => n.TitleId));
        Assert.Contains(otherId, result[candidateId].Select(n => n.TitleId));
    }

    [Fact]
    public async Task Skip_moves_the_window_down_the_ranking()
    {
        if (!fixture.Available)
        {
            return;
        }

        // Round 2 shows ranks 4-6 so that a second keep is a second look at different evidence,
        // not the same prompt asked twice.
        var accountId = await SeedAccountAsync();
        var candidateId = await SeedTitleAsync("Candidate", UnitVector(0));
        var ranked = new List<Guid>();
        for (var i = 0; i < 6; i++)
        {
            ranked.Add(await SeedRatedAsync(accountId, $"N{i}", NearUnit(0, 0.05f * (i + 1)), 8));
        }

        var first = await FindAsync(accountId, [candidateId], new NeighbourFinder.Options(K: 3));
        var second = await FindAsync(
            accountId, [candidateId], new NeighbourFinder.Options(K: 3, Skip: NeighbourFinder.SecondRoundSkip));

        Assert.Equal(ranked.Take(3), first[candidateId].Select(n => n.TitleId));
        Assert.Equal(ranked.Skip(3).Take(3), second[candidateId].Select(n => n.TitleId));
        Assert.Empty(first[candidateId].Select(n => n.TitleId).Intersect(second[candidateId].Select(n => n.TitleId)));
    }

    [Fact]
    public async Task A_candidate_with_no_embedding_is_simply_absent_from_the_map()
    {
        if (!fixture.Available)
        {
            return;
        }

        // Absent, not an empty list with a label: the prompt builder reads a miss as "no neighbour
        // clause", which is the honest rendering.
        var accountId = await SeedAccountAsync();
        var embedded = await SeedTitleAsync("Embedded", UnitVector(0));
        var bare = await SeedTitleAsync("No Embedding", embedding: null);
        await SeedRatedAsync(accountId, "Anchor", NearUnit(0, 0.1f), 9);

        var result = await FindAsync(accountId, [embedded, bare], new NeighbourFinder.Options(K: 3));

        Assert.True(result.ContainsKey(embedded));
        Assert.False(result.ContainsKey(bare));
    }

    [Fact]
    public async Task Another_accounts_ratings_are_never_neighbours()
    {
        if (!fixture.Available)
        {
            return;
        }

        // Raw SQL bypasses the global query filter entirely; this passes only because the
        // AccountId predicate is written out in the query text. If someone "simplifies" it away,
        // this is the test that fails.
        var mine = await SeedAccountAsync();
        var theirs = await SeedAccountAsync();
        var candidateId = await SeedTitleAsync("Candidate", UnitVector(0));
        var theirsRated = await SeedRatedAsync(theirs, "Their Rating", NearUnit(0, 0.01f), 10);
        var mineRated = await SeedRatedAsync(mine, "My Rating", NearUnit(0, 0.30f), 7);

        var result = await FindAsync(mine, [candidateId], new NeighbourFinder.Options(K: 5));
        var ids = result[candidateId].Select(n => n.TitleId).ToList();

        Assert.Contains(mineRated, ids);
        Assert.DoesNotContain(theirsRated, ids);
    }

    [Fact]
    public async Task Episode_and_season_ratings_are_not_neighbours()
    {
        if (!fixture.Available)
        {
            return;
        }

        // Movie/show-level ratings are the label-bearing events everywhere else in this codebase;
        // an episode rating is not a verdict on a title.
        var accountId = await SeedAccountAsync();
        var candidateId = await SeedTitleAsync("Candidate", UnitVector(0));
        var episodeRated = await SeedRatedAsync(accountId, "Episode Rating", NearUnit(0, 0.01f), 10, RatingSubjectType.Episode);
        var titleRated = await SeedRatedAsync(accountId, "Title Rating", NearUnit(0, 0.30f), 7);

        var result = await FindAsync(accountId, [candidateId], new NeighbourFinder.Options(K: 5));
        var ids = result[candidateId].Select(n => n.TitleId).ToList();

        Assert.Contains(titleRated, ids);
        Assert.DoesNotContain(episodeRated, ids);
    }

    [Fact]
    public async Task The_nearest_disliked_neighbour_is_appended_only_when_it_is_near_enough()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();

        // A disliked title genuinely close to the candidate: real evidence for round 2.
        var closeCandidate = await SeedTitleAsync("Close Case", UnitVector(0));
        var closeDisliked = await SeedRatedAsync(accountId, "Close Disliked", NearUnit(0, 0.20f), 2);
        for (var i = 0; i < 3; i++)
        {
            await SeedRatedAsync(accountId, $"Loved {i}", NearUnit(0, 0.01f * (i + 1)), 9);
        }

        var withDisliked = await FindAsync(
            accountId, [closeCandidate], new NeighbourFinder.Options(K: 3, IncludeNearestDisliked: true));

        // Four: the three ranked, plus the disliked one that none of them was.
        Assert.Equal(4, withDisliked[closeCandidate].Count);
        Assert.Equal(closeDisliked, withDisliked[closeCandidate][^1].TitleId);
        Assert.True(withDisliked[closeCandidate][^1].Similarity >= NeighbourFinder.DislikedMinimumSimilarity);

        // …and a disliked title on the far side of the space is a coincidence, not a comparison.
        var farCandidate = await SeedTitleAsync("Far Case", UnitVector(5));
        var farOnly = await FindAsync(
            accountId, [farCandidate], new NeighbourFinder.Options(K: 3, IncludeNearestDisliked: true));
        Assert.DoesNotContain(
            farOnly.GetValueOrDefault(farCandidate, []),
            n => n.TitleId == closeDisliked && n.Similarity < NeighbourFinder.DislikedMinimumSimilarity);
    }

    [Fact]
    public async Task An_empty_batch_asks_the_database_nothing()
    {
        if (!fixture.Available)
        {
            return;
        }

        var result = await FindAsync(Guid.NewGuid(), [], new NeighbourFinder.Options());
        Assert.Empty(result);
    }

    private async Task<IReadOnlyDictionary<Guid, IReadOnlyList<Domain.Ports.CrossReferenceNeighbour>>> FindAsync(
        Guid accountId, IReadOnlyList<Guid> ids, NeighbourFinder.Options options)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        return await new NeighbourFinder(db).FindAsync(accountId, ids, options, CancellationToken.None);
    }

    private static float[] UnitVector(int index)
    {
        var v = new float[1536];
        v[index] = 1f;
        return v;
    }

    private static float[] NearUnit(int index, float noise)
    {
        var v = UnitVector(index);
        v[(index + 1) % v.Length] = noise;
        return v;
    }

    private async Task<Guid> SeedAccountAsync()
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var account = new Account
        {
            Id = Guid.NewGuid(),
            TraktUserSlug = $"xrefknn-{Guid.NewGuid():N}",
            CreatedAt = DateTime.UtcNow,
            LastSeenAt = DateTime.UtcNow,
            PipelineStageChangedAt = DateTime.UtcNow,
        };
        db.Add(account);
        await db.SaveChangesAsync();
        return account.Id;
    }

    private async Task<Guid> SeedTitleAsync(string name, float[]? embedding)
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
            TmdbPopularity = 1m,
            CreatedAt = DateTime.UtcNow,
        };
        db.Add(title);
        if (embedding is not null)
        {
            db.TitleEmbeddings.Add(new TitleEmbedding
            {
                TitleId = title.Id,
                Embedding = new Vector(embedding),
                EmbeddingModel = "test",
                SourceTextHash = Guid.NewGuid().ToString("N"),
                CreatedAt = DateTime.UtcNow,
            });
        }

        await db.SaveChangesAsync();
        return title.Id;
    }

    private async Task<Guid> SeedRatedAsync(
        Guid accountId, string name, float[] embedding, short rating,
        RatingSubjectType subjectType = RatingSubjectType.Movie)
    {
        var titleId = await SeedTitleAsync(name, embedding);
        await using var db = fixture.CreateContext(new AccountContext());
        var at = DateTime.UtcNow.AddDays(-10);
        db.UserRatings.Add(new UserRating
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            TitleId = titleId,
            SubjectType = subjectType,
            Rating = rating,
            RatedAt = at,
            FirstRatedAt = at,
            SyncedAt = at,
        });
        await db.SaveChangesAsync();
        return titleId;
    }
}
