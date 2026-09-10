using FrostAura.Reel.Application.Ingestion;
using FrostAura.Reel.Application.Jobs;
using FrostAura.Reel.Application.Llm;
using FrostAura.Reel.Application.Ml;
using FrostAura.Reel.Application.Ml.ClosedLoop;
using FrostAura.Reel.Application.Pipeline;
using FrostAura.Reel.Application.Ranking;
using FrostAura.Reel.Application.Search;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Feed;
using FrostAura.Reel.Domain.Filters;
using FrostAura.Reel.Domain.Lens;
using FrostAura.Reel.Domain.Library;
using FrostAura.Reel.Domain.Ml;
using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Domain.Ports.Tmdb;
using FrostAura.Reel.Domain.Sync;
using FrostAura.Reel.Domain.Tenancy;
using FrostAura.Reel.Infrastructure.Ml;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrostAura.Reel.Tests.IntegrationTests;

/// <summary>
/// The 2026-09-10 hero inversion, reconstructed on real Postgres and then required not to happen.
///
/// <para><b>What the founder saw.</b> A Fresh build reported <c>ShortlistCount 220,
/// HeroEligibleCount 0, ShortlistP50 5.54, ShortlistP90 5.85</c> against his own
/// <c>MinPredictedRating</c> of 6.0 — and served a hero of Grey's Anatomy, American Ninja Warrior,
/// two Below Decks and Teen Mom 2, every card marked "reaching". His library is 2,045 ratings, 90%
/// movies, with <b>three</b> unscripted titles in it. The filters were doing exactly what they were
/// told; the RANKING layer had collapsed into a 0.3-point band in which the only titles above his
/// floor were the ones the model knew least about.</para>
///
/// <para>The fixture reproduces that shape rather than a tidy one: a shortlist of movies scoring
/// 5.5–5.9 and reality shows scoring 6.2–6.9, a 6.0 floor across both, and an account that has never
/// rated an unscripted title. Every assertion below is a product decision, not a numeric coincidence.
/// Skips when REEL_TEST_CONNECTION_STRING is unset (no DB in the run).</para>
/// </summary>
[Collection("postgres")]
public class TastePriorFeedTests(PostgresFixture fixture)
{
    /// <summary>The founder's own content rule, verbatim.</summary>
    private const decimal Floor = 6.0m;

    /// <summary>
    /// One test account and the genre slugs its whole world is made of.
    ///
    /// <para><b>The slugs are nonced, and that is not cosmetic.</b> The postgres collection shares
    /// one catalogue across every suite in it, so a plain "drama" pool would silently include
    /// hundreds of other tests' titles — and this fixture's entire claim is about the RATIO of
    /// scripted movies to unscripted shows in one account's eligible pool. An <c>IncludeGenre</c>
    /// allowlist over nonced slugs makes the pool exactly what this test seeded, without suppressing
    /// a single rule the real eligibility path applies. The prior reads genre slugs generically, so
    /// nonced names exercise it exactly as real ones do.</para>
    /// </summary>
    private sealed record Fixture(Guid AccountId, string[] VolumeGenres, string NoHistoryGenre);

