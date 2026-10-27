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
using FrostAura.Reel.Domain.Filters;
using FrostAura.Reel.Domain.Lens;
using FrostAura.Reel.Domain.Library;
using FrostAura.Reel.Domain.Ml;
using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Domain.Ports.Tmdb;
using FrostAura.Reel.Domain.Search;
using FrostAura.Reel.Domain.Sync;
using FrostAura.Reel.Domain.Tenancy;
using FrostAura.Reel.Infrastructure.Adapters;
using FrostAura.Reel.Infrastructure.Concurrency;
using FrostAura.Reel.Infrastructure.Ml;
using FrostAura.Reel.Infrastructure.Telemetry;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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
    /// A judge that failed claims nothing (§4.10), and with no card judged a match the turn is NOT an
    /// answer (§4.8; D1): mode NoneFound, an empty grid, every card in the "not checked" tray with no
    /// why-line, no fit and no verdict, a reply that says the cards went unchecked rather than the
    /// model's pre-retrieval sentence, and nothing cached — the next identical turn asks again.
    /// </summary>
    [Fact]
    public async Task A_failed_judge_with_no_judged_pick_is_none_found_with_an_unchecked_tray()
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
        Assert.Equal("NoneFound", done.GetProperty("mode").GetString());
        Assert.Empty(done.GetProperty("results").EnumerateArray());
        Assert.DoesNotContain(events, e => e.Event == "candidate");
        Assert.Equal(8, done.GetProperty("unchecked").GetArrayLength());
        Assert.All(done.GetProperty("unchecked").EnumerateArray(), card =>
        {
            Assert.Equal(JsonValueKind.Null, card.GetProperty("why").ValueKind);
            Assert.Equal(JsonValueKind.Null, card.GetProperty("fit").ValueKind);
            Assert.Equal(JsonValueKind.Null, card.GetProperty("satisfies").ValueKind);
        });
        Assert.Equal(["judge:2/2", "judge-missing:8/8"], done.GetProperty("degraded").EnumerateArray().Select(d => d.GetString()));
        Assert.Equal("8 not checked against what you asked — shown below", done.GetProperty("reason").GetString());

        var reply = Assert.Single(events, e => e.Event == "assistant-message").Data.GetProperty("text").GetString()!;
        Assert.DoesNotContain(VerdictAgent.Reply, reply, StringComparison.Ordinal);
        Assert.Contains("couldn't check", reply, StringComparison.Ordinal);
        Assert.Contains("8 titles I couldn't check against your request are below", reply, StringComparison.Ordinal);

        Assert.Equal(SearchTurnMode.NoneFound, outcome.Mode);
        Assert.Equal("judge:2/2,judge-missing:8/8", outcome.DegradedFlags);
        Assert.Equal(8, outcome.Cards.Count);
        Assert.All(outcome.Cards, c =>
        {
            Assert.Equal(SearchTurnCardTier.Unchecked, c.Tier);
            Assert.True(c.BelowFloor, "a non-grid tier must keep BelowFloor=true so an older image never promotes it.");
            Assert.Equal("None", c.WhySource);
            Assert.Equal("Fallback", c.FitSource);
        });

        var judgedOnce = agent.Judged.Count;
        events.Clear();
        await service.StreamAsync(
            "something brooding and futuristic", [], [], "US", [], ApiUsagePurpose.RerankAsk, emit, CancellationToken.None);
        Assert.Equal(judgedOnce * 2, agent.Judged.Count);
    }

    /// <summary>
    /// The other half of §4.10: with at least one judged match, cards whose batch failed are shown —
    /// after every judged pick, with no why-line — and the reason line counts them apart rather than
    /// as matching what was asked (D10).
    /// </summary>
    [Fact]
    public async Task Unjudged_cards_trail_the_judged_picks_and_are_never_counted_as_matches()
    {
        if (!Fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        await MarkAllExistingTitlesSeenAsync(accountId);
        var tmdb = new ScriptedTmdbClient(Discoverable(TmdbIdFirst + 250, ("Yes", 3), ("Fail", 4)));
        var agent = new VerdictAgent();
        var (service, events) = BuildService(accountId, tmdb, agent, out var emit);

        var outcome = await service.StreamAsync(
            "something brooding and futuristic", [], [], "US", [], ApiUsagePurpose.RerankAsk, emit, CancellationToken.None);

        var done = Assert.Single(events, e => e.Event == "done").Data;
        Assert.Equal("Picks", done.GetProperty("mode").GetString());
        var results = done.GetProperty("results").EnumerateArray().ToList();
        Assert.Equal(7, results.Count);
        Assert.All(results.Take(3), c => Assert.Equal("yes", c.GetProperty("satisfies").GetString()));
        Assert.All(results.Skip(3), c =>
        {
            Assert.StartsWith("Gate Fail", c.GetProperty("name").GetString(), StringComparison.Ordinal);
            Assert.Equal(JsonValueKind.Null, c.GetProperty("satisfies").ValueKind);
            Assert.Equal(JsonValueKind.Null, c.GetProperty("why").ValueKind);
        });
        Assert.Empty(done.GetProperty("unchecked").EnumerateArray());
        Assert.Equal("3 picks for you · 4 more not checked against what you asked", done.GetProperty("reason").GetString());
        // R1-01: every title without a verdict counts, not only a batch that answered for none.
        Assert.Contains("judge-missing:4/7", done.GetProperty("degraded").EnumerateArray().Select(d => d.GetString()));
        Assert.Equal(7, outcome.Cards.Count(c => c.Tier == SearchTurnCardTier.Pick));
    }

    /// <summary>
    /// D2: the judge rules on the request in its conversation. "more please" after ninja films and
    /// after romcoms are different requests, so the second conversation is judged afresh rather than
    /// handed the first one's verdicts from the cache — while the same follow-up in the same context
    /// still reuses them. The judge is told the earlier ask and the resolved reading.
    /// </summary>
    [Fact]
    public async Task The_judge_rules_on_the_request_in_its_conversation_and_caches_by_it()
    {
        if (!Fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        await MarkAllExistingTitlesSeenAsync(accountId);
        var tmdb = new ScriptedTmdbClient(Discoverable(TmdbIdFirst + 600, ("Yes", 5)));
        var agent = new VerdictAgent();
        var cache = new MemoryCache(new MemoryCacheOptions());
        var (service, _) = BuildService(accountId, tmdb, agent, out var emit, cache);

        IReadOnlyList<ChatTurn> Thread(string ask) =>
            [new ChatTurn("user", ask), new ChatTurn("assistant", "Here you go.")];

        await service.StreamAsync("more please", Thread("ninja movies"), [], "US", [], emit, CancellationToken.None);
        var afterNinja = agent.Judged.Count;
        Assert.True(afterNinja > 0);
        Assert.All(agent.Queries, q =>
        {
            Assert.StartsWith("more please\n", q, StringComparison.Ordinal);
            Assert.Contains("Earlier in this conversation the user asked: “ninja movies”", q, StringComparison.Ordinal);
            Assert.Contains("Read in context, they want", q, StringComparison.Ordinal);
        });

        await service.StreamAsync("more please", Thread("romantic comedies"), [], "US", [], emit, CancellationToken.None);
        Assert.True(agent.Judged.Count > afterNinja, "a different conversation's verdicts were reused from the cache.");
        Assert.Contains(agent.Queries, q => q.Contains("“romantic comedies”", StringComparison.Ordinal));

        var afterRomcom = agent.Judged.Count;
        await service.StreamAsync("more please", Thread("ninja movies"), [], "US", [], emit, CancellationToken.None);
        Assert.Equal(afterRomcom, agent.Judged.Count);
    }

    /// <summary>
    /// D3 (§4.8): a card with no prediction never clears a set bar. With a 6.0 bar and no model to
    /// score anything, every judged match is held back with the under-bar cards, nothing reaches the
    /// grid, and the turn says so — the gate and the supply's ClearingFloor agree.
    /// </summary>
    [Fact]
    public async Task An_unscored_card_never_clears_a_set_bar()
    {
        if (!Fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        await MarkAllExistingTitlesSeenAsync(accountId);
        await SetRatingFloorAsync(accountId, "6.0");
        var tmdb = new ScriptedTmdbClient(Discoverable(TmdbIdFirst + 700, ("Yes", 4)));
        var agent = new VerdictAgent();
        var (service, events) = BuildService(accountId, tmdb, agent, out var emit);

        var outcome = await service.StreamAsync(
            "something brooding and futuristic", [], [], "US", [], ApiUsagePurpose.RerankAsk, emit, CancellationToken.None);

        var done = Assert.Single(events, e => e.Event == "done").Data;
        Assert.Equal("NoneFound", done.GetProperty("mode").GetString());
        Assert.Empty(done.GetProperty("results").EnumerateArray());
        Assert.Equal(4, done.GetProperty("belowFloor").GetArrayLength());
        Assert.All(done.GetProperty("belowFloor").EnumerateArray(), c =>
            Assert.Equal(JsonValueKind.Null, c.GetProperty("predictedRating").ValueKind));
        Assert.Equal("no picks matched what you asked · 4 held back by your 6.0 bar — shown below", done.GetProperty("reason").GetString());
        Assert.All(outcome.Cards, c => Assert.Equal(SearchTurnCardTier.BelowFloor, c.Tier));
        Assert.DoesNotContain(events, e => e.Event == "candidate");
        Assert.Contains("have no score from your model yet, so your 6.0 bar holds them back", outcome.AssistantText, StringComparison.Ordinal);
        Assert.DoesNotContain("Under your", outcome.AssistantText, StringComparison.Ordinal);
    }

    /// <summary>
    /// D4: titles the conversation already showed were excluded before anything was judged, so the
    /// reply names them as shown — never "N unseen … none of the ones I checked is a match". And the
    /// supply rides the wire with every enum as its NAME (acceptance D1): a numeric
    /// <c>kind</c> made the client drop the whole <c>done</c> frame and spin forever.
    /// </summary>
    [Fact]
    public async Task Titles_already_shown_here_are_named_as_shown_and_the_supply_kind_is_a_name()
    {
        if (!Fixture.Available)
        {
            return;
        }

        var topic = $"gateshown{Guid.NewGuid():N}"[..20];
        var accountId = await SeedAccountAsync();
        await MarkAllExistingTitlesSeenAsync(accountId);
        var shown = new List<Guid>();
        for (var i = 0; i < 3; i++)
        {
            shown.Add(await SeedTopicalTitleAsync(topic));
        }

        // One more topical title, held out by a standing genre rule: the relaxation offer.
        await SeedTopicalTitleAsync(topic, t => t.Genres = ["horror"]);
        await AddFilterAsync(accountId, FilterKind.ExcludeGenre, "horror");
        await SeedScoresAsync(accountId, shown, 8.0m);

        var tmdb = new ScriptedTmdbClient(Discoverable(TmdbIdFirst + 800, ("No", 4)));
        var agent = new VerdictAgent(keywords: [topic]);
        var (service, events) = BuildService(accountId, tmdb, agent, out var emit, withRetrieval: true);

        var outcome = await service.StreamAsync(
            new LiveSearchExpansionService.AskTurnInput(
                $"{topic} movies", [], shown, [], "US", [], ApiUsagePurpose.RerankAsk),
            emit, CancellationToken.None);

        var done = Assert.Single(events, e => e.Event == "done").Data;
        Assert.Equal("NoneFound", done.GetProperty("mode").GetString());
        var supply = done.GetProperty("supply");
        Assert.Equal(3, supply.GetProperty("clearingFloor").GetInt32());
        var knockout = Assert.Single(supply.GetProperty("knockouts").EnumerateArray());
        Assert.Equal(JsonValueKind.String, knockout.GetProperty("kind").ValueKind);
        Assert.Equal("ExcludeGenre", knockout.GetProperty("kind").GetString());
        Assert.Equal(JsonValueKind.String, knockout.GetProperty("sample")[0].GetProperty("media").ValueKind);

        Assert.StartsWith(
            $"All 3 unseen {topic} titles inside your conditions were already shown in this conversation.",
            outcome.AssistantText,
            StringComparison.Ordinal);
        Assert.DoesNotContain("none of the ones I checked", outcome.AssistantText, StringComparison.Ordinal);
    }

    /// <summary>
    /// The acceptance's "feel-good movie" → "no superheroes please": a follow-up that names no media
    /// keeps the conversation's single media type, and the judge is told so. One that names media
    /// is read as it says.
    /// </summary>
    [Fact]
    public async Task A_follow_up_silent_about_media_keeps_the_previous_reading_of_it()
    {
        if (!Fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        await MarkAllExistingTitlesSeenAsync(accountId);
        var tmdb = new ScriptedTmdbClient(Discoverable(TmdbIdFirst + 900, ("Yes", 3)));
        var agent = new VerdictAgent();
        var (service, _) = BuildService(accountId, tmdb, agent, out var emit);
        var previous = new SearchIntent(["comedy"], ["feel-good"], [MediaType.Movie], "feel-good movie", null, null);

        var carried = await service.StreamAsync(
            new LiveSearchExpansionService.AskTurnInput(
                "no superheroes please", [], [], [], "US", [], ApiUsagePurpose.RerankAsk, PriorIntent: previous),
            emit, CancellationToken.None);
        Assert.Equal([MediaType.Movie], AskReelCarry.Parse(carried.IntentJson)!.MediaTypes);
        Assert.All(agent.Queries, q => Assert.Contains("they want a movie;", q, StringComparison.Ordinal));

        var named = await service.StreamAsync(
            new LiveSearchExpansionService.AskTurnInput(
                "any shows instead?", [], [], [], "US", [], ApiUsagePurpose.RerankAsk, PriorIntent: previous),
            emit, CancellationToken.None);
        Assert.Empty(AskReelCarry.Parse(named.IntentJson)!.MediaTypes);
    }

    /// <summary>
    /// D7: judging has a deadline. A provider that never answers costs its batches (unjudged, the
    /// degraded flag), never the turn — which ends promptly instead of waiting on the adapter's
    /// HTTP timeout.
    /// </summary>
    [Fact]
    public async Task A_judge_that_never_answers_ends_the_turn_at_the_deadline()
    {
        if (!Fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        await MarkAllExistingTitlesSeenAsync(accountId);
        var tmdb = new ScriptedTmdbClient(Discoverable(TmdbIdFirst + 1000, ("Yes", 7)));
        var agent = new VerdictAgent(delay: TimeSpan.FromMinutes(5));
        var cache = new MemoryCache(new MemoryCacheOptions());
        var judge = new AskReelJudge(agent, cache, NullLogger<AskReelJudge>.Instance) { StageDeadline = TimeSpan.FromMilliseconds(400) };
        var (service, events) = BuildService(accountId, tmdb, agent, out var emit, cache, judge: judge);

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var outcome = await service.StreamAsync(
            "something brooding and futuristic", [], [], "US", [], ApiUsagePurpose.RerankAsk, emit, CancellationToken.None);
        clock.Stop();

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(30), $"the turn waited {clock.Elapsed} on a silent judge.");
        Assert.Equal("judge:2/2,judge-missing:7/7", outcome.DegradedFlags);
        Assert.Equal(SearchTurnMode.NoneFound, outcome.Mode);
        Assert.Equal(7, Assert.Single(events, e => e.Event == "done").Data.GetProperty("unchecked").GetArrayLength());
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
    /// IREV2-SYS-02 wired through the turn (WS-1 CR-1): a degraded reading replies with the copy for
    /// ITS cause — a spent allowance names the reset at the account's midnight, a 402 names the empty account, a 401
    /// the rejected key — never "try again in a moment", and the cause rides as its own
    /// <c>plan-cause:</c> token beside <c>plan</c>, so history still reads the turn as plan-failed.
    /// </summary>
    [Theory]
    [InlineData(AgentFailureKind.BudgetSpent, "budget_spent", AskReelDegradedCopy.PlanBudgetSpentReply)]
    [InlineData(AgentFailureKind.ProviderRefused, "payment_required", AgentReply.OutOfCreditReply)]
    [InlineData(AgentFailureKind.ProviderRefused, "unauthorized", AgentReply.KeyRejectedReply)]
    public async Task A_degraded_reading_replies_with_its_cause_and_records_it_beside_the_plan_flag(
        AgentFailureKind failure, string code, string expected)
    {
        if (!Fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        await MarkAllExistingTitlesSeenAsync(accountId);
        // Its own TMDB ids per row: sharing the labelled-degraded test's ids made whichever ran second see
        // those titles as already in the catalogue — and so as seen — and answer none-found.
        var tmdb = new ScriptedTmdbClient(Discoverable(TmdbIdFirst + 3000 + (code[0] * 10), ("Yes", 3)));
        var agent = new VerdictAgent(degradedReading: true, degradedFailure: failure, degradedCode: code);
        var (service, events) = BuildService(accountId, tmdb, agent, out var emit, new MemoryCache(new MemoryCacheOptions()));

        var outcome = await service.StreamAsync(
            "no superheroes please", [], [], "US", [], ApiUsagePurpose.RerankAsk, emit, CancellationToken.None);

        var text = events.First(e => e.Event == "assistant-message").Data.GetProperty("text").GetString()!;
        Assert.StartsWith(expected, text, StringComparison.Ordinal);
        Assert.DoesNotContain(AgentReply.DegradedReply, text, StringComparison.Ordinal);
        Assert.DoesNotContain("no superheroes please", text, StringComparison.Ordinal);
        Assert.Equal(outcome.AssistantText, text);
        // IREV3-SYS-03: only the plan allowance is spent, and the same turn judged — so the reply never
        // says the whole Ask allowance is used up.
        Assert.DoesNotContain(AgentReply.BudgetSpentReply, text, StringComparison.Ordinal);
        Assert.True(SearchTurnDegraded.PlanFailed(outcome.DegradedFlags));
        Assert.Contains($"{SearchTurnDegraded.PlanCausePrefix}{code}", outcome.DegradedFlags!.Split(','));
        var done = events.First(e => e.Event == "done").Data.GetProperty("degraded").EnumerateArray().Select(d => d.GetString()).ToList();
        Assert.Contains(SearchTurnDegraded.Plan, done);
        Assert.Contains($"{SearchTurnDegraded.PlanCausePrefix}{code}", done);
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

    /// <summary>
    /// IREV1-SYS-03: the same turn, but the requested exclusions come from the REAL heuristic rather
    /// than a hand-written list. The heuristic stores the vocabulary's singular ("superhero"), so the
    /// picks drop both the keyword and the genre matches and the caption quotes the singular.
    /// </summary>
    [Fact]
    public async Task A_plural_negation_read_by_the_real_heuristic_keeps_singular_matches_out_of_the_picks()
    {
        if (!Fixture.Available)
        {
            return;
        }

        var topic = $"gatefeel{Guid.NewGuid():N}"[..20];
        var query = $"{topic}, no superheroes please";
        var requested = RealHeuristic(query).ExcludeKeywords;
        Assert.Equal(["superhero"], requested);

        var accountId = await SeedAccountAsync();
        await MarkAllExistingTitlesSeenAsync(accountId);
        for (var i = 0; i < 3; i++)
        {
            await SeedTopicalTitleAsync(topic, t => { t.Name = $"Gate Yes keyword-hero {i}"; t.Keywords = [topic, "superhero"]; });
            await SeedTopicalTitleAsync(topic, t => { t.Name = $"Gate Yes genre-hero {i}"; t.Genres = ["superhero", "comedy"]; });
        }

        for (var i = 0; i < 4; i++)
        {
            await SeedTopicalTitleAsync(topic, t => t.Name = $"Gate Yes calm {i}");
        }

        var agent = new VerdictAgent(keywords: [topic], requestedExclusions: requested);
        var (service, events) = BuildService(
            accountId, new ScriptedTmdbClient([]), agent, out var emit, withRetrieval: true);

        await service.StreamAsync(query, [], [], "US", [], ApiUsagePurpose.RerankAsk, emit, CancellationToken.None);

        var results = Assert.Single(events, e => e.Event == "done").Data.GetProperty("results").EnumerateArray().ToList();
        var picks = results.Where(r => !r.GetProperty("outsideRequest").GetBoolean()).ToList();
        Assert.Equal(4, picks.Count);
        Assert.All(picks, p => Assert.StartsWith("Gate Yes calm", p.GetProperty("name").GetString(), StringComparison.Ordinal));
        Assert.All(
            results.Where(r => r.GetProperty("outsideRequest").GetBoolean()),
            r => Assert.Equal(
                "Closest match we could find, but it's one that matches ‘superhero’ — which you asked to skip.",
                r.GetProperty("why").GetString()));
    }

    /// <summary>The production heuristic, with no network: it reads the sentence locally.</summary>
    private static SearchIntent RealHeuristic(string query)
    {
        using var http = new HttpClient();
        var recorder = new ApiUsageRecorder(
            new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            NullLogger<ApiUsageRecorder>.Instance);
        return new OpenRouterSearchInterpreter(
                http, recorder, new ConfigurationBuilder().Build(), NullLogger<OpenRouterSearchInterpreter>.Instance)
            .Heuristic(query);
    }

    // ── Round 1 (R1-01, R1-02, R1-04, R1-09, R1-10) ─────────────────────────────────────────────

    /// <summary>
    /// R1-01 and XM-06: a card nobody judged is never presented as a MATCH the bar held back, and the
    /// bar binds it all the same. Under a bar no card clears, the judged matches go to the floor tray
    /// and are named under the bar; the cards the judge skipped — a skipped entry in an answered batch
    /// and a whole failed batch alike — go to the floor tray too, with no verdict, are counted in the
    /// reply as unchecked (never named), and raise the missing-verdict flag. The unchecked tray holds
    /// only unchecked cards that clear the bar, so none of it is under the bar.
    /// </summary>
    [Fact]
    public async Task An_unjudged_card_under_the_bar_is_held_back_by_it_but_never_named_as_a_match()
    {
        if (!Fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        await MarkAllExistingTitlesSeenAsync(accountId);
        await SeedScoresAsync(accountId, [], 0m);
        await SetRatingFloorAsync(accountId, "9.9");
        // Two judged matches and three titles the judge skips (a fallback entry in an answered batch).
        var tmdb = new ScriptedTmdbClient(Discoverable(TmdbIdFirst + 1400, ("Yes", 2), ("Fail", 3)));
        var agent = new VerdictAgent();
        var (service, events) = BuildService(accountId, tmdb, agent, out var emit);

        var outcome = await service.StreamAsync(
            "something brooding and futuristic", [], [], "US", [], ApiUsagePurpose.RerankAsk, emit, CancellationToken.None);

        var done = Assert.Single(events, e => e.Event == "done").Data;
        Assert.Equal("NoneFound", done.GetProperty("mode").GetString());
        var held = Names(done.GetProperty("belowFloor"));
        Assert.Empty(done.GetProperty("unchecked").EnumerateArray());
        Assert.Equal(5, held.Count);
        Assert.All(held.Take(2), n => Assert.StartsWith("Gate Yes", n, StringComparison.Ordinal));
        Assert.All(held.Skip(2), n => Assert.StartsWith("Gate Fail", n, StringComparison.Ordinal));
        Assert.Equal(
            ["yes", "yes", null, null, null],
            done.GetProperty("belowFloor").EnumerateArray().Select(c => c.GetProperty("satisfies").GetString()));
        Assert.Equal(
            "5 held back by your 9.9 bar, 3 of them not checked against what you asked — shown below",
            done.GetProperty("reason").GetString());
        // Which batch the skipped titles fall in follows the similarity order; the missing count does not.
        Assert.Contains("judge-missing:3/5", done.GetProperty("degraded").EnumerateArray().Select(d => d.GetString()));

        Assert.Contains("Under your 9.9 bar: Gate Yes", outcome.AssistantText, StringComparison.Ordinal);
        Assert.Contains("3 titles I couldn't check against your request are below", outcome.AssistantText, StringComparison.Ordinal);
        Assert.DoesNotContain("Gate Fail", outcome.AssistantText, StringComparison.Ordinal);
        Assert.Equal(2, outcome.Cards.Count(c => c.Tier == SearchTurnCardTier.BelowFloor && c.Satisfies == "yes"));
        Assert.Equal(3, outcome.Cards.Count(c => c.Tier == SearchTurnCardTier.BelowFloor && c.Satisfies is null));
        Assert.DoesNotContain(outcome.Cards, c => c.Tier == SearchTurnCardTier.Unchecked);
    }

    /// <summary>
    /// R1-01's other half: on a turn that found no match, the partial matches are the closest thing
    /// it did find, so the reply names them — the samurai turn's "Blade of the 47 Ronin" sat in the
    /// near-miss tray of a reply that never said so.
    /// </summary>
    [Theory]
    [InlineData(1, "Close, but not quite: Gate Partial {0} — tucked away below.")]
    [InlineData(2, "Close, but not quite: Gate Partial {0} and Gate Partial {1} — tucked away below.")]
    [InlineData(5, "Close, but not quite: Gate Partial {0}, Gate Partial {1}, Gate Partial {2} and 2 more — tucked away below.")]
    public async Task A_none_found_reply_names_its_near_misses(int partials, string expected)
    {
        if (!Fixture.Available)
        {
            return;
        }

        var first = TmdbIdFirst + 1500 + (partials * 10);
        var accountId = await SeedAccountAsync();
        await MarkAllExistingTitlesSeenAsync(accountId);
        var tmdb = new ScriptedTmdbClient(Discoverable(first, ("Partial", partials), ("No", 2)));
        var (service, events) = BuildService(accountId, tmdb, new VerdictAgent(), out var emit);

        var outcome = await service.StreamAsync(
            "something brooding and futuristic", [], [], "US", [], ApiUsagePurpose.RerankAsk, emit, CancellationToken.None);

        var done = Assert.Single(events, e => e.Event == "done").Data;
        Assert.Equal("NoneFound", done.GetProperty("mode").GetString());
        var tray = Names(done.GetProperty("nearMiss"));
        Assert.Equal(partials, tray.Count);
        Assert.Contains(string.Format(System.Globalization.CultureInfo.InvariantCulture, expected, [.. tray.Select(n => n["Gate Partial ".Length..])]),
            outcome.AssistantText, StringComparison.Ordinal);
    }

    /// <summary>
    /// R1-02: the supply counts, and the reply names, the topic the USER said — not the union of the
    /// model's keywords under its first one's name. The ninja follow-up came back as
    /// [ninja, martial arts, ninja warrior]; the old reply counted all three and called them "ninja
    /// movies". Varied by where the user's word is: this message, an earlier ask (a follow-up that
    /// names no topic), or nowhere — and then every keyword counted is named.
    /// </summary>
    [Theory]
    [InlineData("none of the above are {0} movies. I want {0} movies", false, 2, "{0} titles")]
    [InlineData("more like that please", true, 2, "{0} titles")]
    [InlineData("something else entirely", false, 6, "{0}, {1} arts or {0} warrior titles")]
    public async Task The_supply_counts_and_names_the_topic_the_user_said(
        string messageShape, bool askedEarlier, int matching, string topicShape)
    {
        if (!Fixture.Available)
        {
            return;
        }

        var ninja = $"ninjaq{Guid.NewGuid():N}"[..14];
        var martial = $"martq{Guid.NewGuid():N}"[..13];
        var accountId = await SeedAccountAsync();
        for (var i = 0; i < 2; i++)
        {
            await SeedTopicalTitleAsync(ninja);
        }

        await MarkAllExistingTitlesSeenAsync(accountId);
        for (var i = 0; i < 4; i++)
        {
            await SeedTopicalTitleAsync($"{martial} arts");
        }

        var tmdb = new ScriptedTmdbClient(Discoverable(TmdbIdFirst + 1600 + (matching * 3) + (askedEarlier ? 1 : 0), ("No", 3)));
        var agent = new VerdictAgent(keywords: [ninja, $"{martial} arts", $"{ninja} warrior"]);
        var (service, events) = BuildService(accountId, tmdb, agent, out var emit, withRetrieval: true);
        IReadOnlyList<ChatTurn> history = askedEarlier
            ? [new ChatTurn("user", $"{ninja} movies"), new ChatTurn("assistant", "Nothing new.")]
            : [];

        var outcome = await service.StreamAsync(
            new LiveSearchExpansionService.AskTurnInput(
                string.Format(System.Globalization.CultureInfo.InvariantCulture, messageShape, ninja), history, [], [], "US", [],
                ApiUsagePurpose.RerankAsk),
            emit, CancellationToken.None);

        var done = Assert.Single(events, e => e.Event == "done").Data;
        Assert.Equal("NoneFound", done.GetProperty("mode").GetString());
        Assert.Equal(matching, done.GetProperty("supply").GetProperty("matchingInCatalogue").GetInt32());
        var topic = string.Format(System.Globalization.CultureInfo.InvariantCulture, topicShape, ninja, martial);
        Assert.Contains($"{matching} {topic} in the catalogue", outcome.AssistantText, StringComparison.Ordinal);
        if (matching == 2)
        {
            Assert.DoesNotContain(martial, outcome.AssistantText, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// R1-04: a pointer with nothing on screen to point at is asked back — no model call, no judge
    /// call, no search — and a pointer some answer CAN satisfy is left to the model. Varied over no
    /// answers at all, an answer too short for the ordinal, and an answer long enough.
    /// </summary>
    [Theory]
    [InlineData("more like the second one you showed me first time round", 0, true, "nothing on screen yet for “the second one”")]
    [InlineData("is the third one violent?", 2, true, "at most 2 titles at a time, so there’s no third one")]
    [InlineData("more like the second one", 3, false, null)]
    public async Task A_pointer_with_nothing_to_point_at_is_asked_back_without_a_search(
        string message, int presentedCards, bool clarifies, string? replyPart)
    {
        if (!Fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        await MarkAllExistingTitlesSeenAsync(accountId);
        var tmdb = new ScriptedTmdbClient(Discoverable(TmdbIdFirst + 1700 + presentedCards, ("Yes", 2)));
        var agent = new VerdictAgent();
        var (service, events) = BuildService(accountId, tmdb, agent, out var emit);
        var presented = Enumerable.Range(1, presentedCards)
            .Select(rank => new PresentedCard(1, rank, Guid.NewGuid(), $"Shown {rank}", 2012, MediaType.Movie))
            .ToList();

        var outcome = await service.StreamAsync(
            new LiveSearchExpansionService.AskTurnInput(message, [], [], presented, "US", [], ApiUsagePurpose.RerankAsk),
            emit, CancellationToken.None);

        var done = Assert.Single(events, e => e.Event == "done").Data;
        if (clarifies)
        {
            Assert.Equal(SearchTurnMode.Clarify, done.GetProperty("mode").GetString());
            Assert.Equal(SearchTurnMode.Clarify, outcome.Mode);
            Assert.Equal(0, agent.Interprets);
            Assert.Empty(agent.Judged);
            Assert.Empty(outcome.Cards);
            Assert.Null(outcome.IntentJson);
            Assert.Contains(replyPart!, outcome.AssistantText, StringComparison.Ordinal);
            Assert.DoesNotContain("movies tagged", outcome.AssistantText, StringComparison.Ordinal);
        }
        else
        {
            Assert.Equal(1, agent.Interprets);
            Assert.Equal(SearchTurnMode.Picks, outcome.Mode);
        }
    }

    /// <summary>
    /// R1-09: the turn records the effort its calls are SENT at, read from the purpose catalog — not
    /// a hard-coded "provider-default". Both at none is "none"; a judge configured differently is
    /// written as plan/judge.
    /// </summary>
    [Theory]
    [InlineData(null, "none")]
    [InlineData("low", "none/low")]
    [InlineData("omit", "none/provider-default")]
    public async Task A_turn_records_the_effort_its_calls_are_sent_at(string? judgeEffort, string recorded)
    {
        if (!Fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        await MarkAllExistingTitlesSeenAsync(accountId);
        var settings = new Dictionary<string, string?>();
        if (judgeEffort is not null)
        {
            settings["LLM_RERANK_ASK_EFFORT"] = judgeEffort;
        }

        var catalog = new Application.Llm.LlmPurposeCatalog(
            new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
        var tmdb = new ScriptedTmdbClient(Discoverable(TmdbIdFirst + 1800 + recorded.Length, ("Yes", 2)));
        var (service, _) = BuildService(accountId, tmdb, new VerdictAgent(), out var emit, purposes: catalog);

        var outcome = await service.StreamAsync(
            "something brooding and futuristic", [], [], "US", [], ApiUsagePurpose.RerankAsk, emit, CancellationToken.None);

        Assert.Equal(recorded, outcome.ReasoningEffort);
    }

    /// <summary>
    /// R1-10: a keyless install answers with keyword matches as UNCHECKED — never as picks — inside
    /// the media the message names and the account's conditions, with the bar visible and counted,
    /// honest copy, the no-model flag and its stage timings. The old path streamed American Ninja
    /// Warrior (a reality series) into the grid for "ninja movies", with no reply, flag or timings.
    /// XM-06: the matches the bar holds back are in the floor tray, not the unchecked one.
    /// </summary>
    [Theory]
    [InlineData("{0} movies", "9.9", 2, 2)]
    [InlineData("{0} shows", "0", 1, 0)]
    [InlineData("{0}", "9.9", 3, 3)]
    public async Task A_keyless_turn_shows_keyword_matches_as_unchecked(string messageShape, string floor, int shown, int underBar)
    {
        if (!Fixture.Available)
        {
            return;
        }

        var word = $"keylessq{Guid.NewGuid():N}"[..16];
        var accountId = await SeedAccountAsync();
        await MarkAllExistingTitlesSeenAsync(accountId);
        await SeedScoresAsync(accountId, [], 0m);
        if (floor != "0")
        {
            await SetRatingFloorAsync(accountId, floor);
        }

        await SeedTopicalTitleAsync("unrelated", t => t.Name = $"{word} one");
        await SeedTopicalTitleAsync("unrelated", t => t.Name = $"{word} two");
        await SeedTopicalTitleAsync("unrelated", t => { t.Name = $"{word} the series"; t.MediaType = MediaType.Show; });
        var agent = new VerdictAgent();
        var (service, events) = BuildService(
            accountId, new ScriptedTmdbClient([]), agent, out var emit, embeddings: new UnavailableEmbeddingProvider());

        var outcome = await service.StreamAsync(
            string.Format(System.Globalization.CultureInfo.InvariantCulture, messageShape, word), [], [], "US", [],
            ApiUsagePurpose.RerankAsk, emit, CancellationToken.None);

        var done = Assert.Single(events, e => e.Event == "done").Data;
        Assert.Equal("NoneFound", done.GetProperty("mode").GetString());
        Assert.Empty(done.GetProperty("results").EnumerateArray());
        Assert.DoesNotContain(events, e => e.Event == "candidate");
        Assert.Equal(shown - underBar, done.GetProperty("unchecked").GetArrayLength());
        Assert.Equal(underBar, done.GetProperty("belowFloor").GetArrayLength());
        Assert.Equal(["no-model"], done.GetProperty("degraded").EnumerateArray().Select(d => d.GetString()));
        Assert.True(Assert.Single(events, e => e.Event == "assistant-message").Data.GetProperty("degraded").GetBoolean());
        Assert.StartsWith("Ask Reel has no language model connected right now", outcome.AssistantText, StringComparison.Ordinal);
        if (underBar > 0)
        {
            Assert.Contains($"{underBar} of them score under your {floor} bar.", outcome.AssistantText, StringComparison.Ordinal);
        }
        else
        {
            Assert.DoesNotContain("under your", outcome.AssistantText, StringComparison.Ordinal);
        }

        Assert.Equal(0, agent.Interprets);
        Assert.Equal(SearchTurnMode.NoneFound, outcome.Mode);
        Assert.Equal(SearchTurnDegraded.NoModel, outcome.DegradedFlags);
        Assert.Null(outcome.ReasoningEffort);
        Assert.Equal(shown - underBar, outcome.Cards.Count(c => c.Tier == SearchTurnCardTier.Unchecked));
        Assert.Equal(underBar, outcome.Cards.Count(c => c.Tier == SearchTurnCardTier.BelowFloor));
        using var timings = JsonDocument.Parse(outcome.StageTimingsJson!);
        Assert.True(timings.RootElement.TryGetProperty(AskStageTimings.Discover, out _));
        Assert.True(timings.RootElement.TryGetProperty(AskStageTimings.Score, out _));
    }

    /// <summary>
    /// IREV1-SYS-04: a keyless turn searches only what the user asked FOR. It used to search the raw
    /// sentence, so "no superheroes please" answered 17 superhero titles and a question about a card
    /// on screen matched 14 unrelated titles on its stop words. Varied over a message that only
    /// rules something out (nothing searched), a request with a negation riding along (searched,
    /// with the ruled-out titles dropped from the hits — a word no vocabulary lists included), and a
    /// pointer at a card on screen (a question about that card — nothing searched, even though its
    /// words would match).
    /// </summary>
    [Theory]
    [InlineData("no superheroes please", 0, 0, "only names what to skip")]
    [InlineData("{0} but no superheroes", 0, 1, null)]
    [InlineData("I want {0}, not superheroes", 0, 1, null)]
    [InlineData("{0} but no caped", 0, 1, null)]
    [InlineData("how long is the second one? {0}", 2, 0, "a question about “the second one”")]
    public async Task A_keyless_turn_searches_what_was_asked_for_and_never_what_was_ruled_out(
        string messageShape, int presentedCards, int shown, string? replyPart)
    {
        if (!Fixture.Available)
        {
            return;
        }

        var word = $"keylessn{Guid.NewGuid():N}"[..16];
        var accountId = await SeedAccountAsync();
        await MarkAllExistingTitlesSeenAsync(accountId);
        await SeedTopicalTitleAsync("unrelated", t => t.Name = $"{word} plain");
        await SeedTopicalTitleAsync("superhero", t => t.Name = $"{word} caped");
        await SeedTopicalTitleAsync("superhero", t => t.Name = "A superhero team-up");
        var agent = new VerdictAgent();
        var (service, events) = BuildService(
            accountId, new ScriptedTmdbClient([]), agent, out var emit, embeddings: new UnavailableEmbeddingProvider());
        var presented = Enumerable.Range(1, presentedCards)
            .Select(rank => new PresentedCard(1, rank, Guid.NewGuid(), $"Shown {rank}", 2012, MediaType.Movie))
            .ToList();

        var outcome = await service.StreamAsync(
            new LiveSearchExpansionService.AskTurnInput(
                string.Format(System.Globalization.CultureInfo.InvariantCulture, messageShape, word), [], [], presented, "US", [],
                ApiUsagePurpose.RerankAsk),
            emit, CancellationToken.None);

        var done = Assert.Single(events, e => e.Event == "done").Data;
        var cards = Names(done.GetProperty("unchecked"));
        Assert.Equal(shown, cards.Count);
        Assert.All(cards, n => Assert.Equal($"{word} plain", n));
        Assert.Equal(["no-model"], done.GetProperty("degraded").EnumerateArray().Select(d => d.GetString()));
        Assert.StartsWith("Ask Reel has no language model connected right now", outcome.AssistantText, StringComparison.Ordinal);
        if (replyPart is not null)
        {
            Assert.Contains(replyPart, outcome.AssistantText, StringComparison.Ordinal);
            Assert.Contains("I haven’t searched", outcome.AssistantText, StringComparison.Ordinal);
            Assert.DoesNotContain("found nothing", outcome.AssistantText, StringComparison.Ordinal);
        }

        Assert.Equal(0, agent.Interprets);
        Assert.Equal(SearchTurnMode.NoneFound, outcome.Mode);
    }

    /// <summary>
    /// R1-07: a request with a hard concept never widens past it — one discovery pass, no
    /// popularity last resort — so a topic with nothing eligible answers none-found without judging
    /// filler. A request with no concept (a mood) still widens.
    /// </summary>
    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 3)]
    public async Task A_concept_request_never_widens_past_its_topic(bool withConcept, int discoverPasses)
    {
        if (!Fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        await MarkAllExistingTitlesSeenAsync(accountId);
        var tmdb = new CountingTmdbClient();
        var agent = new VerdictAgent(keywords: withConcept ? ["futuristic"] : []);
        var (service, events) = BuildService(accountId, tmdb, agent, out var emit);

        var outcome = await service.StreamAsync(
            withConcept ? "something futuristic" : "something brooding", [], [], "US", [], ApiUsagePurpose.RerankAsk,
            emit, CancellationToken.None);

        Assert.Equal(discoverPasses, tmdb.DiscoverPasses);
        if (withConcept)
        {
            Assert.Empty(agent.Judged);
            Assert.Equal(SearchTurnMode.NoneFound, outcome.Mode);
            Assert.Equal(0, outcome.Relaxation);
        }
    }

    /// <summary>
    /// R1-52: Ask discovers through WS-3's TMDB arm — the account's standing conditions go WITH the
    /// fetch (language, crowd floor, year window, vote floor, interactive priority), the user's own
    /// rejections ride as exclusions, and the message is searched as a title only when it is shaped
    /// like one. The old pull sent none of the conditions and searched every sentence as a title.
    /// </summary>
    [Theory]
    [InlineData("something futuristic", false)]
    [InlineData("Blade Runner", true)]
    public async Task Ask_discovers_through_the_standing_aware_tmdb_arm(string message, bool titleSearched)
    {
        if (!Fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        await MarkAllExistingTitlesSeenAsync(accountId);
        await AddFilterAsync(accountId, FilterKind.Language, "en");
        await AddFilterAsync(accountId, FilterKind.MinCrowdRating, "6.5");
        await AddFilterAsync(accountId, FilterKind.MinYear, "1990");
        var tmdb = new CountingTmdbClient();
        var agent = new VerdictAgent(keywords: ["futuristic"], requestedExclusions: ["zombies"]);
        var (service, _) = BuildService(accountId, tmdb, agent, out var emit);

        await service.StreamAsync(message, [], [], "US", [], ApiUsagePurpose.RerankAsk, emit, CancellationToken.None);

        var options = Assert.Single(tmdb.MovieOptions);
        Assert.Equal("en", options.OriginalLanguage);
        Assert.Equal(6.5m, options.MinVoteAverage);
        Assert.Equal(TmdbDiscoveryArm.DefaultMinVoteCount, options.MinVoteCount);
        Assert.Equal(new DateTime(1990, 1, 1, 0, 0, 0, DateTimeKind.Utc), tmdb.ReleasedAfter);
        Assert.Equal(titleSearched, tmdb.TitleSearches.Count > 0);
        Assert.Contains("zombies", tmdb.KeywordSearches);
    }

    /// <summary>
    /// IREV1-SYS-01: titles the catalogue tags with the user's concept are judged FIRST, whatever
    /// their similarity. The pool used to be cut to its 24 nearest titles before the evidence-first
    /// order was applied, and the seed is the very vector the kNN arm pulled its neighbours with —
    /// so a tagged title survived only if it was also among the nearest, and the reply counted "5
    /// unseen heist movies tagged that way" that no judge ever saw. Here every tagged title sits at
    /// the BOTTOM of the similarity order, under more distractors than the cut keeps; varied over
    /// several tagged matches (an answer), a lone one that clears a set bar beneath more than 24
    /// nearer titles (IREV2-SYS-01: it must be judged, and it is the answer), and a lone one held
    /// back by the bar (the samurai turn, which must name it under the bar rather than count it and
    /// show nothing). The gate's scorer predicts 8.4 for every title here.
    /// </summary>
    [Theory]
    [InlineData(5, "0", 30)]
    [InlineData(1, "6", 30)]
    [InlineData(1, "9.9", 30)]
    [InlineData(3, "0", 26)]
    public async Task Tagged_titles_are_judged_before_any_similarity_cut(int tagged, string floor, int distractors)
    {
        if (!Fixture.Available)
        {
            return;
        }

        var topic = $"evfirst{Guid.NewGuid():N}"[..16];
        var accountId = await SeedAccountAsync();
        await MarkAllExistingTitlesSeenAsync(accountId);
        if (floor != "0")
        {
            await SeedScoresAsync(accountId, [], 0m);
            await SetRatingFloorAsync(accountId, floor);
        }

        var taggedIds = new List<Guid>();
        for (var i = 0; i < tagged; i++)
        {
            taggedIds.Add(await SeedTopicalTitleAsync(topic, t => t.Name = $"Gate Yes {TaggedMarker} {i}"));
        }

        var tmdb = new ScriptedTmdbClient(Discoverable(TmdbIdFirst + 1900 + (tagged * 40) + distractors, ("No", distractors)));
        var agent = new VerdictAgent(keywords: [topic]);
        var (service, events) = BuildService(
            accountId, tmdb, agent, out var emit, withRetrieval: true, embeddings: new TaggedFarEmbeddingProvider());

        var outcome = await service.StreamAsync(
            $"{topic} movies", [], [], "US", [], ApiUsagePurpose.RerankAsk, emit, CancellationToken.None);

        // Every tagged title reached the judge, though each was the LEAST similar title in the pool.
        Assert.All(taggedIds, id => Assert.Contains(id, agent.Judged));
        var done = Assert.Single(events, e => e.Event == "done").Data;
        var taggedNames = Enumerable.Range(0, tagged).Select(i => $"Gate Yes {TaggedMarker} {i}").ToList();
        if (decimal.Parse(floor, System.Globalization.CultureInfo.InvariantCulture) <= 8.4m)
        {
            Assert.Equal("Picks", done.GetProperty("mode").GetString());
            Assert.Equal(taggedNames.Order(StringComparer.Ordinal), Names(done.GetProperty("results")).Order(StringComparer.Ordinal));
        }
        else
        {
            Assert.Equal("NoneFound", done.GetProperty("mode").GetString());
            Assert.Equal(taggedNames, Names(done.GetProperty("belowFloor")));
            Assert.Contains($"Under your {floor} bar: {taggedNames[0]}", outcome.AssistantText, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("none of the ones I checked", outcome.AssistantText, StringComparison.Ordinal);
    }

    /// <summary>
    /// IREV2-SYS-01: the supply counts every tagged title in the catalogue, the judge rules on at most
    /// 16 a turn. With more tagged titles clearing the bar than that, every judge slot goes to a
    /// tagged title (never to a nearer untagged one), and a none-found reply says how many of them it
    /// checked — never "none of the ones I checked" over titles no judge saw. Varied over how far the
    /// tagged supply overruns the judge.
    /// </summary>
    [Theory]
    [InlineData(20, 30)]
    [InlineData(17, 40)]
    public async Task A_none_found_reply_says_how_many_tagged_titles_it_actually_checked(int tagged, int distractors)
    {
        if (!Fixture.Available)
        {
            return;
        }

        var topic = $"evcount{Guid.NewGuid():N}"[..16];
        var accountId = await SeedAccountAsync();
        await MarkAllExistingTitlesSeenAsync(accountId);
        var taggedIds = new List<Guid>();
        for (var i = 0; i < tagged; i++)
        {
            taggedIds.Add(await SeedTopicalTitleAsync(topic, t => t.Name = $"Gate No {TaggedMarker} {i}"));
        }

        await SeedScoresAsync(accountId, taggedIds, 8.0m);
        await SetRatingFloorAsync(accountId, "6");
        var tmdb = new ScriptedTmdbClient(Discoverable(TmdbIdFirst + 2100 + (tagged * 50) + distractors, ("No", distractors)));
        var agent = new VerdictAgent(keywords: [topic]);
        var (service, events) = BuildService(
            accountId, tmdb, agent, out var emit, withRetrieval: true, embeddings: new TaggedFarEmbeddingProvider());

        var outcome = await service.StreamAsync(
            $"{topic} movies", [], [], "US", [], ApiUsagePurpose.RerankAsk, emit, CancellationToken.None);

        var done = Assert.Single(events, e => e.Event == "done").Data;
        Assert.Equal("NoneFound", done.GetProperty("mode").GetString());
        Assert.Equal(tagged, done.GetProperty("supply").GetProperty("clearingFloor").GetInt32());
        Assert.Equal(AskReelJudge.MaxJudged, agent.Judged.Count);
        Assert.All(agent.Judged, id => Assert.Contains(id, taggedIds));
        var rest = tagged - AskReelJudge.MaxJudged;
        Assert.Contains(
            FormattableString.Invariant(
                $"You have {tagged} unseen {topic} titles tagged that way. I checked 16 of them and none is really a match; the other {rest} {(rest == 1 ? "wasn't" : "weren't")} among the 16 titles I checked this time."),
            outcome.AssistantText,
            StringComparison.Ordinal);
        Assert.DoesNotContain("none of the ones I checked", outcome.AssistantText, StringComparison.Ordinal);
    }

    /// <summary>
    /// IREV3-SYS-01: the facet arm counts the supply at DISCOVERY, before the turn scores anything. A
    /// turn that scores those unscored titles on demand must not then say they "have no score from
    /// your model yet" — the counts in the reply and on the wire are taken after the turn's scores.
    /// </summary>
    [Fact]
    public async Task A_none_found_reply_counts_the_supply_after_the_turn_scored_it()
    {
        if (!Fixture.Available)
        {
            return;
        }

        var topic = $"scoredin{Guid.NewGuid():N}"[..16];
        var accountId = await SeedAccountAsync();
        await MarkAllExistingTitlesSeenAsync(accountId);
        for (var i = 0; i < 4; i++)
        {
            await SeedTopicalTitleAsync(topic, t => t.Name = $"Gate Partial {topic} {i}");
        }

        // An active artifact with no stored scores: every topical title is unscored at discovery and
        // scored on demand (8.4 against a 6.0 bar) once it reaches the judge.
        await SeedScoresAsync(accountId, [], 8.0m);
        await SetRatingFloorAsync(accountId, "6.0");
        var tmdb = new ScriptedTmdbClient([]);
        var agent = new VerdictAgent(keywords: [topic]);
        var (service, events) = BuildService(accountId, tmdb, agent, out var emit, withRetrieval: true);

        var outcome = await service.StreamAsync(
            $"{topic} movies", [], [], "US", [], ApiUsagePurpose.RerankAsk, emit, CancellationToken.None);

        var done = Assert.Single(events, e => e.Event == "done").Data;
        Assert.Equal("NoneFound", done.GetProperty("mode").GetString());
        Assert.Equal(4, done.GetProperty("nearMiss").GetArrayLength());
        var supply = done.GetProperty("supply");
        Assert.Equal(0, supply.GetProperty("unscored").GetInt32());
        Assert.Equal(4, supply.GetProperty("clearingFloor").GetInt32());
        Assert.DoesNotContain("no score from your model yet", outcome.AssistantText, StringComparison.Ordinal);
    }

    private const string TaggedMarker = "tagged-far";

    /// <summary>
    /// Puts every title named with <see cref="TaggedMarker"/> orthogonal to everything else — the
    /// ranking seed and the distractors share one direction — so a cosine cut always drops the
    /// tagged titles first.
    /// </summary>
    private sealed class TaggedFarEmbeddingProvider : IEmbeddingProvider
    {
        public bool IsAvailable => true;

        public Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct = default) =>
            Task.FromResult(texts.Select(t =>
            {
                var v = new float[1536];
                v[t.Contains(TaggedMarker, StringComparison.Ordinal) ? 1 : 0] = 1f;
                return v;
            }).ToArray());
    }

    /// <summary>A TMDB client with nothing to give, recording what each call asked for.</summary>
    private sealed class CountingTmdbClient : ITmdbClient
    {
        private readonly object _gate = new();
        private int _passes;

        public int DiscoverPasses => Volatile.Read(ref _passes);

        public List<TmdbDiscoverOptions> MovieOptions { get; } = [];

        public DateTime? ReleasedAfter { get; private set; }

        public List<string> TitleSearches { get; } = [];

        public List<string> KeywordSearches { get; } = [];

        public Task<IReadOnlyList<TmdbKeyword>> SearchKeywordsAsync(string query, CancellationToken ct = default)
        {
            lock (_gate)
            {
                KeywordSearches.Add(query);
            }

            return Task.FromResult<IReadOnlyList<TmdbKeyword>>([new TmdbKeyword(424_242, query)]);
        }

        public Task<IReadOnlyList<TmdbListItem>> DiscoverByConceptAsync(
            bool movies, IReadOnlyList<int> genreIds, IReadOnlyList<int> keywordIds,
            string? region, DateTime? releasedAfter, int page,
            IReadOnlyList<int>? excludeGenreIds = null, IReadOnlyList<int>? excludeKeywordIds = null,
            DateTime? releasedBefore = null, int? maxRuntimeMinutes = null, TmdbDiscoverOptions? options = null,
            CancellationToken ct = default)
        {
            if (movies && page == 1)
            {
                Interlocked.Increment(ref _passes);
                lock (_gate)
                {
                    if (options is not null)
                    {
                        MovieOptions.Add(options);
                    }

                    ReleasedAfter = releasedAfter;
                }
            }

            return Task.FromResult<IReadOnlyList<TmdbListItem>>([]);
        }

        public Task<IReadOnlyList<TmdbListItem>> SearchTitlesAsync(bool movies, string query, int page, CancellationToken ct = default)
        {
            lock (_gate)
            {
                TitleSearches.Add(query);
            }

            return Task.FromResult<IReadOnlyList<TmdbListItem>>([]);
        }

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

    private sealed class UnavailableEmbeddingProvider : IEmbeddingProvider
    {
        public bool IsAvailable => false;

        public Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct = default) =>
            throw new InvalidOperationException("No embedding provider is configured.");
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
        IMemoryCache? cache = null, bool withRetrieval = false, AskReelJudge? judge = null,
        IEmbeddingProvider? embeddings = null, Application.Llm.ILlmPurposeCatalog? purposes = null)
    {
        var scoped = new AccountContext();
        scoped.SetAccount(accountId);
        var db = Fixture.CreateContext(scoped);

        var eligibility = new EligibilityQueryBuilder(db);
        var featureBuilder = new FeatureVectorBuilder(db);
        // Scores only where an account has an active artifact, and then a constant: the gate's
        // placement is under test here, not a model.
        var engine = new ConstantModelEngine(8.4f);
        var residualModel = new OnlineResidualModel(
            db, featureBuilder, engine, new MemoryCache(new MemoryCacheOptions()), NullLogger<OnlineResidualModel>.Instance);
        var supply = withRetrieval ? new SupplyReporter(db, eligibility) : null;
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
            Application.Search.Retrieval.KeylessTextSearch.Offline(db, eligibility),
            new CatalogWorkCoordinator(),
            cache ?? new MemoryCache(new MemoryCacheOptions()),
            NullLogger<LiveSearchExpansionService>.Instance,
            supply is null ? null : new FacetRetriever(db, eligibility, supply),
            supply,
            judge,
            purposes: purposes);

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

    private Task SetRatingFloorAsync(Guid accountId, string value) =>
        AddFilterAsync(accountId, FilterKind.MinPredictedRating, value);

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

    /// <summary>An active model artifact and a stored prediction for each title — what the supply report reads.</summary>
    private async Task SeedScoresAsync(Guid accountId, IReadOnlyList<Guid> titleIds, decimal predicted)
    {
        await using var db = Fixture.CreateContext(new AccountContext());
        var artifact = new ModelArtifact
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            Version = 1,
            ArtifactBytes = [1],
            TrainingRunId = Guid.NewGuid(),
            Status = ArtifactStatus.Active,
            TrainedAt = DateTime.UtcNow,
            FeatureSchemaJson = JsonSerializer.Serialize(new { version = FeatureSchema.Version, names = FeatureSchema.AllNames() }),
        };
        db.Add(artifact);
        foreach (var titleId in titleIds)
        {
            db.Add(new TitleScore
            {
                Id = Guid.NewGuid(),
                AccountId = accountId,
                TitleId = titleId,
                ModelArtifactId = artifact.Id,
                PredictedRating = predicted,
                ScoredAt = DateTime.UtcNow,
            });
        }

        await db.SaveChangesAsync();
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
        bool degradedReading = false, IReadOnlyList<string>? requestedExclusions = null,
        AgentFailureKind degradedFailure = AgentFailureKind.Truncated, string? degradedCode = null) : ISearchAgent
    {
        public const string Reply = "Brooding and futuristic, coming right up.";

        private readonly object _gate = new();
        private int _inFlight;
        private int _interprets;

        public List<Guid> Judged { get; } = [];

        public List<int> BatchSizes { get; } = [];

        public List<string> Purposes { get; } = [];

        /// <summary>The request text every judge call was given.</summary>
        public List<string> Queries { get; } = [];

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
                ? new AgentReply(AgentReply.DegradedReply, intent, requested, Degraded: true, degradedFailure, degradedCode)
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
                Queries.Add(query);
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

                // "Gate Fail" titles come back as the adapter's fallback — a model that did not answer
                // for them — so a batch can be part-judged and a turn can mix judged and unjudged cards.
                return titles.Select(t => t.Name.StartsWith("Gate Fail", StringComparison.Ordinal)
                        ? new RerankResult(
                            t.TitleId, 0.66d, ResultSource.Fallback, "A science-fiction pick aligned with your taste.", ResultSource.Fallback)
                        : new RerankResult(
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

    private sealed class ConstantModelEngine(float score) : IModelEngine
    {
        public TrainedModel Train(TrainingMatrix matrix, string hyperparamsJson) =>
            throw new NotSupportedException("The gate fixture scores; it never trains.");

        public float[] Score(byte[] artifactBytes, float[][] features) => [.. features.Select(_ => score)];

        public float[] ScoreCached(Guid modelKey, byte[] artifactBytes, float[][] features) => Score(artifactBytes, features);

        public ScoredRow[] ScoreWithContributions(byte[] artifactBytes, float[][] features, string[] featureNames, int topK = 5) =>
            [.. features.Select(_ => new ScoredRow(score, []))];

        public ScoredRow[] ScoreWithContributionsCached(
            Guid modelKey, byte[] artifactBytes, float[][] features, string[] featureNames, int topK = 5) =>
            ScoreWithContributions(artifactBytes, features, featureNames, topK);
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
