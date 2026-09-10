using System.Text.Json;
using FrostAura.Reel.Application.Ingestion;
using FrostAura.Reel.Application.Ml;
using FrostAura.Reel.Application.Ml.ClosedLoop;
using FrostAura.Reel.Application.Ranking;
using FrostAura.Reel.Application.Search;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Lens;
using FrostAura.Reel.Domain.Library;
using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Domain.Ports.Tmdb;
using FrostAura.Reel.Domain.Sync;
using FrostAura.Reel.Domain.Tenancy;
using FrostAura.Reel.Infrastructure.Concurrency;
using FrostAura.Reel.Infrastructure.Ml;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Pgvector;

namespace FrostAura.Reel.Tests.IntegrationTests;

/// <summary>
/// The three ways an Ask Reel turn used to die on input it could not control — a year the model
/// invented, an embedding provider that answered short, and a TMDB blip — plus the input ceiling
/// that stops an unbounded request costing anything at all.
///
/// <para>Every one of these previously surfaced identically: the outer catch-all turned an
/// <c>ArgumentOutOfRangeException</c> / <c>IndexOutOfRangeException</c> / a transient HTTP failure
/// into "search hit a snag — please try again", discarding a pool that had already been discovered,
/// hydrated and in some cases paid for. A crash guard that degrades is worth more than a retry the
/// user has to initiate.</para>
/// </summary>
[Collection("postgres")]
public class AskReelCrashGuardTests(PostgresFixture fixture)
{
    /// <summary>
    /// "Movies from the year 99999" — a plausible thing to type and a trivially plausible thing for
    /// a language model to echo back as <see cref="SearchIntent.MinYear"/>. Both bounds are probed,
    /// at both ends of the calendar: <c>new DateTime(99999, …)</c> and <c>new DateTime(1, …)</c>
    /// both threw before the clamp, and the year rides FOUR construction sites (two in the concept
    /// fetch, two in the person-credit fetch).
    /// </summary>
    [Theory]
    [InlineData(99999, null)]
    [InlineData(null, 99999)]
    [InlineData(1, null)]
    [InlineData(null, -5)]
    [InlineData(99999, 99999)]
    public async Task Year_99999_returns_a_normal_turn(int? minYear, int? maxYear)
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        var tmdb = new ScriptedTmdbClient();
        var agent = new ScriptedAgent(new SearchIntent(
            ["science-fiction"], ["futuristic"], [], "futuristic", minYear, maxYear));
        var (service, events) = BuildService(accountId, tmdb, agent, out var emit);

        await service.StreamAsync($"sci-fi from {minYear ?? maxYear}", region: "US", emit, CancellationToken.None);