    [Fact]
    public async Task A_near_constant_shortlist_under_a_floor_does_not_hand_the_hero_to_a_genre_the_account_never_watches()
    {
        if (!fixture.Available)
        {
            return;
        }

        var account = await SeedFounderShapedAccountAsync();
        var accountId = account.AccountId;
        await SeedCandidatesAsync(account, movies: 340, realityShows: 14);
        await SeedIsShowDrivenArtifactAsync(accountId);
        await AddContentFilterAsync(accountId, FilterKind.MinPredictedRating, ContentFilterRules.FormatRating(Floor));

        await RunBuildAsync(accountId);

        await using var db = fixture.CreateContext(new AccountContext());
        var snapshot = await ActiveSnapshotAsync(db, accountId);
        var items = await db.FeedItems
            .Where(i => i.FeedSnapshotId == snapshot.Id)
            .Join(db.Titles, i => i.TitleId, t => t.Id, (i, t) => new { Item = i, Title = t })
            .ToListAsync();
        var hero = items.Where(x => x.Item.Row == FeedRowKind.Hero).OrderBy(x => x.Item.Rank).ToList();

        Assert.NotEmpty(hero);

        // The media floor: at least round(5 × 0.9) films, from a shortlist that has them.
        Assert.True(
            hero.Count(x => x.Title.MediaType == MediaType.Movie) >= 4,
            $"hero was {string.Join(", ", hero.Select(x => $"{x.Title.Name} ({x.Title.MediaType})"))}");

        // And not one card from a genre this account has never rated — enforced by nothing harder
        // than arithmetic on its own history, which is the point: no genre is named anywhere.
        Assert.DoesNotContain(hero, x => x.Title.Genres.Contains(account.NoHistoryGenre));

        // The floor was relaxed, and the page says so in the user's own units rather than silently
        // building out of the fourteen titles that squeaked over it.
        Assert.NotNull(snapshot.DegradedReason);
        Assert.Contains("6.0", snapshot.DegradedReason);
        Assert.Contains("relaxed", snapshot.DegradedReason, StringComparison.OrdinalIgnoreCase);

        // The instrument agrees with the page: this build genuinely had nothing at the hero's own
        // confidence bar, so the hero is honestly labelled as reaching rather than confident.
        var stat = await db.FeedBuildStats
            .Where(s => s.AccountId == accountId)
            .OrderByDescending(s => s.BuiltAt)
            .FirstAsync();
        Assert.Equal(0, stat.HeroEligibleCount);
        Assert.NotNull(stat.DegradedReason);
    }

    [Fact]
    public async Task The_prior_reorders_the_page_and_changes_no_rating_that_is_shown()
    {
        if (!fixture.Available)
        {
            return;
        }

        // The honesty contract, pinned at the two places a number can leak: the persisted
        // TitleScore (what the model said) and the FeedItem (what the card renders). A prior that
        // quietly wrote itself into either would turn a ranking rule into a lie about a prediction.
        var account = await SeedFounderShapedAccountAsync();
        var accountId = account.AccountId;
        var (_, realityIds) = await SeedCandidatesAsync(account, movies: 340, realityShows: 14);
        await SeedIsShowDrivenArtifactAsync(accountId);
        await AddContentFilterAsync(accountId, FilterKind.MinPredictedRating, ContentFilterRules.FormatRating(Floor));

        await RunBuildAsync(accountId);

        await using var db = fixture.CreateContext(new AccountContext());
        var snapshot = await ActiveSnapshotAsync(db, accountId);
        var items = await db.FeedItems.Where(i => i.FeedSnapshotId == snapshot.Id).ToListAsync();
        var scores = await db.TitleScores
            .Where(s => s.AccountId == accountId && s.ModelArtifactId == snapshot.ModelArtifactId)
            .ToDictionaryAsync(s => s.TitleId, s => s.PredictedRating);

        // Card == score, to the stored resolution, for every card on the page.
        foreach (var item in items.Where(i => scores.ContainsKey(i.TitleId)))
        {
            Assert.Equal(scores[item.TitleId], item.PredictedRating);
        }

        // And the demoted titles still carry their HIGH numbers. This is the whole shape of the
        // fix in one assertion: the model still says the reality shows score best, the product
        // still declines to lead with them, and neither is hiding what it thinks.
        var realityScores = realityIds.Where(scores.ContainsKey).Select(id => scores[id]).ToList();
        Assert.NotEmpty(realityScores);

        var heroTitleIds = items.Where(i => i.Row == FeedRowKind.Hero).Select(i => i.TitleId).ToList();
        var heroScores = heroTitleIds.Where(scores.ContainsKey).Select(id => scores[id]).ToList();
        Assert.NotEmpty(heroScores);
        Assert.True(
            realityScores.Max() > heroScores.Max(),
            $"the demoted titles must keep their honest numbers ({realityScores.Max()} vs {heroScores.Max()})");
    }

