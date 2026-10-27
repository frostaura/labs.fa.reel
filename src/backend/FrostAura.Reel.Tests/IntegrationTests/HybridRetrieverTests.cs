using FrostAura.Reel.Application.Search;
using FrostAura.Reel.Application.Search.Retrieval;
using FrostAura.Reel.Application.Search.Retrieval.Concepts;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Filters;
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
    public async Task The_facet_arm_ranks_by_the_vote_shrunk_crowd_score()
    {
        if (!Fixture.Available)
        {
            return;
        }

        // Task 4 (D-12): a 10.0 on 2 votes ranks below a 7.0 on 5,000, and a widely-voted 5.0 below
        // both — most-voted-first would have led with it.
        var label = $"facetrank{Guid.NewGuid():N}"[..20];
        var accountId = await SeedAccountAsync();
        var thin = await SeedTitleAsync(t => { t.Keywords = [label]; t.TmdbVoteAverage = 10m; t.TmdbVoteCount = 2; });
        var solid = await SeedTitleAsync(t => { t.Keywords = [label]; t.TmdbVoteAverage = 7m; t.TmdbVoteCount = 5_000; });
        var popularButPoor = await SeedTitleAsync(t => { t.Keywords = [label]; t.TmdbVoteAverage = 5m; t.TmdbVoteCount = 9_000; });

        var result = await RetrieveAsync(new RetrievalQuery(
            accountId, new HardConstraints([new ConceptTerm(label, [], [])], [], null, null, MediaScope.Any), SoftPreferences.None, [], 10)
        {
            Arms = RetrievalArms.Facet,
        });

        Assert.Equal([solid, thin, popularButPoor], result.Candidates.Select(c => c.TitleId));
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

    [Fact]
    public async Task A_genre_word_is_matched_as_the_genre_never_as_a_keyword_or_name_containing_it()
    {
        if (!Fixture.Available)
        {
            return;
        }

        // HREV1-RET-06: "romantic comedies" resolved "comedy" to every label containing the word, so How
        // to Build a Better Boy ({family, science-fiction}, keyword "teen comedy") was counted as the
        // founder's one "unseen comedy" while eligibility did not call it a comedy at all. A unique topic
        // concept scopes every count to this test's titles on the shared catalogue.
        var topic = $"genreword{Guid.NewGuid():N}";
        var tagged = await SeedTitleAsync(t => { t.Genres = ["comedy", "romance"]; t.Keywords = [topic]; });
        var teenComedy = await SeedTitleAsync(t =>
        {
            t.Genres = ["family", "science-fiction"];
            t.Keywords = [topic, "teen comedy", "teenage romance"];
        });
        var namedComedy = await SeedTitleAsync(t =>
        {
            t.Name = $"A Romantic Comedy of Errors {Guid.NewGuid():N}";
            t.Genres = ["drama"];
            t.Keywords = [topic];
        });
        // TV romance is judged as an exclusion reads it: the soap alias and the overview stand-in count;
        // the same overview on a FILM does not (TMDB tags films with romance itself).
        var soapShow = await SeedTitleAsync(t => { t.MediaType = MediaType.Show; t.Genres = ["comedy", "soap"]; t.Keywords = [topic]; });
        var overviewShow = await SeedTitleAsync(t =>
        {
            t.MediaType = MediaType.Show;
            t.Genres = ["comedy", "drama"];
            t.Keywords = [topic];
            t.Overview = "Two exes rekindle an old romance.";
        });
        var overviewFilm = await SeedTitleAsync(t => { t.Genres = ["comedy", "drama"]; t.Keywords = [topic]; t.Overview = "Two exes rekindle an old romance."; });

        await using var db = Fixture.CreateContext(new AccountContext());
        var cache = new MemoryCache(new MemoryCacheOptions());
        var resolver = new ConceptResolver(
            new KeywordVocabulary(db, cache),
            new ConceptEmbeddings(new StubEmbeddingProvider(), cache, NullLogger<ConceptEmbeddings>.Instance),
            db,
            NullLogger<ConceptResolver>.Instance);

        // The genre concepts the reducer builds (one per genre), and the plural / adjective spellings.
        foreach (var (words, genre) in new[] { ("comedy", "comedy"), ("comedies", "comedy"), ("romantic", "romance"), ("Romance movies", "romance") })
        {
            var resolved = await resolver.ResolveAsync(new ConceptTerm(words, [], words.Contains(' ') ? [] : [genre]), default);
            Assert.Equal([genre], resolved.Genres);
            Assert.Empty(resolved.ExactLabels!);
            Assert.Empty(HardConstraintFilter.LabelsOf(resolved));
        }

        // A phrase that is a TMDB keyword stays a keyword concept: "teen comedy" is a label, not a genre.
        var phrase = await resolver.ResolveAsync(new ConceptTerm("teen comedy", [], []), default);
        Assert.Contains("teen comedy", phrase.ExactLabels!);
        Assert.Empty(phrase.Genres);

        var hard = await resolver.ResolveAsync(
            HardConstraints.None with
            {
                Concepts = [new ConceptTerm(topic, [topic], []), new ConceptTerm("comedy", [], ["comedy"]), new ConceptTerm("romance", [], ["romance"])],
            },
            default);
        var accountId = await SeedAccountAsync();
        var eligibility = new EligibilityQueryBuilder(db);
        var supply = await new SupplyReporter(db, eligibility).ReportAsync(accountId, hard, CancellationToken.None);
        var matched = await HardConstraintFilter.Apply(db.Titles, db, hard).Select(t => t.Id).ToListAsync();
        Assert.Equal(new HashSet<Guid> { tagged, soapShow, overviewShow }, matched.ToHashSet());
        Assert.Equal(3, supply.MatchingInCatalogue);

        // Evidence names the genre, never a keyword or name that merely contains the genre word.
        var taggedTitle = await db.Titles.SingleAsync(t => t.Id == tagged);
        Assert.Equal(["keyword:" + topic, "genre:comedy", "genre:romance"], HardConstraintFilter.EvidenceFor(taggedTitle, hard));

        // The founder's conflict: comedy and romance are standing exclusions, so all three are held
        // out by conditions and nothing is "an unseen comedy". Each knockout alone unlocks nothing —
        // the other exclusion still binds — which is what lets the reply name the conflict instead.
        db.ContentFilters.AddRange(
            new ContentFilter { Id = Guid.NewGuid(), AccountId = accountId, Kind = FilterKind.ExcludeGenre, Value = "comedy", CreatedAt = DateTime.UtcNow },
            new ContentFilter { Id = Guid.NewGuid(), AccountId = accountId, Kind = FilterKind.ExcludeGenre, Value = "romance", CreatedAt = DateTime.UtcNow.AddSeconds(1) });
        await db.SaveChangesAsync();
        var conflicted = await new SupplyReporter(db, eligibility).ReportAsync(accountId, hard, CancellationToken.None);
        Assert.Equal(3, conflicted.MatchingInCatalogue);
        Assert.Equal(3, conflicted.ExcludedByConditions);
        Assert.Equal(0, conflicted.Eligible);
        Assert.DoesNotContain(conflicted.Knockouts, k => k.Kind == FilterKind.ExcludeGenre);
        _ = (teenComedy, namedComedy, overviewFilm);
    }

    [Fact]
    public async Task Keyword_neighbours_share_specific_evidence_and_say_which()
    {
        if (!Fixture.Available)
        {
            return;
        }

        // The Similar rail's seeding API: eligible titles sharing the anchor's SPECIFIC labels, the
        // most specific shared evidence first; nothing generic, nothing seen, never the anchor.
        var rare = $"safecracker{Guid.NewGuid():N}"[..24];
        var rarer = $"vaultcode{Guid.NewGuid():N}"[..22];
        var accountId = await SeedAccountAsync();
        var anchor = await SeedTitleAsync(t => t.Keywords = [rare, rarer]);
        var both = await SeedTitleAsync(t => t.Keywords = [rare, rarer]);
        var one = await SeedTitleAsync(t => t.Keywords = [rare]);
        var seen = await SeedTitleAsync(t => t.Keywords = [rare, rarer]);
        var unrelated = await SeedTitleAsync(t => t.Keywords = ["office"]);
        await SeedWatchAsync(accountId, seen);

        await using var db = Fixture.CreateContext(new AccountContext());
        var neighbours = await new KeywordNeighbours(db, new EligibilityQueryBuilder(db), new KeywordVocabulary(db, new MemoryCache(new MemoryCacheOptions())))
            .ForAsync(accountId, anchor, 10, [], default);

        Assert.Equal([both, one], neighbours.Select(n => n.TitleId));
        Assert.Contains($"keyword:{rare}", neighbours[1].Evidence);
        Assert.DoesNotContain(neighbours, n => n.TitleId == unrelated || n.TitleId == seen || n.TitleId == anchor);
    }

    [Fact]
    public async Task Rail_supply_counts_a_genre_rail_with_its_stand_ins()
    {
        if (!Fixture.Available)
        {
            return;
        }

        // D-16: a genre rail's supply is the genre as exclusions read it — a TV romance carried only
        // by a stand-in keyword counts — and a title merely NAMED for the genre does not.
        var accountId = await SeedAccountAsync();
        await using var db = Fixture.CreateContext(new AccountContext());
        var supply = new SupplyReporter(db, new EligibilityQueryBuilder(db));
        var before = await supply.ForGenreRailAsync(accountId, "romance", MediaScope.Shows, [], default);

        await SeedTitleAsync(t => { t.MediaType = MediaType.Show; t.Genres = ["drama"]; t.Keywords = ["office romance"]; });
        await SeedTitleAsync(t => { t.MediaType = MediaType.Show; t.Genres = ["drama"]; t.Name = $"Romance Street {Guid.NewGuid():N}"; });

        var after = await supply.ForGenreRailAsync(accountId, "romance", MediaScope.Shows, [], default);
        Assert.Equal(1, after.MatchingInCatalogue - before.MatchingInCatalogue);
    }

    [Fact]
    public async Task Rail_supply_waives_an_include_genre_rule_as_the_rail_does_but_never_an_exclude()
    {
        if (!Fixture.Available)
        {
            return;
        }

        // FD-6: a genre rail waives include-genre and never exclude-genre, so its "your conditions hide
        // N" must count the same way. Counted with the include rule applied, the rail's own genre
        // looked hidden by "only drama" — a rule the rail never applies.
        var genre = $"rail-{Guid.NewGuid():N}";
        var accountId = await SeedAccountAsync();
        var plain = await SeedTitleAsync(t => t.Genres = [genre]);
        var horror = await SeedTitleAsync(t => t.Genres = [genre, "horror"]);
        await using (var seed = Fixture.CreateContext(new AccountContext()))
        {
            seed.ContentFilters.Add(new ContentFilter
            {
                Id = Guid.NewGuid(),
                AccountId = accountId,
                Kind = FilterKind.IncludeGenre,
                Value = "drama",
                CreatedAt = DateTime.UtcNow,
            });
            seed.ContentFilters.Add(new ContentFilter
            {
                Id = Guid.NewGuid(),
                AccountId = accountId,
                Kind = FilterKind.ExcludeGenre,
                Value = "horror",
                CreatedAt = DateTime.UtcNow.AddSeconds(1),
            });
            await seed.SaveChangesAsync();
        }

        await using var db = Fixture.CreateContext(new AccountContext());
        var supply = new SupplyReporter(db, new EligibilityQueryBuilder(db));
        var applied = await supply.ReportAsync(
            accountId, new HardConstraints([new ConceptTerm(string.Empty, [], [genre], [])], [], null, null, MediaScope.Any), default);
        var rail = await supply.ForGenreRailAsync(accountId, genre, MediaScope.Any, [], default);

        Assert.Equal(2, applied.ExcludedByConditions);
        Assert.Equal(2, rail.MatchingInCatalogue);
        Assert.Equal(1, rail.ExcludedByConditions);
        Assert.Equal(1, rail.Eligible);
        Assert.DoesNotContain(rail.Knockouts, k => k.Kind == FilterKind.IncludeGenre);
        var exclude = Assert.Single(rail.Knockouts);
        Assert.Equal(FilterKind.ExcludeGenre, exclude.Kind);
        Assert.Equal(horror, Assert.Single(exclude.Sample).TitleId);
        Assert.NotEqual(plain, horror);
    }

    [Fact]
    public async Task A_mood_request_is_answered_by_titles_carrying_the_mood_not_the_best_rated_anything()
    {
        if (!Fixture.Available)
        {
            return;
        }

        // WS-2's mood requests: "something cosy" had nothing to stand on but an uncached embedding,
        // so it was answered with the pool's best-rated titles of any kind.
        var accountId = await SeedAccountAsync();
        var warm = await SeedTitleAsync(t => { t.Genres = ["drama"]; t.TmdbVoteAverage = 8.4m; t.TmdbVoteCount = 30_000; });
        await SeedAttributesAsync(warm, tone: "warm");
        var smallTown = await SeedTitleAsync(t => { t.Genres = ["drama"]; t.Keywords = ["small town"]; t.TmdbVoteAverage = 8.3m; t.TmdbVoteCount = 30_000; });
        var acclaimed = await SeedTitleAsync(t => { t.Genres = ["action"]; t.TmdbVoteAverage = 9.6m; t.TmdbVoteCount = 900_000; });
        await SeedAttributesAsync(acclaimed, tone: "gritty");

        var result = await RetrieveAsync(new RetrievalQuery(
            accountId, HardConstraints.None, new SoftPreferences(["a cosy movie"], [], []), [], FacetRetriever.MaxWant)
        {
            Arms = RetrievalArms.Catalogue,
        });
        var byId = result.Candidates.ToDictionary(c => c.TitleId);

        Assert.Contains(warm, byId.Keys);
        Assert.Contains("mood:cosy", byId[warm].Evidence);
        Assert.Contains("tone:warm", byId[warm].Evidence);
        Assert.Contains(smallTown, byId.Keys);
        Assert.Contains("keyword:small town", byId[smallTown].Evidence);
        Assert.DoesNotContain(acclaimed, byId.Keys);
        Assert.All(result.Candidates, c => Assert.Contains("mood:cosy", c.Evidence));
    }

    [Fact]
    public async Task A_steer_re_ranks_a_references_neighbours_relative_to_the_reference_and_excludes_none()
    {
        if (!Fixture.Available)
        {
            return;
        }

        // "Like X but funnier" (§4.6): the reference's neighbours, the ones funnier than X first.
        var accountId = await SeedAccountAsync();
        var vector = UniqueVector();
        var reference = await SeedTitleAsync(_ => { }, vector);
        await SeedAttributesAsync(reference, humor: 0.40m);
        var funnier = await SeedTitleAsync(_ => { }, vector);
        await SeedAttributesAsync(funnier, humor: 0.70m);
        var graver = await SeedTitleAsync(_ => { }, vector);
        await SeedAttributesAsync(graver, humor: 0.20m);
        // Funnier than the midpoint but not than the reference: the pivot is the reference, not 0.5.
        var slightly = await SeedTitleAsync(_ => { }, vector);
        await SeedAttributesAsync(slightly, humor: 0.35m);

        var result = await RetrieveAsync(new RetrievalQuery(
            accountId,
            HardConstraints.None,
            new SoftPreferences([], [new AttributeDirection("humor", AttributeTrend.More)], [reference]),
            [reference],
            FacetRetriever.MaxWant)
        {
            Arms = RetrievalArms.Semantic | RetrievalArms.Facet,
        });
        var order = result.Candidates.Select(c => c.TitleId).ToList();
        var byId = result.Candidates.ToDictionary(c => c.TitleId);

        Assert.DoesNotContain(reference, order);
        Assert.Contains(graver, order);
        Assert.Contains(slightly, order);
        Assert.True(order.IndexOf(funnier) < order.IndexOf(graver));
        Assert.True(order.IndexOf(funnier) < order.IndexOf(slightly));
        Assert.Contains("steer:humor+", byId[funnier].Evidence);
        Assert.DoesNotContain("steer:humor+", byId[slightly].Evidence);
        Assert.DoesNotContain("steer:humor+", byId[graver].Evidence);
    }

    [Fact]
    public async Task Without_an_embedding_the_semantic_arm_stands_on_the_reached_labels_precomputed_embeddings()
    {
        if (!Fixture.Available)
        {
            return;
        }

        var label = $"zephyrlull{Guid.NewGuid():N}"[..20];
        var vector = UniqueVector();
        var accountId = await SeedAccountAsync();
        var tagged = await SeedTitleAsync(t => t.Keywords = [label], vector);
        await using (var seed = Fixture.CreateContext(new AccountContext()))
        {
            seed.KeywordLabels.Add(new KeywordLabel
            {
                Label = label,
                TitleCount = 1,
                Embedding = new Vector(vector),
                EmbeddingModel = "text-embedding-3-small",
                CountedAt = DateTime.UtcNow,
                EmbeddedAt = DateTime.UtcNow,
            });
            await seed.SaveChangesAsync();
        }

        try
        {
            var result = await RetrieveAsync(
                new RetrievalQuery(accountId, HardConstraints.None, new SoftPreferences([label], [], []), [], 20)
                {
                    Arms = RetrievalArms.Catalogue | RetrievalArms.Semantic,
                },
                new UnavailableEmbeddings());

            Assert.Equal(1, result.Timings.ElapsedMs["semantic-labels"]);
            Assert.DoesNotContain("semantic", result.SkippedArms);
            var hit = Assert.Single(result.Candidates, c => c.TitleId == tagged);
            Assert.NotNull(hit.Similarity);
            Assert.Contains($"keyword:{label}", hit.Evidence);
        }
        finally
        {
            await using var cleanup = Fixture.CreateContext(new AccountContext());
            await cleanup.KeywordLabels.Where(l => l.Label == label).ExecuteDeleteAsync();
        }
    }

    /// <summary>
    /// R1-18: with a concept the semantic arm only re-ranks titles the catalogue's evidence admits — it
    /// can never add one — so retrieval never waits for its embedding: it takes the vector if it has
    /// arrived, else the reached labels' stand-in, and the call finishes in the background into the
    /// cache. A cold concept text takes 1.2–2.7 s to embed, so the 1.0 s wait was spent on nearly
    /// every turn for nothing (WS-2: retrieve ~1.0 s at the median, arm:semantic skipped). Filled or
    /// thin, the answer is the same.
    /// </summary>
    [Theory]
    [InlineData(4, 4)]
    [InlineData(2, 20)]
    public async Task A_concept_request_never_waits_for_the_semantic_embedding(int seeded, int want)
    {
        if (!Fixture.Available)
        {
            return;
        }

        var label = $"semword{Guid.NewGuid():N}"[..16];
        var accountId = await SeedAccountAsync();
        var ids = new List<Guid>();
        for (var i = 0; i < seeded; i++)
        {
            ids.Add(await SeedTitleAsync(t => t.Keywords = [label]));
        }

        var hard = new HardConstraints([new ConceptTerm(label, [], [])], [], null, null, MediaScope.Any);
        var result = await RetrieveWithScopesAsync(
            new RetrievalQuery(accountId, hard, SoftPreferences.None, [], want) { Arms = RetrievalArms.Catalogue | RetrievalArms.Semantic },
            new SlowEmbeddings(TimeSpan.FromSeconds(5)),
            new SlowTmdbClient(TimeSpan.Zero));

        Assert.Equal(ids.Order(), result.Candidates.Select(c => c.TitleId).Order());
        Assert.True(result.Timings.ElapsedMs["semantic-wait"] < 250, $"semantic-wait {result.Timings.ElapsedMs["semantic-wait"]} ms");
        Assert.Contains("semantic-late", result.SkippedArms);
    }

    /// <summary>
    /// R1-18, the TMDB side: TMDB can add titles the catalogue lacks, so it is still waited for while
    /// the catalogue's evidence is thin — but not once it holds at least half the request
    /// (<see cref="HybridRetriever.SufficientShare"/>), where TMDB would only re-rank and add
    /// provisional titles the next turn can use. Filled, half-filled, and thin.
    /// </summary>
    [Theory]
    [InlineData(4, 4, false)]
    [InlineData(2, 4, false)]
    [InlineData(1, 20, true)]
    public async Task Tmdb_is_waited_for_only_while_the_catalogue_is_thin(int seeded, int want, bool waits)
    {
        if (!Fixture.Available)
        {
            return;
        }

        var label = $"tmdbword{Guid.NewGuid():N}"[..16];
        var accountId = await SeedAccountAsync();
        for (var i = 0; i < seeded; i++)
        {
            await SeedTitleAsync(t => t.Keywords = [label]);
        }

        var hard = new HardConstraints([new ConceptTerm(label, [], [])], [], null, null, MediaScope.Any);
        var result = await RetrieveWithScopesAsync(
            new RetrievalQuery(accountId, hard, SoftPreferences.None, [], want) { Arms = RetrievalArms.Catalogue | RetrievalArms.Tmdb, Message = $"{label} movies" },
            new SlowEmbeddings(TimeSpan.Zero),
            new SlowTmdbClient(waits ? TimeSpan.FromMilliseconds(300) : TimeSpan.FromSeconds(5)));

        Assert.Equal(seeded, result.Candidates.Count);
        if (waits)
        {
            Assert.DoesNotContain("tmdb-late", result.SkippedArms);
            Assert.True(result.Timings.ElapsedMs.ContainsKey("tmdb"), string.Join(", ", result.Timings.ElapsedMs.Keys));
        }
        else
        {
            Assert.True(result.Timings.ElapsedMs["tmdb-wait"] < 250, $"tmdb-wait {result.Timings.ElapsedMs["tmdb-wait"]} ms");
            Assert.Contains("tmdb-late", result.SkippedArms);
        }
    }

    /// <summary>
    /// The other side of R1-18: without a concept the semantic arm is a FINDING arm (a mood, a
    /// reference), so it keeps its budget, and an embedding that lands inside it is used.
    /// </summary>
    [Fact]
    public async Task A_mood_request_still_waits_for_an_embedding_inside_its_budget()
    {
        if (!Fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        var mood = $"moodword{Guid.NewGuid():N}"[..16];
        var result = await RetrieveWithScopesAsync(
            new RetrievalQuery(accountId, HardConstraints.None, new SoftPreferences([mood], [], []), [], 20) { Arms = RetrievalArms.Catalogue | RetrievalArms.Semantic },
            new SlowEmbeddings(TimeSpan.FromMilliseconds(700)),
            new SlowTmdbClient(TimeSpan.Zero));

        Assert.True(result.Timings.ElapsedMs.ContainsKey("semantic"), string.Join(", ", result.Timings.ElapsedMs.Keys));
        Assert.DoesNotContain("semantic", result.SkippedArms);
        Assert.DoesNotContain("semantic-late", result.SkippedArms);
    }

    /// <summary>The retriever with a container behind its own scopes, so the TMDB arm really runs on one.</summary>
    private async Task<RetrievalResult> RetrieveWithScopesAsync(RetrievalQuery query, Domain.Ports.IEmbeddingProvider provider, Domain.Ports.ITmdbClient tmdb)
    {
        var services = new ServiceCollection();
        services.AddMemoryCache();
        services.AddScoped<IAccountContext>(_ => new AccountContext(new UnscopedReadPolicy(UnscopedReadMode.Throw)));
        services.AddDbContext<ReelDbContext>(o => o.UseNpgsql(Fixture.ConnectionString, npgsql => npgsql.UseVector()));
        services.AddScoped<Application.Persistence.IReelDbContext>(sp => sp.GetRequiredService<ReelDbContext>());
        services.AddScoped<EligibilityQueryBuilder>();
        services.AddSingleton(tmdb);
        services.AddScoped<TmdbDiscoveryArm>();
        await using var container = services.BuildServiceProvider(validateScopes: true);

        var scoped = new AccountContext();
        scoped.SetAccount(query.AccountId);
        await using var db = Fixture.CreateContext(scoped);
        var cache = new MemoryCache(new MemoryCacheOptions());
        var eligibility = new EligibilityQueryBuilder(db);
        var embeddings = new ConceptEmbeddings(provider, cache, NullLogger<ConceptEmbeddings>.Instance);
        var retriever = new HybridRetriever(
            db,
            eligibility,
            new SupplyReporter(db, eligibility),
            new ConceptResolver(new KeywordVocabulary(db, cache), embeddings, db, NullLogger<ConceptResolver>.Instance),
            new FullTextArm(db),
            embeddings,
            container.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<HybridRetriever>.Instance);
        return await retriever.RetrieveAsync(query, default);
    }

    /// <summary>Answers every text with a fixed vector, after <paramref name="delay"/>.</summary>
    private sealed class SlowEmbeddings(TimeSpan delay) : Domain.Ports.IEmbeddingProvider
    {
        public bool IsAvailable => true;

        public async Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
        {
            await Task.Delay(delay, ct);
            return [.. texts.Select(_ => UniqueVector())];
        }
    }

    /// <summary>A TMDB that answers everything empty, after <paramref name="delay"/> (or when cancelled).</summary>
    private sealed class SlowTmdbClient(TimeSpan delay) : Domain.Ports.ITmdbClient
    {
        public Task<Domain.Ports.Tmdb.TmdbTitleDetails?> GetMovieAsync(long tmdbId, CancellationToken ct = default) => After<Domain.Ports.Tmdb.TmdbTitleDetails?>(null, ct);

        public Task<Domain.Ports.Tmdb.TmdbTitleDetails?> GetTvAsync(long tmdbId, CancellationToken ct = default) => After<Domain.Ports.Tmdb.TmdbTitleDetails?>(null, ct);

        public Task<IReadOnlyList<Domain.Ports.Tmdb.TmdbListItem>> DiscoverAsync(
            bool movies, int? genreId, string? region, DateTime? releasedAfter, int page, CancellationToken ct = default) => Empty(ct);

        public Task<Domain.Ports.Tmdb.TmdbDiscoverPage> DiscoverSeedPageAsync(bool movies, int year, int minVoteCount, int page, CancellationToken ct = default) =>
            After(new Domain.Ports.Tmdb.TmdbDiscoverPage([], page, 0, 0), ct);

        public Task<IReadOnlyList<Domain.Ports.Tmdb.TmdbListItem>> GetTrendingAsync(bool movies, CancellationToken ct = default) => Empty(ct);

        public Task<IReadOnlyList<Domain.Ports.Tmdb.TmdbListItem>> GetRecommendationsAsync(bool movies, long tmdbId, CancellationToken ct = default) => Empty(ct);

        public Task<IReadOnlyList<Domain.Ports.Tmdb.TmdbListItem>> GetSimilarAsync(bool movies, long tmdbId, CancellationToken ct = default) => Empty(ct);

        public Task<IReadOnlyList<Domain.Ports.Tmdb.TmdbKeyword>> SearchKeywordsAsync(string query, CancellationToken ct = default) =>
            After<IReadOnlyList<Domain.Ports.Tmdb.TmdbKeyword>>([], ct);

        public Task<IReadOnlyList<Domain.Ports.Tmdb.TmdbListItem>> SearchTitlesAsync(bool movies, string query, int page, CancellationToken ct = default) => Empty(ct);

        public Task<Domain.Ports.Tmdb.TmdbPerson?> SearchPersonAsync(string name, CancellationToken ct = default) => After<Domain.Ports.Tmdb.TmdbPerson?>(null, ct);

        public Task<IReadOnlyList<Domain.Ports.Tmdb.TmdbListItem>> GetPersonCreditsAsync(
            long personId, Domain.Ports.Tmdb.PersonCreditRole role, CancellationToken ct = default) => Empty(ct);

        public Task<IReadOnlyList<Domain.Ports.Tmdb.TmdbListItem>> DiscoverByConceptAsync(
            bool movies, IReadOnlyList<int> genreIds, IReadOnlyList<int> keywordIds, string? region, DateTime? releasedAfter, int page,
            IReadOnlyList<int>? excludeGenreIds = null, IReadOnlyList<int>? excludeKeywordIds = null, DateTime? releasedBefore = null,
            int? maxRuntimeMinutes = null, Domain.Ports.Tmdb.TmdbDiscoverOptions? options = null, CancellationToken ct = default) => Empty(ct);

        public Task<IReadOnlyList<Domain.Ports.Tmdb.TmdbWatchProvider>> GetWatchProvidersAsync(long tmdbId, bool movie, string region, CancellationToken ct = default) =>
            After<IReadOnlyList<Domain.Ports.Tmdb.TmdbWatchProvider>>([], ct);

        private Task<IReadOnlyList<Domain.Ports.Tmdb.TmdbListItem>> Empty(CancellationToken ct) =>
            After<IReadOnlyList<Domain.Ports.Tmdb.TmdbListItem>>([], ct);

        private async Task<T> After<T>(T value, CancellationToken ct)
        {
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, ct);
            }

            return value;
        }
    }

    private static float[] UniqueVector()
    {
        var random = new Random(Guid.NewGuid().GetHashCode());
        var v = new float[1536];
        for (var i = 0; i < v.Length; i++)
        {
            v[i] = (float)(random.NextDouble() - 0.5);
        }

        var norm = (float)Math.Sqrt(v.Sum(x => (double)x * x));
        return [.. v.Select(x => x / norm)];
    }

    private async Task SeedAttributesAsync(Guid titleId, string? tone = null, decimal humor = 0.5m)
    {
        await using var db = Fixture.CreateContext(new AccountContext());
        db.TitleAttributes.Add(new TitleAttributes
        {
            TitleId = titleId,
            Darkness = 0.5m,
            Pacing = 0.5m,
            Complexity = 0.5m,
            EmotionalIntensity = 0.5m,
            Humor = humor,
            Optimism = 0.5m,
            EnsembleVsSolo = 0.5m,
            Tone = tone,
            Themes = [],
            ExtractorModel = "fixture",
            ExtractorVersion = 1,
            Status = AttributeExtractionStatus.Done,
            ExtractedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private sealed class UnavailableEmbeddings : Domain.Ports.IEmbeddingProvider
    {
        public bool IsAvailable => false;

        public Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct = default) =>
            throw new InvalidOperationException("No embedding provider.");
    }

    private async Task<RetrievalResult> RetrieveAsync(RetrievalQuery query, Domain.Ports.IEmbeddingProvider? provider = null)
    {
        await using var db = Fixture.CreateContext(new AccountContext());
        var cache = new MemoryCache(new MemoryCacheOptions());
        var eligibility = new EligibilityQueryBuilder(db);
        var embeddings = new ConceptEmbeddings(provider ?? new StubEmbeddingProvider(), cache, NullLogger<ConceptEmbeddings>.Instance);
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
