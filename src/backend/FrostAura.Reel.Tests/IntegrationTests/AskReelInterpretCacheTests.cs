using System.Text.Json;
using FrostAura.Reel.Application.Ingestion;
using FrostAura.Reel.Application.Ml;
using FrostAura.Reel.Application.Ml.ClosedLoop;
using FrostAura.Reel.Application.Ranking;
using FrostAura.Reel.Application.Search;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Filters;
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
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrostAura.Reel.Tests.IntegrationTests;

/// <summary>
/// The same question twice, inside a minute, has to resolve to the same answer — and the discovery
/// cache built to make that cheap has to actually be reachable.
///
/// <para>It was not. Every cache below the interpret call is keyed on the INTERPRETED intent, and
/// the interpret call is a language model: a finder asked one byte-identical question twice, ~90 s
/// apart, and got <c>keywords ["twist ending","tense","suspense"]</c> then <c>["twist ending","tense
/// atmosphere"]</c>. Different intent, different <c>IntentKey</c>, different <c>CacheKey</c>, a
/// second full TMDB pull (3581 ms, then 1850 ms), 43 discovered where the first turn found 80, and
/// 11 of 24 cards changed between two identical questions. The repeat also re-bought the interpret
/// call itself, because nothing cached it.</para>
///
/// <para>So the cache goes in FRONT of the model, keyed on what the user typed rather than on what
/// the model made of it. Everything downstream then keys off one stable intent for free.</para>
/// </summary>
/// <remarks>
/// Inherits <see cref="EmbeddedCatalogTestBase"/> because a real Ask Reel turn embeds whatever it
/// discovered and therefore leaves vectors in the shared HNSW index, exactly as a hand-seeding
/// fixture does.
/// </remarks>
[Collection("postgres")]
public class AskReelInterpretCacheTests(PostgresFixture fixture) : EmbeddedCatalogTestBase(fixture)
{
    [Fact]
    public async Task An_identical_question_reuses_the_intent_and_the_pull_behind_it()
    {
        if (!Fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        await MarkAllExistingTitlesSeenAsync(accountId);

        // One process-wide cache, as production has — the service is transient, the cache is not.
        var cache = new MemoryCache(new MemoryCacheOptions());
        var agent = new DriftingAgent();
        var tmdb = new CountingTmdbClient(Discoverable(72001, 4));
        var log = new CapturingLogger();

        var first = await RunAsync(accountId, cache, agent, tmdb, log, "a tense sci-fi thriller with a great twist");
        var pullsForTheFirstTurn = tmdb.DiscoverCalls;
        var second = await RunAsync(accountId, cache, agent, tmdb, log, "a tense sci-fi thriller with a great twist");

        // One billed interpret call for two turns, and the second turn's intent is the first's.
        Assert.Equal(1, agent.Calls);
        Assert.Equal(IntentOf(first), IntentOf(second));
        // Same intent, therefore same IntentKey — the identity every cache below this point is
        // keyed on, and the one the drifting model made unreachable.
        Assert.Equal(
            LiveSearchExpansionService.IntentKey(
                JsonSerializer.Deserialize<SearchIntent>(IntentOf(first), JsonOptions)!, "US"),
            LiveSearchExpansionService.IntentKey(
                JsonSerializer.Deserialize<SearchIntent>(IntentOf(second), JsonOptions)!, "US"));

        // The payoff: one stable intent means one stable IntentKey, which is what finally makes the
        // discovery cache reachable. The second turn pulls from TMDB not at all — the absolute
        // count for the first turn is the relaxation ladder's business, not this test's.
        Assert.True(pullsForTheFirstTurn > 0, "the first turn should have pulled from TMDB at all.");
        Assert.Equal(pullsForTheFirstTurn, tmdb.DiscoverCalls);

        // And the turn says so out loud, because "interpret: 2805 ms" and "interpret: 0 ms" are the
        // difference between a fast turn and a slow one and nobody should have to guess which.
        Assert.Contains(log.Lines, l => l.Contains("interpret=0ms(cached)", StringComparison.Ordinal));
    }

    /// <summary>
    /// F5-05. The TMDB half of discovery has hit since F2-02 — the test above proves it — and yet a
    /// byte-identical repeat still spent 1.5 seconds in the <c>discover</c> stage and came back with
    /// a different pool size (87, then 86). The rest of that stage is the SEMANTIC arm: a paid
    /// concept embedding plus a pgvector kNN over the account's own catalogue, re-bought in full
    /// every time and drifting because the catalogue moves underneath it.
    ///
    /// <para>The concept is the identity of that call — it is literally the string the provider is
    /// paid to embed — so it is what the arm is keyed on. Asserted here as "the provider was asked
    /// for that exact text once across two turns", which is the same fact as the ledger delta and is
    /// visible per-turn, where <c>ExternalApiUsages</c> (a per-day aggregate) is not.</para>
    /// </summary>
    [Fact]
    public async Task An_identical_question_reuses_the_semantic_pull_as_well_as_the_tmdb_one()
    {
        if (!Fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        await MarkAllExistingTitlesSeenAsync(accountId);

        var cache = new MemoryCache(new MemoryCacheOptions());
        var agent = new DriftingAgent();
        var tmdb = new CountingTmdbClient(Discoverable(72301, 4));
        var embeddings = new CountingEmbeddingProvider();
        var log = new CapturingLogger();
        const string Question = "a tense sci-fi thriller with a great twist";

        await RunAsync(accountId, cache, agent, tmdb, log, Question, embeddings);
        var pullsForTheFirstTurn = tmdb.DiscoverCalls;
        await RunAsync(accountId, cache, agent, tmdb, log, Question, embeddings);

        // The concept the agent's answer renders to — computed through the production helper, so a
        // change to how a concept is assembled cannot leave this test asserting a dead string.
        var concept = LiveSearchExpansionService.ConceptText(
            new AgentReply("Coming up.", agent.LastIntent, agent.LastIntent), Question, AskReelNegations.None);

        Assert.Equal(1, embeddings.TimesAskedFor(concept));
        // And the half that was already fixed stays fixed: no second TMDB pull either.
        Assert.True(pullsForTheFirstTurn > 0, "the first turn should have pulled from TMDB at all.");
        Assert.Equal(pullsForTheFirstTurn, tmdb.DiscoverCalls);
    }

    /// <summary>
    /// The probe: one field of the semantic key moved. A different account asking the identical
    /// question must re-pull, because the neighbours come from that account's OWN eligible
    /// catalogue — a shared list would be one tenant reading another's pool.
    /// </summary>
    [Fact]
    public async Task Another_account_does_not_share_the_semantic_pull()
    {
        if (!Fixture.Available)
        {
            return;
        }

        var mine = await SeedAccountAsync();
        var theirs = await SeedAccountAsync();
        await MarkAllExistingTitlesSeenAsync(mine);
        await MarkAllExistingTitlesSeenAsync(theirs);

        var cache = new MemoryCache(new MemoryCacheOptions());
        // A model that does NOT drift, so both tenants render the identical concept and the only
        // thing that can separate their two embed calls is the account on the key.
        var agent = new SteadyAgent();
        var tmdb = new CountingTmdbClient(Discoverable(72401, 4));
        var embeddings = new CountingEmbeddingProvider();
        var log = new CapturingLogger();
        const string Question = "a tense sci-fi thriller with a great twist";

        await RunAsync(mine, cache, agent, tmdb, log, Question, embeddings);
        await RunAsync(theirs, cache, agent, tmdb, log, Question, embeddings);

        var concept = LiveSearchExpansionService.ConceptText(
            new AgentReply("Coming up.", agent.LastIntent, agent.LastIntent), Question, AskReelNegations.None);

        Assert.Equal(2, embeddings.TimesAskedFor(concept));
    }

    /// <summary>
    /// The probe: one field of the key moved. Adding a standing exclusion changes the summary the
    /// prompt is handed, so the cached intent is the answer to a different question and must not be
    /// served — this is the same reasoning that puts the prompt version and the model id on every
    /// other Ask Reel key.
    /// </summary>
    [Fact]
    public async Task An_added_exclusion_is_a_miss()
    {
        if (!Fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        await MarkAllExistingTitlesSeenAsync(accountId);

        var cache = new MemoryCache(new MemoryCacheOptions());
        var agent = new DriftingAgent();
        var tmdb = new CountingTmdbClient(Discoverable(72101, 4));
        var log = new CapturingLogger();

        await RunAsync(accountId, cache, agent, tmdb, log, "a tense sci-fi thriller with a great twist");
        await AddFilterAsync(accountId, FilterKind.ExcludeGenre, "horror");
        await RunAsync(accountId, cache, agent, tmdb, log, "a tense sci-fi thriller with a great twist");

        Assert.Equal(2, agent.Calls);
    }

    /// <summary>
    /// One process, many tenants, one <see cref="IMemoryCache"/>. The account rides the key because
    /// the cached value is a REPLY — written against this person's taste summary and their rules —
    /// and serving it to somebody else would be a cross-tenant leak in a product whose whole model
    /// is per-user.
    /// </summary>
    [Fact]
    public async Task Another_account_asking_the_same_question_is_a_miss()
    {
        if (!Fixture.Available)
        {
            return;
        }

        var mine = await SeedAccountAsync();
        var theirs = await SeedAccountAsync();
        await MarkAllExistingTitlesSeenAsync(mine);
        await MarkAllExistingTitlesSeenAsync(theirs);

        var cache = new MemoryCache(new MemoryCacheOptions());
        var agent = new DriftingAgent();
        var tmdb = new CountingTmdbClient(Discoverable(72201, 4));
        var log = new CapturingLogger();

        await RunAsync(mine, cache, agent, tmdb, log, "a tense sci-fi thriller with a great twist");
        await RunAsync(theirs, cache, agent, tmdb, log, "a tense sci-fi thriller with a great twist");

        Assert.Equal(2, agent.Calls);
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static string IntentOf(IReadOnlyList<(string Event, JsonElement Data)> events) =>
        events.Single(e => e.Event == "done").Data.GetProperty("debug").GetProperty("intent").GetRawText();

    private async Task<List<(string Event, JsonElement Data)>> RunAsync(
        Guid accountId, IMemoryCache cache, ISearchAgent agent, ITmdbClient tmdb, CapturingLogger log, string message,
        IEmbeddingProvider? embeddings = null)
    {
        var (service, events) = BuildService(accountId, tmdb, agent, cache, log, out var emit, embeddings);
        await service.StreamAsync(
            message, [], [], "US", [], ApiUsagePurpose.RerankAsk, emit, CancellationToken.None, debug: true);
        return events;
    }

    private static IReadOnlyList<TmdbListItem> Discoverable(long firstTmdbId, int count) =>
        Enumerable.Range(0, count)
            .Select(i => new TmdbListItem(
                firstTmdbId + i, true, $"Cached Title {firstTmdbId + i}", "/c.jpg", null,
                "A tense pursuit.", 40m - i, 6.5m, 900,
                new DateTime(2012 + i, 6, 16, 0, 0, 0, DateTimeKind.Utc), [53], "en"))
            .ToList();

    private async Task AddFilterAsync(Guid accountId, FilterKind kind, string value)
    {
        await using var db = Fixture.CreateContext(new AccountContext());
        db.ContentFilters.Add(new ContentFilter
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            Kind = kind,
            Value = value,
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private (LiveSearchExpansionService Service, List<(string Event, JsonElement Data)> Events) BuildService(
        Guid accountId, ITmdbClient tmdb, ISearchAgent agent, IMemoryCache cache, CapturingLogger log,
        out LiveSearchExpansionService.Emit emit, IEmbeddingProvider? embeddings = null)
    {
        var scoped = new AccountContext();
        scoped.SetAccount(accountId);
        var db = Fixture.CreateContext(scoped);

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
            cache,
            log);

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
            TraktUserSlug = $"interpret-cache-{Guid.NewGuid():N}",
            CreatedAt = DateTime.UtcNow,
            LastSeenAt = DateTime.UtcNow,
            PipelineStageChangedAt = DateTime.UtcNow,
        };
        db.Add(account);
        await db.SaveChangesAsync();
        return account.Id;
    }

    private async Task MarkAllExistingTitlesSeenAsync(Guid accountId)
    {
        await using var db = Fixture.CreateContext(new AccountContext());
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

    private sealed class CapturingLogger : ILogger<LiveSearchExpansionService>
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Lines.Add(formatter(state, exception));
    }

    /// <summary>
    /// A model that answers the same sentence differently every time — which is not a fault, it is
    /// what a temperature above zero does, and it is the whole reason the caches underneath it never
    /// hit. The keyword that moves is the one the finder watched move on the wire.
    /// </summary>
    private sealed class DriftingAgent : ISearchAgent
    {
        private static readonly string[] Drift = ["suspense", "tense atmosphere", "slow burn", "unreliable narrator"];

        public int Calls { get; private set; }

        public SearchIntent LastIntent { get; private set; } = new([], [], [], string.Empty, null, null);

        public bool IsAvailable => true;

        public string ModelId => "drifting-agent";

        public Task<AgentReply> InterpretAsync(
            IReadOnlyList<ChatTurn> history, string message, string tasteSummary, IReadOnlyList<string> shownTitles,
            CancellationToken ct = default, string? standingFilters = null)
        {
            LastIntent = new SearchIntent(
                ["thriller"], ["twist ending", Drift[Calls % Drift.Length]], [], message, null, null);
            Calls++;
            return Task.FromResult(new AgentReply("Coming up.", LastIntent, LastIntent));
        }

        public Task<IReadOnlyList<RerankResult>> RerankBatchAsync(
            IReadOnlyList<RerankInput> titles, string query, LensPersona persona, string? anchorName = null,
            string usagePurpose = ApiUsagePurpose.Rerank, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<RerankResult>>(
                titles.Select(t => new RerankResult(t.TitleId, 0.7d, ResultSource.Llm, "Because it fits.", ResultSource.Llm, Satisfies.Yes)).ToList());
    }

    /// <summary>
    /// A model that answers the same sentence the same way every time — the control against
    /// <see cref="DriftingAgent"/>, for tests that need two turns to render one identical concept.
    /// </summary>
    private sealed class SteadyAgent : ISearchAgent
    {
        public SearchIntent LastIntent { get; } =
            new(["thriller"], ["twist ending", "suspense"], [], "a tense sci-fi thriller with a great twist", null, null);

        public bool IsAvailable => true;

        public string ModelId => "steady-agent";

        public Task<AgentReply> InterpretAsync(
            IReadOnlyList<ChatTurn> history, string message, string tasteSummary, IReadOnlyList<string> shownTitles,
            CancellationToken ct = default, string? standingFilters = null) =>
            Task.FromResult(new AgentReply("Coming up.", LastIntent, LastIntent));

        public Task<IReadOnlyList<RerankResult>> RerankBatchAsync(
            IReadOnlyList<RerankInput> titles, string query, LensPersona persona, string? anchorName = null,
            string usagePurpose = ApiUsagePurpose.Rerank, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<RerankResult>>(
                titles.Select(t => new RerankResult(t.TitleId, 0.7d, ResultSource.Llm, "Because it fits.", ResultSource.Llm, Satisfies.Yes)).ToList());
    }

    /// <summary>
    /// Records every text it is asked to embed, so "was this concept bought twice" is a question the
    /// test can answer. <c>ExternalApiUsages</c> cannot: it is a per-day aggregate of
    /// (provider, purpose, day, count), so a single turn's delta is not isolable from it — which is
    /// exactly why the finder had to infer the cache miss from a drifting pool size instead.
    /// </summary>
    private sealed class CountingEmbeddingProvider : IEmbeddingProvider
    {
        private readonly UnitEmbeddingProvider _inner = new();
        private readonly List<string> _asked = [];

        public bool IsAvailable => true;

        public int TimesAskedFor(string text) => _asked.Count(t => t == text);

        public Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
        {
            _asked.AddRange(texts);
            return _inner.EmbedAsync(texts, ct);
        }
    }

    private sealed class UnitEmbeddingProvider : IEmbeddingProvider
    {
        public bool IsAvailable => true;

        public Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct = default) =>
            Task.FromResult(texts.Select(t =>
            {
                var v = new float[1536];
                for (var i = 0; i < v.Length; i++)
                {
                    v[i] = (float)Math.Sin(t.Length + i);
                }

                var norm = (float)Math.Sqrt(v.Sum(x => (double)x * x));
                for (var i = 0; i < v.Length; i++)
                {
                    v[i] /= norm;
                }

                return v;
            }).ToArray());
    }

    /// <summary>Counts the TMDB pulls, which is what a discovery-cache hit is visible as.</summary>
    private sealed class CountingTmdbClient(IReadOnlyList<TmdbListItem> rows) : ITmdbClient
    {
        public int DiscoverCalls { get; private set; }

        // TMDB knows every concept the fixture asks about, so the discovery arm pulls (R1-52).

        public Task<IReadOnlyList<TmdbKeyword>> SearchKeywordsAsync(string query, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TmdbKeyword>>([new TmdbKeyword(9_000_000 + ((query.GetHashCode(StringComparison.Ordinal) & 0x7fffffff) % 1_000_000), query)]);

        public Task<IReadOnlyList<TmdbListItem>> DiscoverByConceptAsync(
            bool movies, IReadOnlyList<int> genreIds, IReadOnlyList<int> keywordIds,
            string? region, DateTime? releasedAfter, int page,
            IReadOnlyList<int>? excludeGenreIds = null, IReadOnlyList<int>? excludeKeywordIds = null,
            DateTime? releasedBefore = null, int? maxRuntimeMinutes = null, TmdbDiscoverOptions? options = null,
            CancellationToken ct = default)
        {
            if (movies)
            {
                DiscoverCalls++;
            }

            return Task.FromResult(movies ? rows : []);
        }

        public Task<IReadOnlyList<TmdbListItem>> SearchTitlesAsync(bool movies, string query, int page, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TmdbListItem>>([]);

        public Task<TmdbPerson?> SearchPersonAsync(string name, CancellationToken ct = default) =>
            Task.FromResult<TmdbPerson?>(null);

        public Task<IReadOnlyList<TmdbListItem>> GetPersonCreditsAsync(long personId, PersonCreditRole role, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TmdbListItem>>([]);

        public Task<TmdbTitleDetails?> GetMovieAsync(long tmdbId, CancellationToken ct = default) =>
            Task.FromResult<TmdbTitleDetails?>(null);

        public Task<TmdbTitleDetails?> GetTvAsync(long tmdbId, CancellationToken ct = default) =>
            Task.FromResult<TmdbTitleDetails?>(null);

        public Task<TmdbDiscoverPage> DiscoverSeedPageAsync(bool movies, int year, int minVoteCount, int page, CancellationToken ct = default) =>
            Task.FromResult(new TmdbDiscoverPage([], page, 0, 0));

        public Task<IReadOnlyList<TmdbListItem>> DiscoverAsync(
            bool movies, TmdbDiscoverOptions options, int page, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TmdbListItem>>([]);

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