    [Fact]
    public async Task The_hero_is_ordered_by_the_fit_verdict_when_one_is_judged_before_assembly()
    {
        if (!fixture.Available)
        {
            return;
        }

        // Forty candidates, all judged by the pre-assembly pass. Five of them score WORSE on the
        // model (they are newer, and the seeded artifact reads release age) and are blessed by the
        // agent; the other thirty-five score better and are condemned. Without a fit before
        // assembly the hero cannot contain a blessed title; with one it must.
        var account = await SeedFounderShapedAccountAsync();
        var accountId = account.AccountId;
        var blessed = await SeedFitFixtureAsync(account, preferred: 5, plain: 35);
        await SeedIsShowDrivenArtifactAsync(accountId);

        var agent = new NarrativeFitAgent(blessed);
        await RunBuildAsync(accountId, agent: agent);

        await using var db = fixture.CreateContext(new AccountContext());
        var snapshot = await ActiveSnapshotAsync(db, accountId);
        var hero = await db.FeedItems
            .Where(i => i.FeedSnapshotId == snapshot.Id && i.Row == FeedRowKind.Hero)
            .OrderBy(i => i.Rank)
            .Select(i => i.TitleId)
            .ToListAsync();

        Assert.True(agent.RerankCalls > 0, "the pre-assembly pass must have asked the agent something");
        Assert.NotEmpty(hero);
        Assert.All(hero, id => Assert.Contains(id, blessed));
    }

    [Fact]
    public async Task Without_an_agent_the_hero_falls_back_to_calibrated_times_prior()
    {
        if (!fixture.Available)
        {
            return;
        }

        // The documented fallback, and the control arm for the test above: the identical fixture
        // with no agent at all must seat the titles the MODEL prefers, which are exactly the ones
        // the blessed set is not.
        var account = await SeedFounderShapedAccountAsync();
        var accountId = account.AccountId;
        var blessed = await SeedFitFixtureAsync(account, preferred: 5, plain: 35);
        await SeedIsShowDrivenArtifactAsync(accountId);

        await RunBuildAsync(accountId);

        await using var db = fixture.CreateContext(new AccountContext());
        var snapshot = await ActiveSnapshotAsync(db, accountId);
        var hero = await db.FeedItems
            .Where(i => i.FeedSnapshotId == snapshot.Id && i.Row == FeedRowKind.Hero)
            .OrderBy(i => i.Rank)
            .Select(i => i.TitleId)
            .ToListAsync();

        Assert.NotEmpty(hero);
        Assert.DoesNotContain(hero, id => blessed.Contains(id));
    }

    [Fact]
    public async Task Paged_rows_honour_the_prior_so_a_demoted_genre_cannot_return_on_scroll()
    {
        if (!fixture.Available)
        {
            return;
        }

        // Page 2 is the same feed as page 1. A prior that kept a no-history genre out of the hero
        // and then let it straight back in on scroll would be a delay, not a rule.
        var account = await SeedFounderShapedAccountAsync();
        var accountId = account.AccountId;
        await SeedCandidatesAsync(account, movies: 60, realityShows: 14);
        await SeedIsShowDrivenArtifactAsync(accountId);

        var page = await MoreAsync(accountId, FeedRowContext.Hero, limit: 10);

        Assert.NotEmpty(page);
        Assert.DoesNotContain(page, i => i.Title.Genres.Contains(account.NoHistoryGenre));
        Assert.All(page, i => Assert.Equal(MediaType.Movie, i.Title.MediaType));
    }

    // ── Fixture ─────────────────────────────────────────────────────────────────────────────

    private static async Task<FeedSnapshot> ActiveSnapshotAsync(Infrastructure.Persistence.ReelDbContext db, Guid accountId) =>
        await db.FeedSnapshots
            .Where(s => s.AccountId == accountId && s.Status == SnapshotStatus.Active)
            .OrderByDescending(s => s.GeneratedAt)
            .FirstAsync();

