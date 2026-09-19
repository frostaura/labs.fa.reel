using FrostAura.Reel.Application.CrossReference;
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
using FrostAura.Reel.Domain.CrossReference;
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
using FrostAura.Reel.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Pgvector;

namespace FrostAura.Reel.Tests.IntegrationTests;

/// <summary>
/// What a cross-reference verdict is actually allowed to do to the page, asserted against real
/// Postgres and a real feed build.
///
/// <para><b>This file is the guard on the keep-only decision of 2026-09-11.</b> Measured on the
/// founder's own holdout, prompt v2, five split origins pooled: keeps went on to average 6.83
/// against 6.16 for rule-outs, and were loved at 33.2% against a 23% base rate — but the same
/// verdict's <c>out</c> arm caught 16.2% [Wilson 12.3, 21.0] of the titles he later loved against
/// 22.8% of the ones he later disliked. So a Keep lifts the rank key by at most a fifth and an Out
/// does nothing whatsoever, and the tests below are written so that reinstating a demotion means
/// deleting an assertion that says why it should not exist.</para>
///
/// <para><b>The displayed number is the other half.</b> <c>PredictedRating</c> is asserted byte for
/// byte across a build with verdicts and a build without: the 2026-09-10 ranking-layer contract is
/// that an LLM opinion may reorder the page and may never move the number on the card, because the
/// moment it does, <c>/api/accuracy</c> stops measuring the model and starts measuring the judge.</para>
///
/// <para><b>Fixture hygiene</b> follows <c>CrossReferenceJobTests</c>: a per-instance genre scope, a
/// language nothing else uses, a reserved embedding dimension block, and raw-SQL cleanup in
/// <see cref="DisposeAsync"/>, because the catalogue and its vector index are shared by every class
/// in the postgres collection.</para>
/// </summary>
[Collection("postgres")]
public class CrossReferenceFeedIntegrationTests(PostgresFixture fixture) : EmbeddedCatalogTestBase(fixture)
{
    /// <summary>The prompt generation these tests write and read verdicts at. See the note on the judge stub.</summary>
    private const int PromptVersion = 2;

    /// <summary>Reserved far from every sibling fixture's query vectors, and deterministic.</summary>
    private const int EmbeddingDimensionBase = 1300;

    /// <summary>A language nothing else in the collection uses.</summary>
    private const string TestLanguage = "zy";

    private readonly string _scope = $"xreffeed{Guid.NewGuid():N}";

    private readonly List<Guid> _accounts = [];

    private int _seeded;

    public override async Task DisposeAsync()
    {
        await base.DisposeAsync();
        if (!Fixture.Available)
        {
            return;
        }

        await using var db = Fixture.CreateContext(new AccountContext());
        var accounts = _accounts.ToArray();
        await db.Database.ExecuteSqlAsync(
            $"""DELETE FROM "TitleCrossReferenceVerdicts" WHERE "AccountId" = ANY({accounts});""");
        await db.Database.ExecuteSqlAsync($"""DELETE FROM "ExposureEvents" WHERE "AccountId" = ANY({accounts});""");
        await db.Database.ExecuteSqlAsync(
            $"""DELETE FROM "FeedItems" WHERE "TitleId" IN (SELECT "Id" FROM "Titles" WHERE {_scope} = ANY("Genres"));""");
        await db.Database.ExecuteSqlAsync($"""DELETE FROM "FeedSnapshots" WHERE "AccountId" = ANY({accounts});""");
        await db.Database.ExecuteSqlAsync($"""DELETE FROM "FeedBuildStats" WHERE "AccountId" = ANY({accounts});""");
        await db.Database.ExecuteSqlAsync(
            $"""DELETE FROM "TitleScores" WHERE "TitleId" IN (SELECT "Id" FROM "Titles" WHERE {_scope} = ANY("Genres"));""");
        await db.Database.ExecuteSqlAsync(
            $"""DELETE FROM "TitleAttributes" WHERE "TitleId" IN (SELECT "Id" FROM "Titles" WHERE {_scope} = ANY("Genres"));""");
        await db.Database.ExecuteSqlAsync(
            $"""DELETE FROM "TitleEmbeddings" WHERE "TitleId" IN (SELECT "Id" FROM "Titles" WHERE {_scope} = ANY("Genres"));""");
        await db.Database.ExecuteSqlAsync(
            $"""DELETE FROM "UserRatings" WHERE "TitleId" IN (SELECT "Id" FROM "Titles" WHERE {_scope} = ANY("Genres"));""");
        await db.Database.ExecuteSqlAsync($"""DELETE FROM "Titles" WHERE {_scope} = ANY("Genres");""");
        await db.Database.ExecuteSqlAsync($"""DELETE FROM "SyncJobs" WHERE "AccountId" = ANY({accounts});""");
    }

