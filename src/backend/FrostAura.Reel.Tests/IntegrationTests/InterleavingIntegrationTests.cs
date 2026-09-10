using FrostAura.Reel.Application.Ingestion;
using FrostAura.Reel.Application.Jobs;
using FrostAura.Reel.Application.Ml;
using FrostAura.Reel.Application.Experiments;
using FrostAura.Reel.Application.Ml.ClosedLoop;
using FrostAura.Reel.Application.Pipeline;
using FrostAura.Reel.Application.Ranking;
using FrostAura.Reel.Application.Ranking.Interleaving;
using FrostAura.Reel.Application.Search;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Feed;
using FrostAura.Reel.Domain.Lens;
using FrostAura.Reel.Domain.Library;
using FrostAura.Reel.Domain.Ml;
using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Domain.Ports.Tmdb;
using FrostAura.Reel.Domain.Sync;
using FrostAura.Reel.Domain.Tenancy;
using FrostAura.Reel.Infrastructure.Ml;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrostAura.Reel.Tests.IntegrationTests;

/// <summary>
/// The interleaving harness against real Postgres — the half the simulations cannot reach.
///
/// <para>What the unit tests prove is that the draft is balanced and the sequential test is honest.
/// What only a real build can prove is that the attribution actually SURVIVES the pipeline: that
/// every card a drafted slate produced carries its ranker on the exposure row, that a Preserve
/// rebuild does not silently drop the attribution of cards it is keeping on screen, that a serve
/// path with no slate writes nothing, and — the one that matters most — that with the switch off
/// the feed and the log are exactly what they were before this existed.</para>
///
/// <para>Skips when REEL_TEST_CONNECTION_STRING is unset.</para>
/// </summary>
[Collection("postgres")]
public class InterleavingIntegrationTests(PostgresFixture fixture)
{
    private static readonly InterleaveOptions On = new()
    {
        Enabled = true,
        PolicyA = CalibratedModelPolicy.Id,
        PolicyB = PopularityBaselinePolicy.Id,
    };

    [Fact]
    public async Task A_fresh_interleaved_build_attributes_every_card_to_one_of_the_two_rankers()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        await SeedLovedTitlesAsync(accountId, 12);
        await SeedEligibleTitlesAsync(120);
        await SeedActiveArtifactAsync(accountId);

        await RunBuildAsync(accountId, On);

        await using var db = fixture.CreateContext(new AccountContext());
        var snapshotId = await ActiveSnapshotIdAsync(db, accountId);
        var exposures = await db.ExposureEvents
            .Where(e => e.AccountId == accountId && e.FeedSnapshotId == snapshotId)
            .ToListAsync();

        Assert.NotEmpty(exposures);
        Assert.All(exposures, e =>
        {
            Assert.NotNull(e.PolicyId);
            Assert.NotNull(e.InterleaveGroup);
            Assert.Contains(e.PolicyId, new[] { CalibratedModelPolicy.Id, PopularityBaselinePolicy.Id });
        });

        // Every card the snapshot shows is a card the log attributes — the two are the same set,
        // not merely the same size.
        var feedTitles = await db.FeedItems
            .Where(i => i.FeedSnapshotId == snapshotId)
            .Select(i => i.TitleId)
            .ToListAsync();
        Assert.Equal(feedTitles.OrderBy(x => x), exposures.Select(e => e.TitleId).OrderBy(x => x));

        // Per slate: balanced to within one seat, and no title drafted twice.
        var slates = exposures.GroupBy(e => e.InterleaveGroup!.Value).ToList();
        Assert.True(slates.Count >= 2, "the seeded pool should produce a hero and at least one rail.");
        foreach (var slate in slates)
        {
            var fromA = slate.Count(e => e.PolicyId == CalibratedModelPolicy.Id);
            var fromB = slate.Count() - fromA;

            Assert.True(
                Math.Abs(fromA - fromB) <= 1,
                FormattableString.Invariant($"Slate {slate.Key} split {fromA}/{fromB} — the draft is not balanced."));
            Assert.Equal(slate.Count(), slate.Select(e => e.TitleId).Distinct().Count());
        }

