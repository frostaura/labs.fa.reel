using FrostAura.Reel.Application.Ml;
using FrostAura.Reel.Application.Ml.ClosedLoop;
using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Application.Ranking;
using FrostAura.Reel.Application.Search;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Feed;
using FrostAura.Reel.Domain.Lens;
using FrostAura.Reel.Domain.Library;
using FrostAura.Reel.Domain.Ml;
using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Domain.Sync;
using FrostAura.Reel.Domain.Tenancy;
using FrostAura.Reel.Infrastructure.Ml;
using FrostAura.Reel.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrostAura.Reel.Tests.IntegrationTests;

/// <summary>
/// <c>POST /feed/more</c>'s engine against WS-5's P0 contract: every card states where its why-line
/// came from and carries its score breakdown, a failed model pass never reads as model output, an
/// anchored page admits only cards with shared evidence (and carries it), and two rails of one
/// page — fired concurrently, as Home fires them — never serve the same title.
/// </summary>
[Collection("postgres")]
public class FeedPaginatorTrustTests(PostgresFixture fixture) : EmbeddedCatalogTestBase(fixture)
{
    [Fact]
    public async Task A_failed_curation_pass_is_labelled_fallback_and_never_shows_the_stub()
    {
        if (!Fixture.Available)
        {
            return;
        }

        var genre = $"trust-{Guid.NewGuid():N}";
        var accountId = await SeedAccountAsync();
        await SeedTitlesAsync(50, genre);
        await SeedActiveArtifactAsync(accountId);
        var agent = new StubbingAgent();

        var items = await MoreAsync(accountId, Genre(genre), agent);

        Assert.True(agent.Calls > 0);
        Assert.NotEmpty(items);
        Assert.All(items, i =>
        {
            Assert.DoesNotContain("aligned with your taste", i.WhyThis);
            Assert.NotEqual(WhySourceKind.Unknown, i.WhySource.Kind);
            Assert.NotEqual(WhySourceKind.Llm, i.WhySource.Kind);
            Assert.Null(i.LlmFit);
            Assert.NotNull(i.BaseScore);
            Assert.NotNull(i.CalibrationDelta);
            Assert.NotNull(i.ResidualApplied);
            Assert.NotNull(i.DemotionApplied);
            // The breakdown reconstructs the card's number: base + calibration − demotion.
            Assert.Equal(
                Math.Round(i.Predicted, 2),
                Math.Round(i.BaseScore!.Value + i.CalibrationDelta!.Value - i.DemotionApplied!.Value, 2));
        });
        Assert.Contains(items, i => i.WhySource == ExposureWhySource.Fallback);

        // Off an anchored row, a card whose model pass failed shows no line at all: the soft
        // templater copy is not shown in the model's place.
        Assert.All(items.Where(i => i.WhySource == ExposureWhySource.Fallback), i => Assert.Equal(string.Empty, i.WhyThis));
    }

    [Fact]
    public async Task On_an_anchored_page_a_failed_pass_shows_the_evidence_line_and_the_model_was_given_the_evidence()
    {
        if (!Fixture.Available)
        {
            return;
        }

        var genre = $"trust-{Guid.NewGuid():N}";
        var accountId = await SeedAccountAsync();
        var axis = Random.Shared.Next(300, 590);
        var anchor = await SeedTitlesAsync(1, genre, keywords: ["heist"], axis: axis, name: "Anchor Heat");
        await SeedTitlesAsync(10, genre, keywords: ["heist"], axis: axis);
        await SeedActiveArtifactAsync(accountId);
        var agent = new StubbingAgent();

        var items = await MoreAsync(
            accountId, new MoreFeedRequest(FeedRowContext.BecauseYouLoved, anchor[0], null, [], 12), agent);

        Assert.NotEmpty(items);
        Assert.All(items, i =>
        {
            Assert.Equal(ExposureWhySource.Fallback, i.WhySource);
            // The templated ANCHORED line, built from the evidence the gate admitted the card on.
            Assert.Contains("heist", i.WhyThis, StringComparison.Ordinal);
            Assert.Contains("Anchor Heat", i.WhyThis, StringComparison.Ordinal);
        });

        // And the model was told the checked link, naming the title rather than "the anchor".
        Assert.NotEmpty(agent.Inputs);
        Assert.All(agent.Inputs, input =>
            Assert.Contains("shares the heist premise with Anchor Heat", input.Context!.TopContributions));
    }

