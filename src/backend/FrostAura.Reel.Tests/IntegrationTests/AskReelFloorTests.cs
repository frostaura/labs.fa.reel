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
using Microsoft.Extensions.Logging.Abstractions;

namespace FrostAura.Reel.Tests.IntegrationTests;

/// <summary>
/// What the account's minimum-predicted-rating filter does to a turn — and, as much as anything,
/// what the user is told about it.
///
/// <para>The rule this replaces did three dishonest things. <c>Count &gt; 0</c> meant a turn where
/// ONE card cleared a 6.0 bar returned that card and silently binned the rest. When nothing
/// cleared, it kept the best six and called that "relaxed" — a truncation nobody asked for wearing
/// the label of a concession. And the outside-request channel skipped the floor entirely, so a
/// title the user had already excluded could appear beneath a bar it did not clear.</para>
///
/// <para>The decision is a pure function, so it is pinned here exhaustively rather than through a
/// trained model whose exact predictions no test can dictate. The turn-level test that follows
/// checks the WIRING and the wire shape: the counts a client renders, and the invariant that ties
/// them together.</para>
/// </summary>
[Collection("postgres")]
public class AskReelFloorTests(PostgresFixture fixture)
{
    private sealed record Pick(string Name, decimal? Predicted);

    private static IReadOnlyList<Pick> Picks(params decimal?[] predictions) =>
        predictions.Select((p, i) => new Pick($"card-{i}", p)).ToList();

    /// <summary>
    /// The headline defect: a drop has to be reported even when the turn looks healthy. Six cards
    /// clear a 6.0 bar and four do not — the four are handed back to be shown and counted, not
    /// deleted.
    /// </summary>
    [Fact]
    public void A_drop_is_reported_even_when_some_cards_clear()
    {
        var verdict = AskReelFloor.Apply(
            Picks(9m, 8m, 7.5m, 7m, 6.5m, 6m, 5.9m, 5m, 4m, 3m), [], p => p.Predicted, floor: 6m, minKept: 6);

        Assert.False(verdict.Relaxed);
        Assert.Equal(6, verdict.Kept.Count);
        Assert.Equal(4, verdict.DroppedByFloor);
        Assert.Equal(["card-6", "card-7", "card-8", "card-9"], verdict.BelowFloor.Select(p => p.Name));
    }

    /// <summary>
    /// A relaxed turn returns EVERYTHING. The old rule truncated to the relax threshold and called
    /// it a concession, so "your floor was relaxed" also meant "and most of your results are gone".
    /// </summary>
    [Fact]
    public void A_relaxed_turn_returns_everything_not_six()
    {
        var picks = Picks(6.5m, 6.1m, 5.9m, 5.8m, 5m, 4.5m, 4m, 3.5m, 3m, 2m);
        var verdict = AskReelFloor.Apply(picks, [], p => p.Predicted, floor: 6m, minKept: 6);

        Assert.True(verdict.Relaxed);
        Assert.Equal(10, verdict.Kept.Count);
        Assert.Equal(picks, verdict.Kept);
        // Nothing is "dropped" on a relaxed turn — the floor did not bind, so there is no tray.
        Assert.Equal(0, verdict.DroppedByFloor);
    }

    /// <summary>
    /// The outside-request channel is subject to the same bar. It used to skip the floor entirely,
    /// which is the worst combination available: a title the user excluded, shown anyway, beneath a
    /// rating they said they would not go below.
    /// </summary>
    [Fact]
    public void The_outside_channel_goes_through_the_same_floor()
    {
        var verdict = AskReelFloor.Apply(
            Picks(9m, 8m, 7m, 6.5m),
            [new Pick("outside-clears", 7m), new Pick("outside-misses", 4m)],
            p => p.Predicted, floor: 6m, minKept: 5);

        Assert.False(verdict.Relaxed);
        Assert.Equal(["outside-clears"], verdict.KeptOutside.Select(p => p.Name));
        Assert.Equal(["outside-misses"], verdict.BelowFloor.Select(p => p.Name));
    }

    /// <summary>
    /// Both channels count towards whether the floor binds at all — the threshold is about how much
    /// is left on the screen, and an outside card is still on the screen.
    /// </summary>
    [Fact]
    public void The_relax_threshold_counts_both_channels()
    {
        var clean = Picks(9m, 8m, 5m);
        var outside = new List<Pick> { new("outside-a", 7m), new("outside-b", 6.5m), new("outside-c", 6.1m) };

        // 5 clear out of 6 → binds.
        Assert.False(AskReelFloor.Apply(clean, outside, p => p.Predicted, 6m, minKept: 5).Relaxed);
        // The same 5, now short of a threshold of 6 → relaxes.
        Assert.True(AskReelFloor.Apply(clean, outside, p => p.Predicted, 6m, minKept: 6).Relaxed);
    }

