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
/// What happens when the turn asks for something the account has permanently ruled out.
///
/// <para>Until now: nothing anyone could see. The interpret prompt was never told about the
/// account's standing rules, so the model happily promised a genre the pipeline was about to
/// exclude, and the turn came back thin or empty underneath a reply that had just said yes. The
/// collision was invisible to the model and unexplainable to the user, and it looked exactly like
/// the recommender being bad at its job.</para>
///
/// <para>Rungs 1 and 2 of the ladder: TELL the model, and NAME what survives. Rung 3 — a per-turn
/// override — is deliberately not built, so there is no test here for one.</para>
/// </summary>
/// <remarks>
/// Inherits <see cref="EmbeddedCatalogTestBase"/> even though it never writes a
/// <c>TitleEmbedding</c> itself: a real Ask Reel turn embeds whatever it discovered, so this class
/// leaves vectors in the shared HNSW index exactly as a hand-seeding fixture does. The source guard
/// matches the literal seeding shapes and cannot see that, which makes remembering it the author's
/// job — and forgetting costs some other class its determinism, not this one.
/// </remarks>
[Collection("postgres")]
public class AskReelFilterConflictTests(PostgresFixture fixture) : EmbeddedCatalogTestBase(fixture)
{
    [Fact]
    public async Task A_turn_that_asks_for_an_excluded_genre_is_told_so_before_the_results()
    {
        if (!Fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        await MarkAllExistingTitlesSeenAsync(accountId);
        await AddFilterAsync(accountId, FilterKind.ExcludeGenre, "horror");

        var tmdb = new ScriptedTmdbClient(Discoverable(71001, 4));
        var agent = new CapturingAgent(new SearchIntent(["horror", "thriller"], ["tense"], [], "tense horror", null, null));
        var (service, events) = BuildService(accountId, tmdb, agent, out var emit);

        await service.StreamAsync("something tense and horrifying", region: "US", emit, CancellationToken.None);

        var conflictIndex = events.FindIndex(e => e.Event == "conflict");
        Assert.True(conflictIndex >= 0, "the collision was never named.");

        var conflict = events[conflictIndex].Data;
        Assert.Equal(["horror"], conflict.GetProperty("excludedGenres").EnumerateArray().Select(g => g.GetString()));
        // Numbers travel as numbers: the copy around them is the client's to shape.
        Assert.Equal(JsonValueKind.Number, conflict.GetProperty("poolSize").ValueKind);
        Assert.Equal(JsonValueKind.Number, conflict.GetProperty("catalogueSize").ValueKind);
        Assert.Contains("horror", conflict.GetProperty("message").GetString()!, StringComparison.Ordinal);
        Assert.Contains("Conditions", conflict.GetProperty("message").GetString()!, StringComparison.Ordinal);

        // Before the grid starts ranking, so the client can render it above the results rather than
        // as an afterthought under them.
        var rankingIndex = events.FindIndex(e =>
            e.Event == "phase" && e.Data.GetProperty("stage").GetString() == "ranking");
        Assert.True(rankingIndex > conflictIndex, "the collision has to be named before the ranking phase.");
    }

    /// <summary>The probe: the same turn, with the one filter row removed.</summary>
    [Fact]
    public async Task No_collision_means_no_frame()
    {
        if (!Fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        await MarkAllExistingTitlesSeenAsync(accountId);

        var tmdb = new ScriptedTmdbClient(Discoverable(71101, 4));
        var agent = new CapturingAgent(new SearchIntent(["horror", "thriller"], ["tense"], [], "tense horror", null, null));
        var (service, events) = BuildService(accountId, tmdb, agent, out var emit);

        await service.StreamAsync("something tense and horrifying", region: "US", emit, CancellationToken.None);

        Assert.DoesNotContain(events, e => e.Event == "conflict");
    }

    [Fact]
    public async Task The_standing_rules_reach_the_model_before_it_answers()
    {
        if (!Fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        await MarkAllExistingTitlesSeenAsync(accountId);
        await AddFilterAsync(accountId, FilterKind.ExcludeGenre, "comedy");
        await AddFilterAsync(accountId, FilterKind.ExcludeGenre, "animation");
        await AddFilterAsync(accountId, FilterKind.MinYear, "2000");
        await AddFilterAsync(accountId, FilterKind.Language, "en");
        await AddFilterAsync(accountId, FilterKind.MinCrowdRating, "6.0");

        var tmdb = new ScriptedTmdbClient(Discoverable(71201, 3));
        var agent = new CapturingAgent(new SearchIntent(["thriller"], ["tense"], [], "tense thriller", null, null));
        var (service, _) = BuildService(accountId, tmdb, agent, out var emit);

        await service.StreamAsync("something tense", region: "US", emit, CancellationToken.None);

        Assert.Equal(
            "excludes: animation, comedy; min year 2000; language en; min crowd 6.0",
            agent.StandingFilters);
    }

    [Fact]
    public async Task An_account_with_no_rules_sends_the_model_nothing()
    {
        if (!Fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        await MarkAllExistingTitlesSeenAsync(accountId);

        var tmdb = new ScriptedTmdbClient(Discoverable(71301, 3));
        var agent = new CapturingAgent(new SearchIntent(["thriller"], ["tense"], [], "tense thriller", null, null));
        var (service, _) = BuildService(accountId, tmdb, agent, out var emit);

        await service.StreamAsync("something tense", region: "US", emit, CancellationToken.None);

        Assert.Null(agent.StandingFilters);
    }

    /// <summary>
    /// The eligible-pool size is logged on EVERY turn, collision or not. Without it, "40 discovered"
    /// is unreadable: out of 400 eligible it is a discovery problem, out of 12 it is a filter
    /// problem, and the two want opposite fixes.
    /// </summary>
    [Fact]
    public async Task The_eligible_pool_size_is_logged_on_every_turn()
    {
        if (!Fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        await MarkAllExistingTitlesSeenAsync(accountId);

        var tmdb = new ScriptedTmdbClient(Discoverable(71401, 3));
        var agent = new CapturingAgent(new SearchIntent(["thriller"], ["tense"], [], "tense thriller", null, null));
        var log = new CapturingLogger();
        var (service, _) = BuildService(accountId, tmdb, agent, out var emit, log);

        await service.StreamAsync("something tense", region: "US", emit, CancellationToken.None);

        Assert.Contains(log.Lines, l => l.Contains("in the account's pool", StringComparison.Ordinal));
    }

    // ── The collision rules themselves, pinned without a database ─────────────────────────────

    [Fact]
    public void An_excluded_genre_the_turn_asked_for_collides_whatever_its_casing()
    {
        var rules = FilterRuleSet.Create(["horror"], null, null, null, null, null, null);
        var intent = new SearchIntent(["Horror", "thriller"], [], [], "", null, null);

        Assert.Equal(["Horror"], AskReelStandingFilters.CollidingGenres(rules, intent));
    }

    [Fact]
    public void A_genre_the_turn_did_not_ask_for_is_not_a_collision()
    {
        var rules = FilterRuleSet.Create(["horror"], null, null, null, null, null, null);
        var intent = new SearchIntent(["thriller"], [], [], "", null, null);

        Assert.Empty(AskReelStandingFilters.CollidingGenres(rules, intent));
    }

    /// <summary>
    /// A year window collides only when it is WHOLLY outside the standing one. A partial overlap
    /// still has titles to offer and warning about it would be noise on a turn that works.
    /// </summary>
    [Theory]
    [InlineData(2000, null, 1990, 1999, true)]   // "the 90s" against "nothing before 2000"
    [InlineData(2000, null, 1995, 2005, false)]  // overlaps — there is something to show
    [InlineData(null, 2010, 2015, null, true)]   // "recent" against "nothing after 2010"
    [InlineData(null, null, 1990, 1999, false)]  // no standing window at all
    public void A_year_window_collides_only_when_it_is_wholly_outside(
        int? standingMin, int? standingMax, int? askedMin, int? askedMax, bool collides)
    {
        var rules = FilterRuleSet.Create(null, null, null, null, null, standingMin, standingMax);
        var intent = new SearchIntent([], [], [], "", askedMin, askedMax);

        Assert.Equal(collides, AskReelStandingFilters.YearWindowCollides(rules, intent));
    }

    [Fact]
    public void The_year_message_renders_the_bounds_invariantly()
    {
        var rules = FilterRuleSet.Create(null, null, null, null, null, 2000, null);
        var message = AskReelStandingFilters.Message([], yearCollides: true, rules);

        Assert.Contains("from 2000 onwards", message, StringComparison.Ordinal);
        Assert.DoesNotContain("2,000", message, StringComparison.Ordinal);
    }

    private static IReadOnlyList<TmdbListItem> Discoverable(long firstTmdbId, int count) =>
        Enumerable.Range(0, count)
            .Select(i => new TmdbListItem(
                firstTmdbId + i, true, $"Conflicted Title {firstTmdbId + i}", "/c.jpg", null,
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
        Guid accountId, ITmdbClient tmdb, ISearchAgent agent, out LiveSearchExpansionService.Emit emit,
        CapturingLogger? log = null)
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
            log ?? (ILogger<LiveSearchExpansionService>)NullLogger<LiveSearchExpansionService>.Instance);

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
            TraktUserSlug = $"conflict-{Guid.NewGuid():N}",
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

    /// <summary>Answers with a fixed intent and remembers what it was told about the account's rules.</summary>
    private sealed class CapturingAgent(SearchIntent intent) : ISearchAgent
    {
        public string? StandingFilters { get; private set; }

        public bool IsAvailable => true;

        public string ModelId => "capturing-conflict-agent";

        public Task<AgentReply> InterpretAsync(
            IReadOnlyList<ChatTurn> history, string message, string tasteSummary, IReadOnlyList<string> shownTitles,
            CancellationToken ct = default, string? standingFilters = null)
        {
            StandingFilters = standingFilters;
            return Task.FromResult(new AgentReply("Coming up.", intent));
        }

        public Task<IReadOnlyList<RerankResult>> RerankBatchAsync(
            IReadOnlyList<RerankInput> titles, string query, LensPersona persona, string? anchorName = null,
            string usagePurpose = ApiUsagePurpose.Rerank, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<RerankResult>>(
                titles.Select(t => new RerankResult(t.TitleId, 0.7d, "Because it fits.")).ToList());
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
        public Task<IReadOnlyList<TmdbKeyword>> SearchKeywordsAsync(string query, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TmdbKeyword>>([]);

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
