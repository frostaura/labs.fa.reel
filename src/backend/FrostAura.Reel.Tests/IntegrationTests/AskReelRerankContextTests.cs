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
/// What Ask Reel tells the judge about a candidate.
///
/// <para>The feed build has always handed the re-rank model tone, era, themes, the seven attribute
/// scalars and the people — the half of a title that a paid extractor derived and almost nothing
/// then read. Ask Reel, the surface where a person is actually waiting for the verdict, sent a
/// name, a year, a genre list and an overview, and got correspondingly shallow why-lines back.</para>
///
/// <para>The second claim here matters as much as the first: a title the catalogue has NOT enriched
/// carries nulls, not zeros. <c>TitleAttributes</c> scalars are non-nullable decimals, so a Pending
/// row reads 0.0 for every one of them — and 0.0 darkness tells the model "measured, and bright",
/// which is a claim the catalogue is not entitled to make. The probe is one Done row and one
/// Pending row in the same batch.</para>
/// </summary>
[Collection("postgres")]
public class AskReelRerankContextTests(PostgresFixture fixture) : EmbeddedCatalogTestBase(fixture)
{
    [Fact]
    public async Task An_enriched_title_carries_its_tone_era_and_credits_and_an_unenriched_one_carries_nulls()
    {
        if (!Fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        await MarkAllExistingTitlesSeenAsync(accountId);

        var enriched = await SeedTitleAsync(41001, "The Enriched One", seed: 91);
        var unenriched = await SeedTitleAsync(41002, "The Bare One", seed: 92);
        await EnrichAsync(enriched, tone: "brooding", era: "near-future", themes: ["identity", "memory"]);
        await LeaveExtractionPendingAsync(unenriched);

        var tmdb = new ScriptedTmdbClient([41001, 41002]);
        var agent = new CapturingAgent();
        var (service, events) = BuildService(accountId, tmdb, agent, out var emit);

        await service.StreamAsync("something brooding and futuristic", region: "US", emit, CancellationToken.None);

        Assert.Contains(events, e => e.Event == "done");
        // Keyed by id, not name: discovery upserts the TMDB row over the seeded title, so the name
        // the judge sees is TMDB's. The id is what the context was built against.
        var judged = agent.Judged.ToDictionary(i => i.TitleId);

        var enrichedInput = Assert.Contains(enriched, judged);
        var context = enrichedInput.Context;
        Assert.NotNull(context);
        Assert.Equal("brooding", context.Tone);
        Assert.Equal("near-future", context.Era);
        Assert.Equal(["identity", "memory"], context.Themes);
        Assert.Equal(0.8m, context.Darkness);
        Assert.Equal("Denis Villeneuve", context.Director);
        Assert.Equal("Hampton Fancher", context.Writer);
        // Top-billed three, in cast order — the fourth name is seeded precisely so the cut is visible.
        Assert.Equal(["Ryan Gosling", "Ana de Armas", "Harrison Ford"], context.TopCast);
        // Ask Reel's scorer computes no contributions, so the honest answer is an empty list rather
        // than a fabricated ranking of features nothing measured.
        Assert.Empty(context.TopContributions);

        var bareInput = Assert.Contains(unenriched, judged);
        var bare = bareInput.Context;
        Assert.NotNull(bare);
        Assert.Null(bare.Tone);
        Assert.Null(bare.Era);
        Assert.Empty(bare.Themes);
        // The point of the whole test: a Pending attributes row holds 0.0 in every scalar column,
        // and 0.0 is a measurement. Absence has to arrive as absence.
        Assert.Null(bare.Darkness);
        Assert.Null(bare.Pacing);
        Assert.Null(bare.Complexity);
        Assert.Null(bare.EmotionalIntensity);
        Assert.Null(bare.Humor);
        Assert.Null(bare.Optimism);
        Assert.Null(bare.EnsembleVsSolo);
        Assert.Null(bare.Director);
        Assert.Null(bare.Writer);
        Assert.Empty(bare.TopCast);
    }

    /// <summary>
    /// A tone that is not in the closed vocabulary is dropped rather than passed on. Rows written
    /// before the vocabulary was enforced carry free-text labels, and a made-up tone in the prompt
    /// is worse than no tone at all — the model will happily reason from it.
    /// </summary>
    [Fact]
    public async Task A_tone_outside_the_closed_vocabulary_never_reaches_the_judge()
    {
        if (!Fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        await MarkAllExistingTitlesSeenAsync(accountId);

        var polluted = await SeedTitleAsync(41003, "The Polluted One", seed: 93);
        await EnrichAsync(polluted, tone: "vibes-forward and a bit chaotic", era: "modern", themes: ["heist"]);

        var tmdb = new ScriptedTmdbClient([41003]);
        var agent = new CapturingAgent();
        var (service, events) = BuildService(accountId, tmdb, agent, out var emit);

        await service.StreamAsync("a chaotic heist", region: "US", emit, CancellationToken.None);

        Assert.Contains(events, e => e.Event == "done");
        var input = Assert.Single(agent.Judged, i => i.TitleId == polluted);
        Assert.Null(input.Context!.Tone);
        Assert.Equal("modern", input.Context.Era); // the rest of the row still comes through
    }

    private async Task<Guid> SeedTitleAsync(long tmdbId, string name, int seed)
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
            Embedding = new Vector(UnitVector(seed)),
            EmbeddingModel = EmbeddingText.Model,
            SourceTextHash = EmbeddingText.Hash(name),
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        return title.Id;
    }

    private async Task EnrichAsync(Guid titleId, string tone, string era, string[] themes)
    {
        await using var db = Fixture.CreateContext(new AccountContext());
        db.TitleAttributes.Add(new TitleAttributes
        {
            TitleId = titleId,
            Tone = tone,
            Era = era,
            Themes = themes,
            Darkness = 0.8m,
            Pacing = 0.3m,
            Complexity = 0.7m,
            EmotionalIntensity = 0.6m,
            Humor = 0.1m,
            Optimism = 0.2m,
            EnsembleVsSolo = 0.4m,
            ExtractorModel = "test",
            ExtractorVersion = 1,
            Status = AttributeExtractionStatus.Done,
            ExtractedAt = DateTime.UtcNow,
        });

        AddCredit(db, titleId, 1001, "Denis Villeneuve", CreditRole.Director, null);
        AddCredit(db, titleId, 1002, "Hampton Fancher", CreditRole.Writer, null);
        AddCredit(db, titleId, 1003, "Ryan Gosling", CreditRole.Actor, 0);
        AddCredit(db, titleId, 1004, "Ana de Armas", CreditRole.Actor, 1);
        AddCredit(db, titleId, 1005, "Harrison Ford", CreditRole.Actor, 2);
        AddCredit(db, titleId, 1006, "Sylvia Hoeks", CreditRole.Actor, 3);
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// An attributes row that exists but has not completed — the shape that carries 0.0 in every
    /// scalar column and would read as a measurement if the builder trusted the row's Status.
    /// </summary>
    private async Task LeaveExtractionPendingAsync(Guid titleId)
    {
        await using var db = Fixture.CreateContext(new AccountContext());
        db.TitleAttributes.Add(new TitleAttributes
        {
            TitleId = titleId,
            ExtractorModel = "test",
            ExtractorVersion = 1,
            Status = AttributeExtractionStatus.Pending,
        });
        await db.SaveChangesAsync();
    }

    private static void AddCredit(
        Infrastructure.Persistence.ReelDbContext db, Guid titleId, long personTmdbId, string name,
        CreditRole role, int? castOrder)
    {
        var person = db.Persons.FirstOrDefault(p => p.TmdbId == personTmdbId);
        if (person is null)
        {
            person = new Person { Id = Guid.NewGuid(), TmdbId = personTmdbId, Name = name };
            db.Add(person);
        }

        db.Add(new TitleCredit
        {
            Id = Guid.NewGuid(),
            TitleId = titleId,
            PersonId = person.Id,
            Role = role,
            CastOrder = castOrder,
        });
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

    private (LiveSearchExpansionService Service, List<(string Event, JsonElement Data)> Events) BuildService(
        Guid accountId, ITmdbClient tmdb, ISearchAgent agent, out LiveSearchExpansionService.Emit emit)
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
            TraktUserSlug = $"ctx-{Guid.NewGuid():N}",
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

    /// <summary>Records every candidate the judge was shown, in the shape it was shown them.</summary>
    private sealed class CapturingAgent : ISearchAgent
    {
        public List<RerankInput> Judged { get; } = [];

        public bool IsAvailable => true;

        public string ModelId => "capturing-agent";

        public Task<AgentReply> InterpretAsync(
            IReadOnlyList<ChatTurn> history, string message, string tasteSummary, IReadOnlyList<string> shownTitles,
            CancellationToken ct = default, string? standingFilters = null) =>
            Task.FromResult(new AgentReply(
                "Brooding and futuristic, coming up.",
                new SearchIntent(["science-fiction"], ["futuristic"], [], "brooding futuristic", null, null)));

        public Task<IReadOnlyList<RerankResult>> RerankBatchAsync(
            IReadOnlyList<RerankInput> titles, string query, LensPersona persona, string? anchorName = null,
            string usagePurpose = ApiUsagePurpose.Rerank, CancellationToken ct = default)
        {
            Judged.AddRange(titles);
            return Task.FromResult<IReadOnlyList<RerankResult>>(
                titles.Select(t => new RerankResult(t.TitleId, 0.7d, ResultSource.Llm, "Because it fits.", ResultSource.Llm, Satisfies.Yes)).ToList());
        }
    }

    private sealed class UnitEmbeddingProvider : IEmbeddingProvider
    {
        public bool IsAvailable => true;

        public Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct = default) =>
            Task.FromResult(texts.Select(t => UnitVector(t.Length)).ToArray());
    }

    /// <summary>Discovery that answers with exactly the TMDB ids this test already seeded.</summary>
    private sealed class ScriptedTmdbClient(IReadOnlyList<long> tmdbIds) : ITmdbClient
    {
        private IReadOnlyList<TmdbListItem> Rows => tmdbIds
            .Select(id => new TmdbListItem(
                id, true, $"Row {id}", "/r.jpg", null, "A brooding, futuristic search for what is real.",
                40m, 6.5m, 900, new DateTime(2017, 10, 6, 0, 0, 0, DateTimeKind.Utc), [878], "en"))
            .ToList();

        // TMDB knows every concept the fixture asks about, so the discovery arm pulls (R1-52).

        public Task<IReadOnlyList<TmdbKeyword>> SearchKeywordsAsync(string query, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TmdbKeyword>>([new TmdbKeyword(9_000_000 + ((query.GetHashCode(StringComparison.Ordinal) & 0x7fffffff) % 1_000_000), query)]);

        public Task<IReadOnlyList<TmdbListItem>> DiscoverByConceptAsync(
            bool movies, IReadOnlyList<int> genreIds, IReadOnlyList<int> keywordIds,
            string? region, DateTime? releasedAfter, int page,
            IReadOnlyList<int>? excludeGenreIds = null, IReadOnlyList<int>? excludeKeywordIds = null,
            DateTime? releasedBefore = null, int? maxRuntimeMinutes = null, TmdbDiscoverOptions? options = null,
            CancellationToken ct = default) =>
            Task.FromResult(movies ? Rows : []);

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