    [Fact]
    public async Task A_keep_moves_a_title_up_an_out_moves_nothing_and_the_shown_number_never_changes()
    {
        if (!Fixture.Available)
        {
            return;
        }

        var accountId = await SeedBuildableAccountAsync();
        await SeedRatedNeighboursAsync(accountId, 8);
        await SeedCandidatesAsync(accountId, 14);

        // ── Baseline: a build with an empty verdict table ────────────────────────────────────
        await RunBuildAsync(accountId);
        var before = await OrderAsync(accountId);
        Assert.True(before.Count >= 4, "the fixture must produce a page with something on it");

        // Nothing has judged anything, so every exposure row records that honestly — as NULL, and
        // never as "Unsure", which is a judgement.
        var baselineExposures = await ExposuresAsync(accountId);
        Assert.NotEmpty(baselineExposures);
        Assert.All(baselineExposures, e => Assert.Null(e.XrefVerdict));
        Assert.All(baselineExposures, e => Assert.Null(e.XrefConfidence));

        // Two titles from the middle of the page: one to keep, one to rule out. Middle rather than
        // last so a keep has somewhere to go and an out has somewhere to fall — if the out DID
        // demote, this fixture would show it.
        var keepTitle = before[^2];
        var outTitle = before[before.Count / 2];
        Assert.NotEqual(keepTitle, outTitle);

        await ResetPageAsync(accountId);
        var profileHash = await ProfileHashAsync(accountId);
        await SeedVerdictAsync(accountId, keepTitle, profileHash, CrossReferenceVerdict.Keep, 1.0m);
        await SeedVerdictAsync(accountId, outTitle, profileHash, CrossReferenceVerdict.Out, 0.95m);

        // ── The same build, with the verdicts in place ───────────────────────────────────────
        await RunBuildAsync(accountId);
        var after = await OrderAsync(accountId);

        // The keep moved up…
        Assert.True(
            after.IndexOf(keepTitle) < before.IndexOf(keepTitle),
            $"a confirmed keep did not move up: {before.IndexOf(keepTitle)} → {after.IndexOf(keepTitle)}");

        // …and NOTHING ELSE moved. Asserting on the sequence with the keep removed proves both
        // halves at once: the ruled-out title kept its place relative to every other title, and no
        // third title was reordered as a side effect. A demotion of any size would fail this.
        Assert.Equal(
            before.Where(id => id != keepTitle).ToList(),
            after.Where(id => id != keepTitle).ToList());

        // The card's number is untouched, to the last digit the column stores. This is the
        // ranking-layer contract, and it is what keeps /api/accuracy a measurement of the model.
        var beforeRatings = await RatingsAsync(accountId, before);
        Assert.Equal(_baselineRatings, beforeRatings);

        // The standing reached the exposure log — and only for the titles that actually have one.
        var exposures = await ExposuresAsync(accountId);
        var byTitle = exposures.ToDictionary(e => e.TitleId, e => e);
        Assert.Equal("ProvisionalKeep", byTitle[keepTitle].XrefVerdict);
        Assert.Equal(1.000m, byTitle[keepTitle].XrefConfidence);
        Assert.Equal("Out", byTitle[outTitle].XrefVerdict);
        Assert.Equal(0.950m, byTitle[outTitle].XrefConfidence);
        Assert.All(
            exposures.Where(e => e.TitleId != keepTitle && e.TitleId != outTitle),
            e => Assert.Null(e.XrefVerdict));
    }

