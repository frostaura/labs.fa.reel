using System.Text.Json;
using FrostAura.Reel.Api.Endpoints;
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

namespace FrostAura.Reel.Tests.IntegrationTests;

/// <summary>
/// The founder's own view of a turn: what the model understood, how hard discovery had to work, and
/// what happened to every card.
///
/// <para>Two halves, deliberately separated. The ENTITLEMENT is a pure function of the query flag
/// and the account tier, because <c>/api/search/ask</c> is not a founder-only route — only one
/// field of its answer is, so a group filter is the wrong tool and an inline comparison repeated
/// per handler is how a diagnostic surface ends up open by a typo. The CONTENT is decided by the
/// service, which never sees a tier and cannot accidentally widen the audience.</para>
/// </summary>
/// <remarks>
/// Inherits <see cref="EmbeddedCatalogTestBase"/> even though it never writes a
/// <c>TitleEmbedding</c> itself: a real Ask Reel turn embeds whatever it discovered, so this class
/// leaves vectors in the shared HNSW index exactly as a hand-seeding fixture does. The source guard
/// matches the literal seeding shapes and cannot see that, which makes remembering it the author's
/// job — and forgetting costs some other class its determinism, not this one.
/// </remarks>
[Collection("postgres")]
public class AskReelDebugModeTests(PostgresFixture fixture) : EmbeddedCatalogTestBase(fixture)
{
    /// <summary>The probe: the same request, with only the tier moved.</summary>
    [Theory]
    [InlineData(AccountTier.Founder, true)]
    [InlineData(AccountTier.Paid, false)]
    [InlineData(AccountTier.Free, false)]
    public void The_entitlement_turns_on_the_tier_and_nothing_else(AccountTier tier, bool allowed) =>
        Assert.Equal(allowed, EndpointHelpers.AllowsFounderDebug("1", tier));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("true")]
    [InlineData("1 ")]
    public void Anything_but_an_explicit_one_leaves_diagnostics_off(string? flag) =>
        Assert.False(EndpointHelpers.AllowsFounderDebug(flag, AccountTier.Founder));

    /// <summary>An unresolvable account is not a founder — the absence of a tier is not a tier.</summary>
    [Fact]
    public void An_unknown_account_is_never_entitled() =>
        Assert.False(EndpointHelpers.AllowsFounderDebug("1", null));

    [Fact]
    public async Task An_entitled_turn_carries_the_whole_diagnostic_block()
    {
        if (!Fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        await MarkAllExistingTitlesSeenAsync(accountId);

        var tmdb = new ScriptedTmdbClient(Discoverable(81001, 4));
        var (service, events) = BuildService(accountId, tmdb, out var emit);

        await service.StreamAsync(
            "something tense", [], [], "US", [], ApiUsagePurpose.RerankAsk, emit, CancellationToken.None, debug: true);

        var done = Assert.Single(events, e => e.Event == "done");
        var debug = done.Data.GetProperty("debug");
        Assert.Equal(JsonValueKind.Object, debug.ValueKind);

        // Every field the founder needs to tell a discovery problem from a filter problem from a
        // ranking problem, without adding a log line and redeploying.
        foreach (var field in new[]
                 {
                     "intent", "relaxation", "eligibleCount", "eligiblePoolSize",
                     "streamed", "kept", "droppedByFloor", "timingsMs",
                 })
        {
            Assert.True(debug.TryGetProperty(field, out var value), $"debug.{field} is missing.");
            Assert.NotEqual(JsonValueKind.Undefined, value.ValueKind);
        }

        // The intent is the model's OWN reading of the turn, not a re-description of it.
        Assert.Equal("tense thriller", debug.GetProperty("intent").GetProperty("freeText").GetString());

        // The accounting inside the block agrees with the accounting on the frame — two sources for
        // one fact is how they start disagreeing.
        Assert.Equal(done.Data.GetProperty("kept").GetInt32(), debug.GetProperty("kept").GetInt32());
        Assert.Equal(done.Data.GetProperty("streamed").GetInt32(), debug.GetProperty("streamed").GetInt32());
        Assert.Equal(done.Data.GetProperty("droppedByFloor").GetInt32(), debug.GetProperty("droppedByFloor").GetInt32());
    }

    [Fact]
    public async Task An_unentitled_turn_carries_none_of_it()
    {
        if (!Fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        await MarkAllExistingTitlesSeenAsync(accountId);

        var tmdb = new ScriptedTmdbClient(Discoverable(81101, 4));
        var (service, events) = BuildService(accountId, tmdb, out var emit);

        await service.StreamAsync("something tense", region: "US", emit, CancellationToken.None);

        var done = Assert.Single(events, e => e.Event == "done");
        // Present-but-null rather than absent, so the client has one shape to render either way.
        Assert.True(
            !done.Data.TryGetProperty("debug", out var debug) || debug.ValueKind == JsonValueKind.Null,
            "diagnostics reached a turn that was not entitled to them.");

        // The counts a normal client renders are NOT privileged — the honesty of the result set is
        // for everyone; only the pipeline's internals are founder-only.
        Assert.True(done.Data.TryGetProperty("kept", out _));
        Assert.True(done.Data.TryGetProperty("droppedByFloor", out _));
    }

    private static IReadOnlyList<TmdbListItem> Discoverable(long firstTmdbId, int count) =>
        Enumerable.Range(0, count)
            .Select(i => new TmdbListItem(
                firstTmdbId + i, true, $"Debugged Title {firstTmdbId + i}", "/d.jpg", null,
                "A tense pursuit.", 40m - i, 6.5m, 900,
                new DateTime(2012 + i, 6, 16, 0, 0, 0, DateTimeKind.Utc), [53], "en"))
            .ToList();

    private (LiveSearchExpansionService Service, List<(string Event, JsonElement Data)> Events) BuildService(
        Guid accountId, ITmdbClient tmdb, out LiveSearchExpansionService.Emit emit)
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
            new ScriptedAgent(),
            tmdb,
            new UnitEmbeddingProvider(),
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

        // The ENDPOINT serialises with JsonSerializerDefaults.Web, so this fixture does too. The
        // difference is invisible for the hand-written anonymous frames (already camelCase) and
        // decisive for `intent`, which is a record: asserting against PascalCase here would pin a
        // wire shape no client will ever receive.
        var web = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var events = new List<(string, JsonElement)>();
        emit = (name, payload) =>
        {
            events.Add((name, JsonSerializer.SerializeToElement(payload, web)));
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
            TraktUserSlug = $"debug-{Guid.NewGuid():N}",
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

    private sealed class ScriptedAgent : ISearchAgent
    {
        public bool IsAvailable => true;

        public string ModelId => "scripted-debug-agent";

        public Task<AgentReply> InterpretAsync(
            IReadOnlyList<ChatTurn> history, string message, string tasteSummary, IReadOnlyList<string> shownTitles,
            CancellationToken ct = default, string? standingFilters = null) =>
            Task.FromResult(new AgentReply(
                "Coming up.",
                new SearchIntent(["thriller"], ["tense"], [], "tense thriller", null, null)));

        public Task<IReadOnlyList<RerankResult>> RerankBatchAsync(
            IReadOnlyList<RerankInput> titles, string query, LensPersona persona, string? anchorName = null,
            string usagePurpose = ApiUsagePurpose.Rerank, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<RerankResult>>(
                titles.Select(t => new RerankResult(t.TitleId, 0.7d, ResultSource.Llm, "Because it fits.", ResultSource.Llm, Satisfies.Yes)).ToList());
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

    private sealed class ScriptedTmdbClient(IReadOnlyList<TmdbListItem> rows) : ITmdbClient
    {
        // TMDB knows every concept the fixture asks about, so the discovery arm pulls (R1-52).
        public Task<IReadOnlyList<TmdbKeyword>> SearchKeywordsAsync(string query, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TmdbKeyword>>([new TmdbKeyword(9_000_000 + ((query.GetHashCode(StringComparison.Ordinal) & 0x7fffffff) % 1_000_000), query)]);

        public Task<IReadOnlyList<TmdbListItem>> DiscoverByConceptAsync(
            bool movies, IReadOnlyList<int> genreIds, IReadOnlyList<int> keywordIds,
            string? region, DateTime? releasedAfter, int page,
            IReadOnlyList<int>? excludeGenreIds = null, IReadOnlyList<int>? excludeKeywordIds = null,
            DateTime? releasedBefore = null, int? maxRuntimeMinutes = null, TmdbDiscoverOptions? options = null,
            CancellationToken ct = default) =>
            Task.FromResult(movies ? rows : []);

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