    [Fact]
    public async Task Three_rails_sharing_one_pool_each_fill_from_the_lane_not_from_a_pre_cut_slice()
    {
        if (!Fixture.Available)
        {
            return;
        }

        // The review's proof: three genre rails over ONE 60-title pool, one token, limit 12. Claiming
        // only from the curation slice (limit + 8) dealt 12, 8 and 0 — and an empty page reports
        // exhausted, which the client latches.
        var a = $"trust-a-{Guid.NewGuid():N}";
        var b = $"trust-b-{Guid.NewGuid():N}";
        var c = $"trust-c-{Guid.NewGuid():N}";
        var accountId = await SeedAccountAsync();
        await SeedTitlesAsync(60, a, b, thirdGenre: c);
        await SeedActiveArtifactAsync(accountId);
        var token = Guid.NewGuid().ToString("N");
        using var cache = new MemoryCache(new MemoryCacheOptions());

        var pages = new List<IReadOnlyList<ScoredFeedItem>>();
        foreach (var genre in new[] { a, b, c })
        {
            pages.Add(await MoreAsync(
                accountId, Genre(genre) with { Limit = 12, PageToken = token }, new UnavailableSearchAgent(), cache));
        }

        Assert.All(pages, page => Assert.Equal(12, page.Count));
        var all = pages.SelectMany(p => p.Select(i => i.Title.Id)).ToList();
        Assert.Equal(all.Count, all.Distinct().Count());
    }

    [Fact]
    public async Task A_rail_re_requested_with_its_own_token_gets_its_own_cards_back()
    {
        if (!Fixture.Available)
        {
            return;
        }

        // An RTK refetch or tag invalidation inside one Home view: the same rail, the same token.
        // Before claims were owned by their row it found its own cards taken and dealt 8 different ones.
        var a = $"trust-a-{Guid.NewGuid():N}";
        var b = $"trust-b-{Guid.NewGuid():N}";
        var accountId = await SeedAccountAsync();
        await SeedTitlesAsync(40, a, b);
        await SeedActiveArtifactAsync(accountId);
        var token = Guid.NewGuid().ToString("N");
        using var cache = new MemoryCache(new MemoryCacheOptions());

        var first = await MoreAsync(accountId, Genre(a) with { PageToken = token }, new UnavailableSearchAgent(), cache);
        var other = await MoreAsync(accountId, Genre(b) with { PageToken = token }, new UnavailableSearchAgent(), cache);
        var again = await MoreAsync(accountId, Genre(a) with { PageToken = token }, new UnavailableSearchAgent(), cache);

        Assert.Equal(10, first.Count);
        Assert.Equal(first.Select(i => i.Title.Id), again.Select(i => i.Title.Id));
        Assert.Empty(other.Select(i => i.Title.Id).Intersect(first.Select(i => i.Title.Id)));
    }

    [Fact]
    public async Task Concurrent_first_requests_for_one_page_all_claim_into_one_set()
    {
        if (!Fixture.Available)
        {
            return;
        }

        // Twenty rails race to create the page's set, each wanting the single best title. With an
        // atomic create every rail sees the others' claims, so twenty distinct titles are dealt;
        // the old check-then-create let racing first requests each build a private set.
        var accountId = await SeedAccountAsync();
        var titles = await SeedTitlesAsync(20, $"claims-{Guid.NewGuid():N}");
        for (var round = 0; round < 10; round++)
        {
            var token = Guid.NewGuid().ToString("N");
            var claims = Enumerable.Range(0, 20).Select(async rail =>
            {
                await using var db = Fixture.CreateContext(new AccountContext());
                return await FeedPageClaims.ClaimAsync(
                    db, accountId, token, $"genre:rail-{rail}", titles, 1, DateTime.UtcNow, CancellationToken.None);
            });

            var dealt = (await Task.WhenAll(claims)).Select(c => Assert.Single(c)).ToList();
            Assert.Equal(20, dealt.Distinct().Count());
        }
    }

