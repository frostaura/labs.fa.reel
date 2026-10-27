using System.Text.Json;
using FrostAura.Reel.Application.Ingestion;
using FrostAura.Reel.Application.Ml;
using FrostAura.Reel.Application.Ml.ClosedLoop;
using FrostAura.Reel.Application.Ranking;
using FrostAura.Reel.Application.Search;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Library;
using FrostAura.Reel.Domain.Lens;
using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Domain.Sync;
using FrostAura.Reel.Domain.Ports.Tmdb;
using FrostAura.Reel.Domain.Tenancy;
using FrostAura.Reel.Infrastructure.Adapters;
using FrostAura.Reel.Infrastructure.Concurrency;
using FrostAura.Reel.Infrastructure.Ml;
using FrostAura.Reel.Infrastructure.Telemetry;
using FrostAura.Reel.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Pgvector;

namespace FrostAura.Reel.Tests.IntegrationTests;

/// <summary>
/// Live "Ask Reel" expansion on real Postgres with a fake TMDB + stub embeddings/interpreter:
/// a concept query pulls unseen matching titles from TMDB, streams them, and excludes anything
/// the user has already seen — and a repeat query reuses the cache instead of re-hitting TMDB.
/// </summary>
[Collection("postgres")]
public class LiveSearchExpansionTests(PostgresFixture fixture) : EmbeddedCatalogTestBase(fixture)
{
    // Was quarantined as a "local-only flake" until 2026-09-10. The cause was in the fake TMDB
    // client below, not the environment: its list rows carried OriginalLanguage = null, so every
    // freshly-discovered candidate was upserted with Language = null and then dropped by the
    // English-only eligibility default added in e790bb2 — "found: 3, scored: 0". Deterministic
    // everywhere, CI included. The fake now carries "en" the way TMDB's real discover rows do.
    [Fact]
    public async Task Streams_unseen_matches_excludes_seen_and_caches_the_fetch()
    {
        if (!Fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        await SeedSeenTitleAsync(accountId, tmdbId: 8059, name: "Sharknado"); // already watched → must not appear

        var tmdb = new FakeTmdbClient();
        var (service, events) = BuildService(accountId, tmdb, out var emit);

        await service.StreamAsync("shark horror", region: "US", emit, CancellationToken.None);

        // Event sequence: at least one phase and a terminal done. The stub judge rules on nothing, so
        // no card is a judged match and the turn's cards arrive in the "not checked" tray of a
        // none-found answer (§4.8) rather than as streamed picks — discovery is what is under test.
        Assert.Contains(events, e => e.Event == "phase");
        var done = Assert.Single(events, e => e.Event == "done");
        Assert.Equal("NoneFound", done.Data.GetProperty("mode").GetString());

        var names = done.Data.GetProperty("results").EnumerateArray()
            .Concat(done.Data.GetProperty("unchecked").EnumerateArray())
            .Select(r => r.GetProperty("name").GetString())
            .ToList();
        Assert.Contains("Jaws 2", names);
        Assert.Contains("Deep Blue Sea", names);
        Assert.DoesNotContain("Sharknado", names); // seen → excluded by EligibilityQueryBuilder

        // Repeat the identical query → served from cache, no further TMDB firehose.
        var fetchesAfterFirst = tmdb.ConceptFetches;
        events.Clear();
        await service.StreamAsync("shark horror", region: "US", emit, CancellationToken.None);
        Assert.Equal(fetchesAfterFirst, tmdb.ConceptFetches); // cache hit → zero new fetches
        Assert.Contains(events, e => e.Event == "done");
    }

    /// <summary>
    /// HREV2-ASK-03 (FD-3's default): a person query is a suggestion surface, so it offers only the
    /// credits the account has NOT seen — v1, the engine an unflagged deploy serves, offered Dune: Part
    /// Two, Arrival, Sicario and Prisoners, all watched, as picks for "Denis Villeneuve films". The
    /// reply counts the seen credits and names none. Varied over how the title was seen: a watch, a
    /// rating with no watch row, and both credits seen (an honest none-found, no cards).
    /// </summary>
    /// <remarks>
    /// The person-credit rows carry OriginalLanguage "en": without it both films were dropped by the
    /// English-only default before they could be shown (the cause of this test's old quarantine).
    /// </remarks>
    [Theory]
    [InlineData("watch", false)]
    [InlineData("rating", false)]
    [InlineData("watch", true)]
    public async Task A_director_query_offers_only_unseen_credits_and_counts_the_seen_ones(string seenBy, bool bothSeen)
    {
        if (!Fixture.Available)
        {
            return;
        }

        // Ids of this case's own: the catalogue is shared across the cases, and a TMDB id is unique per medium.
        var inception = Random.Shared.NextInt64(1_000_000_000, 2_000_000_000);
        var tenet = inception + 1;
        var accountId = await SeedAccountAsync();
        // Credited in the catalogue, as a synced title is: the seen count reads the credits (HREV2-ASK-03).
        // Named for this case alone: titles are global, and an earlier case's Nolan films are unseen here.
        var director = $"Christopher Nolan {Guid.NewGuid():N}"[..26];
        var nolan = await SeedPersonAsync(director);
        await SeedSeenTitleAsync(accountId, tmdbId: inception, name: "Inception", seenBy, creditedTo: (nolan, CreditRole.Director));
        if (bothSeen)
        {
            await SeedSeenTitleAsync(accountId, tmdbId: tenet, name: "Tenet", seenBy, creditedTo: (nolan, CreditRole.Director));
        }

        var tmdb = new FakeTmdbClient
        {
            PersonResult = new TmdbPerson(525, director, "Directing"),
            PersonCredits =
            [
                new(inception, true, "Inception", "/i.jpg", null, "A heist inside dreams.", 80m, 8.3m, 30000, new DateTime(2010, 7, 16, 0, 0, 0, DateTimeKind.Utc), [28, 878], "en"),
                new(tenet, true, "Tenet", "/t.jpg", null, "Time inversion espionage.", 70m, 7.3m, 9000, new DateTime(2020, 8, 26, 0, 0, 0, DateTimeKind.Utc), [28, 878], "en"),
            ],
        };
        var agent = new FakePersonAgent(new PersonQuery(director, PersonRole.Director), $"{director} made Inception and Tenet.");
        var (service, events) = BuildService(accountId, tmdb, out var emit, agent);

        await service.StreamAsync($"show me all movies {director} directs", region: "US", emit, CancellationToken.None);

        Assert.True(tmdb.PersonCreditFetches > 0, "the director query must route through person credits, not keyword/title search");

        var done = Assert.Single(events, e => e.Event == "done");
        var shown = events.Where(e => e.Event is "candidate" or "candidate-scored" or "picks" or "tray" or "done")
            .Select(e => e.Data.ToString())
            .ToList();
        Assert.DoesNotContain(shown, frame => frame.Contains("Inception", StringComparison.Ordinal));
        var reply = string.Concat(events.Where(e => e.Event == "assistant-message").Select(e => e.Data.GetProperty("text").GetString()));
        Assert.DoesNotContain("Inception", reply, StringComparison.Ordinal);
        if (bothSeen)
        {
            Assert.Equal("NoneFound", done.Data.GetProperty("mode").GetString());
            Assert.Empty(done.Data.GetProperty("results").EnumerateArray());
            Assert.DoesNotContain(shown, frame => frame.Contains("Tenet", StringComparison.Ordinal));
            Assert.DoesNotContain("Tenet", reply, StringComparison.Ordinal);
        }
        else
        {
            var names = done.Data.GetProperty("results").EnumerateArray().Select(r => r.GetProperty("name").GetString()).ToList();
            Assert.Equal(["Tenet"], names);
            Assert.Equal($"You've seen 1 title directed by {director}; here is one you haven't.", reply);
        }
    }

    /// <summary>
    /// HREV2-ASK-03's reply count. v1 counted "you've seen N" over TMDB's newest-first credit page
    /// alone, inside the account's conditions: "Liam Neeson movies" said "You've seen 11" when the
    /// account had seen 39 of his catalogued films, and offered two titles under the bar while four
    /// that cleared it sat past the page. The seen count is now the catalogue's credits by the same
    /// person predicate the supply and v2 count with, whatever the conditions hold out; the shelf takes
    /// the catalogued credits too. Varied over how many seen credits sit past the page, over one the
    /// account's conditions hold out, and over none at all. A seen title on TMDB's page with no credit
    /// row (his uncredited voice in The Rise of Skywalker, live) is not counted, or v1 says 40 where
    /// v2 says 39.
    /// </summary>
    [Theory]
    [InlineData(3, false)]
    [InlineData(3, true)]
    [InlineData(0, true)]
    [InlineData(0, false)]
    public async Task A_person_reply_counts_every_seen_credit_and_offers_unseen_ones_past_the_discovery_page(int offPageSeen, bool heldByConditions)
    {
        if (!Fixture.Available)
        {
            return;
        }

        var name = $"Actor {Guid.NewGuid():N}"[..14];
        var pageSeen = Random.Shared.NextInt64(1_000_000_000, 2_000_000_000);
        var pageUnseen = pageSeen + 1;
        var accountId = await SeedAccountAsync();
        await SeedSeenTitleAsync(accountId, tmdbId: pageSeen, name: "Page Seen");
        var personId = await SeedPersonAsync(name);
        for (var i = 0; i < offPageSeen; i++)
        {
            await SeedCreditedTitleAsync(personId, $"Off Page Seen {i}", "en", accountId);
        }

        if (heldByConditions)
        {
            // A French film: the English-only default holds it out, and it is still a credit he has seen.
            await SeedCreditedTitleAsync(personId, "Off Page Seen Abroad", "fr", accountId);
        }

        await SeedCreditedTitleAsync(personId, "Off Page Unseen", "en", seenBy: null);
        // A seen title credited to someone else is not his.
        await SeedCreditedTitleAsync(await SeedPersonAsync($"Other {Guid.NewGuid():N}"[..14]), "Somebody Else's", "en", accountId);

        var tmdb = new FakeTmdbClient
        {
            PersonResult = new TmdbPerson(626, name, "Acting"),
            PersonCredits =
            [
                new(pageSeen, true, "Page Seen", "/s.jpg", null, "Seen already.", 60m, 7.0m, 8000, new DateTime(2025, 3, 1, 0, 0, 0, DateTimeKind.Utc), [28], "en"),
                new(pageUnseen, true, "Page Unseen", "/u.jpg", null, "Not seen yet.", 50m, 6.8m, 7000, new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc), [28], "en"),
            ],
        };
        var agent = new FakePersonAgent(new PersonQuery(name, PersonRole.Cast), $"{name} is in lots of things.");
        var (service, events) = BuildService(accountId, tmdb, out var emit, agent);

        await service.StreamAsync($"{name} movies", region: "US", emit, CancellationToken.None);

        var done = Assert.Single(events, e => e.Event == "done");
        var names = done.Data.GetProperty("results").EnumerateArray().Select(r => r.GetProperty("name").GetString()).ToList();
        Assert.Equal(["Off Page Unseen", "Page Unseen"], names.Order());
        var seen = offPageSeen + (heldByConditions ? 1 : 0);
        var reply = string.Concat(events.Where(e => e.Event == "assistant-message").Select(e => e.Data.GetProperty("text").GetString()));
        Assert.Equal(
            seen == 0
                ? $"Here are 2 starring {name} you haven't seen."
                : $"You've seen {seen} {(seen == 1 ? "title" : "titles")} starring {name}; here are 2 you haven't.",
            reply);
    }