    /// <summary>
    /// An account shaped like the founder's where it matters: 180 rated movies and 20 rated shows
    /// (MovieRatedShare 0.9), spread across the volume genres he actually rates, and not one
    /// unscripted title. 200 ratings clears <c>TastePrior.MinRatingsForNoHistory</c>, so the
    /// no-history term is live — which is the condition under test.
    /// </summary>
    private async Task<Fixture> SeedFounderShapedAccountAsync()
    {
        var nonce = Guid.NewGuid().ToString("N")[..8];
        string[] volumeGenres =
            [$"thriller-{nonce}", $"action-{nonce}", $"drama-{nonce}", $"crime-{nonce}"];
        var noHistoryGenre = $"reality-{nonce}";

        await using var db = fixture.CreateContext(new AccountContext());
        var account = new Account
        {
            Id = Guid.NewGuid(),
            TraktUserSlug = $"tasteprior-{Guid.NewGuid():N}",
            Tier = AccountTier.Founder,
            CreatedAt = DateTime.UtcNow,
            LastSeenAt = DateTime.UtcNow,
            PipelineStageChangedAt = DateTime.UtcNow,
        };
        db.Add(account);

        // The allowlist that makes this account's eligible pool exactly what this method seeds —
        // see the Fixture remarks. The no-history genre is ON the allowlist on purpose: the point
        // is that the prior keeps it off the hero, not that a filter kept it out of the pool.
        foreach (var genre in volumeGenres.Append(noHistoryGenre))
        {
            db.ContentFilters.Add(new ContentFilter
            {
                Id = Guid.NewGuid(),
                AccountId = account.Id,
                Kind = FilterKind.IncludeGenre,
                Value = genre,
                CreatedAt = DateTime.UtcNow,
            });
        }

        for (var i = 0; i < 200; i++)
        {
            var isMovie = i % 10 != 9; // 180 / 20 ⇒ MovieRatedShare 0.9
            var title = NewTitle(
                $"rated {i}",
                isMovie ? MediaType.Movie : MediaType.Show,
                popularity: 1m + (i * 0.001m),
                year: 2005 + (i % 15),
                genres: [volumeGenres[i % volumeGenres.Length]]);
            db.Add(title);
            db.UserRatings.Add(new UserRating
            {
                Id = Guid.NewGuid(),
                AccountId = account.Id,
                TitleId = title.Id,
                SubjectType = isMovie ? RatingSubjectType.Movie : RatingSubjectType.Show,
                // A spread with real loved titles in it, so the anchor rotation has a pool and the
                // user mean is a genuine middle rather than a constant.
                Rating = (short)(5 + (i % 5)),
                RatedAt = DateTime.UtcNow.AddDays(-3 * i),
            });
        }

        await db.SaveChangesAsync();
        return new Fixture(account.Id, volumeGenres, noHistoryGenre);
    }

    /// <summary>
    /// The candidate pool in the live shape: scripted MOVIES the model scores 5.5–5.9, and reality
    /// SHOWS it scores 6.2–6.9. The shows are given high TMDB popularity for the same reason they
    /// have it in reality — an unscripted format that fills a schedule accumulates reach — which
    /// also keeps them out of the deep-cuts row on merit rather than by assertion.
    /// </summary>
    private async Task<(List<Guid> Movies, List<Guid> Reality)> SeedCandidatesAsync(
        Fixture account, int movies, int realityShows)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var genres = account.VolumeGenres;
        var movieIds = new List<Guid>(movies);
        var realityIds = new List<Guid>(realityShows);

        for (var i = 0; i < movies; i++)
        {
            var title = NewTitle(
                $"candidate movie {i}",
                MediaType.Movie,
                popularity: 2m + (i * 0.001m),
                year: 2000 + (i % 21),
                genres: [genres[i % genres.Length], genres[(i + 1) % genres.Length]]);
            db.Add(title);
            movieIds.Add(title.Id);
        }

        for (var i = 0; i < realityShows; i++)
        {
            var title = NewTitle(
                $"candidate reality {i}",
                MediaType.Show,
                popularity: 20m + i,
                year: 2009 + (i % 8),
                genres: [account.NoHistoryGenre]);
            db.Add(title);
            realityIds.Add(title.Id);
        }

