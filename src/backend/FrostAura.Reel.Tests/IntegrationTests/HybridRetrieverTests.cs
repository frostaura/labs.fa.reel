using FrostAura.Reel.Application.Search;
using FrostAura.Reel.Application.Search.Retrieval;
using FrostAura.Reel.Application.Search.Retrieval.Concepts;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Library;
using FrostAura.Reel.Domain.Tenancy;
using FrostAura.Reel.Infrastructure.Persistence;
using FrostAura.Reel.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Pgvector;

namespace FrostAura.Reel.Tests.IntegrationTests;

/// <summary>
/// The hybrid retriever on real Postgres (program §4.7): concepts resolve through the vocabulary and
/// its synonyms, the full-text arm finds what the tags miss, facets act as filters on the semantic
/// arm, and every arm stays inside eligibility, the turn's own rejections and the exclude list.
/// Every assertion is about the titles this class seeded; the shared catalogue can only add
/// candidates around them, never change what these ones are. Embedding rows are removed and the
/// vector index vacuumed on teardown (<see cref="EmbeddedCatalogTestBase"/>).
/// </summary>
[Collection("postgres")]
public class HybridRetrieverTests(PostgresFixture fixture) : EmbeddedCatalogTestBase(fixture)
{
    [Fact]
    public async Task A_concept_request_returns_only_titles_with_catalogue_evidence()
    {
        if (!Fixture.Available)
        {
            return;
        }

        var tag = Guid.NewGuid().ToString("N")[..8];
        var accountId = await SeedAccountAsync();
        var conceptVector = StubVector("ninja");

        var tagged = await SeedTitleAsync(t => t.Keywords = ["female ninja"], conceptVector);
        var synonym = await SeedTitleAsync(t => t.Keywords = ["kunoichi"]);
        var overviewOnly = await SeedTitleAsync(t => t.Overview = $"A young shinobi {tag} trains in secret.");
        var nearButOffTopic = await SeedTitleAsync(t => t.Keywords = ["office"], conceptVector);
        var watched = await SeedTitleAsync(t => t.Keywords = ["ninja"]);
        var comedy = await SeedTitleAsync(t => { t.Keywords = ["ninja"]; t.Genres = ["comedy"]; });
        var shown = await SeedTitleAsync(t => t.Keywords = ["ninja"]);
        await SeedWatchAsync(accountId, watched);

        var hard = new HardConstraints([new ConceptTerm("ninja movies", [], [])], [], null, null, MediaScope.Any);
        var result = await RetrieveAsync(new RetrievalQuery(accountId, hard, SoftPreferences.None, [shown], 200)
        {
            Arms = RetrievalArms.Catalogue | RetrievalArms.Semantic,
            ExcludeGenres = ["comedy"],
        });
        var byId = result.Candidates.ToDictionary(c => c.TitleId);

        Assert.Contains(tagged, byId.Keys);
        Assert.Contains("keyword:female ninja", byId[tagged].Evidence);
        Assert.NotNull(byId[tagged].Similarity);
        Assert.Contains(synonym, byId.Keys);
        Assert.Contains("keyword:kunoichi", byId[synonym].Evidence);
        Assert.Contains(overviewOnly, byId.Keys);
        Assert.Contains("fts:overview", byId[overviewOnly].Evidence);

        // The semantic arm ranks; it never admits. The nearest title of all carries no ninja evidence.
        Assert.DoesNotContain(nearButOffTopic, byId.Keys);
        Assert.DoesNotContain(watched, byId.Keys);
        Assert.DoesNotContain(comedy, byId.Keys);
        Assert.DoesNotContain(shown, byId.Keys);
        Assert.All(result.Candidates, c => Assert.Contains(c.Evidence, e => e.StartsWith("keyword:", StringComparison.Ordinal)
            || e.StartsWith("name:", StringComparison.Ordinal) || e.StartsWith("genre:", StringComparison.Ordinal)
            || e.StartsWith("fts:", StringComparison.Ordinal)));
        Assert.Equal(result.Candidates.OrderByDescending(c => c.FusedScore).Select(c => c.TitleId), result.Candidates.Select(c => c.TitleId));
        Assert.Empty(result.SkippedArms);
    }