    [Fact]
    public async Task One_account_cannot_grow_the_page_store_without_bound()
    {
        if (!Fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        var titles = await SeedTitlesAsync(3, $"claims-{Guid.NewGuid():N}");
        var start = DateTime.UtcNow;
        for (var page = 0; page < 30; page++)
        {
            await using var db = Fixture.CreateContext(new AccountContext());
            await FeedPageClaims.ClaimAsync(
                db, accountId, $"page-{page}", "genre:x", titles, 1, start.AddSeconds(page), CancellationToken.None);
        }

        Assert.InRange(FeedPageClaims.HeldPages(accountId), 1, FeedPageClaims.MaxPagesPerAccount);

        // An over-long token is not a page at all: no set, every title free.
        await using var check = Fixture.CreateContext(new AccountContext());
        var unclaimed = await FeedPageClaims.ClaimAsync(
            check, accountId, new string('x', FeedPageClaims.MaxTokenLength + 1), "genre:y", titles, 3,
            DateTime.UtcNow, CancellationToken.None);
        Assert.Equal(3, unclaimed.Count);
    }

    [Fact]
    public async Task Without_a_model_every_card_is_templater_or_none_and_never_unknown()
    {
        if (!Fixture.Available)
        {
            return;
        }

        var genre = $"trust-{Guid.NewGuid():N}";
        var accountId = await SeedAccountAsync();
        await SeedTitlesAsync(30, genre);
        await SeedActiveArtifactAsync(accountId);

        var items = await MoreAsync(accountId, Genre(genre), new UnavailableSearchAgent());

        Assert.NotEmpty(items);
        Assert.All(items, i => Assert.Contains(i.WhySource.Kind, new[] { WhySourceKind.Templater, WhySourceKind.None }));
        Assert.All(items, i => Assert.Equal(i.WhySource == ExposureWhySource.None, i.WhyThis.Length == 0));
    }

    [Fact]
    public async Task Two_rails_of_one_page_fired_at_once_never_serve_the_same_title()
    {
        if (!Fixture.Available)
        {
            return;
        }

        // Two genre rails over ONE shared pool — every title is in both genres, so without the page
        // claim the two rails would deal the identical top slice.
        var a = $"trust-a-{Guid.NewGuid():N}";
        var b = $"trust-b-{Guid.NewGuid():N}";
        var accountId = await SeedAccountAsync();
        await SeedTitlesAsync(60, a, b);
        await SeedActiveArtifactAsync(accountId);
        var token = Guid.NewGuid().ToString("N");
        using var cache = new MemoryCache(new MemoryCacheOptions());

        var first = MoreAsync(accountId, Genre(a) with { PageToken = token }, new UnavailableSearchAgent(), cache);
        var second = MoreAsync(accountId, Genre(b) with { PageToken = token }, new UnavailableSearchAgent(), cache);
        var pages = await Task.WhenAll(first, second);

        Assert.NotEmpty(pages[0]);
        Assert.NotEmpty(pages[1]);
        Assert.Empty(pages[0].Select(i => i.Title.Id).Intersect(pages[1].Select(i => i.Title.Id)));

        // Control: without a token the same two requests DO overlap — the claim is what separates them.
        var unclaimedA = await MoreAsync(accountId, Genre(a), new UnavailableSearchAgent(), cache);
        var unclaimedB = await MoreAsync(accountId, Genre(b), new UnavailableSearchAgent(), cache);
        Assert.NotEmpty(unclaimedA.Select(i => i.Title.Id).Intersect(unclaimedB.Select(i => i.Title.Id)));
    }

    [Fact]
    public async Task A_page_token_also_keeps_tonights_snapshot_off_the_rails()
    {
        if (!Fixture.Available)
        {
            return;
        }

        var genre = $"trust-{Guid.NewGuid():N}";
        var accountId = await SeedAccountAsync();
        var titles = await SeedTitlesAsync(40, genre);
        await SeedActiveArtifactAsync(accountId);

        // Tonight's picks already show the rail's would-be head.
        var unclaimed = await MoreAsync(accountId, Genre(genre), new UnavailableSearchAgent());
        var onTonight = unclaimed.Take(3).Select(i => i.Title.Id).ToList();
        await SeedActiveSnapshotAsync(accountId, onTonight);

        var claimed = await MoreAsync(
            accountId, Genre(genre) with { PageToken = Guid.NewGuid().ToString("N") }, new UnavailableSearchAgent());
        Assert.NotEmpty(claimed);
        Assert.Empty(claimed.Select(i => i.Title.Id).Intersect(onTonight));

        // And the client-supplied page exclusion works on its own, without a token.
        var excluded = await MoreAsync(
            accountId, Genre(genre) with { PageExcludeTitleIds = onTonight }, new UnavailableSearchAgent());
        Assert.Empty(excluded.Select(i => i.Title.Id).Intersect(onTonight));
        Assert.Contains(titles, t => excluded.Any(i => i.Title.Id == t));
    }

    [Theory]
    [InlineData(FeedRowContext.BecauseYouLoved)]
    // The title page's "More like this" (WS-10A's Similar evidence): the same gate, the same chips.
    [InlineData(FeedRowContext.Similar)]
    // Seeded through WS-3's KeywordNeighbours, as the host wires it (WS-3 CR-6).
    [InlineData(FeedRowContext.Similar, true)]
    public async Task An_anchored_page_serves_only_cards_with_shared_evidence_and_carries_it(
        FeedRowContext context, bool keywordNeighbours = false)
    {
        if (!Fixture.Available)
        {
            return;
        }

        var genre = $"trust-{Guid.NewGuid():N}";
        var accountId = await SeedAccountAsync();

        // The anchor and twenty neighbours share one plot direction; half the neighbours share the
        // anchor's story keyword, half share nothing checkable.
        var axis = Random.Shared.Next(300, 590);
        var anchor = await SeedTitlesAsync(1, genre, keywords: ["heist"], axis: axis);
        var withEvidence = await SeedTitlesAsync(10, genre, keywords: ["heist"], axis: axis);
        var without = await SeedTitlesAsync(10, genre, keywords: ["courtroom"], axis: axis);
        await SeedActiveArtifactAsync(accountId);

        var items = await MoreAsync(
            accountId,
            new MoreFeedRequest(context, anchor[0], null, [], 12),
            new UnavailableSearchAgent(),
            keywordNeighbours: keywordNeighbours);

        Assert.NotEmpty(items);
        Assert.All(items, i => Assert.Contains(i.Title.Id, withEvidence));
        Assert.All(items, i => Assert.Equal([new SharedEvidence("keyword", "heist")], i.Evidence!));
        Assert.DoesNotContain(items, i => without.Contains(i.Title.Id));
    }

    [Fact]
    public async Task A_cold_page_serves_model_order_now_and_its_curation_lands_for_the_next_view()
    {
        if (!Fixture.Available)
        {
            return;
        }

        var genre = $"cold-{Guid.NewGuid():N}";
        var accountId = await SeedAccountAsync();
        await SeedTitlesAsync(50, genre);
        await SeedActiveArtifactAsync(accountId);
        var agent = new SlowCuratingAgent(TimeSpan.FromMilliseconds(400));
        var cache = new MemoryCache(new MemoryCacheOptions());

        // The first view does not wait on the model: model order, templated (or no) lines, and
        // nothing the request did not ask the model for is labelled as if it had.
        var first = await MoreAsync(accountId, Genre(genre), agent, cache, offRequestCuration: true);
        Assert.NotEmpty(first);
        Assert.All(first, i => Assert.NotEqual(ExposureWhySource.Llm, i.WhySource));
        Assert.All(first, i => Assert.NotEqual(ExposureWhySource.Fallback, i.WhySource));

        // The pass ran off the request and filled the fit cache: a later view is curated.
        IReadOnlyList<ScoredFeedItem> later = [];
        for (var attempt = 0; attempt < 50 && !later.Any(i => i.WhySource == ExposureWhySource.Llm); attempt++)
        {
            await Task.Delay(100);
            later = await MoreAsync(accountId, Genre(genre), agent, cache, offRequestCuration: true);
        }

        Assert.True(agent.Calls > 0);
        Assert.Contains(later, i => i.WhySource == ExposureWhySource.Llm);
    }

    [Fact]
    public async Task A_title_rated_after_its_lane_was_scored_is_not_served_from_the_cached_window()
    {
        if (!Fixture.Available)
        {
            return;
        }

        var genre = $"cached-{Guid.NewGuid():N}";
        var accountId = await SeedAccountAsync();
        await SeedTitlesAsync(50, genre);
        await SeedActiveArtifactAsync(accountId);
        var cache = new MemoryCache(new MemoryCacheOptions());

        var first = await MoreAsync(accountId, Genre(genre), new UnavailableSearchAgent(), cache);
        var rated = first[0].Title.Id;
        await using (var db = Fixture.CreateContext(new AccountContext()))
        {
            db.UserRatings.Add(new UserRating
            {
                Id = Guid.NewGuid(),
                AccountId = accountId,
                TitleId = rated,
                SubjectType = RatingSubjectType.Movie,
                Rating = 8,
                RatedAt = DateTime.UtcNow,
                FirstRatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        // The lane's scored window is reused for a minute; the claim-time re-check is what keeps a
        // title the account just rated off the page.
        var second = await MoreAsync(accountId, Genre(genre), new UnavailableSearchAgent(), cache);
        Assert.NotEmpty(second);
        Assert.DoesNotContain(second, i => i.Title.Id == rated);
    }

    private static MoreFeedRequest Genre(string genre) =>
        new(FeedRowContext.Genre, AnchorTitleId: null, Genre: genre, ExcludeTitleIds: [], Limit: 10);

    private async Task<IReadOnlyList<ScoredFeedItem>> MoreAsync(
        Guid accountId, MoreFeedRequest request, ISearchAgent agent, IMemoryCache? sharedCache = null, bool offRequestCuration = false,
        bool keywordNeighbours = false)
    {
        await using var db = Fixture.CreateContext(new AccountContext());
        var account = await db.Accounts.FirstAsync(a => a.Id == accountId);
        var featureBuilder = new FeatureVectorBuilder(db);
        var eligibility = new EligibilityQueryBuilder(db);
        var engine = new FastTreeModelEngine();
        var cache = sharedCache ?? new MemoryCache(new MemoryCacheOptions());
        var tmdb = new SilentTmdbClient();
        var paginator = new FeedPaginator(
            db, featureBuilder, new CandidateRetriever(db, eligibility), engine,
            new OnlineResidualModel(db, featureBuilder, engine, cache, NullLogger<OnlineResidualModel>.Instance),
            new RatingCalibrationService(db, cache), new DislikeSignal(db, cache),
            new CandidateGenerator(db, tmdb, eligibility, NullLogger<CandidateGenerator>.Instance), tmdb, agent,
            new PersonalisationContext(db, new DislikeSignal(db, cache)),
            new FrostAura.Reel.Application.CrossReference.RerankBudget(
                db, new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build()),
            cache, NullLogger<FeedPaginator>.Instance, offRequestCuration ? ScopesFor(agent, cache) : null,
            keywordNeighbours
                ? new FrostAura.Reel.Application.Search.Retrieval.KeywordNeighbours(
                    db, eligibility, new FrostAura.Reel.Application.Search.Retrieval.Concepts.KeywordVocabulary(db, cache))
                : null);
        return await paginator.MoreAsync(account, request, CancellationToken.None);
    }

    /// <summary>The DI scope the off-request curation pass resolves its agent and context from.</summary>
    private IServiceScopeFactory ScopesFor(ISearchAgent agent, IMemoryCache cache)
    {
        var services = new ServiceCollection();
        services.AddScoped<AccountContext>();
        services.AddScoped<IAccountContext>(sp => sp.GetRequiredService<AccountContext>());
        services.AddScoped<IReelDbContext>(sp => Fixture.CreateContext(sp.GetRequiredService<AccountContext>()));
        services.AddSingleton(agent);
        services.AddScoped(sp => new PersonalisationContext(
            sp.GetRequiredService<IReelDbContext>(), new DislikeSignal(sp.GetRequiredService<IReelDbContext>(), cache)));
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    /// <summary>A model pass that answers well, but slowly — what a cold rail waits on today.</summary>
    private sealed class SlowCuratingAgent(TimeSpan delay) : ISearchAgent
    {
        public int Calls;

        public bool IsAvailable => true;

        public string ModelId => "slow-curating-agent";

        public Task<AgentReply> InterpretAsync(IReadOnlyList<ChatTurn> history, string message, string tasteSummary, IReadOnlyList<string> shownTitles, CancellationToken ct = default, string? standingFilters = null) =>
            throw new NotSupportedException();

        public async Task<IReadOnlyList<RerankResult>> RerankBatchAsync(
            IReadOnlyList<RerankInput> titles, string query, LensPersona persona, string? anchorName = null, string usagePurpose = ApiUsagePurpose.Rerank, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Calls);
            await Task.Delay(delay, ct);
            return [.. titles.Select(t => new RerankResult(
                t.TitleId, 0.8d, ResultSource.Llm, "You keep coming back to slow-burn crime like this.", ResultSource.Llm))];
        }
    }

    private async Task<Guid> SeedAccountAsync()
    {
        await using var db = Fixture.CreateContext(new AccountContext());
        var account = new Account
        {
            Id = Guid.NewGuid(),
            TraktUserSlug = $"paginator-trust-{Guid.NewGuid():N}",
            Tier = AccountTier.Paid,
            CreatedAt = DateTime.UtcNow,
            LastSeenAt = DateTime.UtcNow,
            PipelineStageChangedAt = DateTime.UtcNow,
        };
        db.Add(account);
        await db.SaveChangesAsync();
        return account.Id;
    }

    private async Task<IReadOnlyList<Guid>> SeedTitlesAsync(
        int count, string genre, string? secondGenre = null, string[]? keywords = null, int? axis = null,
        string? name = null, string? thirdGenre = null)
    {
        await using var db = Fixture.CreateContext(new AccountContext());
        var ids = new List<Guid>(count);
        for (var i = 0; i < count; i++)
        {
            var title = new Title
            {
                Id = Guid.NewGuid(),
                MediaType = MediaType.Movie,
                TmdbId = Random.Shared.NextInt64(1, long.MaxValue),
                TraktSlug = string.Empty,
                Name = name ?? $"trust {genre} {i} {Guid.NewGuid():N}",
                Language = "en",
                PosterPath = "/p.jpg",
                Genres = new[] { genre, secondGenre, thirdGenre }.OfType<string>().ToArray(),
                Keywords = keywords ?? [],
                // Low popularity: never perturbs the shared catalogue's popularity head.
                TmdbPopularity = 1.5m - (i * 0.001m),
                ReleasedAt = DateTime.UtcNow.AddYears(-2),
                CreatedAt = DateTime.UtcNow,
            };
            db.Add(title);
            if (axis is { } a)
            {
                var vector = new float[1536];
                vector[a] = 1f;
                vector[a + 1] = 0.001f * i; // a hair apart, so ordering is stable
                db.TitleEmbeddings.Add(new TitleEmbedding
                {
                    TitleId = title.Id,
                    Embedding = new Pgvector.Vector(vector),
                    EmbeddingModel = "test",
                    SourceTextHash = $"hash-{title.Id:N}",
                    CreatedAt = DateTime.UtcNow,
                });
            }

            ids.Add(title.Id);
        }

        await db.SaveChangesAsync();
        return ids;
    }

    private async Task SeedActiveSnapshotAsync(Guid accountId, IReadOnlyList<Guid> heroTitleIds)
    {
        await using var db = Fixture.CreateContext(new AccountContext());
        var snapshot = new FeedSnapshot
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            ModelArtifactId = Guid.NewGuid(),
            GeneratedAt = DateTime.UtcNow,
            Status = SnapshotStatus.Active,
        };
        db.FeedSnapshots.Add(snapshot);
        for (var i = 0; i < heroTitleIds.Count; i++)
        {
            db.FeedItems.Add(new FeedItem
            {
                Id = Guid.NewGuid(),
                AccountId = accountId,
                FeedSnapshotId = snapshot.Id,
                Row = FeedRowKind.Hero,
                Rank = i,
                TitleId = heroTitleIds[i],
                PredictedRating = 7m,
                WhyThisSentence = "You rate Tony Gilroy 7.8 on average.",
            });
        }

        await db.SaveChangesAsync();
    }

    private async Task SeedActiveArtifactAsync(Guid accountId)
    {
        var rng = new Random(29);
        var names = FeatureSchema.AllNames();
        var rows = new float[240][];
        var labels = new float[240];
        for (var i = 0; i < rows.Length; i++)
        {
            var v = new float[names.Length];
            for (var j = 0; j < v.Length; j++)
            {
                v[j] = (float)rng.NextDouble();
            }

            rows[i] = v;
            labels[i] = Math.Clamp(3f + (5f * v[0]), 1f, 10f);
        }

        var trained = new FastTreeModelEngine().Train(
            new TrainingMatrix(rows, labels, names),
            """{"Trees":40,"Leaves":8,"MinExamplesPerLeaf":5,"LearningRate":0.2}""");

        await using var db = Fixture.CreateContext(new AccountContext());
        db.ModelArtifacts.Add(new ModelArtifact
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            Version = 1,
            Algo = "FastTree",
            ArtifactBytes = trained.ArtifactBytes,
            FeatureSchemaJson = System.Text.Json.JsonSerializer.Serialize(
                new { version = FeatureSchema.Version, names = FeatureSchema.AllNames() }),
            TrainingRunId = Guid.NewGuid(),
            Status = ArtifactStatus.Active,
            TrainedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    /// <summary>Every batch fails the way a truncated completion does: WS-1's stub, labelled Fallback.</summary>
    private sealed class StubbingAgent : ISearchAgent
    {
        public int Calls;

        /// <summary>Every candidate the paginator handed the model.</summary>
        public System.Collections.Concurrent.ConcurrentBag<RerankInput> Inputs { get; } = [];

        public bool IsAvailable => true;

        public string ModelId => "stubbing-agent";

        public Task<AgentReply> InterpretAsync(IReadOnlyList<ChatTurn> history, string message, string tasteSummary, IReadOnlyList<string> shownTitles, CancellationToken ct = default, string? standingFilters = null) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<RerankResult>> RerankBatchAsync(
            IReadOnlyList<RerankInput> titles, string query, LensPersona persona, string? anchorName = null, string usagePurpose = ApiUsagePurpose.Rerank, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Calls);
            foreach (var title in titles)
            {
                Inputs.Add(title);
            }

            return Task.FromResult<IReadOnlyList<RerankResult>>(titles
                .Select(t => new RerankResult(
                    t.TitleId, (double)(t.Predicted ?? 6m) / 10d, ResultSource.Fallback,
                    "A action pick aligned with your taste.", ResultSource.Fallback))
                .ToList());
        }
    }
}