        var done = Assert.Single(events, e => e.Event == "done");
        Assert.False(Failed(done.Data), $"reason was '{done.Data.GetProperty("reason").GetString()}'");
    }

    /// <summary>
    /// A person query walks the OTHER pair of <c>new DateTime</c> sites — the credit filter — so the
    /// same absurd year has to be survivable down that arm too.
    /// </summary>
    [Fact]
    public async Task Year_99999_returns_a_normal_turn_on_the_person_arm()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        var tmdb = new ScriptedTmdbClient
        {
            PersonResult = new TmdbPerson(525, "Christopher Nolan", "Directing"),
            PersonCredits =
            [
                new(27205, true, "Inception", "/i.jpg", null, "A heist inside dreams.", 80m, 8.3m, 30000,
                    new DateTime(2010, 7, 16, 0, 0, 0, DateTimeKind.Utc), [28, 878], "en"),
            ],
        };
        var agent = new ScriptedAgent(new SearchIntent(
            [], [], [], "nolan", 99999, 99999, [new PersonQuery("Christopher Nolan", PersonRole.Director)]));
        var (service, events) = BuildService(accountId, tmdb, agent, out var emit);

        await service.StreamAsync("everything Nolan directed in 99999", region: "US", emit, CancellationToken.None);

        var done = Assert.Single(events, e => e.Event == "done");
        Assert.False(Failed(done.Data), $"reason was '{done.Data.GetProperty("reason").GetString()}'");
    }

    /// <summary>
    /// A provider that answers with fewer vectors than it was given texts (a truncated or rate-
    /// limited response) used to walk off the end of the array inside the embed-and-store loop and
    /// take the turn with it. The honest degradation is to store what arrived and rank on it.
    /// </summary>
    [Fact]
    public async Task Truncated_embedding_response_degrades_instead_of_throwing()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        await MarkAllExistingTitlesSeenAsync(accountId);

        // Three freshly-discovered titles, none of them embedded yet, against a provider that
        // answers with ONE vector. The store loop indexed straight off the end of that array.
        var tmdb = new ScriptedTmdbClient { Results = Discoverable(33001, 3) };
        var agent = new ScriptedAgent(new SearchIntent(["horror"], ["shark"], [], "shark horror", null, null));
        var (service, events) = BuildService(
            accountId, tmdb, agent, out var emit, new TruncatingEmbeddingProvider(keep: 1));

        await service.StreamAsync("shark horror", region: "US", emit, CancellationToken.None);

        var done = Assert.Single(events, e => e.Event == "done");
        Assert.False(Failed(done.Data), $"reason was '{done.Data.GetProperty("reason").GetString()}'");
    }

    /// <summary>
    /// The seed embedding for the ranking vector is a single-element index into the provider's
    /// answer. An empty answer is a plausible provider outcome and must cost the turn its cosine
    /// ordering, not its results.
    /// </summary>
    [Fact]
    public async Task An_empty_ranking_seed_degrades_instead_of_throwing()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        await MarkAllExistingTitlesSeenAsync(accountId);

        // A provider that answers NOTHING: the semantic arm bows out on its own, the store loop has
        // nothing to store, and the ranking seed — a bare [0] index — is what used to blow up.
        var tmdb = new ScriptedTmdbClient { Results = Discoverable(31001, 2) };
        var agent = new ScriptedAgent(new SearchIntent(["science-fiction"], ["futuristic"], [], "futuristic", null, null));
        var (service, events) = BuildService(
            accountId, tmdb, agent, out var emit, new TruncatingEmbeddingProvider(keep: 0));

        await service.StreamAsync("something futuristic", region: "US", emit, CancellationToken.None);

        var done = Assert.Single(events, e => e.Event == "done");
        Assert.False(Failed(done.Data), $"reason was '{done.Data.GetProperty("reason").GetString()}'");
        // Without a seed there is no cosine, so the turn is thinner — never empty.
        Assert.NotEmpty(done.Data.GetProperty("results").EnumerateArray());
    }

    /// <summary>
    /// TMDB is one of TWO discovery arms. A blip on it used to propagate to the turn's catch-all
    /// and throw away the semantic arm's results as well — the arm that needs no network at all,
    /// because it is a pgvector query over titles already held. Every TMDB entry point the turn can
    /// reach is failed here (keyword resolution, both media concept pulls, both title searches):
    /// they are separate calls and only one of them failing would not prove the wrapper covers the
    /// set. Anything in the result set therefore came from the semantic arm, and could only have.
    ///
    /// <para>The eligible pool is deliberately NOT narrowed to this test's own seeds. The embedding
    /// column carries an HNSW index, and a post-filter kNN over a pool that is tiny relative to the
    /// index silently comes back empty rather than slow — see <c>memory/gotchas.md</c>. Asserting
    /// "the semantic arm returned SOMETHING while TMDB was down" is the claim this test is actually
    /// making, and it is the one that does not depend on index recall.</para>
    /// </summary>
    [Fact]
    public async Task Tmdb_arm_failure_still_returns_the_semantic_arm()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        await SeedEmbeddedUnseenTitleAsync(32001, "Neighbour One", seed: 81);
        await SeedEmbeddedUnseenTitleAsync(32002, "Neighbour Two", seed: 82);
        await SeedEmbeddedUnseenTitleAsync(32003, "Neighbour Three", seed: 83);

        var tmdb = new ScriptedTmdbClient { ThrowOnDiscovery = true };
        var agent = new ScriptedAgent(new SearchIntent(["science-fiction"], ["futuristic"], [], "futuristic", null, null));
        var (service, events) = BuildService(accountId, tmdb, agent, out var emit);

        await service.StreamAsync("something fantastically futuristic", region: "US", emit, CancellationToken.None);

        var done = Assert.Single(events, e => e.Event == "done");
        var trace = string.Join(" | ", events.Select(e => e.Event + ":" + e.Data.GetRawText()));
        Assert.False(Failed(done.Data), trace);
        Assert.NotEmpty(done.Data.GetProperty("results").EnumerateArray());
    }

    /// <summary>
    /// The endpoint's pre-spend ceiling. Everything past the bound is refused with a graceful,
    /// honest <c>done</c> — <c>failed:false</c>, because nothing broke — before an interpret call,
    /// a TMDB pull or a persisted turn exists.
    ///
    /// <para>The guard is a pure function precisely so it can be pinned here without a host: the
    /// endpoint's only job is to call it, emit its reason and return. Each bound is probed at both
    /// sides of its edge, because an off-by-one on a ceiling is the failure mode that never shows
    /// up in manual testing.</para>
    /// </summary>
    [Theory]
    // query length — at the bound, then one past it
    [InlineData(1000, 0, 0, 0, false)]
    [InlineData(1001, 0, 0, 0, true)]
    // history turn count
    [InlineData(10, 12, 10, 0, false)]
    [InlineData(10, 13, 10, 0, true)]
    // one oversized history turn
    [InlineData(10, 1, 1000, 0, false)]
    [InlineData(10, 1, 1001, 0, true)]
    // shown-id count
    [InlineData(10, 0, 0, 300, false)]
    [InlineData(10, 0, 0, 301, true)]
    public void An_oversized_query_returns_a_graceful_done_and_costs_nothing(
        int queryChars, int historyTurns, int historyTurnChars, int shownCount, bool refused)
    {
        var history = Enumerable.Range(0, historyTurns).Select(_ => new string('h', historyTurnChars)).ToList();
        var reason = AskRequestLimits.Refuse(new string('q', queryChars), history, shownCount);

        if (!refused)
        {
            Assert.Null(reason);
            return;
        }

        Assert.NotNull(reason);
        // The refusal is prose a person reads in the chat, not a status code — and it never claims
        // something broke, because nothing did.
        Assert.NotEmpty(reason);
        var payload = JsonSerializer.SerializeToElement(AskRequestLimits.RefusalPayload(reason));
        Assert.Empty(payload.GetProperty("results").EnumerateArray());
        Assert.False(payload.GetProperty("failed").GetBoolean());
        Assert.Equal(reason, payload.GetProperty("reason").GetString());
    }

    /// <summary>True when the turn's terminal frame admitted a crash.</summary>
    private static bool Failed(JsonElement done) =>
        done.TryGetProperty("failed", out var failed) && failed.ValueKind == JsonValueKind.True;

    private (LiveSearchExpansionService Service, List<(string Event, JsonElement Data)> Events) BuildService(
        Guid accountId, ITmdbClient tmdb, ISearchAgent agent, out LiveSearchExpansionService.Emit emit,
        IEmbeddingProvider? embeddings = null)
    {
        var scoped = new AccountContext();
        scoped.SetAccount(accountId);
        var db = fixture.CreateContext(scoped);

        var eligibility = new EligibilityQueryBuilder(db);
        var featureBuilder = new FeatureVectorBuilder(db);
        var engine = new FastTreeModelEngine();
        var residualModel = new OnlineResidualModel(
            db, featureBuilder, engine, new MemoryCache(new MemoryCacheOptions()), NullLogger<OnlineResidualModel>.Instance);
        var service = new LiveSearchExpansionService(
            db,
            scoped,
            agent,
            tmdb,
            embeddings ?? new UnitEmbeddingProvider(),
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
            new LexicalSearchService(eligibility),
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
        await using var db = fixture.CreateContext(new AccountContext());
        var account = new Account
        {
            Id = Guid.NewGuid(),
            TraktUserSlug = $"guard-{Guid.NewGuid():N}",
            CreatedAt = DateTime.UtcNow,
            LastSeenAt = DateTime.UtcNow,
            PipelineStageChangedAt = DateTime.UtcNow,
        };
        db.Add(account);
        await db.SaveChangesAsync();
        return account.Id;
    }

    /// <summary>A global, English, already-hydrated, UNSEEN title carrying a normalized embedding.</summary>
    private async Task SeedEmbeddedUnseenTitleAsync(long tmdbId, string name, int seed)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var title = new Title
        {
            Id = Guid.NewGuid(),
            MediaType = MediaType.Movie,
            TmdbId = tmdbId,
            TraktSlug = string.Empty,
            Name = name,
            Genres = ["science-fiction"],
            Language = "en",
            Overview = "A futuristic, tense sci-fi journey.",
            LastMetadataRefreshAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
        };
        db.Add(title);
        db.TitleEmbeddings.Add(new TitleEmbedding
        {
            TitleId = title.Id,
            Embedding = new Vector(UnitVector(seed)),
            EmbeddingModel = EmbeddingText.Model,
            SourceTextHash = EmbeddingText.Hash(name),
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// <paramref name="count"/> distinct, English, movie-shaped discovery rows starting at
    /// <paramref name="firstTmdbId"/> — deliberately per-test ids, because the catalogue is global
    /// and shared: reusing a sibling's ids would mean the titles are already embedded and the
    /// embed-and-store loop this class is aiming at never runs.
    /// </summary>
    private static IReadOnlyList<TmdbListItem> Discoverable(long firstTmdbId, int count) =>
        Enumerable.Range(0, count)
            .Select(i => new TmdbListItem(
                firstTmdbId + i, true, $"Guarded Title {firstTmdbId + i}", "/g.jpg", null,
                "A tense pursuit with sharks in it.", 40m - i, 6.5m, 900,
                new DateTime(2001 + i, 6, 16, 0, 0, 0, DateTimeKind.Utc), [27, 53], "en"))
            .ToList();

    /// <summary>
    /// Marks every title already in the shared database watched for this account, so the eligible
    /// pool is exactly what the test seeds afterwards. Titles are global and the postgres collection
    /// runs on one database.
    /// </summary>
    private async Task MarkAllExistingTitlesSeenAsync(Guid accountId)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        foreach (var titleId in await db.Titles.Select(t => t.Id).ToListAsync())
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

    private static float[] UnitVector(int seed)
    {
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

        return v;
    }

    /// <summary>An agent that answers with a fixed intent and never touches a network.</summary>
    private sealed class ScriptedAgent(SearchIntent intent) : ISearchAgent
    {
        public bool IsAvailable => true;

        public string ModelId => "scripted-guard-agent";

        public Task<AgentReply> InterpretAsync(
            IReadOnlyList<ChatTurn> history, string message, string tasteSummary, IReadOnlyList<string> shownTitles,
            CancellationToken ct = default, string? standingFilters = null) =>
            Task.FromResult(new AgentReply("Here you go.", intent));

        public Task<IReadOnlyList<RerankResult>> RerankBatchAsync(
            IReadOnlyList<RerankInput> titles, string query, LensPersona persona, string? anchorName = null,
            string usagePurpose = ApiUsagePurpose.Rerank, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<RerankResult>>([]);
    }

    /// <summary>Deterministic unit vectors, one per text — the honest provider these tests contrast with.</summary>
    private sealed class UnitEmbeddingProvider : IEmbeddingProvider
    {
        public bool IsAvailable => true;

        public Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct = default) =>
            Task.FromResult(texts.Select(t => UnitVector(t.Length)).ToArray());
    }

    /// <summary>
    /// A provider that answers SHORT — <paramref name="keep"/> vectors regardless of how many texts
    /// it was handed. The exact shape of a truncated or partially rate-limited provider response,
    /// and the input the store loop and the ranking seed both used to index straight off the end of.
    /// </summary>
    private sealed class TruncatingEmbeddingProvider(int keep) : IEmbeddingProvider
    {
        public bool IsAvailable => true;

        public Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct = default) =>
            Task.FromResult(texts.Take(keep).Select(t => UnitVector(t.Length)).ToArray());
    }

    /// <summary>A TMDB client that can be told to answer nothing, or to fail outright.</summary>
    private sealed class ScriptedTmdbClient : ITmdbClient
    {
        /// <summary>What concept discovery answers with. Per-test ids keep the shared catalogue from
        /// deciding whether a title still needs embedding when this class runs after its siblings.</summary>
        public IReadOnlyList<TmdbListItem> Results = DefaultMovies;

        /// <summary>Concept discovery, title search and keyword resolution all throw — a TMDB outage.</summary>
        public bool ThrowOnDiscovery;

        public TmdbPerson? PersonResult;

        public IReadOnlyList<TmdbListItem> PersonCredits = [];

        private static readonly TmdbListItem[] DefaultMovies =
        [
            new(579, true, "Jaws 2", "/jaws2.jpg", null, "A killer shark returns to Amity.", 50m, 6.5m, 900,
                new DateTime(1978, 6, 16, 0, 0, 0, DateTimeKind.Utc), [27, 53], "en"),
            new(8914, true, "Deep Blue Sea", "/dbs.jpg", null, "Genetically enhanced sharks fight back.", 40m, 5.9m, 1800,
                new DateTime(1999, 7, 28, 0, 0, 0, DateTimeKind.Utc), [28, 27], "en"),
        ];

        public Task<IReadOnlyList<TmdbKeyword>> SearchKeywordsAsync(string query, CancellationToken ct = default) =>
            ThrowOnDiscovery
                ? throw new HttpRequestException("TMDB keyword search is down.")
                : Task.FromResult<IReadOnlyList<TmdbKeyword>>([new TmdbKeyword(3070, "shark")]);

        public Task<IReadOnlyList<TmdbListItem>> DiscoverByConceptAsync(
            bool movies, IReadOnlyList<int> genreIds, IReadOnlyList<int> keywordIds,
            string? region, DateTime? releasedAfter, int page,
            IReadOnlyList<int>? excludeGenreIds = null, IReadOnlyList<int>? excludeKeywordIds = null,
            DateTime? releasedBefore = null, int? maxRuntimeMinutes = null, TmdbDiscoverOptions? options = null,
            CancellationToken ct = default) =>
            ThrowOnDiscovery
                ? throw new HttpRequestException("TMDB discover is down.")
                : Task.FromResult(movies ? Results : []);

        public Task<IReadOnlyList<TmdbListItem>> SearchTitlesAsync(bool movies, string query, int page, CancellationToken ct = default) =>
            ThrowOnDiscovery
                ? throw new HttpRequestException("TMDB search is down.")
                : Task.FromResult<IReadOnlyList<TmdbListItem>>([]);

        public Task<TmdbPerson?> SearchPersonAsync(string name, CancellationToken ct = default) =>
            Task.FromResult(PersonResult);

        public Task<IReadOnlyList<TmdbListItem>> GetPersonCreditsAsync(long personId, PersonCreditRole role, CancellationToken ct = default) =>
            Task.FromResult(PersonCredits);

        public Task<TmdbTitleDetails?> GetMovieAsync(long tmdbId, CancellationToken ct = default) =>
            Task.FromResult<TmdbTitleDetails?>(new TmdbTitleDetails(
                tmdbId, "/p.jpg", null, 40m, 6m, 500, 120, "An overview.", null, null, [], [], null, ["shark"], "en", ["en"]));

        public Task<TmdbTitleDetails?> GetTvAsync(long tmdbId, CancellationToken ct = default) =>
            Task.FromResult<TmdbTitleDetails?>(null);

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
