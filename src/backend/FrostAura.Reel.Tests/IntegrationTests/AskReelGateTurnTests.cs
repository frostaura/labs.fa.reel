using System.Text.Json;
using FrostAura.Reel.Application.Ingestion;
using FrostAura.Reel.Application.Ml;
using FrostAura.Reel.Application.Ml.ClosedLoop;
using FrostAura.Reel.Application.Ranking;
using FrostAura.Reel.Application.Search;
using FrostAura.Reel.Application.Search.AskReel;
using FrostAura.Reel.Application.Search.Retrieval;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Lens;
using FrostAura.Reel.Domain.Library;
using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Domain.Ports.Tmdb;
using FrostAura.Reel.Domain.Search;
using FrostAura.Reel.Domain.Sync;
using FrostAura.Reel.Domain.Tenancy;
using FrostAura.Reel.Infrastructure.Concurrency;
using FrostAura.Reel.Infrastructure.Ml;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrostAura.Reel.Tests.IntegrationTests;

/// <summary>
/// The interim relevance gate on a real turn (WS-2 task 1i/1j): what the judge's request verdict does
/// to every card, what a turn with nothing left says, and what a failed judge may never claim.
///
/// <para>The verdicts are scripted by title name — "Yes", "Partial", "No" — so the placement is
/// asserted on the wire, on the stored rows and in the judge's own call log, with the real pipeline
/// (eligibility, discovery, scoring, persistence projection) running underneath.</para>
/// </summary>
/// <remarks>
/// Inherits <see cref="EmbeddedCatalogTestBase"/>: a real turn embeds what it discovered, so this
/// class leaves vectors in the shared HNSW index exactly as a hand-seeding fixture does.
/// </remarks>
[Collection("postgres")]
public class AskReelGateTurnTests(PostgresFixture fixture) : EmbeddedCatalogTestBase(fixture)
{
    private const long TmdbIdFirst = 9_740_001;