    [Fact]
    public async Task A_stale_keep_lifts_at_half_strength_and_an_unsure_verdict_is_recorded_but_inert()
    {
        if (!Fixture.Available)
        {
            return;
        }

        var accountId = await SeedBuildableAccountAsync();
        await SeedRatedNeighboursAsync(accountId, 8);
        await SeedCandidatesAsync(accountId, 14);

        await RunBuildAsync(accountId);
        var before = await OrderAsync(accountId);
        var unsureTitle = before[before.Count / 2];
        var stale = before[^1];
        Assert.NotEqual(stale, unsureTitle);

        await ResetPageAsync(accountId);
        // Judged at a fingerprint the account has since moved off. The row is not deleted — a full
        // re-judgement per fingerprint step would cost more than the feature is worth — so it lifts
        // at half strength and says so.
        await SeedVerdictAsync(accountId, stale, "movedon0000beef", CrossReferenceVerdict.Keep, 1.0m);
        await SeedVerdictAsync(
            accountId, unsureTitle, await ProfileHashAsync(accountId), CrossReferenceVerdict.Unsure, 0.5m);

        await RunBuildAsync(accountId);
        var after = await OrderAsync(accountId);

        // The unsure verdict is a judgement, is recorded as one, and reorders nothing: take the
        // stale keep out of both sequences and the page is exactly what it was. Its ABSOLUTE index
        // does move, because the keep travelled past it — which is the keep's doing, not its own.
        var exposures = (await ExposuresAsync(accountId)).ToDictionary(e => e.TitleId, e => e);
        Assert.Equal("Unsure", exposures[unsureTitle].XrefVerdict);
        Assert.Equal(
            before.Where(id => id != stale).ToList(),
            after.Where(id => id != stale).ToList());

        // The stale keep still lifts — half of +0.1 rather than nothing — which is the lens's
        // Fresh → Stale → None ladder applied to a verdict, and it is recorded as stale evidence
        // rather than deleted.
        Assert.Equal("ProvisionalKeep", exposures[stale].XrefVerdict);
        Assert.True(
            after.IndexOf(stale) < before.IndexOf(stale),
            "a stale keep did not lift at all; staleness halves a lift, it never removes one");
    }

    [Fact]
    public async Task A_verdict_at_another_prompt_generation_is_invisible_to_the_feed()
    {
        if (!Fixture.Available)
        {
            return;
        }

        // A stored verdict means what its prompt generation meant. Reading v1 rows into a feed
        // being served by v2 would mix two different questions under one answer — which is exactly
        // why the version is part of the verdict key.
        var accountId = await SeedBuildableAccountAsync();
        await SeedRatedNeighboursAsync(accountId, 8);
        await SeedCandidatesAsync(accountId, 12);

        await RunBuildAsync(accountId);
        var before = await OrderAsync(accountId);

        await ResetPageAsync(accountId);
        await SeedVerdictAsync(
            accountId, before[^1], await ProfileHashAsync(accountId), CrossReferenceVerdict.Keep, 1.0m,
            promptVersion: PromptVersion - 1);

        await RunBuildAsync(accountId);

        Assert.Equal(before, await OrderAsync(accountId));
        Assert.All(await ExposuresAsync(accountId), e => Assert.Null(e.XrefVerdict));
    }

    // ── Fixture ─────────────────────────────────────────────────────────────────────────────

    /// <summary>The displayed ratings from the FIRST build, captured for the byte-identity assertion.</summary>
    private Dictionary<Guid, decimal> _baselineRatings = [];

