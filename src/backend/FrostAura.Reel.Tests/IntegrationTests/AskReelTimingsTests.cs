using System.Diagnostics;
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
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Pgvector;

namespace FrostAura.Reel.Tests.IntegrationTests;

/// <summary>
/// Where an Ask Reel turn's seconds went — the instrumentation that makes "the chat feels slow" a
/// question with an answer rather than an argument.
///
/// <para>The probe is the lexical fallback: a turn that bails out before the agent call must record
/// what it DID and nothing else. A stage list that always contains eight entries is a checklist,
/// not a measurement, and the first person to optimise against it would be optimising a constant.</para>
/// </summary>
/// <remarks>
/// Inherits <see cref="EmbeddedCatalogTestBase"/> even though it never writes a
/// <c>TitleEmbedding</c> itself: a real Ask Reel turn embeds whatever it discovered, so this class
/// leaves vectors in the shared HNSW index exactly as a hand-seeding fixture does. The source guard
/// matches the literal seeding shapes and cannot see that, which makes remembering it the author's
/// job — and forgetting costs some other class its determinism, not this one.
/// </remarks>
[Collection("postgres")]
public class AskReelTimingsTests(PostgresFixture fixture) : EmbeddedCatalogTestBase(fixture)
{
    [Fact]
    public async Task A_full_turn_records_every_stage_exactly_once_and_within_its_own_duration()
    {
        if (!Fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        await MarkAllExistingTitlesSeenAsync(accountId);

        var tmdb = new ScriptedTmdbClient(Discoverable(51001, 4));
        var log = new CapturingLogger();
        var (service, events) = BuildService(
            accountId, tmdb, new ScriptedAgent(), new UnitEmbeddingProvider(), out var emit, log);

        var sw = Stopwatch.StartNew();
        await service.StreamAsync(
            "something brooding and futuristic", [], [], "US", [], ApiUsagePurpose.RerankAsk, emit,
            CancellationToken.None, debug: true);
        sw.Stop();

        // The END-OF-TURN LOG is where all eight stages live, because it is written after the last
        // of them finishes.
        var line = Assert.Single(log.Lines, l => l.Contains("returned.", StringComparison.Ordinal));
        Assert.All(AskStageTimings.Sequential, stage => Assert.Contains($"{stage}=", line, StringComparison.Ordinal));

        // The breakdowns that ran (R1-07): the TMDB arm, the kNN arm, the first judge batch and the
        // first card. The facet arm needs a retriever this fixture does not wire, so it is ABSENT —
        // a stage that did not run is never reported as zero.
        Assert.All(
            new[] { AskStageTimings.DiscoverTmdb, AskStageTimings.DiscoverKnn, AskStageTimings.RerankFirstBatch, AskStageTimings.FirstCard },
            stage => Assert.Contains($"{stage}=", line, StringComparison.Ordinal));
        Assert.DoesNotContain($"{AskStageTimings.DiscoverFacet}=", line, StringComparison.Ordinal);

        // Every discovered title is new to the catalogue, so it is embedded and hydrated on the turn,
        // and the ranking seed is awaited: all three show up as their own breakdowns.
        Assert.All(
            new[] { AskStageTimings.EmbedTitles, AskStageTimings.EmbedSeed, AskStageTimings.ScoreHydrate },
            stage => Assert.Contains($"{stage}=", line, StringComparison.Ordinal));

        var done = Assert.Single(events, e => e.Event == "done");
        var timings = done.Data.GetProperty("timingsMs");
        Assert.Equal(JsonValueKind.Object, timings.ValueKind);
        var recorded = timings.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetInt64(), StringComparer.Ordinal);

        // The WIRE carries every stage that had finished when the frame was written — which is all of
        // them but `emit`, the stage whose work is writing this frame. A payload cannot contain the
        // duration of its own transmission, and reporting a zero there would be a lie rather than a
        // rounding. Every other stage is present, once, in pipeline order; discovery and eligibility
        // run once per relaxation level and still report a single accumulated total.
        Assert.Equal(
            AskStageTimings.Sequential.Where(stage => stage != AskStageTimings.Emit).ToList(),
            recorded.Keys.Where(AskStageTimings.Sequential.Contains).ToList());
        Assert.Equal(
            AskStageTimings.Order.Where(recorded.ContainsKey).ToList(),
            recorded.Keys.ToList());

        Assert.All(recorded, kv => Assert.True(kv.Value >= 0, $"{kv.Key} recorded a negative duration."));

        // A breakdown never exceeds the stage it breaks down, and the first card comes after the
        // first judge batch returned.
        Assert.True(recorded[AskStageTimings.DiscoverTmdb] <= recorded[AskStageTimings.Discover] + 1);
        Assert.True(recorded[AskStageTimings.DiscoverKnn] <= recorded[AskStageTimings.Discover] + 1);
        Assert.True(recorded[AskStageTimings.RerankFirstBatch] <= recorded[AskStageTimings.Rerank] + 1);
        Assert.True(recorded[AskStageTimings.EmbedTitles] + recorded[AskStageTimings.EmbedSeed] <= recorded[AskStageTimings.Embed] + 2);
        Assert.True(recorded[AskStageTimings.ScoreHydrate] <= recorded[AskStageTimings.Score] + 1);
        Assert.True(recorded[AskStageTimings.FirstCard] <= sw.Elapsed.TotalMilliseconds + 1);

        // The pipeline's stages are sequential and disjoint, so their sum cannot exceed the turn. A
        // generous slack absorbs the wall-clock overhead of the assertions plus timer resolution.
        var sum = recorded.Where(kv => AskStageTimings.Sequential.Contains(kv.Key)).Sum(kv => kv.Value);
        Assert.True(
            sum <= sw.Elapsed.TotalMilliseconds + 50,
            $"stages sum to {sum}ms but the whole turn took {sw.Elapsed.TotalMilliseconds:F0}ms — "
            + "a stage is being double-counted or nested inside another.");
    }