    /// <summary>
    /// THE PROBE. One card moves across the bar and exactly one count moves each way — the property
    /// an off-by-one in either direction breaks and no other assertion here would notice.
    /// </summary>
    [Fact]
    public void Flipping_one_card_across_the_bar_moves_the_counts_by_exactly_one()
    {
        var before = AskReelFloor.Apply(
            Picks(9m, 8m, 7.5m, 7m, 6.5m, 6m, 5.9m, 5m), [], p => p.Predicted, floor: 6m, minKept: 6);
        var after = AskReelFloor.Apply(
            Picks(9m, 8m, 7.5m, 7m, 6.5m, 6m, 6.1m, 5m), [], p => p.Predicted, floor: 6m, minKept: 6);

        Assert.Equal(before.Kept.Count + 1, after.Kept.Count);
        Assert.Equal(before.DroppedByFloor - 1, after.DroppedByFloor);
    }

    /// <summary>
    /// A card with no prediction clears the bar, deliberately: a missing prediction means this
    /// account has no model yet, and refusing every card because we cannot score them is refusing
    /// to answer. The caller's contract is to make one more scoring attempt BEFORE relying on this,
    /// so the exemption only ever covers cards that are genuinely unscorable.
    /// </summary>
    [Fact]
    public void An_unscorable_card_clears_the_floor()
    {
        var verdict = AskReelFloor.Apply(
            Picks(null, null, null, null, null, null, 2m), [], p => p.Predicted, floor: 6m, minKept: 6);

        Assert.False(verdict.Relaxed);
        Assert.Equal(6, verdict.Kept.Count);
        Assert.Equal(["card-6"], verdict.BelowFloor.Select(p => p.Name));
    }

    /// <summary>No floor configured is not a floor of zero — it is the filter not existing.</summary>
    [Fact]
    public void No_floor_set_changes_nothing()
    {
        var clean = Picks(9m, 2m, null);
        var outside = new List<Pick> { new("outside", 1m) };
        var verdict = AskReelFloor.Apply(clean, outside, p => p.Predicted, floor: 0m, minKept: 6);

        Assert.False(verdict.Relaxed);
        Assert.Equal(clean, verdict.Kept);
        Assert.Equal(outside, verdict.KeptOutside);
        Assert.Equal(0, verdict.DroppedByFloor);
    }

    /// <summary>
    /// The wiring, end to end on a real turn with a real <see cref="FilterKind.MinPredictedRating"/>
    /// row: the four accounting fields reach the wire, and the invariant that makes them trustworthy
    /// holds — everything streamed is either kept or in the tray, and nothing is unaccounted for.
    /// </summary>
    [Fact]
    public async Task A_turn_accounts_for_every_card_it_streamed()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        await MarkAllExistingTitlesSeenAsync(accountId);
        await SetRatingFloorAsync(accountId, "6.5");

        var tmdb = new ScriptedTmdbClient(Discoverable(61001, 8));
        var (service, events) = BuildService(accountId, tmdb, out var emit);

        await service.StreamAsync("something brooding and futuristic", region: "US", emit, CancellationToken.None);

        var done = Assert.Single(events, e => e.Event == "done");
        var streamed = done.Data.GetProperty("streamed").GetInt32();
        var kept = done.Data.GetProperty("kept").GetInt32();
        var dropped = done.Data.GetProperty("droppedByFloor").GetInt32();

        Assert.Equal(streamed, kept + dropped);
        Assert.Equal(kept, done.Data.GetProperty("results").GetArrayLength());
        Assert.Equal(dropped, done.Data.GetProperty("belowFloor").GetArrayLength());

        // Every card on the wire carries the server's own ranking value, and the array order is the
        // contract the client renders — blend is there to explain that order, not to re-derive it.
        foreach (var card in done.Data.GetProperty("results").EnumerateArray())
        {
            Assert.True(card.TryGetProperty("blend", out var blend) && blend.ValueKind != JsonValueKind.Null);
        }
    }

    private static IReadOnlyList<TmdbListItem> Discoverable(long firstTmdbId, int count) =>
        Enumerable.Range(0, count)
            .Select(i => new TmdbListItem(
                firstTmdbId + i, true, $"Floored Title {firstTmdbId + i}", "/f.jpg", null,
                "A brooding, futuristic search for what is real.", 40m - i, 6.5m, 900,
                new DateTime(2010 + i, 6, 16, 0, 0, 0, DateTimeKind.Utc), [878], "en"))
            .ToList();

    private async Task SetRatingFloorAsync(Guid accountId, string value)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        db.ContentFilters.Add(new ContentFilter
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            Kind = FilterKind.MinPredictedRating,
            Value = value,
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private (LiveSearchExpansionService Service, List<(string Event, JsonElement Data)> Events) BuildService(
        Guid accountId, ITmdbClient tmdb, out LiveSearchExpansionService.Emit emit)
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
            TraktUserSlug = $"floor-{Guid.NewGuid():N}",
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

    private sealed class ScriptedAgent : ISearchAgent
    {
        public bool IsAvailable => true;

        public string ModelId => "scripted-floor-agent";

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