    [Fact]
    public async Task A_vibe_query_returns_multiple_semantic_neighbours_from_the_embedded_catalog()
    {
        if (!Fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        // Four UNSEEN titles that already carry embeddings — the semantic arm's raw material.
        await SeedEmbeddedUnseenTitleAsync(tmdbId: 157336, name: "Interstellar", seed: 1);
        await SeedEmbeddedUnseenTitleAsync(tmdbId: 335984, name: "Blade Runner 2049", seed: 2);
        await SeedEmbeddedUnseenTitleAsync(tmdbId: 693134, name: "Dune: Part Two", seed: 3);
        await SeedEmbeddedUnseenTitleAsync(tmdbId: 438631, name: "Dune", seed: 4);

        // TMDB whiffs on the vibe (EmptyDiscovery) — so any results MUST come from the semantic arm
        // over the embedded catalogue, which is exactly the recall the founder wanted restored.
        var tmdb = new FakeTmdbClient { EmptyDiscovery = true };
        var (service, events) = BuildService(accountId, tmdb, out var emit, new FakeVibeAgent());

        await service.StreamAsync("something fantastically futuristic and tense", region: "US", emit, CancellationToken.None);

        var done = Assert.Single(events, e => e.Event == "done");
        var names = done.Data.GetProperty("results").EnumerateArray()
            .Select(r => r.GetProperty("name").GetString()!)
            .ToList();
        Assert.True(names.Count > 1, $"the semantic arm should surface multiple embedded neighbours; got {names.Count}");
        Assert.Contains(names, n => n is "Interstellar" or "Blade Runner 2049" or "Dune" or "Dune: Part Two");
    }

    [Fact]
    public async Task An_exclusion_with_enough_clean_candidates_never_shows_a_contradicting_title()
    {
        if (!Fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        // Titles are GLOBAL and this collection shares one database, so anything another test
        // seeded is unseen-eligible here too. Mark it all watched so the clean/contradicting
        // counts below are exactly the ones this test seeds.
        await MarkAllExistingTitlesSeenAsync(accountId);

        // 14 clean thrillers (≥ MinResults) and 6 horror titles. The old top-up refilled to
        // ReturnCount with the horror ones, unflagged — the QA-confirmed defect.
        for (var i = 0; i < 14; i++)
        {
            await SeedEmbeddedUnseenTitleAsync(9000 + i, $"Clean Thriller {i}", seed: 10 + i, genres: ["thriller"], overview: "A tense pursuit.");
        }

        for (var i = 0; i < 6; i++)
        {
            await SeedEmbeddedUnseenTitleAsync(9500 + i, $"Haunting {i}", seed: 40 + i, genres: ["horror"], overview: "A haunting.");
        }

        var tmdb = new FakeTmdbClient { EmptyDiscovery = true };
        var (service, events) = BuildService(accountId, tmdb, out var emit, new FakeExclusionAgent());

        await service.StreamAsync("a tense thriller, no horror please", region: "US", emit, CancellationToken.None);

        var done = Assert.Single(events, e => e.Event == "done");
        var results = done.Data.GetProperty("results").EnumerateArray().ToList();
        Assert.NotEmpty(results);
        Assert.All(results, r =>
        {
            Assert.DoesNotContain("horror", r.GetProperty("genres").EnumerateArray().Select(g => g.GetString()));
            Assert.False(r.GetProperty("outsideRequest").GetBoolean());
        });

        // Nothing contradicting reaches the stream either — not even flagged.
        var candidates = events.Where(e => e.Event == "candidate").ToList();
        Assert.All(candidates, c => Assert.False(c.Data.GetProperty("outsideRequest").GetBoolean()));
    }

    [Fact]
    public async Task Too_few_clean_candidates_show_a_flagged_outside_channel_after_the_clean_picks()
    {
        if (!Fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        // Same isolation guard as above: only the titles seeded below may be eligible, or the
        // clean count drifts with whatever other tests happened to run first.
        await MarkAllExistingTitlesSeenAsync(accountId);

        // Only 5 clean → the floor tops up with 7 contradicting titles, flagged and trailing.
        for (var i = 0; i < 5; i++)
        {
            await SeedEmbeddedUnseenTitleAsync(9600 + i, $"Clean Thriller {i}", seed: 10 + i, genres: ["thriller"], overview: "A tense pursuit.");
        }

        for (var i = 0; i < 8; i++)
        {
            await SeedEmbeddedUnseenTitleAsync(9700 + i, $"Haunting {i}", seed: 40 + i, genres: ["horror"], overview: "A haunting.");
        }

        var tmdb = new FakeTmdbClient { EmptyDiscovery = true };
        var (service, events) = BuildService(accountId, tmdb, out var emit, new FakeExclusionAgent());

        await service.StreamAsync("a tense thriller, no horror please", region: "US", emit, CancellationToken.None);

        var done = Assert.Single(events, e => e.Event == "done");
        var results = done.Data.GetProperty("results").EnumerateArray().ToList();
        var outsideFlags = results.Select(r => r.GetProperty("outsideRequest").GetBoolean()).ToList();

        Assert.Equal(5, outsideFlags.Count(f => !f));
        Assert.Equal(7, outsideFlags.Count(f => f)); // MinResults(12) − 5, never up to ReturnCount

        // Outside cards NEVER interleave: every clean pick precedes every flagged one.
        Assert.Equal(outsideFlags.OrderBy(f => f), outsideFlags);

        // Every flagged card is a genuine contradiction wearing the honest deterministic caption,
        // never an LLM pitch.
        foreach (var r in results.Where(r => r.GetProperty("outsideRequest").GetBoolean()))
        {
            Assert.Contains("horror", r.GetProperty("genres").EnumerateArray().Select(g => g.GetString()));
            Assert.Equal(
                "Closest match we could find, but it's a horror title — which you asked to skip.",
                r.GetProperty("why").GetString());
        }

        // The reason is a CLAUSE LIST in the user's words — see AskReelReason for why.
        Assert.Equal(
            "5 picks match what you asked · 7 close but outside what you asked, shown separately",
            done.Data.GetProperty("reason").GetString());

        // Nothing vanishes unexplained: everything streamed is either kept or in the tray.
        Assert.Equal(12, done.Data.GetProperty("streamed").GetInt32());
        Assert.Equal(12, done.Data.GetProperty("kept").GetInt32());
        Assert.Equal(0, done.Data.GetProperty("droppedByFloor").GetInt32());
        Assert.Empty(done.Data.GetProperty("belowFloor").EnumerateArray());
    }

    /// <summary>A canned agent that wants thrillers and explicitly rules horror out.</summary>
    private sealed class FakeExclusionAgent : ISearchAgent
    {
        public bool IsAvailable => true;

        public string ModelId => "fake-exclusion-agent";

        /// <summary>
        /// <c>Requested</c> is populated, and that is not incidental. The turn under test is "a
        /// tense thriller, NO HORROR PLEASE" — the user typed the negation — and only a negation
        /// the user typed (or a standing rule) may route a title into the flagged outside channel.
        /// An agent that left <c>Requested</c> null would be modelling a model INFERRING the
        /// exclusion, which is a different turn and is deliberately no longer a negation at all:
        /// "which you asked to skip" printed for something the user never said is the caption
        /// defect F4-02 measured. The production agent fills this from the free deterministic
        /// reading of the same sentence.
        /// </summary>
        public Task<AgentReply> InterpretAsync(
            IReadOnlyList<ChatTurn> history, string message, string tasteSummary, IReadOnlyList<string> shownTitles, CancellationToken ct = default, string? standingFilters = null) =>
            Task.FromResult(new AgentReply(
                "Tense thrillers, steering clear of horror.",
                new SearchIntent(["thriller"], ["tense"], [], "tense thriller", null, null, null, ExcludeGenres: ["horror"]),
                new SearchIntent(["thriller"], ["tense"], [], message, null, null, null, ExcludeGenres: ["horror"])));

        public Task<IReadOnlyList<RerankResult>> RerankBatchAsync(
            IReadOnlyList<RerankInput> titles, string query, LensPersona persona, string? anchorName = null, string usagePurpose = ApiUsagePurpose.Rerank, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<RerankResult>>(
                titles.Select(t => new RerankResult(t.TitleId, 0.7d, ResultSource.Llm, "Because it fits.", ResultSource.Llm, Satisfies.Yes)).ToList());
    }

    /// <summary>A canned agent that resolves a turn straight to a vibe intent (genres + keywords, no person).</summary>
    private sealed class FakeVibeAgent : ISearchAgent
    {
        public bool IsAvailable => true;

        public string ModelId => "fake-vibe-agent";

        public Task<AgentReply> InterpretAsync(
            IReadOnlyList<ChatTurn> history, string message, string tasteSummary, IReadOnlyList<string> shownTitles, CancellationToken ct = default, string? standingFilters = null) =>
            Task.FromResult(new AgentReply(
                "Fantastically futuristic, tense, high-energy science fiction.",
                new SearchIntent(["science-fiction"], ["futuristic", "dystopian"], [], "fantastically futuristic tense", null, null, null)));

        public Task<IReadOnlyList<RerankResult>> RerankBatchAsync(
            IReadOnlyList<RerankInput> titles, string query, LensPersona persona, string? anchorName = null, string usagePurpose = ApiUsagePurpose.Rerank, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<RerankResult>>(
                titles.Select(t => new RerankResult(t.TitleId, 0.7d, ResultSource.Llm, "Because it fits.", ResultSource.Llm, Satisfies.Yes)).ToList());
    }

    /// <summary>Seeds a global, English, UNSEEN title that already carries a normalized embedding.</summary>
    private async Task SeedEmbeddedUnseenTitleAsync(long tmdbId, string name, int seed, string[]? genres = null, string? overview = null)
    {
        await using var db = Fixture.CreateContext(new AccountContext());
        var title = new Title
        {
            Id = Guid.NewGuid(),
            MediaType = MediaType.Movie,
            TmdbId = tmdbId,
            TraktSlug = string.Empty,
            Name = name,
            Genres = genres ?? ["science-fiction"],
            Language = "en",
            Overview = overview ?? "A futuristic, tense sci-fi journey.",
            LastMetadataRefreshAt = DateTime.UtcNow, // already hydrated → no live TMDB fetch during the test
            CreatedAt = DateTime.UtcNow,
        };
        db.Add(title);

        var v = new float[1536];
        for (var i = 0; i < v.Length; i++)
        {
            v[i] = (float)Math.Sin(seed + i);
        }

        var norm = (float)Math.Sqrt(v.Sum(x => (double)x * x));
        for (var i = 0; i < v.Length; i++)
        {
            v[i] /= norm;
        }

        db.TitleEmbeddings.Add(new TitleEmbedding
        {
            TitleId = title.Id,
            Embedding = new Vector(v),
            EmbeddingModel = EmbeddingText.Model,
            SourceTextHash = EmbeddingText.Hash(name),
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    /// <summary>A canned agent that resolves a turn straight to a named-person intent (no LLM).</summary>
    private sealed class FakePersonAgent(PersonQuery person, string? reply = null) : ISearchAgent
    {
        public bool IsAvailable => true;

        public string ModelId => "fake-person-agent";

        public Task<AgentReply> InterpretAsync(
            IReadOnlyList<ChatTurn> history, string message, string tasteSummary, IReadOnlyList<string> shownTitles, CancellationToken ct = default, string? standingFilters = null) =>
            Task.FromResult(new AgentReply(
                reply ?? $"Here are films {person.Role} {person.Name}.",
                new SearchIntent([], [], [], message, null, null, [person])));

        public Task<IReadOnlyList<RerankResult>> RerankBatchAsync(
            IReadOnlyList<RerankInput> titles, string query, LensPersona persona, string? anchorName = null, string usagePurpose = ApiUsagePurpose.Rerank, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<RerankResult>>(
                titles.Select(t => new RerankResult(t.TitleId, 0.7d, ResultSource.Llm, "Because it fits.", ResultSource.Llm, Satisfies.Yes)).ToList());
    }

    private (LiveSearchExpansionService Service, List<(string Event, JsonElement Data)> Events) BuildService(
        Guid accountId, ITmdbClient tmdb, out LiveSearchExpansionService.Emit emit, ISearchAgent? agent = null)
    {
        var scoped = new AccountContext();
        scoped.SetAccount(accountId);
        var db = Fixture.CreateContext(scoped);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["TestHelpers:ModelStubMode"] = "true" })
            .Build();
        var scopeFactory = new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        var recorder = new ApiUsageRecorder(scopeFactory, NullLogger<ApiUsageRecorder>.Instance);
        var interpreter = new OpenRouterSearchInterpreter(
            new HttpClient(), recorder, config, NullLogger<OpenRouterSearchInterpreter>.Instance);
        var searchAgent = agent ?? new OpenRouterSearchAgent(
            new HttpClient(), interpreter, recorder, config, NullLogger<OpenRouterSearchAgent>.Instance);

        var eligibility = new EligibilityQueryBuilder(db);
        var featureBuilder = new FeatureVectorBuilder(db);
        var engine = new FastTreeModelEngine();
        var residualModel = new OnlineResidualModel(
            db, featureBuilder, engine, new MemoryCache(new MemoryCacheOptions()), NullLogger<OnlineResidualModel>.Instance);
        var service = new LiveSearchExpansionService(
            db,
            scoped,
            searchAgent,
            tmdb,
            new StubEmbeddingProvider(),
            eligibility,
            new CandidateGenerator(db, tmdb, eligibility, NullLogger<CandidateGenerator>.Instance),
            new TitleHydrator(db, tmdb, NullLogger<TitleHydrator>.Instance),
            featureBuilder,
            new PersonalisationContext(db, new DislikeSignal(db, new MemoryCache(new MemoryCacheOptions()))),
            new OnDemandScorer(
                db, featureBuilder, engine, residualModel,
                new DislikeSignal(db, new MemoryCache(new MemoryCacheOptions())),
                new RatingCalibrationService(db, new MemoryCache(new MemoryCacheOptions()))),
            new ExposureLog(db, NullLogger<ExposureLog>.Instance),
            Application.Search.Retrieval.KeylessTextSearch.Offline(db, eligibility),
            new CatalogWorkCoordinator(),
            new MemoryCache(new MemoryCacheOptions()),
            NullLogger<LiveSearchExpansionService>.Instance);

        var events = new List<(string, JsonElement)>();
        emit = (name, payload) =>
        {
            events.Add((name, JsonSerializer.SerializeToElement(payload)));
            return Task.CompletedTask;
        };
        return (service, events);
    }

    private async Task<Guid> SeedAccountAsync()
    {
        await using var db = Fixture.CreateContext(new AccountContext());
        var account = new Account
        {
            Id = Guid.NewGuid(),
            TraktUserSlug = $"ask-{Guid.NewGuid():N}",
            CreatedAt = DateTime.UtcNow,
            LastSeenAt = DateTime.UtcNow,
            PipelineStageChangedAt = DateTime.UtcNow,
        };
        db.Add(account);
        await db.SaveChangesAsync();
        return account.Id;
    }

    /// <summary>
    /// Marks every title already in the shared database as watched for this account, so a test's
    /// eligible pool contains exactly what it seeds afterwards. Titles are global and the postgres
    /// collection runs on one database, so without this a test's clean/contradicting counts depend
    /// on which sibling tests ran first.
    /// </summary>
    private async Task MarkAllExistingTitlesSeenAsync(Guid accountId)
    {
        await using var db = Fixture.CreateContext(new AccountContext());
        var titleIds = await db.Titles.Select(t => t.Id).ToListAsync();
        foreach (var titleId in titleIds)
        {
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
        }

        await db.SaveChangesAsync();
    }

    /// <summary>
    /// A title the account has seen — watched (the default) or only rated (<paramref name="seenBy"/>
    /// "rating") — credited to <paramref name="creditedTo"/> when given.
    /// </summary>
    private async Task SeedSeenTitleAsync(
        Guid accountId, long tmdbId, string name, string seenBy = "watch", (Guid Person, CreditRole Role)? creditedTo = null)
    {
        await using var db = Fixture.CreateContext(new AccountContext());
        var title = new Title
        {
            Id = Guid.NewGuid(),
            MediaType = MediaType.Movie,
            TmdbId = tmdbId,
            TraktSlug = string.Empty,
            Name = name,
            Genres = ["horror"],
            CreatedAt = DateTime.UtcNow,
        };
        db.Add(title);
        if (creditedTo is { } credit)
        {
            db.TitleCredits.Add(new TitleCredit { Id = Guid.NewGuid(), TitleId = title.Id, PersonId = credit.Person, Role = credit.Role });
        }

        if (seenBy == "rating")
        {
            db.UserRatings.Add(new UserRating
            {
                Id = Guid.NewGuid(),
                AccountId = accountId,
                TitleId = title.Id,
                SubjectType = RatingSubjectType.Movie,
                Rating = 8,
                RatedAt = DateTime.UtcNow,
                FirstRatedAt = DateTime.UtcNow,
            });
        }
        else
        {
            db.WatchedTitles.Add(new WatchedTitle
            {
                Id = Guid.NewGuid(),
                AccountId = accountId,
                TitleId = title.Id,
                Plays = 1,
                IsFullyWatched = true,
                LastWatchedAt = DateTime.UtcNow,
                FirstSyncedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
        }

        await db.SaveChangesAsync();
    }

    private async Task<Guid> SeedPersonAsync(string name)
    {
        await using var db = Fixture.CreateContext(new AccountContext());
        var person = new Person { Id = Guid.NewGuid(), TmdbId = Random.Shared.NextInt64(1_000_000_000, long.MaxValue), Name = name };
        db.Persons.Add(person);
        await db.SaveChangesAsync();
        return person.Id;
    }

    /// <summary>A catalogued, hydrated film crediting <paramref name="personId"/> as cast, seen by <paramref name="seenBy"/> when given.</summary>
    private async Task SeedCreditedTitleAsync(Guid personId, string name, string language, Guid? seenBy)
    {
        await using var db = Fixture.CreateContext(new AccountContext());
        var title = new Title
        {
            Id = Guid.NewGuid(),
            MediaType = MediaType.Movie,
            TmdbId = Random.Shared.NextInt64(2_000_000_000, long.MaxValue),
            TraktSlug = string.Empty,
            Name = name,
            Year = 2004,
            ReleasedAt = new DateTime(2004, 11, 12, 0, 0, 0, DateTimeKind.Utc),
            Genres = ["drama"],
            Language = language,
            PosterPath = "/p.jpg",
            Overview = "A life, told plainly.",
            TmdbVoteAverage = 7.4m,
            TmdbVoteCount = 5_000,
            LastMetadataRefreshAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
        };
        db.Add(title);
        db.TitleCredits.Add(new TitleCredit { Id = Guid.NewGuid(), TitleId = title.Id, PersonId = personId, Role = CreditRole.Actor });
        if (seenBy is { } accountId)
        {
            db.WatchedTitles.Add(new WatchedTitle
            {
                Id = Guid.NewGuid(),
                AccountId = accountId,
                TitleId = title.Id,
                Plays = 1,
                IsFullyWatched = true,
                LastWatchedAt = DateTime.UtcNow,
                FirstSyncedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
        }

        await db.SaveChangesAsync();
    }

    /// <summary>Returns canned shark candidates for concept-discovery; trivial details for hydration.</summary>
    private sealed class FakeTmdbClient : ITmdbClient
    {
        public int ConceptFetches;

        /// <summary>When set, concept discovery returns nothing — proving the semantic arm carries recall alone.</summary>
        public bool EmptyDiscovery;

        private static readonly TmdbListItem[] Movies =
        [
            new(579, true, "Jaws 2", "/jaws2.jpg", null, "A killer shark returns to Amity.", 50m, 6.5m, 900, new DateTime(1978, 6, 16, 0, 0, 0, DateTimeKind.Utc), [27, 53], "en"),
            new(8914, true, "Deep Blue Sea", "/dbs.jpg", null, "Genetically enhanced sharks fight back.", 40m, 5.9m, 1800, new DateTime(1999, 7, 28, 0, 0, 0, DateTimeKind.Utc), [28, 27], "en"),
            new(8059, true, "Sharknado", "/sn.jpg", null, "Sharks rain on Los Angeles.", 30m, 3.3m, 1200, new DateTime(2013, 7, 11, 0, 0, 0, DateTimeKind.Utc), [27, 878], "en"),
        ];

        public Task<IReadOnlyList<TmdbKeyword>> SearchKeywordsAsync(string query, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TmdbKeyword>>([new TmdbKeyword(3070, "shark")]);

        /// <summary>The exclusion + year arguments of the LAST concept call, so tests can assert
        /// that a negation actually reached discovery rather than only the ranking demerit.</summary>
        public IReadOnlyList<int>? LastExcludeGenreIds;
        public IReadOnlyList<int>? LastExcludeKeywordIds;
        public DateTime? LastReleasedAfter;
        public DateTime? LastReleasedBefore;

        public Task<IReadOnlyList<TmdbListItem>> DiscoverByConceptAsync(
            bool movies, IReadOnlyList<int> genreIds, IReadOnlyList<int> keywordIds,
            string? region, DateTime? releasedAfter, int page,
            IReadOnlyList<int>? excludeGenreIds = null, IReadOnlyList<int>? excludeKeywordIds = null,
            DateTime? releasedBefore = null, int? maxRuntimeMinutes = null, TmdbDiscoverOptions? options = null, CancellationToken ct = default)
        {
            Interlocked.Increment(ref ConceptFetches);
            if (movies)
            {
                LastExcludeGenreIds = excludeGenreIds;
                LastExcludeKeywordIds = excludeKeywordIds;
                LastReleasedAfter = releasedAfter;
                LastReleasedBefore = releasedBefore;
            }

            return Task.FromResult<IReadOnlyList<TmdbListItem>>(EmptyDiscovery || !movies ? [] : Movies);
        }

        public Task<IReadOnlyList<TmdbListItem>> SearchTitlesAsync(bool movies, string query, int page, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TmdbListItem>>([]);

        public int PersonCreditFetches;
        public TmdbPerson? PersonResult;
        public IReadOnlyList<TmdbListItem> PersonCredits = [];

        public Task<TmdbPerson?> SearchPersonAsync(string name, CancellationToken ct = default) =>
            Task.FromResult(PersonResult);

        public Task<IReadOnlyList<TmdbListItem>> GetPersonCreditsAsync(long personId, PersonCreditRole role, CancellationToken ct = default)
        {
            Interlocked.Increment(ref PersonCreditFetches);
            return Task.FromResult(PersonCredits);
        }

        public Task<TmdbTitleDetails?> GetMovieAsync(long tmdbId, CancellationToken ct = default) =>
            Task.FromResult<TmdbTitleDetails?>(new TmdbTitleDetails(
                tmdbId, "/p.jpg", null, 40m, 6m, 500, 120, "An overview.", null, null, [], [], null, ["shark"], "en", ["en"]));

        public Task<TmdbTitleDetails?> GetTvAsync(long tmdbId, CancellationToken ct = default) =>
            Task.FromResult<TmdbTitleDetails?>(null);

        // The bulk-seed walk is not exercised by this fixture; SeedCatalogJobHandlerTests owns its own
        // scripted TMDB client. An empty page reads as "this partition is exhausted", which is the
        // only honest answer a fake that was never given a corpus can give.
        public Task<TmdbDiscoverPage> DiscoverSeedPageAsync(bool movies, int year, int minVoteCount, int page, CancellationToken ct = default) =>
            Task.FromResult(new TmdbDiscoverPage([], page, 0, 0));

        public Task<IReadOnlyList<TmdbListItem>> DiscoverAsync(
            bool movies, int? genreId, string? region, DateTime? releasedAfter, int page, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TmdbListItem>>([]);

        public Task<IReadOnlyList<TmdbListItem>> GetTrendingAsync(bool movies, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TmdbListItem>>([]);

        public Task<IReadOnlyList<TmdbListItem>> GetRecommendationsAsync(bool movies, long tmdbId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TmdbListItem>>([]);

        public Task<IReadOnlyList<TmdbListItem>> GetSimilarAsync(bool movies, long tmdbId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TmdbListItem>>([]);

        public Task<IReadOnlyList<TmdbWatchProvider>> GetWatchProvidersAsync(long tmdbId, bool movie, string region, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TmdbWatchProvider>>([]);
    }
}