    /// <summary>
    /// The page as an ordered sequence of title ids — row order, then rank within the row. This is
    /// the ordering the verdict factor is allowed to change, and nothing else about the page is.
    /// </summary>
    private async Task<List<Guid>> OrderAsync(Guid accountId)
    {
        await using var db = Fixture.CreateContext(Scoped(accountId));
        var snapshot = await db.FeedSnapshots
            .Where(s => s.AccountId == accountId)
            .OrderByDescending(s => s.GeneratedAt)
            .FirstAsync();

        var items = await db.FeedItems
            .Where(i => i.FeedSnapshotId == snapshot.Id)
            .OrderBy(i => i.Row)
            .ThenBy(i => i.AnchorTitleId)
            .ThenBy(i => i.Rank)
            .Select(i => new { i.TitleId, i.PredictedRating })
            .ToListAsync();

        // Captured on the first call and asserted against on a later one — the two builds have to
        // agree about the number on every card they share.
        if (_baselineRatings.Count == 0)
        {
            _baselineRatings = items
                .GroupBy(i => i.TitleId)
                .ToDictionary(g => g.Key, g => g.First().PredictedRating);
        }

        return items.Select(i => i.TitleId).ToList();
    }

    private async Task<Dictionary<Guid, decimal>> RatingsAsync(Guid accountId, IReadOnlyList<Guid> titleIds)
    {
        await using var db = Fixture.CreateContext(Scoped(accountId));
        var snapshot = await db.FeedSnapshots
            .Where(s => s.AccountId == accountId)
            .OrderByDescending(s => s.GeneratedAt)
            .FirstAsync();

        return (await db.FeedItems
                .Where(i => i.FeedSnapshotId == snapshot.Id && titleIds.Contains(i.TitleId))
                .Select(i => new { i.TitleId, i.PredictedRating })
                .ToListAsync())
            .GroupBy(i => i.TitleId)
            .ToDictionary(g => g.Key, g => g.First().PredictedRating);
    }

    private async Task<List<ExposureEvent>> ExposuresAsync(Guid accountId)
    {
        await using var db = Fixture.CreateContext(Scoped(accountId));
        return await db.ExposureEvents
            .Where(e => e.AccountId == accountId)
            .OrderBy(e => e.OccurredAt)
            .ToListAsync();
    }

    /// <summary>
    /// Clears the page and its exposures so the next build is a genuine Fresh one.
    ///
    /// <para>Deliberately not a forced trigger reason: <c>FeedBuildPolicy.IsUserForced</c> also
    /// holds back the superseded snapshot's anchors, which would rotate the page for a reason that
    /// has nothing to do with the verdicts under test.</para>
    /// </summary>
    private async Task ResetPageAsync(Guid accountId)
    {
        await using var db = Fixture.CreateContext(new AccountContext());
        await db.Database.ExecuteSqlAsync(
            $"""DELETE FROM "FeedItems" WHERE "FeedSnapshotId" IN (SELECT "Id" FROM "FeedSnapshots" WHERE "AccountId" = {accountId});""");
        await db.Database.ExecuteSqlAsync($"""DELETE FROM "FeedSnapshots" WHERE "AccountId" = {accountId};""");
        await db.Database.ExecuteSqlAsync($"""DELETE FROM "ExposureEvents" WHERE "AccountId" = {accountId};""");
    }

    /// <summary>The account's current taste fingerprint, computed exactly as the build computes it.</summary>
    private async Task<string> ProfileHashAsync(Guid accountId)
    {
        await using var db = Fixture.CreateContext(Scoped(accountId));
        var cache = new MemoryCache(new MemoryCacheOptions());
        var features = new FeatureVectorBuilder(db);
        var taste = await features.BuildTasteStateAsync(accountId, DateTime.UtcNow, CancellationToken.None);
        var facts = new TasteFactsBuilder(db, new DislikeSignal(db, cache), features, cache);
        return (await facts.BuildAsync(accountId, taste, DateTime.UtcNow, CancellationToken.None)).ProfileHash;
    }