    /// <summary>
    /// The probe. With no embedding provider the turn takes the keyless branch, which sits BEFORE
    /// the agent call — it searches the catalogue and scores what it found, and there is nothing to
    /// interpret, embed, rerank or blend, so the record says exactly that (R1-10).
    /// </summary>
    [Fact]
    public async Task The_lexical_fallback_records_only_the_work_it_actually_did()
    {
        if (!Fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        var tmdb = new ScriptedTmdbClient([]);
        var log = new CapturingLogger();
        var (service, events) = BuildService(
            accountId, tmdb, new ScriptedAgent(), new UnavailableEmbeddingProvider(), out var emit, log);

        await service.StreamAsync(
            "something brooding", [], [], "US", [], ApiUsagePurpose.RerankAsk, emit, CancellationToken.None, debug: true);

        Assert.Contains(events, e => e.Event == "done");
        var line = Assert.Single(log.Lines, l => l.Contains("keyless lexical answer", StringComparison.Ordinal));

        Assert.Contains($"{AskStageTimings.Discover}=", line, StringComparison.Ordinal);
        Assert.Contains($"{AskStageTimings.Score}=", line, StringComparison.Ordinal);
        Assert.All(
            AskStageTimings.Order.Where(stage => stage != AskStageTimings.Discover && stage != AskStageTimings.Score),
            stage => Assert.DoesNotContain($" {stage}=", " " + line, StringComparison.Ordinal));
    }

    /// <summary>
    /// Without the debug opt-in the field is present-but-null rather than absent, so the client has
    /// one shape to render either way.
    /// </summary>
    [Fact]
    public async Task Timings_are_withheld_unless_the_turn_asked_for_them()
    {
        if (!Fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        await MarkAllExistingTitlesSeenAsync(accountId);

        var tmdb = new ScriptedTmdbClient(Discoverable(51101, 2));
        var (service, events) = BuildService(accountId, tmdb, new ScriptedAgent(), new UnitEmbeddingProvider(), out var emit);

        await service.StreamAsync("something brooding and futuristic", [], [], "US", [], emit, CancellationToken.None);

        var done = Assert.Single(events, e => e.Event == "done");
        Assert.True(
            !done.Data.TryGetProperty("timingsMs", out var timings) || timings.ValueKind == JsonValueKind.Null,
            "a turn that did not ask for diagnostics must not carry them.");
    }

    /// <summary>
    /// R1-07: the first card's wait, attributed. §4.11's retrieve budget (0.3 s p50, 1.0 s p95) has no
    /// line for three costs this pipeline can pay before the first judge call — embedding titles new to
    /// the catalogue, hydrating them from TMDB, and the part of an uncached concept embedding the
    /// discovery arms did not hide — so each is its own breakdown, and a latency run can say which one
    /// a slow first card was waiting on instead of leaving it inside <c>embed</c> and <c>score</c>.
    ///
    /// <para>Two fixtures. COLD: the titles come from TMDB and are new, TMDB details take 60 ms each,
    /// the new titles' embedding 250 ms, and the concept embedding 2.0 s — longer than the kNN arm's
    /// 1.0 s budget. WARM: the titles are already catalogued, hydrated and embedded, and the concept
    /// embeds instantly. The same turn must report the cold costs where they were paid and none of
    /// them warm.</para>
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_first_card_breakdown_names_what_it_waited_on(bool cold)
    {
        if (!Fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        await MarkAllExistingTitlesSeenAsync(accountId);

        IReadOnlyList<TmdbListItem> fromTmdb = cold ? Discoverable(51201, 4) : [];
        if (!cold)
        {
            for (var i = 0; i < 4; i++)
            {
                await SeedCataloguedTitleAsync(51301 + i, $"Catalogued Title {51301 + i}", i + 3);
            }
        }

        var conceptDelayCold = TimeSpan.FromMilliseconds(2000);
        var conceptDelay = cold ? conceptDelayCold : TimeSpan.Zero;
        var titlesDelay = TimeSpan.FromMilliseconds(250);
        var tmdb = new ScriptedTmdbClient(fromTmdb, detailsDelay: TimeSpan.FromMilliseconds(60));
        var log = new CapturingLogger();
        var (service, events) = BuildService(
            accountId, tmdb, new ScriptedAgent(), new DelayedEmbeddingProvider(conceptDelay, titlesDelay), out var emit, log);

        await service.StreamAsync(
            "something brooding and futuristic", [], [], "US", [], ApiUsagePurpose.RerankAsk, emit,
            CancellationToken.None, debug: true);

        var done = Assert.Single(events, e => e.Event == "done");
        var recorded = done.Data.GetProperty("timingsMs").EnumerateObject()
            .ToDictionary(p => p.Name, p => p.Value.GetInt64(), StringComparer.Ordinal);
        Assert.Contains(AskStageTimings.FirstCard, recorded.Keys);

        if (cold)
        {
            // The kNN arm stops waiting at its budget; the rest of the embedding lands on the seed.
            Assert.InRange(recorded[AskStageTimings.DiscoverKnn], 0, (long)LiveSearchExpansionService.SemanticEmbedBudget.TotalMilliseconds + 400);
            Assert.True(
                recorded[AskStageTimings.EmbedSeed] >= 200,
                $"the seed waited {recorded[AskStageTimings.EmbedSeed]} ms on a 2.0 s embedding that discovery cut off at 1.0 s.");
            Assert.True(recorded[AskStageTimings.EmbedTitles] >= 240, $"embed.titles was {recorded[AskStageTimings.EmbedTitles]} ms for a 250 ms call.");
            Assert.True(recorded[AskStageTimings.ScoreHydrate] >= 55, $"score.hydrate was {recorded[AskStageTimings.ScoreHydrate]} ms for 60 ms TMDB calls.");

            // The first card waited for all of it: none of the three runs underneath anything else.
            Assert.True(
                recorded[AskStageTimings.FirstCard]
                    >= recorded[AskStageTimings.EmbedSeed] + recorded[AskStageTimings.EmbedTitles] + recorded[AskStageTimings.ScoreHydrate],
                "the first card arrived before work that sits on its critical path finished.");
            Assert.True(
                recorded[AskStageTimings.FirstCard] >= (long)conceptDelay.TotalMilliseconds,
                "the ranking seed waits for the whole concept embedding, so the first card cannot beat it.");
        }
        else
        {
            // Nothing new to embed or hydrate is ABSENT, not zero; a finished concept costs the seed nothing.
            Assert.DoesNotContain(AskStageTimings.EmbedTitles, recorded.Keys);
            Assert.DoesNotContain(AskStageTimings.ScoreHydrate, recorded.Keys);
            Assert.InRange(recorded[AskStageTimings.EmbedSeed], 0, 100);
            Assert.True(
                recorded[AskStageTimings.FirstCard] < conceptDelayCold.TotalMilliseconds,
                $"a warm turn's first card took {recorded[AskStageTimings.FirstCard]} ms with no model or TMDB latency in it.");
        }
    }

    /// <summary>The accumulate-don't-append rule, pinned without a database.</summary>
    [Fact]
    public void A_stage_entered_twice_reports_one_accumulated_total()
    {
        var timings = new AskStageTimings();
        using (timings.Stage(AskStageTimings.Discover))
        {
            Thread.Sleep(5);
        }

        using (timings.Stage(AskStageTimings.Discover))
        {
            Thread.Sleep(5);
        }

        var snapshot = timings.Snapshot();
        Assert.Equal([AskStageTimings.Discover], snapshot.Keys.ToList());
        Assert.True(snapshot[AskStageTimings.Discover] >= 9, $"expected the two 5 ms stages to add up; got {snapshot[AskStageTimings.Discover]}ms.");
    }

    /// <summary>A stage that never ran is absent, not zero — absence and "instant" are different facts.</summary>
    [Fact]
    public void A_stage_that_never_ran_is_absent_rather_than_zero()
    {
        var timings = new AskStageTimings();
        using (timings.Stage(AskStageTimings.Interpret))
        {
        }

        Assert.Equal([AskStageTimings.Interpret], timings.Snapshot().Keys.ToList());
        Assert.DoesNotContain(AskStageTimings.Rerank, timings.ToString(), StringComparison.Ordinal);
    }

    private static IReadOnlyList<TmdbListItem> Discoverable(long firstTmdbId, int count) =>
        Enumerable.Range(0, count)
            .Select(i => new TmdbListItem(
                firstTmdbId + i, true, $"Timed Title {firstTmdbId + i}", "/t.jpg", null,
                "A brooding, futuristic search for what is real.", 40m - i, 6.5m, 900,
                new DateTime(2015 + i, 6, 16, 0, 0, 0, DateTimeKind.Utc), [878], "en"))
            .ToList();

    private (LiveSearchExpansionService Service, List<(string Event, JsonElement Data)> Events) BuildService(
        Guid accountId, ITmdbClient tmdb, ISearchAgent agent, IEmbeddingProvider embeddings,
        out LiveSearchExpansionService.Emit emit, CapturingLogger? log = null)
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
            embeddings,
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
            TraktUserSlug = $"timing-{Guid.NewGuid():N}",
            CreatedAt = DateTime.UtcNow,
            LastSeenAt = DateTime.UtcNow,
            PipelineStageChangedAt = DateTime.UtcNow,
        };
        db.Add(account);
        await db.SaveChangesAsync();
        return account.Id;
    }

    /// <summary>A global, English, already-hydrated, UNSEEN title that already carries an embedding.</summary>
    private async Task SeedCataloguedTitleAsync(long tmdbId, string name, int seed)
    {
        await using var db = Fixture.CreateContext(new AccountContext());
        var title = new Title
        {
            Id = Guid.NewGuid(),
            MediaType = MediaType.Movie,
            TmdbId = tmdbId,
            TraktSlug = string.Empty,
            Name = name,
            Genres = ["science-fiction"],
            Language = "en",
            Overview = "A brooding, futuristic search for what is real.",
            LastMetadataRefreshAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
        };
        db.Add(title);
        db.TitleEmbeddings.Add(new TitleEmbedding
        {
            TitleId = title.Id,
            Embedding = new Vector(Unit(seed)),
            EmbeddingModel = EmbeddingText.Model,
            SourceTextHash = EmbeddingText.Hash(name),
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private static float[] Unit(int seed)
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

    /// <summary>
    /// Keeps every rendered log line. The end-of-turn line is the production artifact these timings
    /// exist for — asserting on the wire alone would leave the thing an operator actually reads
    /// untested.
    /// </summary>
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

    private sealed class ScriptedAgent : ISearchAgent
    {
        public bool IsAvailable => true;

        public string ModelId => "scripted-timing-agent";

        public Task<AgentReply> InterpretAsync(
            IReadOnlyList<ChatTurn> history, string message, string tasteSummary, IReadOnlyList<string> shownTitles,
            CancellationToken ct = default, string? standingFilters = null) =>
            Task.FromResult(new AgentReply(
                "Brooding and futuristic, coming up.",
                new SearchIntent(["science-fiction"], ["futuristic"], [], "brooding futuristic", null, null)));

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

    /// <summary>
    /// An embedding provider with a latency per kind of call: the turn's concept (one text that is not a
    /// catalogue title) or a batch of title texts. Vectors are the same deterministic unit vectors.
    /// </summary>
    private sealed class DelayedEmbeddingProvider(TimeSpan conceptDelay, TimeSpan titlesDelay) : IEmbeddingProvider
    {
        public bool IsAvailable => true;

        public async Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
        {
            var titles = texts.Any(t => t.Contains("Timed Title", StringComparison.Ordinal)
                || t.Contains("Catalogued Title", StringComparison.Ordinal));
            var delay = titles ? titlesDelay : conceptDelay;
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, ct);
            }

            return texts.Select(t => Unit(t.Length)).ToArray();
        }
    }

    /// <summary>A keyless install — the condition that routes a turn down the lexical branch.</summary>
    private sealed class UnavailableEmbeddingProvider : IEmbeddingProvider
    {
        public bool IsAvailable => false;

        public Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct = default) =>
            throw new InvalidOperationException("No embedding provider is configured.");
    }

    private sealed class ScriptedTmdbClient(IReadOnlyList<TmdbListItem> rows, TimeSpan detailsDelay = default) : ITmdbClient
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

        public async Task<TmdbTitleDetails?> GetMovieAsync(long tmdbId, CancellationToken ct = default)
        {
            if (detailsDelay > TimeSpan.Zero)
            {
                await Task.Delay(detailsDelay, ct);
            }

            return null;
        }

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