    private static readonly JsonSerializerOptions WireJson = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Every verdict lands in its own place: yes → picks (with the model's why and verdict), partial →
    /// the collapsed near-miss tray, no → nowhere at all. The reply goes out once, before the first
    /// pick — never as the turn's opening frame, where it used to promise what the grid then lacked.
    /// </summary>
    [Fact]
    public async Task Every_card_lands_where_the_judge_put_it()
    {
        if (!Fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        await MarkAllExistingTitlesSeenAsync(accountId);
        var tmdb = new ScriptedTmdbClient(Discoverable(TmdbIdFirst, ("Yes", 4), ("Partial", 3), ("No", 3)));
        var agent = new VerdictAgent();
        var (service, events) = BuildService(accountId, tmdb, agent, out var emit);

        var outcome = await service.StreamAsync(
            "something brooding and futuristic", [], [], "US", [], ApiUsagePurpose.RerankAsk, emit, CancellationToken.None);

        var done = Assert.Single(events, e => e.Event == "done").Data;
        var picks = Names(done.GetProperty("results"));
        var nearMiss = Names(done.GetProperty("nearMiss"));

        Assert.Equal(4, picks.Count);
        Assert.All(picks, n => Assert.StartsWith("Gate Yes", n, StringComparison.Ordinal));
        Assert.Equal(3, nearMiss.Count);
        Assert.All(nearMiss, n => Assert.StartsWith("Gate Partial", n, StringComparison.Ordinal));
        Assert.DoesNotContain(
            events.Where(e => e.Event is "candidate").Select(e => e.Data.GetProperty("name").GetString()),
            n => n!.StartsWith("Gate No", StringComparison.Ordinal));
        Assert.Empty(done.GetProperty("belowFloor").EnumerateArray());
        Assert.Equal("Picks", done.GetProperty("mode").GetString());
        Assert.Equal("4 picks for you · 3 close but not quite — tucked away below", done.GetProperty("reason").GetString());

        // The model's sentence and verdict ride every pick; the reply precedes the first card.
        Assert.All(done.GetProperty("results").EnumerateArray(), card =>
        {
            Assert.Equal("yes", card.GetProperty("satisfies").GetString());
            Assert.Equal("Fits the request.", card.GetProperty("why").GetString());
        });
        var replyAt = events.FindIndex(e => e.Event == "assistant-message");
        var firstCardAt = events.FindIndex(e => e.Event == "candidate");
        Assert.True(replyAt >= 0 && replyAt < firstCardAt, "the reply must go out just before the first pick.");
        Assert.Single(events, e => e.Event == "assistant-message");

        // Stored: picks then the near-miss tray, never a `no`. Non-pick tiers keep BelowFloor=true so
        // an image older than the tier can never promote them.
        Assert.Equal(7, outcome.Cards.Count);
        Assert.Equal(4, outcome.Cards.Count(c => c.Tier == SearchTurnCardTier.Pick && !c.BelowFloor && c.Satisfies == "yes"));
        Assert.Equal(3, outcome.Cards.Count(c => c.Tier == SearchTurnCardTier.NearMiss && c.BelowFloor && c.Satisfies == "partial"));
        Assert.All(outcome.Cards.Where(c => c.Tier == SearchTurnCardTier.Pick), c => Assert.Equal("Llm", c.WhySource));
        Assert.Equal(Enumerable.Range(0, 7), outcome.Cards.Select(c => c.Rank));
        Assert.Equal(SearchTurnMode.Picks, outcome.Mode);
        Assert.Null(outcome.DegradedFlags);
        Assert.NotNull(outcome.StageTimingsJson);
        Assert.Contains("\"judge\":\"verdict-agent\"", outcome.ModelIdsJson, StringComparison.Ordinal);
        Assert.Equal(SearchTurnDegraded.ProviderDefaultEffort, outcome.ReasoningEffort);
    }

    /// <summary>
    /// The judge sees at most sixteen titles, in batches of at most six, with batches in flight
    /// together (B-7, C*1) — and each batch's picks reach the wire as that batch returns.
    /// </summary>
    [Fact]
    public async Task The_judge_is_bounded_and_concurrent()
    {
        if (!Fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        await MarkAllExistingTitlesSeenAsync(accountId);
        var tmdb = new ScriptedTmdbClient(Discoverable(TmdbIdFirst + 100, ("Yes", 30)));
        var agent = new VerdictAgent(delay: TimeSpan.FromMilliseconds(250));
        var (service, events) = BuildService(accountId, tmdb, agent, out var emit);

        await service.StreamAsync(
            "something brooding and futuristic", [], [], "US", [], ApiUsagePurpose.RerankAsk, emit, CancellationToken.None);

        Assert.Equal(AskReelJudge.MaxJudged, agent.Judged.Count);
        Assert.Equal(AskReelJudge.MaxJudged, agent.Judged.Distinct().Count());
        Assert.All(agent.BatchSizes, size => Assert.InRange(size, 1, AskReelJudge.BatchSize));
        Assert.Equal(3, agent.BatchSizes.Count);
        Assert.True(agent.MaxConcurrent >= 2, $"batches ran one at a time (max {agent.MaxConcurrent} in flight).");
        Assert.True(agent.MaxConcurrent <= AskReelJudge.MaxConcurrent);
        Assert.All(agent.Purposes, p => Assert.Equal(ApiUsagePurpose.RerankAsk, p));

        var done = Assert.Single(events, e => e.Event == "done").Data;
        Assert.Equal(AskReelJudge.MaxJudged, done.GetProperty("results").GetArrayLength());
    }

    /// <summary>
    /// A judge that failed claims nothing (§4.10): its cards are shown below every judged pick, in
    /// retrieval order, with no why-line and no fit; the turn says it is degraded; and nothing it
    /// returned is cached — the next identical turn asks the model again (A-8, C NEW-3).
    /// </summary>
    [Fact]
    public async Task A_failed_judge_shows_unjudged_cards_without_a_why_and_caches_nothing()
    {
        if (!Fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        await MarkAllExistingTitlesSeenAsync(accountId);
        var tmdb = new ScriptedTmdbClient(Discoverable(TmdbIdFirst + 200, ("Yes", 8)));
        var agent = new VerdictAgent(failEverything: true);
        var cache = new MemoryCache(new MemoryCacheOptions());
        var (service, events) = BuildService(accountId, tmdb, agent, out var emit, cache);

        var outcome = await service.StreamAsync(
            "something brooding and futuristic", [], [], "US", [], ApiUsagePurpose.RerankAsk, emit, CancellationToken.None);

        var done = Assert.Single(events, e => e.Event == "done").Data;
        Assert.Equal(8, done.GetProperty("results").GetArrayLength());
        Assert.All(done.GetProperty("results").EnumerateArray(), card =>
        {
            Assert.Equal(JsonValueKind.Null, card.GetProperty("why").ValueKind);
            Assert.Equal(JsonValueKind.Null, card.GetProperty("fit").ValueKind);
            Assert.Equal(JsonValueKind.Null, card.GetProperty("satisfies").ValueKind);
        });
        Assert.Equal(["judge:2/2"], done.GetProperty("degraded").EnumerateArray().Select(d => d.GetString()));
        Assert.Equal("judge:2/2", outcome.DegradedFlags);
        Assert.All(outcome.Cards, c => Assert.Equal("None", c.WhySource));
        Assert.All(outcome.Cards, c => Assert.Equal("Fallback", c.FitSource));
        Assert.DoesNotContain(
            events.Where(e => e.Event == "candidate").Select(e => e.Data.GetProperty("why")),
            why => why.ValueKind == JsonValueKind.String);

        var judgedOnce = agent.Judged.Count;
        events.Clear();
        await service.StreamAsync(
            "something brooding and futuristic", [], [], "US", [], ApiUsagePurpose.RerankAsk, emit, CancellationToken.None);
        Assert.Equal(judgedOnce * 2, agent.Judged.Count);
    }

    /// <summary>
    /// The model verdicts ARE cached: the same request a second time asks the judge about nothing.
    /// </summary>
    [Fact]
    public async Task Model_verdicts_are_reused_for_the_same_request()
    {
        if (!Fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        await MarkAllExistingTitlesSeenAsync(accountId);
        var tmdb = new ScriptedTmdbClient(Discoverable(TmdbIdFirst + 300, ("Yes", 5)));
        var agent = new VerdictAgent();
        var cache = new MemoryCache(new MemoryCacheOptions());
        var (service, _) = BuildService(accountId, tmdb, agent, out var emit, cache);

        await service.StreamAsync(
            "something brooding and futuristic", [], [], "US", [], ApiUsagePurpose.RerankAsk, emit, CancellationToken.None);
        var judgedOnce = agent.Judged.Count;
        await service.StreamAsync(
            "something brooding and futuristic", [], [], "US", [], ApiUsagePurpose.RerankAsk, emit, CancellationToken.None);

        Assert.True(judgedOnce > 0);
        Assert.Equal(judgedOnce, agent.Judged.Count);
    }

    /// <summary>
    /// Nothing matches: zero picks, mode noneFound, and the reply is the deterministic account of the
    /// supply — the "ninja" shape. Every topical title is seen, so the reply's seen count equals the
    /// independent count of the account's watched titles carrying the topic, and the model's own
    /// pre-retrieval sentence is never shown.
    /// </summary>
    [Fact]
    public async Task Nothing_left_is_an_honest_none_found_answer_with_the_supply_behind_it()
    {
        if (!Fixture.Available)
        {
            return;
        }

        var topic = $"gatetopic{Guid.NewGuid():N}"[..20];
        var accountId = await SeedAccountAsync();
        var topical = new List<Guid>();
        for (var i = 0; i < 5; i++)
        {
            topical.Add(await SeedTopicalTitleAsync(topic));
        }

        await MarkAllExistingTitlesSeenAsync(accountId);
        var tmdb = new ScriptedTmdbClient(Discoverable(TmdbIdFirst + 400, ("No", 6)));
        var agent = new VerdictAgent(keywords: [topic]);
        var (service, events) = BuildService(accountId, tmdb, agent, out var emit, withRetrieval: true);

        var outcome = await service.StreamAsync(
            $"{topic} movies", [], [], "US", [], ApiUsagePurpose.RerankAsk, emit, CancellationToken.None);

        var done = Assert.Single(events, e => e.Event == "done").Data;
        Assert.Equal("NoneFound", done.GetProperty("mode").GetString());
        Assert.Empty(done.GetProperty("results").EnumerateArray());
        Assert.DoesNotContain(events, e => e.Event == "candidate");

        int truth;
        await using (var db = Fixture.CreateContext(new AccountContext()))
        {
            truth = await db.WatchedTitles
                .Where(w => w.AccountId == accountId && db.Titles.Any(t => t.Id == w.TitleId && t.Keywords!.Contains(topic)))
                .CountAsync();
        }

        var supply = done.GetProperty("supply");
        Assert.Equal(truth, supply.GetProperty("alreadySeen").GetInt32());
        Assert.Equal(5, truth);
        Assert.Equal(0, supply.GetProperty("eligible").GetInt32());

        var reply = Assert.Single(events, e => e.Event == "assistant-message").Data.GetProperty("text").GetString()!;
        Assert.Equal(outcome.AssistantText, reply);
        Assert.DoesNotContain(VerdictAgent.Reply, reply, StringComparison.Ordinal);
        Assert.StartsWith($"There are no {topic} titles left for you.", reply, StringComparison.Ordinal);
        Assert.Contains($"you've seen {truth}", reply, StringComparison.Ordinal);
        Assert.Equal(SearchTurnMode.NoneFound, outcome.Mode);
        Assert.Empty(outcome.Cards);
        Assert.Equal(AskReelReason.Empty, done.GetProperty("reason").GetString());
    }

    /// <summary>
    /// A degraded reading (every interpret attempt failed) is shown as degraded, never cached — the
    /// next identical ask reads the request again — and recorded on the turn (A-14, B-5).
    /// </summary>
    [Fact]
    public async Task A_degraded_reading_is_labelled_and_never_cached()
    {
        if (!Fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        await MarkAllExistingTitlesSeenAsync(accountId);
        var tmdb = new ScriptedTmdbClient(Discoverable(TmdbIdFirst + 500, ("Yes", 3)));
        var agent = new VerdictAgent(degradedReading: true);
        var cache = new MemoryCache(new MemoryCacheOptions());
        var (service, events) = BuildService(accountId, tmdb, agent, out var emit, cache);

        var outcome = await service.StreamAsync(
            "no superheroes please", [], [], "US", [], ApiUsagePurpose.RerankAsk, emit, CancellationToken.None);
        await service.StreamAsync(
            "no superheroes please", [], [], "US", [], ApiUsagePurpose.RerankAsk, emit, CancellationToken.None);

        Assert.Equal(2, agent.Interprets);
        var reply = events.First(e => e.Event == "assistant-message").Data;
        Assert.True(reply.GetProperty("degraded").GetBoolean());
        Assert.Equal(AgentReply.DegradedReply, reply.GetProperty("text").GetString());
        Assert.DoesNotContain("no superheroes please", reply.GetProperty("text").GetString()!, StringComparison.Ordinal);
        Assert.Equal(SearchTurnDegraded.Plan, outcome.DegradedFlags);
        Assert.Contains("plan", events.First(e => e.Event == "done").Data.GetProperty("degraded").EnumerateArray().Select(d => d.GetString()));
    }

    /// <summary>
    /// "No superheroes please" (A-9): the user typed the plural, the catalogue labels the singular —
    /// as a keyword on some titles and as a GENRE on others. Neither may reach the picks; the
    /// contradicting titles are at most the flagged outside channel, captioned with the user's word.
    /// </summary>
    [Fact]
    public async Task A_plural_negation_keeps_singular_keyword_and_genre_matches_out_of_the_picks()
    {
        if (!Fixture.Available)
        {
            return;
        }

        var topic = $"gatefeel{Guid.NewGuid():N}"[..20];
        var accountId = await SeedAccountAsync();
        await MarkAllExistingTitlesSeenAsync(accountId);
        var heroes = new List<Guid>();
        for (var i = 0; i < 3; i++)
        {
            heroes.Add(await SeedTopicalTitleAsync(topic, t => { t.Name = $"Gate Yes keyword-hero {i}"; t.Keywords = [topic, "superhero"]; }));
            heroes.Add(await SeedTopicalTitleAsync(topic, t => { t.Name = $"Gate Yes genre-hero {i}"; t.Genres = ["superhero", "comedy"]; }));
        }

        for (var i = 0; i < 4; i++)
        {
            await SeedTopicalTitleAsync(topic, t => t.Name = $"Gate Yes calm {i}");
        }

        var agent = new VerdictAgent(keywords: [topic], requestedExclusions: ["superheroes"]);
        var (service, events) = BuildService(
            accountId, new ScriptedTmdbClient([]), agent, out var emit, withRetrieval: true);

        await service.StreamAsync(
            $"{topic}, no superheroes please", [], [], "US", [], ApiUsagePurpose.RerankAsk, emit, CancellationToken.None);

        var results = Assert.Single(events, e => e.Event == "done").Data.GetProperty("results").EnumerateArray().ToList();
        var picks = results.Where(r => !r.GetProperty("outsideRequest").GetBoolean()).ToList();
        Assert.Equal(4, picks.Count);
        Assert.All(picks, p => Assert.StartsWith("Gate Yes calm", p.GetProperty("name").GetString(), StringComparison.Ordinal));
        Assert.All(
            results.Where(r => r.GetProperty("outsideRequest").GetBoolean()),
            r => Assert.Equal(
                "Closest match we could find, but it's one that matches ‘superheroes’ — which you asked to skip.",
                r.GetProperty("why").GetString()));
    }

    // ── fixture ─────────────────────────────────────────────────────────────────────────────

    private static List<string> Names(JsonElement cards) =>
        cards.EnumerateArray().Select(c => c.GetProperty("name").GetString()!).ToList();

    private static IReadOnlyList<TmdbListItem> Discoverable(long firstTmdbId, params (string Verdict, int Count)[] groups)
    {
        var rows = new List<TmdbListItem>();
        var id = firstTmdbId;
        foreach (var (verdict, count) in groups)
        {
            for (var i = 0; i < count; i++, id++)
            {
                rows.Add(new TmdbListItem(
                    id, true, $"Gate {verdict} {id}", "/g.jpg", null,
                    "A brooding, futuristic search for what is real.", 40m - (id - firstTmdbId), 6.5m, 900,
                    new DateTime(2012, 6, 16, 0, 0, 0, DateTimeKind.Utc), [878], "en"));
            }
        }

        return rows;
    }

    private async Task<Guid> SeedTopicalTitleAsync(string topic, Action<Title>? shape = null)
    {
        await using var db = Fixture.CreateContext(new AccountContext());
        var title = new Title
        {
            Id = Guid.NewGuid(),
            MediaType = MediaType.Movie,
            TmdbId = Random.Shared.NextInt64(1_000_000_000, long.MaxValue),
            TraktSlug = string.Empty,
            Name = $"gate-topical {Guid.NewGuid():N}",
            Language = "en",
            Year = 2010,
            RuntimeMinutes = 100,
            PosterPath = "/p.jpg",
            Genres = ["action"],
            Keywords = [topic],
            TmdbVoteAverage = 7.5m,
            TmdbVoteCount = 1_000,
            TmdbPopularity = 0m,
            CreatedAt = DateTime.UtcNow,
        };
        shape?.Invoke(title);
        db.Add(title);
        await db.SaveChangesAsync();
        return title.Id;
    }

    private (LiveSearchExpansionService Service, List<(string Event, JsonElement Data)> Events) BuildService(
        Guid accountId, ITmdbClient tmdb, ISearchAgent agent, out LiveSearchExpansionService.Emit emit,
        IMemoryCache? cache = null, bool withRetrieval = false)
    {
        var scoped = new AccountContext();
        scoped.SetAccount(accountId);
        var db = Fixture.CreateContext(scoped);

        var eligibility = new EligibilityQueryBuilder(db);
        var featureBuilder = new FeatureVectorBuilder(db);
        var engine = new FastTreeModelEngine();
        var residualModel = new OnlineResidualModel(
            db, featureBuilder, engine, new MemoryCache(new MemoryCacheOptions()), NullLogger<OnlineResidualModel>.Instance);
        var supply = withRetrieval ? new SupplyReporter(db, eligibility) : null;
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
            cache ?? new MemoryCache(new MemoryCacheOptions()),
            NullLogger<LiveSearchExpansionService>.Instance,
            supply is null ? null : new FacetRetriever(db, eligibility, supply),
            supply);

        var events = new List<(string, JsonElement)>();
        emit = (name, payload) =>
        {
            lock (events)
            {
                // The endpoint's own serializer settings, so the shape asserted is the one a client gets.
                events.Add((name, JsonSerializer.SerializeToElement(payload, WireJson)));
            }

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
            TraktUserSlug = $"gate-{Guid.NewGuid():N}",
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

    /// <summary>
    /// A judge whose request verdict is read off the title's name, which records every call it gets:
    /// the titles, the batch sizes, the purposes, and how many calls were in flight at once.
    /// </summary>
    private sealed class VerdictAgent(
        TimeSpan? delay = null, bool failEverything = false, IReadOnlyList<string>? keywords = null,
        bool degradedReading = false, IReadOnlyList<string>? requestedExclusions = null) : ISearchAgent
    {
        public const string Reply = "Brooding and futuristic, coming right up.";

        private readonly object _gate = new();
        private int _inFlight;
        private int _interprets;

        public List<Guid> Judged { get; } = [];

        public List<int> BatchSizes { get; } = [];

        public List<string> Purposes { get; } = [];

        public int MaxConcurrent { get; private set; }

        public int Interprets => Volatile.Read(ref _interprets);

        public bool IsAvailable => true;

        public string ModelId => "verdict-agent";

        public Task<AgentReply> InterpretAsync(
            IReadOnlyList<ChatTurn> history, string message, string tasteSummary, IReadOnlyList<string> shownTitles,
            CancellationToken ct = default, string? standingFilters = null)
        {
            Interlocked.Increment(ref _interprets);
            var intent = new SearchIntent(["science-fiction"], keywords ?? ["futuristic"], [], "brooding futuristic", null, null);
            // The user's own reading carries the negation — the only source a negation may come from.
            var requested = intent with { ExcludeKeywords = requestedExclusions };
            return Task.FromResult(degradedReading
                ? new AgentReply(AgentReply.DegradedReply, intent, requested, Degraded: true, AgentFailureKind.Truncated)
                : new AgentReply(Reply, intent, requested));
        }

        public async Task<IReadOnlyList<RerankResult>> RerankBatchAsync(
            IReadOnlyList<RerankInput> titles, string query, LensPersona persona, string? anchorName = null,
            string usagePurpose = ApiUsagePurpose.Rerank, CancellationToken ct = default)
        {
            lock (_gate)
            {
                _inFlight++;
                MaxConcurrent = Math.Max(MaxConcurrent, _inFlight);
                Judged.AddRange(titles.Select(t => t.TitleId));
                BatchSizes.Add(titles.Count);
                Purposes.Add(usagePurpose);
            }

            try
            {
                if (delay is { } wait)
                {
                    await Task.Delay(wait, ct);
                }

                if (failEverything)
                {
                    return titles
                        .Select(t => new RerankResult(
                            t.TitleId, 0.66d, ResultSource.Fallback, "A science-fiction pick aligned with your taste.", ResultSource.Fallback))
                        .ToList();
                }

                return titles.Select(t => new RerankResult(
                        t.TitleId, 0.8d, ResultSource.Llm, "Fits the request.", ResultSource.Llm, VerdictOf(t.Name)))
                    .ToList();
            }
            finally
            {
                lock (_gate)
                {
                    _inFlight--;
                }
            }
        }

        private static Satisfies? VerdictOf(string name) =>
            name.StartsWith("Gate Yes", StringComparison.Ordinal) ? Satisfies.Yes
            : name.StartsWith("Gate Partial", StringComparison.Ordinal) ? Satisfies.Partial
            : name.StartsWith("Gate No", StringComparison.Ordinal) ? Satisfies.No
            : Satisfies.No;
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