    private async Task SeedVerdictAsync(
        Guid accountId, Guid titleId, string profileHash, CrossReferenceVerdict verdict, decimal confidence,
        int promptVersion = PromptVersion)
    {
        await using var db = Fixture.CreateContext(new AccountContext());
        db.TitleCrossReferenceVerdicts.Add(new TitleCrossReferenceVerdict
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            TitleId = titleId,
            ProfileHash = profileHash,
            PromptVersion = promptVersion,
            Round = CrossReferenceRound.First,
            Verdict = verdict,
            ReasonCode = verdict switch
            {
                CrossReferenceVerdict.Keep => CrossReferenceReasons.NeighbourLoved,
                CrossReferenceVerdict.Out => CrossReferenceReasons.NeighbourDisliked,
                _ => CrossReferenceReasons.InsufficientSignal,
            },
            Confidence = confidence,
            NeighbourIdsJson = "[]",
            ModelId = "stub/cross-reference",
            BatchIndex = 0,
            GeneratedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private async Task<Guid> SeedAccountAsync()
    {
        await using var db = Fixture.CreateContext(new AccountContext());
        var account = new Account
        {
            Id = Guid.NewGuid(),
            TraktUserSlug = $"xreffeed-{Guid.NewGuid():N}",
            CreatedAt = DateTime.UtcNow,
            LastSeenAt = DateTime.UtcNow,
            PipelineStageChangedAt = DateTime.UtcNow,
        };
        db.Add(account);
        // Narrows the eligible pool to this instance's own rows — the catalogue is global — and
        // opts into the language they carry, without which the pool is English-only and empty.
        db.ContentFilters.Add(new ContentFilter
        {
            Id = Guid.NewGuid(),
            AccountId = account.Id,
            Kind = FilterKind.IncludeGenre,
            Value = _scope,
            CreatedAt = DateTime.UtcNow,
        });
        db.ContentFilters.Add(new ContentFilter
        {
            Id = Guid.NewGuid(),
            AccountId = account.Id,
            Kind = FilterKind.Language,
            Value = TestLanguage,
            CreatedAt = DateTime.UtcNow,
        });
        db.ModelArtifacts.Add(new ModelArtifact
        {
            Id = Guid.NewGuid(),
            AccountId = account.Id,
            Version = 1,
            ArtifactBytes = [1],
            TrainingRunId = Guid.NewGuid(),
            Status = ArtifactStatus.Active,
            TrainedAt = DateTime.UtcNow,
            FeatureSchemaJson = System.Text.Json.JsonSerializer.Serialize(
                new { version = FeatureSchema.Version, names = FeatureSchema.AllNames() }),
        });
        await db.SaveChangesAsync();
        _accounts.Add(account.Id);
        return account.Id;
    }

    /// <summary>The same account with an artifact a real feed build can actually score with.</summary>
    private async Task<Guid> SeedBuildableAccountAsync()
    {
        var accountId = await SeedAccountAsync();
        var rng = new Random(41);
        var names = FeatureSchema.AllNames();
        var rows = new float[120][];
        var labels = new float[120];
        for (var i = 0; i < rows.Length; i++)
        {
            var v = new float[names.Length];
            for (var j = 0; j < v.Length; j++)
            {
                v[j] = (float)rng.NextDouble();
            }

            rows[i] = v;
            labels[i] = Math.Clamp(3f + (4f * v[0]), 1f, 8f);
        }

        var trained = new FastTreeModelEngine().Train(
            new TrainingMatrix(rows, labels, names),
            """{"Trees":10,"Leaves":4,"MinExamplesPerLeaf":5,"LearningRate":0.2}""");

        await using var db = Fixture.CreateContext(new AccountContext());
        await db.Database.ExecuteSqlAsync(
            $"""UPDATE "ModelArtifacts" SET "ArtifactBytes" = {trained.ArtifactBytes} WHERE "AccountId" = {accountId}""");
        return accountId;
    }

    private async Task SeedRatedNeighboursAsync(Guid accountId, int count)
    {
        await using var db = Fixture.CreateContext(new AccountContext());
        for (var i = 0; i < count; i++)
        {
            var title = NewTitle($"Rated {i}");
            db.Add(title);
            db.TitleEmbeddings.Add(NewEmbedding(title.Id));
            db.UserRatings.Add(new UserRating
            {
                Id = Guid.NewGuid(),
                AccountId = accountId,
                TitleId = title.Id,
                SubjectType = RatingSubjectType.Movie,
                Rating = (short)(i % 2 == 0 ? 9 : 3),
                RatedAt = DateTime.UtcNow.AddDays(-100 + i),
                FirstRatedAt = DateTime.UtcNow.AddDays(-100 + i),
                SyncedAt = DateTime.UtcNow.AddDays(-100 + i),
            });
        }

        await db.SaveChangesAsync();
    }

    private async Task SeedCandidatesAsync(Guid accountId, int count)
    {
        await using var db = Fixture.CreateContext(new AccountContext());
        var artifactId = await db.ModelArtifacts
            .Where(a => a.AccountId == accountId)
            .Select(a => a.Id)
            .FirstAsync();

        for (var i = 0; i < count; i++)
        {
            var title = NewTitle($"Candidate {i:D2} {Guid.NewGuid():N}");
            db.Add(title);
            db.TitleEmbeddings.Add(NewEmbedding(title.Id));
            db.TitleAttributes.Add(new TitleAttributes
            {
                TitleId = title.Id,
                ExtractorModel = "xreffeed/test",
                ExtractorVersion = 2,
                Status = AttributeExtractionStatus.Done,
                AttemptCount = 1,
                ExtractedAt = DateTime.UtcNow,
                Themes = ["revenge"],
                Pacing = 0.5m,
            });
            db.TitleScores.Add(new TitleScore
            {
                Id = Guid.NewGuid(),
                AccountId = accountId,
                TitleId = title.Id,
                ModelArtifactId = artifactId,
                PredictedRating = 7.5m,
                ScoredAt = DateTime.UtcNow,
            });
        }

        await db.SaveChangesAsync();
    }

    private Title NewTitle(string name) => new()
    {
        Id = Guid.NewGuid(),
        MediaType = MediaType.Movie,
        TmdbId = Random.Shared.NextInt64(1, long.MaxValue),
        TraktSlug = $"xreffeed-{Guid.NewGuid():N}",
        Name = name,
        Language = TestLanguage,
        PosterPath = "/p.jpg",
        Genres = ["drama", _scope],
        TmdbPopularity = 1m,
        Overview = "A synthetic title for the cross-reference feed tests.",
        // Fixed and old, so the freshness term is exactly 1.0 for every card and cannot introduce
        // an ordering difference between two builds run minutes apart.
        ReleasedAt = new DateTime(2005, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        LastMetadataRefreshAt = DateTime.UtcNow,
        CreatedAt = DateTime.UtcNow,
    };

    private TitleEmbedding NewEmbedding(Guid titleId)
    {
        var vector = new float[1536];
        vector[EmbeddingDimensionBase + (Interlocked.Increment(ref _seeded) % 200)] = 1f;
        return new TitleEmbedding
        {
            TitleId = titleId,
            Embedding = new Vector(vector),
            EmbeddingModel = "test",
            SourceTextHash = Guid.NewGuid().ToString("N"),
            CreatedAt = DateTime.UtcNow,
        };
    }

    /// <summary>
    /// The real feed build, with the verdict reader wired in exactly as DI supplies it — and with
    /// the cross-reference SCHEDULER left out, because nothing here is testing the enqueue.
    /// </summary>
    private async Task RunBuildAsync(Guid accountId)
    {
        await using var db = Fixture.CreateContext(Scoped(accountId));
        var cache = new MemoryCache(new MemoryCacheOptions());
        var features = new FeatureVectorBuilder(db);
        var eligibility = new EligibilityQueryBuilder(db);
        var dislikes = new DislikeSignal(db, cache);
        var tmdb = new OfflineTmdbClient();
        var handler = new BuildFeedJobHandler(
            db,
            features,
            new CandidateGenerator(db, tmdb, eligibility, NullLogger<CandidateGenerator>.Instance),
            new CandidateRetriever(db, eligibility),
            eligibility,
            new FeedStarvationDiagnostic(db, eligibility),
            new TitleHydrator(db, tmdb, NullLogger<TitleHydrator>.Instance),
            new FastTreeModelEngine(),
            new OnlineResidualModel(
                db, features, new FastTreeModelEngine(), cache, NullLogger<OnlineResidualModel>.Instance),
            new RatingCalibrationService(db, cache),
            dislikes,
            new UnavailableSearchAgent(),
            new TasteFactsBuilder(db, dislikes, features, cache),
            new PersonalisationContext(db, dislikes),
            cache,
            new NoopEventHub(),
            new ExposureLog(db, NullLogger<ExposureLog>.Instance),
            NullLogger<BuildFeedJobHandler>.Instance,
            crossReferenceFactors: new CrossReferenceFactors(
                db,
                new CrossReferenceService(db, NullLogger<CrossReferenceService>.Instance),
                // Version-only: the build reads verdicts, it never judges. Pinned rather than taken
                // from the adapter so a prompt bump does not silently change what these assert.
                new FixedVersionCrossReferenceJudge(PromptVersion)));

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

    private static AccountContext Scoped(Guid accountId)
    {
        var context = new AccountContext();
        context.SetAccount(accountId);
        return context;
    }

    private sealed class NoopEventHub : IPipelineEventHub
    {
        public void Publish(Guid accountId, string type, IReadOnlyDictionary<string, object?> data)
        {
        }

        public async IAsyncEnumerable<PipelineSseEvent> SubscribeAsync(
            Guid accountId,
            long? lastEventId,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    /// <summary>A TMDB port with no corpus: discovery adds nothing, so the pool is what was seeded.</summary>
    private sealed class OfflineTmdbClient : ITmdbClient
    {
        public Task<TmdbTitleDetails?> GetMovieAsync(long tmdbId, CancellationToken ct = default) =>
            Task.FromResult<TmdbTitleDetails?>(null);

        public Task<TmdbTitleDetails?> GetTvAsync(long tmdbId, CancellationToken ct = default) =>
            Task.FromResult<TmdbTitleDetails?>(null);

        public Task<TmdbDiscoverPage> DiscoverSeedPageAsync(
            bool movies, int year, int minVoteCount, int page, CancellationToken ct = default) =>
            Task.FromResult(new TmdbDiscoverPage([], page, 0, 0));

        public Task<IReadOnlyList<TmdbListItem>> DiscoverAsync(
            bool movies, int? genreId, string? region, DateTime? releasedAfter, int page, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TmdbListItem>>([]);

        public Task<IReadOnlyList<TmdbListItem>> GetTrendingAsync(bool movies, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TmdbListItem>>([]);

        public Task<IReadOnlyList<TmdbListItem>> GetRecommendationsAsync(
            bool movies, long tmdbId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TmdbListItem>>([]);

        public Task<IReadOnlyList<TmdbListItem>> GetSimilarAsync(
            bool movies, long tmdbId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TmdbListItem>>([]);

        public Task<IReadOnlyList<TmdbKeyword>> SearchKeywordsAsync(string query, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TmdbKeyword>>([]);

        public Task<IReadOnlyList<TmdbListItem>> SearchTitlesAsync(
            bool movies, string query, int page, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TmdbListItem>>([]);

        public Task<TmdbPerson?> SearchPersonAsync(string name, CancellationToken ct = default) =>
            Task.FromResult<TmdbPerson?>(null);

        public Task<IReadOnlyList<TmdbListItem>> GetPersonCreditsAsync(
            long personId, PersonCreditRole role, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TmdbListItem>>([]);

        public Task<IReadOnlyList<TmdbListItem>> DiscoverByConceptAsync(
            bool movies, IReadOnlyList<int> genreIds, IReadOnlyList<int> keywordIds, string? region,
            DateTime? releasedAfter, int page, IReadOnlyList<int>? excludeGenreIds = null,
            IReadOnlyList<int>? excludeKeywordIds = null, DateTime? releasedBefore = null,
            int? maxRuntimeMinutes = null, TmdbDiscoverOptions? options = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TmdbListItem>>([]);

        public Task<IReadOnlyList<TmdbWatchProvider>> GetWatchProvidersAsync(
            long tmdbId, bool movie, string region, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TmdbWatchProvider>>([]);
    }
}
