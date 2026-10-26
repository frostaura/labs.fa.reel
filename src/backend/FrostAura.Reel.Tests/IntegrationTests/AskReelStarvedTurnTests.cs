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

namespace FrostAura.Reel.Tests.IntegrationTests;

/// <summary>
/// A turn that found nothing is still a turn, and its terminal frame has to prove it.
///
/// <para>The starved branch emitted <c>{ results, reason }</c> and nothing else, while every other
/// <c>done</c> in the product carries the turn's whole accounting. A client reads one frame shape,
/// so the omission was not a smaller payload — it was an <b>undefined</b> <c>kept</c>, an
/// <b>undefined</b> <c>belowFloor</c> and an <b>undefined</b> <c>streamed</c> reaching code that
/// had every right to read them, which is how the honest answer "nothing matched" renders as the
/// dishonest one "something broke".</para>
///
/// <para>The assertion is deliberately a <b>key-set comparison against a real turn</b> rather than
/// a hand-written list: a list has to be remembered when a field is added to <c>done</c>, and the
/// field that gets forgotten is exactly the one this class exists to catch.</para>
/// </summary>
/// <remarks>
/// Inherits <see cref="EmbeddedCatalogTestBase"/> because the comparison turn is a real one and
/// embeds whatever it discovered, leaving vectors in the shared HNSW index. The catalogue rows go
/// too — see <see cref="DisposeAsync"/>.
/// </remarks>
[Collection("postgres")]
public class AskReelStarvedTurnTests(PostgresFixture fixture) : EmbeddedCatalogTestBase(fixture)
{
    /// <summary>The first of this class's scripted TMDB ids — see <see cref="DisposeAsync"/>.</summary>
    private const long ScriptedTmdbIdFirst = 777001;

    /// <summary>One past the last id this class may create. See <see cref="DisposeAsync"/>.</summary>
    private const long CatalogueIdEnd = 778000;

    /// <summary>
    /// Removes this class's catalogue rows, not just its vectors: a leftover <c>Title</c> is picked
    /// up by a later class's enrichment sweep, embedded, and lands in the shared HNSW index where
    /// no amount of eligibility isolation in the victim can reach it.
    /// </summary>
    public override async Task DisposeAsync()
    {
        await base.DisposeAsync();
        if (!Fixture.Available)
        {
            return;
        }

        await using var db = Fixture.CreateContext(new AccountContext());
        var titleIds = await db.Titles
            .Where(t => t.TmdbId >= ScriptedTmdbIdFirst && t.TmdbId < CatalogueIdEnd)
            .Select(t => t.Id)
            .ToListAsync();
        if (titleIds.Count == 0)
        {
            return;
        }

        await db.SearchTurnCards.Where(c => titleIds.Contains(c.TitleId)).ExecuteDeleteAsync();
        await db.Titles.Where(t => titleIds.Contains(t.Id)).ExecuteDeleteAsync();
    }

    /// <summary>
    /// THE FIXTURE VARIATION: the same pair of turns with one field moved — the founder debug flag.
    /// It is the field most likely to make the two frames diverge, because it is the only one whose
    /// value is decided per-turn rather than per-result, and a starved branch that forgot the
    /// gating would carry diagnostics to an account not entitled to them (or deny them to one that
    /// is). The key set must be identical either way; only the two values change.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_starved_turn_carries_every_key_a_normal_turn_carries(bool debug)
    {
        if (!Fixture.Available)
        {
            return;
        }

        // A turn that finds things, purely to have the frame shape stated by the product rather
        // than by this file.
        var found = await RunTurnAsync(Discoverable(ScriptedTmdbIdFirst, 4), debug);
        Assert.True(found.GetProperty("kept").GetInt32() > 0, "the comparison turn found nothing either.");

        // And one that cannot: nothing discoverable, and an account whose whole eligible catalogue
        // is already watched — including the four titles the turn above just pulled in.
        var starved = await RunTurnAsync([], debug);

        Assert.Equal(KeysOf(found), KeysOf(starved));

        // The accounting a client renders, stated rather than absent. `streamed` is genuinely zero
        // here and not a placeholder: the branch returns before the first `candidate` frame.
        Assert.Empty(starved.GetProperty("results").EnumerateArray());
        Assert.Equal(0, starved.GetProperty("streamed").GetInt32());
        Assert.Equal(0, starved.GetProperty("kept").GetInt32());
        Assert.Equal(0, starved.GetProperty("droppedByFloor").GetInt32());
        Assert.Empty(starved.GetProperty("belowFloor").EnumerateArray());

        // The copy is the one thing that does NOT become the normal turn's: this turn's honest
        // answer is nothing, and it says so in its own words.
        Assert.Equal(AskReelReason.Empty, starved.GetProperty("reason").GetString());

        // Same entitlement, same present-but-null shape as the turn that found something.
        var expected = debug ? JsonValueKind.Object : JsonValueKind.Null;
        Assert.Equal(expected, starved.GetProperty("debug").ValueKind);
        Assert.Equal(
            debug ? JsonValueKind.Object : JsonValueKind.Null,
            starved.GetProperty("timingsMs").ValueKind);

        if (debug)
        {
            // The founder's block is a diagnostic surface, and the turn it is most needed on is
            // this one — "nothing matched" with no eligible-pool count beside it is unactionable.
            Assert.Equal(
                KeysOf(found.GetProperty("debug")),
                KeysOf(starved.GetProperty("debug")));
            Assert.Equal(0, starved.GetProperty("debug").GetProperty("eligibleCount").GetInt32());
        }
    }

    /// <summary>The frame's own field names, ordered so two frames compare as sets.</summary>
    private static IReadOnlyList<string> KeysOf(JsonElement frame) =>
        frame.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();

    /// <summary>
    /// One turn on a brand-new account whose entire eligible catalogue is already watched, so the
    /// only titles it can possibly return are the ones <paramref name="discoverable"/> brings in.
    /// </summary>
    private async Task<JsonElement> RunTurnAsync(IReadOnlyList<TmdbListItem> discoverable, bool debug)
    {
        var accountId = await SeedAccountAsync();
        await MarkAllExistingTitlesSeenAsync(accountId);

        var (service, events) = BuildService(accountId, new ScriptedTmdbClient(discoverable), out var emit);
        await service.StreamAsync(
            "something tense", [], [], "US", [], ApiUsagePurpose.RerankAsk, emit, CancellationToken.None, debug);

        return Assert.Single(events, e => e.Event == "done").Data;
    }

    private static IReadOnlyList<TmdbListItem> Discoverable(long firstTmdbId, int count) =>
        Enumerable.Range(0, count)
            .Select(i => new TmdbListItem(
                firstTmdbId + i, true, $"Starved Fixture {firstTmdbId + i}", "/s.jpg", null,
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

        // The endpoint serialises with JsonSerializerDefaults.Web, so this fixture does too — the
        // key set under assertion here is the one a client actually receives.
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
            TraktUserSlug = $"starved-{Guid.NewGuid():N}",
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
        var now = DateTime.UtcNow;
        foreach (var titleId in await db.Titles.Select(t => t.Id).ToListAsync())
        {
            db.WatchedTitles.Add(new WatchedTitle
            {
                Id = Guid.NewGuid(),
                AccountId = accountId,
                TitleId = titleId,
                Plays = 1,
                IsFullyWatched = true,
                LastWatchedAt = now,
                FirstSyncedAt = now,
                UpdatedAt = now,
            });
        }

        await db.SaveChangesAsync();
    }

    private sealed class ScriptedAgent : ISearchAgent
    {
        public bool IsAvailable => true;

        public string ModelId => "scripted-starved-agent";

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