    [Fact]
    public async Task A_mood_without_a_concept_draws_the_semantic_neighbours_from_the_eligible_pool()
    {
        if (!Fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        var mood = $"wistful {Guid.NewGuid():N}";
        var near = await SeedTitleAsync(t => t.Keywords = ["office"], StubVector(mood));
        var nearButWatched = await SeedTitleAsync(t => t.Keywords = ["office"], StubVector(mood));
        await SeedWatchAsync(accountId, nearButWatched);

        var result = await RetrieveAsync(new RetrievalQuery(
            accountId, HardConstraints.None, new SoftPreferences([mood], [], []), [], 10)
        {
            Arms = RetrievalArms.Semantic,
        });

        Assert.Equal(near, result.Candidates[0].TitleId);
        Assert.True(result.Candidates[0].Similarity > 0.999);
        Assert.DoesNotContain(nearButWatched, result.Candidates.Select(c => c.TitleId));
    }

    [Fact]
    public async Task The_resolver_reaches_synonyms_plurals_and_genre_words_through_the_vocabulary()
    {
        if (!Fixture.Available)
        {
            return;
        }

        await SeedTitleAsync(t => t.Keywords = ["shinobi no mono", "ninja warrior", "time loop", "courtroom drama", "shipwreck", "heist", "android", "volcano"]);
        await using var db = Fixture.CreateContext(new AccountContext());
        var cache = new MemoryCache(new MemoryCacheOptions());
        var resolver = new ConceptResolver(
            new KeywordVocabulary(db, cache),
            new ConceptEmbeddings(new StubEmbeddingProvider(), cache, NullLogger<ConceptEmbeddings>.Instance),
            db,
            NullLogger<ConceptResolver>.Instance);

        var ninja = await resolver.ResolveAsync(new ConceptTerm("Ninja movies", [], []), default);
        Assert.Contains("ninja warrior", ninja.ExactLabels!);
        Assert.Contains("shinobi no mono", ninja.ExactLabels!);
        Assert.Contains("shinobi", ninja.CatalogueLabels);

        var heists = await resolver.ResolveAsync(new ConceptTerm("heists", [], []), default);
        Assert.Contains("heist", heists.ExactLabels!);

        Assert.Contains("time loop", (await resolver.ResolveAsync(new ConceptTerm("a time loop film", [], []), default)).ExactLabels!);
        Assert.Contains("courtroom drama", (await resolver.ResolveAsync(new ConceptTerm("courtroom", [], []), default)).ExactLabels!);
        Assert.Contains("shipwreck", (await resolver.ResolveAsync(new ConceptTerm("survival at sea", [], []), default)).ExactLabels!);

        // A phrase no label carries falls back to its words and their synonyms.
        Assert.Contains("android", (await resolver.ResolveAsync(new ConceptTerm("ai robot movies", [], []), default)).ExactLabels!);
        Assert.Contains("volcano", (await resolver.ResolveAsync(new ConceptTerm("disaster", [], []), default)).ExactLabels!);

        var scifi = await resolver.ResolveAsync(new ConceptTerm("Sci-Fi & Fantasy", [], []), default);
        Assert.Equal(["science-fiction", "fantasy"], scifi.Genres);
    }

    private async Task<RetrievalResult> RetrieveAsync(RetrievalQuery query)
    {
        await using var db = Fixture.CreateContext(new AccountContext());
        var cache = new MemoryCache(new MemoryCacheOptions());
        var eligibility = new EligibilityQueryBuilder(db);
        var embeddings = new ConceptEmbeddings(new StubEmbeddingProvider(), cache, NullLogger<ConceptEmbeddings>.Instance);
        var retriever = new HybridRetriever(
            db,
            eligibility,
            new SupplyReporter(db, eligibility),
            new ConceptResolver(new KeywordVocabulary(db, cache), embeddings, db, NullLogger<ConceptResolver>.Instance),
            new FullTextArm(db),
            embeddings,
            new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            NullLogger<HybridRetriever>.Instance);
        return await retriever.RetrieveAsync(query, default);
    }

    /// <summary>The vector <see cref="StubEmbeddingProvider"/> returns for <paramref name="text"/>.</summary>
    private static float[] StubVector(string text) =>
        new StubEmbeddingProvider().EmbedAsync([text.Trim().ToLowerInvariant()]).GetAwaiter().GetResult()[0];

    private async Task<Guid> SeedAccountAsync()
    {
        await using var db = Fixture.CreateContext(new AccountContext());
        var account = new Account
        {
            Id = Guid.NewGuid(),
            TraktUserSlug = $"hybrid-{Guid.NewGuid():N}",
            Tier = AccountTier.Paid,
            CreatedAt = DateTime.UtcNow,
            LastSeenAt = DateTime.UtcNow,
            PipelineStageChangedAt = DateTime.UtcNow,
        };
        db.Add(account);
        await db.SaveChangesAsync();
        return account.Id;
    }

    private async Task SeedWatchAsync(Guid accountId, Guid titleId)
    {
        await using var db = Fixture.CreateContext(new AccountContext());
        db.Add(new WatchedTitle
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            TitleId = titleId,
            Plays = 1,
            LastWatchedAt = DateTime.UtcNow,
            IsFullyWatched = true,
            FirstSyncedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private async Task<Guid> SeedTitleAsync(Action<Title> shape, float[]? vector = null)
    {
        await using var db = Fixture.CreateContext(new AccountContext());
        var title = new Title
        {
            Id = Guid.NewGuid(),
            MediaType = MediaType.Movie,
            TmdbId = Random.Shared.NextInt64(1, long.MaxValue),
            TraktSlug = string.Empty,
            Name = $"hybrid-fixture {Guid.NewGuid():N}",
            Language = "en",
            Genres = ["action"],
            Year = 2015,
            TmdbVoteAverage = 7.5m,
            TmdbVoteCount = 900,
            TmdbPopularity = 0m,
            CreatedAt = DateTime.UtcNow,
        };
        shape(title);
        db.Add(title);
        if (vector is not null)
        {
            db.Add(new TitleEmbedding
            {
                TitleId = title.Id,
                Embedding = new Vector(vector),
                EmbeddingModel = "text-embedding-3-small",
                SourceTextHash = "fixture",
                CreatedAt = DateTime.UtcNow,
            });
        }

        await db.SaveChangesAsync();
        return title.Id;
    }
}