        // Both rankers actually contributed across the build. A run where one arm supplied
        // everything would still pass the per-slate balance check on 1-card slates, and would mean
        // the comparison had quietly become a ranker against itself.
        Assert.Contains(exposures, e => e.PolicyId == CalibratedModelPolicy.Id);
        Assert.Contains(exposures, e => e.PolicyId == PopularityBaselinePolicy.Id);
    }

    [Fact]
    public async Task The_same_snapshot_drafts_the_same_slate_twice_running()
    {
        if (!fixture.Available)
        {
            return;
        }

        // The determinism the exposure log's audit trail depends on, at the level the seed is used:
        // one snapshot id and one row key must always produce the same coin flip and the same
        // group id, in this process and in the next one.
        var snapshotId = Guid.NewGuid();
        Assert.Equal(InterleaveSeed.GroupId(snapshotId, "hero"), InterleaveSeed.GroupId(snapshotId, "hero"));
        Assert.Equal(InterleaveSeed.ADraftsFirst(snapshotId, "loved:x"), InterleaveSeed.ADraftsFirst(snapshotId, "loved:x"));
        Assert.NotEqual(InterleaveSeed.GroupId(snapshotId, "hero"), InterleaveSeed.GroupId(snapshotId, "newforyou"));

        await Task.CompletedTask;
    }

    [Fact]
    public async Task A_preserve_rebuild_keeps_the_seats_and_the_attribution_that_came_with_them()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        await SeedLovedTitlesAsync(accountId, 12);
        await SeedEligibleTitlesAsync(120);
        await SeedActiveArtifactAsync(accountId);

        await RunBuildAsync(accountId, On);

        Dictionary<Guid, string> before;
        Guid firstSnapshotId;
        await using (var db = fixture.CreateContext(new AccountContext()))
        {
            firstSnapshotId = await ActiveSnapshotIdAsync(db, accountId);
            before = await db.ExposureEvents
                .Where(e => e.AccountId == accountId && e.FeedSnapshotId == firstSnapshotId)
                .ToDictionaryAsync(e => e.TitleId, e => e.PolicyId!);
        }

        // Rate one hero pick: eligibility drops it, so the Preserve rebuild has a real change to
        // write and mints a new snapshot rather than suppressing itself. Every OTHER card keeps its
        // seat — and must keep the ranker that earned it, or the experiment silently loses the rows
        // that are still on the user's screen.
        var rated = await HeroTitleAsync(accountId, firstSnapshotId);
        await using (var db = fixture.CreateContext(new AccountContext()))
        {
            db.UserRatings.Add(new UserRating
            {
                Id = Guid.NewGuid(),
                AccountId = accountId,
                TitleId = rated,
                SubjectType = RatingSubjectType.Movie,
                Rating = 9,
                RatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        // Deliberately built with the switch OFF: turning the experiment off must stop new slates
        // being drafted; it must not orphan the ones already on screen.
        await RunBuildAsync(accountId, InterleaveOptions.Disabled, triggerReason: "in-app rating");

        await using (var db = fixture.CreateContext(new AccountContext()))
        {
            var secondSnapshotId = await ActiveSnapshotIdAsync(db, accountId);
            Assert.NotEqual(firstSnapshotId, secondSnapshotId);

            var after = await db.ExposureEvents
                .Where(e => e.AccountId == accountId && e.FeedSnapshotId == secondSnapshotId)
                .ToListAsync();

            var kept = after.Where(e => before.ContainsKey(e.TitleId)).ToList();
            Assert.NotEmpty(kept);
            Assert.All(kept, e =>
            {
                Assert.Equal(before[e.TitleId], e.PolicyId);
                Assert.NotNull(e.InterleaveGroup);
            });

            // The rated title is gone from the feed entirely, and cards the preserved snapshot never
            // showed are drafted by nobody — they were chosen by the ordinary, non-interleaved fill.
            Assert.DoesNotContain(after, e => e.TitleId == rated);
            Assert.All(after.Where(e => !before.ContainsKey(e.TitleId)), e => Assert.Null(e.PolicyId));
        }
    }

    [Fact]
    public async Task A_serve_path_with_no_slate_writes_no_attribution()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        await SeedEligibleTitlesAsync(1);

        var scoped = new AccountContext();
        scoped.SetAccount(accountId);
        await using var db = fixture.CreateContext(scoped);
        var titleId = await db.Titles.OrderBy(t => t.Id).Select(t => t.Id).FirstAsync();
        var log = new ExposureLog(db, NullLogger<ExposureLog>.Instance);

        // Exactly the call shape FeedEndpoints' live refill makes: a card, a rank, a number, and no
        // policy — because a refill is not a slate and there is nothing to attribute it to.
        var written = await log.RecordAsync(
            accountId,
            ExposureSurface.FeedRefill,
            Guid.NewGuid(),
            Guid.NewGuid(),
            DateTime.UtcNow,
            [new ExposureCard(titleId, 0, 7.4m, WhySource: ExposureWhySource.Preserved)],
            CancellationToken.None);

        Assert.Equal(1, written);
        var row = await db.ExposureEvents.SingleAsync(e => e.AccountId == accountId && e.TitleId == titleId);
        Assert.Null(row.PolicyId);
        Assert.Null(row.InterleaveGroup);

        // And the pair is all-or-nothing: half an attribution would be a slate the summary could
        // count but not attribute, or a policy it could attribute but not group.
        var halfWritten = await log.RecordAsync(
            accountId,
            ExposureSurface.FeedPage,
            null,
            Guid.NewGuid(),
            DateTime.UtcNow,
            [new ExposureCard(titleId, 0, 7.4m, PolicyId: CalibratedModelPolicy.Id)],
            CancellationToken.None);

        Assert.Equal(1, halfWritten);
        var partial = await db.ExposureEvents.SingleAsync(e =>
            e.AccountId == accountId && e.Surface == ExposureSurface.FeedPage);
        Assert.Null(partial.PolicyId);
        Assert.Null(partial.InterleaveGroup);
    }

    [Fact]
    public async Task With_the_switch_off_the_feed_and_the_log_are_exactly_what_they_were()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        await SeedLovedTitlesAsync(accountId, 12);
        await SeedEligibleTitlesAsync(120);
        await SeedActiveArtifactAsync(accountId);

        // Explicitly disabled, and — separately — not configured at all, which is what every
        // pre-existing construction site in this suite does. The two must agree.
        await RunBuildAsync(accountId, InterleaveOptions.Disabled);
        var withDisabled = await FeedShapeAsync(accountId);

        await SupersedeEverythingAsync(accountId);
        await RunBuildAsync(accountId, options: null);
        var withNothing = await FeedShapeAsync(accountId);

        Assert.Equal(withDisabled.Titles, withNothing.Titles);
        Assert.All(withDisabled.Policies, Assert.Null);
        Assert.All(withNothing.Policies, Assert.Null);
    }

    [Fact]
    public async Task The_summary_counts_slates_the_way_the_test_needs_them_counted()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        await SeedEligibleTitlesAsync(24);

        // Eight hand-built slates of two cards each: five where A's card earned a save, two where
        // B's did, one where both did. The arithmetic is the point — five/two decided, one tie —
        // because everything downstream of it (the p-value, the CI, the estimate) is a pure
        // function of those three numbers and would look perfectly plausible if they were wrong.
        List<Guid> titles;
        await using (var db = fixture.CreateContext(new AccountContext()))
        {
            titles = await db.Titles.OrderBy(t => t.Id).Select(t => t.Id).Take(24).ToListAsync();
        }

        var served = new DateTime(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc);
        var outcomes = new[] { "A", "A", "A", "A", "A", "B", "B", "both" };
        await using (var db = fixture.CreateContext(new AccountContext()))
        {
            for (var slate = 0; slate < outcomes.Length; slate++)
            {
                var group = Guid.NewGuid();
                var at = served.AddDays(slate);
                var cardA = titles[slate * 2];
                var cardB = titles[(slate * 2) + 1];
                db.ExposureEvents.Add(NewExposure(accountId, cardA, group, CalibratedModelPolicy.Id, at, 0));
                db.ExposureEvents.Add(NewExposure(accountId, cardB, group, PopularityBaselinePolicy.Id, at, 1));

                if (outcomes[slate] is "A" or "both")
                {
                    db.UserTitleReactions.Add(NewSave(accountId, cardA, at.AddHours(1)));
                }

                if (outcomes[slate] is "B" or "both")
                {
                    db.UserTitleReactions.Add(NewSave(accountId, cardB, at.AddHours(1)));
                }
            }

            await db.SaveChangesAsync();
        }

        await using (var db = fixture.CreateContext(new AccountContext()))
        {
            var summary = await new InterleaveSummaryQuery(db, On)
                .ComputeAsync(accountId, served.AddDays(9), CancellationToken.None);

            Assert.Equal(8, summary.Slates);
            Assert.Equal(7, summary.DecidedSlates);
            Assert.Equal(1, summary.Ties);
            Assert.Equal(5, summary.WinsA);
            Assert.Equal(2, summary.WinsB);
            Assert.Equal(5d / 7d, summary.PreferenceRateA!.Value, 6);

            // Seven decided slates at 5–2 is nowhere near a verdict, and the panel must say so.
            Assert.InRange(summary.AlwaysValidP, 0.05d, 1d);
            Assert.False(summary.Significant);
            Assert.True(summary.CiLow < summary.PreferenceRateA);
            Assert.True(summary.CiHigh > summary.PreferenceRateA);

            // One decided slate a day, and the projection says months — the honest cadence, derived
            // from the observed rate rather than from a hoped-for one.
            Assert.Equal(1d, summary.DecidedSlatesPerDay!.Value, 1);
            Assert.InRange(summary.SlatesToDecisionAt65, 60, 200);
            Assert.InRange(summary.EstimatedDaysToDecision!.Value, 50d, 200d);

            var standingA = summary.Standings.Single(s => s.PolicyId == CalibratedModelPolicy.Id);
            Assert.Equal("A", standingA.Team);
            Assert.Equal(8, standingA.Cards);
            Assert.Equal(6, standingA.Credits);
            Assert.Equal(5, standingA.SlateWins);
        }

        // Re-pointing an arm must not silently blend two experiments: slates drafted between the old
        // pair stop entering the p-value and fall back to the standings.
        await using (var db = fixture.CreateContext(new AccountContext()))
        {
            var reconfigured = await new InterleaveSummaryQuery(
                    db,
                    new InterleaveOptions { Enabled = true, PolicyA = ProfileRerankPolicy.Id, PolicyB = PopularityBaselinePolicy.Id })
                .ComputeAsync(accountId, served.AddDays(9), CancellationToken.None);

            Assert.Equal(0, reconfigured.Slates);
            Assert.Equal(0, reconfigured.DecidedSlates);
            Assert.Equal(1d, reconfigured.AlwaysValidP);
            Assert.Contains(reconfigured.Standings, s => s.PolicyId == CalibratedModelPolicy.Id && s.Team is null);
        }
    }

    private static ExposureEvent NewExposure(
        Guid accountId, Guid titleId, Guid group, string policyId, DateTime at, int rank) => new()
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            TitleId = titleId,
            OccurredAt = at,
            Surface = ExposureSurface.FeedRow,
            RowKey = "deepcuts",
            Rank = rank,
            FeedSnapshotId = null,
            ModelArtifactId = Guid.NewGuid(),
            PredictedRating = 7.1m,
            WhySource = ExposureWhySource.Templater,
            PolicyId = policyId,
            InterleaveGroup = group,
        };

    private static UserTitleReaction NewSave(Guid accountId, Guid titleId, DateTime at) => new()
    {
        Id = Guid.NewGuid(),
        AccountId = accountId,
        TitleId = titleId,
        Kind = ReactionKind.SaveForLater,
        CreatedAt = at,
    };

    private async Task<(List<Guid> Titles, List<string?> Policies)> FeedShapeAsync(Guid accountId)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var snapshotId = await ActiveSnapshotIdAsync(db, accountId);
        var titles = await db.FeedItems
            .Where(i => i.FeedSnapshotId == snapshotId)
            .OrderBy(i => i.Row).ThenBy(i => i.AnchorTitleId).ThenBy(i => i.Rank)
            .Select(i => i.TitleId)
            .ToListAsync();
        var policies = await db.ExposureEvents
            .Where(e => e.AccountId == accountId && e.FeedSnapshotId == snapshotId)
            .OrderBy(e => e.TitleId)
            .Select(e => e.PolicyId)
            .ToListAsync();
        return (titles, policies);
    }

    /// <summary>
    /// Retires every snapshot so the next build is Fresh again. Without it the second build in a
    /// comparison runs in Preserve mode and reproduces the first by contract rather than by the
    /// property under test.
    /// </summary>
    private async Task SupersedeEverythingAsync(Guid accountId)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        await db.FeedItems
            .Where(i => i.AccountId == accountId)
            .ExecuteDeleteAsync();
        await db.ExposureEvents
            .Where(e => e.AccountId == accountId)
            .ExecuteDeleteAsync();
        await db.FeedSnapshots
            .Where(s => s.AccountId == accountId)
            .ExecuteDeleteAsync();
    }

    private static async Task<Guid> ActiveSnapshotIdAsync(Infrastructure.Persistence.ReelDbContext db, Guid accountId) =>
        await db.FeedSnapshots
            .Where(s => s.AccountId == accountId && s.Status == SnapshotStatus.Active)
            .OrderByDescending(s => s.GeneratedAt)
            .Select(s => s.Id)
            .FirstAsync();

    private async Task<Guid> HeroTitleAsync(Guid accountId, Guid snapshotId)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        return await db.FeedItems
            .Where(i => i.FeedSnapshotId == snapshotId && i.Row == FeedRowKind.Hero)
            .OrderBy(i => i.Rank)
            .Select(i => i.TitleId)
            .FirstAsync();
    }

    private async Task RunBuildAsync(Guid accountId, InterleaveOptions? options, string? triggerReason = null)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var featureBuilder = new FeatureVectorBuilder(db);
        var eligibility = new EligibilityQueryBuilder(db);
        var tmdb = new OfflineTmdbClient();
        Microsoft.Extensions.Caching.Memory.MemoryCache Cache() =>
            new(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions());

        var handler = new BuildFeedJobHandler(
            db,
            featureBuilder,
            new CandidateGenerator(db, tmdb, eligibility, NullLogger<CandidateGenerator>.Instance),
            new CandidateRetriever(db, eligibility),
            eligibility,
            new FeedStarvationDiagnostic(db, eligibility),
            new TitleHydrator(db, tmdb, NullLogger<TitleHydrator>.Instance),
            new FastTreeModelEngine(),
            new OnlineResidualModel(db, featureBuilder, new FastTreeModelEngine(), Cache(), NullLogger<OnlineResidualModel>.Instance),
            new RatingCalibrationService(db, Cache()),
            new DislikeSignal(db, Cache()),
            new UnavailableSearchAgent(),
            new TasteFactsBuilder(db, new DislikeSignal(db, Cache()), featureBuilder, Cache()),
            new PersonalisationContext(db, new DislikeSignal(db, Cache())),
            Cache(),
            new NoopEventHub(),
            new ExposureLog(db, NullLogger<ExposureLog>.Instance),
            NullLogger<BuildFeedJobHandler>.Instance,
            options);

        await handler.ExecuteAsync(
            new SyncJob
            {
                Id = Guid.NewGuid(),
                AccountId = accountId,
                Kind = JobKind.BuildFeed,
                EnqueuedAt = DateTime.UtcNow,
                TriggerReason = triggerReason,
            },
            CancellationToken.None);
    }

    private async Task<Guid> SeedAccountAsync()
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var account = new Account
        {
            Id = Guid.NewGuid(),
            TraktUserSlug = $"interleave-{Guid.NewGuid():N}",
            Tier = AccountTier.Founder,
            CreatedAt = DateTime.UtcNow,
            LastSeenAt = DateTime.UtcNow,
            PipelineStageChangedAt = DateTime.UtcNow,
        };
        db.Add(account);
        await db.SaveChangesAsync();
        return account.Id;
    }

    private async Task SeedLovedTitlesAsync(Guid accountId, int count)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        for (var i = 0; i < count; i++)
        {
            var title = NewTitle($"loved {i}", popularity: 1m + (i * 0.01m));
            db.Add(title);
            db.UserRatings.Add(new UserRating
            {
                Id = Guid.NewGuid(),
                AccountId = accountId,
                TitleId = title.Id,
                SubjectType = RatingSubjectType.Movie,
                Rating = (short)(8 + (i % 3)),
                RatedAt = DateTime.UtcNow.AddDays(-30 * i),
            });
        }

        await db.SaveChangesAsync();
    }

    /// <summary>
    /// An eligible pool whose popularity order is deliberately the REVERSE of its predicted order,
    /// so the two policies disagree about almost everything and the draft has real work to do. A
    /// pool where they agreed would pass every balance assertion while proving nothing.
    /// </summary>
    private async Task SeedEligibleTitlesAsync(int count)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        for (var i = 0; i < count; i++)
        {
            db.Add(NewTitle($"candidate {i}", popularity: 2m + ((count - i) * 0.5m)));
        }

        await db.SaveChangesAsync();
    }

    private static Title NewTitle(string name, decimal popularity) => new()
    {
        Id = Guid.NewGuid(),
        MediaType = MediaType.Movie,
        TmdbId = Random.Shared.NextInt64(1, long.MaxValue),
        TraktSlug = string.Empty,
        Name = $"{name} {Guid.NewGuid():N}",
        Language = "en",
        PosterPath = "/p.jpg",
        Genres = ["drama"],
        Year = 2015,
        RuntimeMinutes = 110,
        TraktRating = 7.2m,
        TmdbVoteAverage = 7.4m,
        TmdbPopularity = popularity,
        ReleasedAt = new DateTime(2015, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        LastMetadataRefreshAt = DateTime.UtcNow,
        CreatedAt = DateTime.UtcNow,
    };

    private async Task SeedActiveArtifactAsync(Guid accountId)
    {
        var rng = new Random(23);
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
            labels[i] = Math.Clamp(3f + (4f * v[0]), 1f, 8f);
        }

        var trained = new FastTreeModelEngine().Train(
            new TrainingMatrix(rows, labels, names),
            """{"Trees":40,"Leaves":8,"MinExamplesPerLeaf":5,"LearningRate":0.2}""");

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

    private sealed class UnavailableSearchAgent : ISearchAgent
    {
        public bool IsAvailable => false;

        public string ModelId => "stub";

        public Task<AgentReply> InterpretAsync(IReadOnlyList<ChatTurn> history, string message, string tasteSummary, IReadOnlyList<string> shownTitles, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<RerankResult>> RerankBatchAsync(IReadOnlyList<RerankInput> titles, string query, LensPersona persona, string? anchorName = null, string usagePurpose = ApiUsagePurpose.Rerank, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class NoopEventHub : IPipelineEventHub
    {
        public void Publish(Guid accountId, string type, IReadOnlyDictionary<string, object?> data) { }

        public async IAsyncEnumerable<PipelineSseEvent> SubscribeAsync(
            Guid accountId, long? lastEventId, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield break;
        }
    }
}