        await db.SaveChangesAsync();
        return (movieIds, realityIds);
    }

    /// <summary>
    /// Forty movies for the fit pass: <paramref name="preferred"/> recent ones the seeded artifact
    /// scores LOWER (it reads release age), and <paramref name="plain"/> older ones it scores
    /// higher. Returns the preferred ids — the set the narrative-fit agent blesses.
    /// </summary>
    private async Task<HashSet<Guid>> SeedFitFixtureAsync(Fixture account, int preferred, int plain)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var genre = account.VolumeGenres[0];
        var blessed = new HashSet<Guid>();

        for (var i = 0; i < plain; i++)
        {
            db.Add(NewTitle(
                $"plain {i}", MediaType.Movie, popularity: 2m + (i * 0.001m), year: 2001 + (i % 4), genres: [genre]));
        }

        for (var i = 0; i < preferred; i++)
        {
            var title = NewTitle(
                $"blessed {i}", MediaType.Movie, popularity: 2m + (i * 0.001m), year: 2020, genres: [genre]);
            db.Add(title);
            blessed.Add(title.Id);
        }

        await db.SaveChangesAsync();
        return blessed;
    }

    /// <summary>
    /// A renderable, eligible title. <c>LastMetadataRefreshAt</c> is set so the build's hydration
    /// phase is a no-op — this suite is about ranking, not about the TMDB round trip.
    /// </summary>
    private static Title NewTitle(string name, MediaType media, decimal popularity, int year, string[] genres)
    {
        var released = new DateTime(year, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        return new Title
        {
            Id = Guid.NewGuid(),
            MediaType = media,
            TmdbId = Random.Shared.NextInt64(1, long.MaxValue),
            TraktSlug = string.Empty,
            Name = $"{name} {Guid.NewGuid():N}",
            Language = "en", // eligibility defaults to English-only with no language filter configured
            PosterPath = "/p.jpg",
            Genres = genres,
            Year = year,
            RuntimeMinutes = media == MediaType.Movie ? 110 : 45,
            TraktRating = 7.2m,
            TmdbVoteAverage = 7.4m,
            TmdbPopularity = popularity,
            ReleasedAt = media == MediaType.Movie ? released : null,
            FirstAiredAt = media == MediaType.Show ? released : null,
            LastMetadataRefreshAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
        };
    }

    private async Task AddContentFilterAsync(Guid accountId, FilterKind kind, string value)
    {
        await using var db = fixture.CreateContext(new AccountContext());
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

    /// <summary>
    /// A FastTree over the REAL schema whose response is driven by exactly two features:
    /// <c>isShow</c> (index 2) and <c>releaseAgeLog</c> (index 0). Every other column is constant
    /// zero in training, so the model never splits on one and the serve-time vectors' other values
    /// cannot perturb the fixture. Labels: movies 5.5–5.9, shows 6.2–6.9 — the live band, either
    /// side of a 6.0 floor.
    /// </summary>
    private async Task SeedIsShowDrivenArtifactAsync(Guid accountId)
    {
        var names = FeatureSchema.AllNames();
        const int Rows = 400;
        var vectors = new float[Rows][];
        var labels = new float[Rows];
        for (var i = 0; i < Rows; i++)
        {
            var v = new float[names.Length];
            var isShow = i % 2 == 1;
            var age = (float)(i / (double)Rows); // 0..1 across the trained band
            v[0] = 1.5f + (2.0f * age);          // log1p(years) for titles released 2000–2021
            v[2] = isShow ? 1f : 0f;
            vectors[i] = v;
            labels[i] = isShow ? 6.2f + (0.7f * age) : 5.5f + (0.4f * age);
        }

        var trained = new FastTreeModelEngine().Train(
            new TrainingMatrix(vectors, labels, names),
            """{"Trees":200,"Leaves":16,"MinExamplesPerLeaf":2,"LearningRate":0.3}""");

        await using var db = fixture.CreateContext(new AccountContext());
        db.ModelArtifacts.Add(new ModelArtifact
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            Version = 1,
            Algo = "FastTree",
            ArtifactBytes = trained.ArtifactBytes,
            FeatureSchemaJson = System.Text.Json.JsonSerializer.Serialize(
                new { version = FeatureSchema.Version, names }),
            TrainingRunId = Guid.NewGuid(),
            Status = ArtifactStatus.Active,
            TrainedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private async Task RunBuildAsync(Guid accountId, ISearchAgent? agent = null)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var featureBuilder = new FeatureVectorBuilder(db);
        var eligibility = new EligibilityQueryBuilder(db);
        var tmdb = new OfflineTmdbClient();
        MemoryCache NewCache() => new(new MemoryCacheOptions());
        var handler = new BuildFeedJobHandler(
            db,
            featureBuilder,
            new CandidateGenerator(db, tmdb, eligibility, NullLogger<CandidateGenerator>.Instance),
            new CandidateRetriever(db, eligibility),
            eligibility,
            new FeedStarvationDiagnostic(db, eligibility),
            new TitleHydrator(db, tmdb, NullLogger<TitleHydrator>.Instance),
            new FastTreeModelEngine(),
            new OnlineResidualModel(
                db, featureBuilder, new FastTreeModelEngine(), NewCache(), NullLogger<OnlineResidualModel>.Instance),
            new RatingCalibrationService(db, NewCache()),
            new DislikeSignal(db, NewCache()),
            agent ?? new UnavailableSearchAgent(),
            new TasteFactsBuilder(db, new DislikeSignal(db, NewCache()), featureBuilder, NewCache()),
            new PersonalisationContext(db, new DislikeSignal(db, NewCache())),
            NewCache(),
            new NoopEventHub(),
            new ExposureLog(db, NullLogger<ExposureLog>.Instance),
            NullLogger<BuildFeedJobHandler>.Instance);

        await handler.ExecuteAsync(
            new SyncJob
            {
                Id = Guid.NewGuid(),
                AccountId = accountId,
                Kind = JobKind.BuildFeed,
                EnqueuedAt = DateTime.UtcNow,
            },
            CancellationToken.None);
    }

    private async Task<IReadOnlyList<ScoredFeedItem>> MoreAsync(Guid accountId, FeedRowContext row, int limit)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var account = await db.Accounts.FirstAsync(a => a.Id == accountId);
        var featureBuilder = new FeatureVectorBuilder(db);
        var eligibility = new EligibilityQueryBuilder(db);
        var engine = new FastTreeModelEngine();
        var cache = new MemoryCache(new MemoryCacheOptions());
        var tmdb = new OfflineTmdbClient();
        var paginator = new FeedPaginator(
            db,
            featureBuilder,
            new CandidateRetriever(db, eligibility),
            engine,
            new OnlineResidualModel(db, featureBuilder, engine, cache, NullLogger<OnlineResidualModel>.Instance),
            new RatingCalibrationService(db, cache),
            new DislikeSignal(db, cache),
            new CandidateGenerator(db, tmdb, eligibility, NullLogger<CandidateGenerator>.Instance),
            tmdb,
            new UnavailableSearchAgent(),
            new PersonalisationContext(db, new DislikeSignal(db, cache)),
            new FrostAura.Reel.Application.CrossReference.RerankBudget(
                db, new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build()),
            cache,
            NullLogger<FeedPaginator>.Instance);

        return await paginator.MoreAsync(
            account, new MoreFeedRequest(row, null, null, [], limit), CancellationToken.None);
    }

    /// <summary>
    /// Blesses exactly the titles it was handed (fit 0.95) and condemns everything else (fit 0.05),
    /// deterministically and with an accepted why-line either way — so the ORDER is the only thing
    /// the test can be reading.
    /// </summary>
    private sealed class NarrativeFitAgent(HashSet<Guid> blessed) : ISearchAgent
    {
        public int RerankCalls;

        public bool IsAvailable => true;

        public string ModelId => "narrative-fit-stub";

        public Task<AgentReply> InterpretAsync(
            IReadOnlyList<ChatTurn> history, string message, string tasteSummary,
            IReadOnlyList<string> shownTitles, CancellationToken ct = default, string? standingFilters = null) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<RerankResult>> RerankBatchAsync(
            IReadOnlyList<RerankInput> titles, string query, LensPersona persona,
            string? anchorName = null, string usagePurpose = ApiUsagePurpose.Rerank, CancellationToken ct = default)
        {
            Interlocked.Increment(ref RerankCalls);
            return Task.FromResult<IReadOnlyList<RerankResult>>(titles
                .Select(t => new RerankResult(
                    t.TitleId,
                    blessed.Contains(t.TitleId) ? 0.95d : 0.05d,
                    $"You keep coming back to this kind of thing — {t.TitleId:N}"))
                .ToList());
        }
    }

    /// <summary>TMDB port that yields nothing, so the build runs entirely off the seeded catalogue.</summary>
    private sealed class OfflineTmdbClient : ITmdbClient
    {
        public Task<TmdbTitleDetails?> GetMovieAsync(long tmdbId, CancellationToken ct = default) => Task.FromResult<TmdbTitleDetails?>(null);

        public Task<TmdbTitleDetails?> GetTvAsync(long tmdbId, CancellationToken ct = default) => Task.FromResult<TmdbTitleDetails?>(null);

        // The bulk-seed walk is not exercised by this fixture; SeedCatalogJobHandlerTests owns its own
        // scripted TMDB client. An empty page reads as "this partition is exhausted", which is the
        // only honest answer a fake that was never given a corpus can give.
        public Task<TmdbDiscoverPage> DiscoverSeedPageAsync(bool movies, int year, int minVoteCount, int page, CancellationToken ct = default) =>
            Task.FromResult(new TmdbDiscoverPage([], page, 0, 0));

        public Task<IReadOnlyList<TmdbListItem>> DiscoverAsync(bool movies, int? genreId, string? region, DateTime? releasedAfter, int page, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TmdbListItem>>([]);

        public Task<IReadOnlyList<TmdbListItem>> GetTrendingAsync(bool movies, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TmdbListItem>>([]);

        public Task<IReadOnlyList<TmdbListItem>> GetRecommendationsAsync(bool movies, long tmdbId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TmdbListItem>>([]);

        public Task<IReadOnlyList<TmdbListItem>> GetSimilarAsync(bool movies, long tmdbId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TmdbListItem>>([]);

        public Task<IReadOnlyList<TmdbKeyword>> SearchKeywordsAsync(string query, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TmdbKeyword>>([]);

        public Task<IReadOnlyList<TmdbListItem>> SearchTitlesAsync(bool movies, string query, int page, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TmdbListItem>>([]);

        public Task<TmdbPerson?> SearchPersonAsync(string name, CancellationToken ct = default) => Task.FromResult<TmdbPerson?>(null);

        public Task<IReadOnlyList<TmdbListItem>> GetPersonCreditsAsync(long personId, PersonCreditRole role, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TmdbListItem>>([]);

        public Task<IReadOnlyList<TmdbListItem>> DiscoverByConceptAsync(bool movies, IReadOnlyList<int> genreIds, IReadOnlyList<int> keywordIds, string? region, DateTime? releasedAfter, int page, IReadOnlyList<int>? excludeGenreIds = null, IReadOnlyList<int>? excludeKeywordIds = null, DateTime? releasedBefore = null, int? maxRuntimeMinutes = null, TmdbDiscoverOptions? options = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TmdbListItem>>([]);

        public Task<IReadOnlyList<TmdbWatchProvider>> GetWatchProvidersAsync(long tmdbId, bool movie, string region, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TmdbWatchProvider>>([]);
    }

    /// <summary>Never available, so the build orders by calibrated × prior and nothing else.</summary>
    private sealed class UnavailableSearchAgent : ISearchAgent
    {
        public bool IsAvailable => false;

        public string ModelId => "stub";

        public Task<AgentReply> InterpretAsync(
            IReadOnlyList<ChatTurn> history, string message, string tasteSummary,
            IReadOnlyList<string> shownTitles, CancellationToken ct = default, string? standingFilters = null) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<RerankResult>> RerankBatchAsync(
            IReadOnlyList<RerankInput> titles, string query, LensPersona persona,
            string? anchorName = null, string usagePurpose = ApiUsagePurpose.Rerank, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class NoopEventHub : IPipelineEventHub
    {
        public void Publish(Guid accountId, string type, IReadOnlyDictionary<string, object?> data)
        {
        }

        public async IAsyncEnumerable<PipelineSseEvent> SubscribeAsync(
            Guid accountId, long? lastEventId,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield break;
        }
    }
}
