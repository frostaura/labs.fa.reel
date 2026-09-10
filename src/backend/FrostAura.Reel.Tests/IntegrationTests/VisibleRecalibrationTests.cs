using FrostAura.Reel.Application.Configuration;
using FrostAura.Reel.Application.Ingestion;
using FrostAura.Reel.Application.Jobs;
using FrostAura.Reel.Application.Ml;
using FrostAura.Reel.Application.Ml.ClosedLoop;
using FrostAura.Reel.Application.Pipeline;
using FrostAura.Reel.Application.Ranking;
using FrostAura.Reel.Application.Search;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Feed;
using FrostAura.Reel.Domain.Library;
using FrostAura.Reel.Domain.Ml;
using FrostAura.Reel.Domain.Lens;
using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Domain.Ports.Tmdb;
using FrostAura.Reel.Domain.Sync;
using FrostAura.Reel.Domain.Tenancy;
using FrostAura.Reel.Infrastructure.Ml;
using FrostAura.Reel.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrostAura.Reel.Tests.IntegrationTests;

/// <summary>
/// Visible recalibration on real Postgres — the half of the founder's second observation ("the sync
/// happens instantly and nothing changes") that lives in the database rather than in the browser.
///
/// <para>Preserve-mode no-op suppression compares the assembled feed to the Active one on
/// <c>(Row, AnchorTitleId, Rank, TitleId)</c> and wrote NOTHING when they matched — so a build that
/// changed every prediction and no item threw the recalibration away one line before the screen.
/// Worse, the freeze was inconsistent: <c>TitleScores</c> are persisted before assembly, so Saved,
/// the live feed refill, the paginator and the digest all showed the fresh number while the hero
/// showed the stale one. The same title could read 6.9 in one place and 5.6 in another.</para>
///
/// <para>What these tests pin is the split: item identity still governs whether a SNAPSHOT is
/// minted (the stable-"tonight" contract is untouched — no new snapshot, no new
/// <c>GeneratedAt</c>, so the client's generatedAt-keyed rows never remount), while score drift
/// governs whether the existing rows are updated in place and announced.</para>
///
/// <para>Skips when REEL_TEST_CONNECTION_STRING is unset, matching the other integration tests.</para>
/// </summary>
[Collection("postgres")]
public class VisibleRecalibrationTests(PostgresFixture fixture)
{
    [Fact]
    public async Task A_preserve_build_updates_scores_in_place_without_minting_a_snapshot()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedBuildableAccountAsync();
        await RunBuildAsync(accountId);

        var (snapshotId, generatedAt, snapshotCount) = await ActiveSnapshotAsync(accountId);
        var before = await ItemsAsync(snapshotId);
        Assert.NotEmpty(before);

        // Stand in for "the model's opinion moved": push every persisted card two points below what
        // the model will produce on the next pass. The build itself is deterministic here (offline
        // TMDB, fixed catalogue, fixed artifact), so the second pass re-derives the ORIGINAL numbers
        // and the gap between them is exactly the drift the old code discarded.
        await ShiftPersistedRatingsAsync(snapshotId, -2.0m);

        var events = new RecordingEventHub();
        await RunBuildAsync(accountId, triggerReason: "delta-sync", events: events);

        var (afterSnapshotId, afterGeneratedAt, afterSnapshotCount) = await ActiveSnapshotAsync(accountId);

        // The contract the client depends on: same snapshot, same GeneratedAt, no extra snapshot in
        // history. Rows key on GeneratedAt, so a change here would remount the page.
        Assert.Equal(snapshotId, afterSnapshotId);
        Assert.Equal(generatedAt, afterGeneratedAt);
        Assert.Equal(snapshotCount, afterSnapshotCount);

        // ...and yet the numbers moved back to what the model actually says.
        var after = await ItemsAsync(afterSnapshotId);
        Assert.Equal(before.Count, after.Count);
        foreach (var (titleId, predicted) in before)
        {
            Assert.Equal(predicted, after[titleId]);
        }

        // The row ids are the ORIGINAL rows, updated — not a fresh set written under the same
        // snapshot id, which would break the "no new FeedItems" half of the contract just as badly.
        Assert.Equal(
            await ItemIdsAsync(snapshotId, before.Count),
            await ItemIdsAsync(afterSnapshotId, before.Count));

        var frame = Assert.Single(events.Frames, f => f.Type == PipelineEventTypes.FeedScoresUpdated);
        Assert.Equal(generatedAt, Assert.IsType<DateTime>(frame.Data["generatedAt"]));
        var changed = Assert.IsType<List<Dictionary<string, object?>>>(frame.Data["changed"]);
        Assert.Equal(before.Count, changed.Count);
        Assert.All(changed, c => Assert.Equal(before[(Guid)c["titleId"]!], (decimal)c["predictedRating"]!));

        // No feed-ready: that frame invalidates the Feed tag and remounts every row, which is the
        // exact churn the stable-tonight contract exists to prevent.
        Assert.DoesNotContain(events.Frames, f => f.Type == PipelineEventTypes.FeedReady);
    }

    [Fact]
    public async Task A_build_where_nothing_moved_stays_the_true_no_op_it_always_was()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedBuildableAccountAsync();
        await RunBuildAsync(accountId);

        var (snapshotId, generatedAt, snapshotCount) = await ActiveSnapshotAsync(accountId);
        var before = await ItemsAsync(snapshotId);

        var events = new RecordingEventHub();
        await RunBuildAsync(accountId, triggerReason: "delta-sync", events: events);

        var (afterSnapshotId, afterGeneratedAt, afterSnapshotCount) = await ActiveSnapshotAsync(accountId);
        Assert.Equal(snapshotId, afterSnapshotId);
        Assert.Equal(generatedAt, afterGeneratedAt);
        Assert.Equal(snapshotCount, afterSnapshotCount);
        Assert.Equal(before, await ItemsAsync(afterSnapshotId));

        // The frame is published only when a DISPLAYED rating moved. Announcing an unchanged page
        // would train the user to ignore the one signal that means "your model learned something".
        Assert.DoesNotContain(events.Frames, f => f.Type == PipelineEventTypes.FeedScoresUpdated);
        Assert.DoesNotContain(events.Frames, f => f.Type == PipelineEventTypes.FeedReady);
    }

    [Fact]
    public async Task A_score_only_update_still_records_its_no_op_build_stat()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedBuildableAccountAsync();
        await RunBuildAsync(accountId);
        var (snapshotId, _, _) = await ActiveSnapshotAsync(accountId);
        await ShiftPersistedRatingsAsync(snapshotId, -2.0m);

        await RunBuildAsync(accountId, triggerReason: "delta-sync");

        await using var db = fixture.CreateContext(new AccountContext());
        var stats = await db.FeedBuildStats
            .Where(s => s.AccountId == accountId)
            .OrderBy(s => s.BuiltAt)
            .ToListAsync();

        Assert.Equal(2, stats.Count);
        // Still a no-op as far as the PAGE is concerned — the instrument must not start reporting
        // Preserve builds where none happened just because some numbers were rewritten.
        Assert.Equal(FeedBuildOutcome.NoOp, stats[^1].Mode);
        Assert.Null(stats[^1].FeedSnapshotId);
    }

    [Fact]
    public async Task A_calibration_breach_past_the_one_day_floor_is_owed_a_retrain()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        var artifactId = Guid.NewGuid();
        await SeedArtifactAsync(accountId, artifactId, trainedAt: DateTime.UtcNow.AddDays(-3));
        // 30 pairs the CURRENT artifact produced, every one of them two points optimistic: bias
        // +2.0 and MAE 2.0 — comfortably past |bias| > 0.75 and at the minimum sample.
        await SeedCalibrationPairsAsync(accountId, artifactId, count: 30, predicted: 8m, actual: 6);

        Assert.Equal(
            RetrainTrigger.CalibrationBreach,
            await GateFor(accountId).EvaluateAsync(accountId, DateTime.UtcNow, CancellationToken.None));
    }

    [Fact]
    public async Task The_same_breach_inside_the_one_day_floor_is_not()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        var artifactId = Guid.NewGuid();
        await SeedArtifactAsync(accountId, artifactId, trainedAt: DateTime.UtcNow.AddHours(-6));
        await SeedCalibrationPairsAsync(accountId, artifactId, count: 30, predicted: 8m, actual: 6);

        // The breach reads the same pairs last night's train was fitted against. Without the floor a
        // genuinely hard account retrains every single night on numbers that have not moved.
        Assert.Null(await GateFor(accountId).EvaluateAsync(accountId, DateTime.UtcNow, CancellationToken.None));
    }

    [Fact]
    public async Task Too_few_pairs_is_noise_not_a_breach()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        var artifactId = Guid.NewGuid();
        await SeedArtifactAsync(accountId, artifactId, trainedAt: DateTime.UtcNow.AddDays(-3));
        await SeedCalibrationPairsAsync(accountId, artifactId, count: 29, predicted: 9m, actual: 2);

        Assert.Null(await GateFor(accountId).EvaluateAsync(accountId, DateTime.UtcNow, CancellationToken.None));
    }

    [Fact]
    public async Task Pairs_from_a_superseded_artifact_do_not_convict_the_current_one()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        var artifactId = Guid.NewGuid();
        await SeedArtifactAsync(accountId, artifactId, trainedAt: DateTime.UtcNow.AddDays(-3));
        // Wildly wrong — but scored by a model that has already been replaced. Counting these would
        // keep an account in breach long after the retrain that fixed it.
        await SeedCalibrationPairsAsync(accountId, Guid.NewGuid(), count: 40, predicted: 10m, actual: 1);

        Assert.Null(await GateFor(accountId).EvaluateAsync(accountId, DateTime.UtcNow, CancellationToken.None));
    }

    [Fact]
    public async Task The_established_new_ratings_rule_still_fires_inside_the_floor()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        var artifactId = Guid.NewGuid();
        var trainedAt = DateTime.UtcNow.AddHours(-2);
        await SeedArtifactAsync(accountId, artifactId, trainedAt);
        await SeedFreshRatingsAsync(accountId, count: RetrainPolicy.DefaultMinNewRatings, firstRatedAt: trainedAt.AddMinutes(5));

        // The floor belongs to the breach trigger alone. The nightly cadence must not be narrowed by
        // a rule that was added beside it.
        Assert.Equal(
            RetrainTrigger.NewRatings,
            await GateFor(accountId).EvaluateAsync(accountId, DateTime.UtcNow, CancellationToken.None));
    }

    [Fact]
    public async Task The_post_sync_outlook_promises_tonight_only_when_a_retrain_is_actually_owed()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        var artifactId = Guid.NewGuid();
        await SeedArtifactAsync(accountId, artifactId, trainedAt: DateTime.UtcNow.AddHours(-2));

        var now = DateTime.UtcNow;
        var quiet = await GateFor(accountId).OutlookAsync(accountId, now, CancellationToken.None);
        Assert.False(quiet.Queued);
        Assert.Null(quiet.At);

        await SeedFreshRatingsAsync(accountId, RetrainPolicy.DefaultMinNewRatings, DateTime.UtcNow.AddHours(-1));

        var due = await GateFor(accountId).OutlookAsync(accountId, now, CancellationToken.None);
        Assert.True(due.Queued);
        Assert.Equal(RetrainPolicy.NextNightlyRunUtc(now), due.At);
    }

    [Fact]
    public async Task A_train_already_in_flight_is_now_rather_than_tonight()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        await SeedArtifactAsync(accountId, Guid.NewGuid(), trainedAt: DateTime.UtcNow.AddHours(-2));

        await using (var db = fixture.CreateContext(new AccountContext()))
        {
            db.SyncJobs.Add(new SyncJob
            {
                Id = Guid.NewGuid(),
                AccountId = accountId,
                Kind = JobKind.Train,
                Status = JobStatus.Running,
                EnqueuedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var outlook = await GateFor(accountId).OutlookAsync(accountId, DateTime.UtcNow, CancellationToken.None);
        Assert.True(outlook.Queued);
        Assert.Null(outlook.At);
    }

    [Fact]
    public async Task The_learned_band_reports_what_the_latest_retrain_actually_bought()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        var previousTrainedAt = DateTime.UtcNow.AddDays(-2);
        var trainedAt = DateTime.UtcNow.AddHours(-1);
        await SeedArtifactAsync(accountId, Guid.NewGuid(), previousTrainedAt, ArtifactStatus.Superseded, version: 1);
        await SeedArtifactAsync(accountId, Guid.NewGuid(), trainedAt, ArtifactStatus.Active, version: 2);

        // 14 labels that landed between the two trains — what THIS artifact learned from.
        await SeedFreshRatingsAsync(accountId, 14, previousTrainedAt.AddHours(1));
        // ...and one that landed after it trained, which this artifact could not have seen.
        await SeedFreshRatingsAsync(accountId, 1, trainedAt.AddMinutes(5));

        await SeedEvaluationAsync(accountId, mae: 1.5m, bias: 0.4m, spearman: 0.22m, computedAt: previousTrainedAt);
        await SeedEvaluationAsync(accountId, mae: 1.1m, bias: 0.2m, spearman: 0.31m, computedAt: trainedAt);

        var shared = await SeedSnapshotPairAsync(accountId, sharedTitles: 4, changedTitles: 3);

        var learned = await new ModelLearnedQuery(fixture.CreateContext(ScopedTo(accountId)))
            .GetAsync(accountId, CancellationToken.None);

        Assert.NotNull(learned);
        Assert.Equal(trainedAt, learned!.Value.TrainedAt, TimeSpan.FromSeconds(1));
        Assert.Equal(14, learned.Value.NewRatingsSinceLast);
        Assert.Equal(1.1m, learned.Value.CalibrationMae);
        Assert.Equal(0.2m, learned.Value.CalibrationBias);
        Assert.Equal(0.31m, learned.Value.Spearman);
        // "runs 0.4 closer on your scores now" — previous MAE minus current, so positive is better.
        Assert.Equal(0.4m, learned.Value.MaeImprovement);
        Assert.Equal(3, learned.Value.PicksChanged);
        Assert.Equal(4, shared);
    }

    [Fact]
    public async Task Without_a_predecessor_artifact_there_is_no_band()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        await SeedArtifactAsync(accountId, Guid.NewGuid(), DateTime.UtcNow.AddHours(-1));
        await SeedFreshRatingsAsync(accountId, 40, DateTime.UtcNow.AddDays(-10));
        await SeedEvaluationAsync(accountId, mae: 1.1m, bias: 0.2m, spearman: 0.31m, computedAt: DateTime.UtcNow);

        // There is no "since last" to count against, and counting from zero would report the
        // founder's entire 2,000-rating history as "just learned" on the first band anyone sees.
        Assert.Null(await new ModelLearnedQuery(fixture.CreateContext(ScopedTo(accountId)))
            .GetAsync(accountId, CancellationToken.None));
    }

    [Fact]
    public async Task A_retrain_that_learned_nothing_new_shows_no_band()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        var previousTrainedAt = DateTime.UtcNow.AddDays(-2);
        var trainedAt = DateTime.UtcNow.AddHours(-1);
        await SeedArtifactAsync(accountId, Guid.NewGuid(), previousTrainedAt, ArtifactStatus.Superseded, version: 1);
        await SeedArtifactAsync(accountId, Guid.NewGuid(), trainedAt, ArtifactStatus.Active, version: 2);
        await SeedEvaluationAsync(accountId, mae: 1.1m, bias: 0.2m, spearman: 0.31m, computedAt: trainedAt);

        // A 14-day staleness retrain with no new labels is real and routine — "Learned from 0 new
        // ratings" is worse than saying nothing at all.
        Assert.Null(await new ModelLearnedQuery(fixture.CreateContext(ScopedTo(accountId)))
            .GetAsync(accountId, CancellationToken.None));
    }

    // ── Fixtures ────────────────────────────────────────────────────────────────────────────

    private static AccountContext ScopedTo(Guid accountId)
    {
        var scoped = new AccountContext();
        scoped.SetAccount(accountId);
        return scoped;
    }

    private async Task SeedEvaluationAsync(
        Guid accountId, decimal mae, decimal bias, decimal spearman, DateTime computedAt)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        db.EvaluationResults.Add(new EvaluationResult
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            TrainingRunId = Guid.NewGuid(),
            Mae = mae,
            Rmse = mae + 0.4m,
            CalibrationBias = bias,
            SpearmanRho = spearman,
            ComputedAt = computedAt,
        });
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Two snapshots — a superseded one and the Active one that replaced it — sharing
    /// <paramref name="sharedTitles"/> cards and differing by <paramref name="changedTitles"/>.
    /// Returns the shared count so the caller's expectation is stated twice, from both ends.
    /// </summary>
    private async Task<int> SeedSnapshotPairAsync(Guid accountId, int sharedTitles, int changedTitles)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var shared = new List<Guid>();
        for (var i = 0; i < sharedTitles; i++)
        {
            var title = NewTitle($"snapshot shared {i}", popularity: 1m);
            db.Add(title);
            shared.Add(title.Id);
        }

        var fresh = new List<Guid>();
        var stale = new List<Guid>();
        for (var i = 0; i < changedTitles; i++)
        {
            var newTitle = NewTitle($"snapshot new {i}", popularity: 1m);
            var oldTitle = NewTitle($"snapshot old {i}", popularity: 1m);
            db.Add(newTitle);
            db.Add(oldTitle);
            fresh.Add(newTitle.Id);
            stale.Add(oldTitle.Id);
        }

        var previous = AddSnapshot(db, accountId, DateTime.UtcNow.AddHours(-4), SnapshotStatus.Superseded, [.. shared, .. stale]);
        var current = AddSnapshot(db, accountId, DateTime.UtcNow.AddHours(-1), SnapshotStatus.Active, [.. shared, .. fresh]);
        Assert.NotEqual(previous, current);
        await db.SaveChangesAsync();
        return sharedTitles;
    }

    private static Guid AddSnapshot(
        ReelDbContext db, Guid accountId, DateTime generatedAt, SnapshotStatus status, IReadOnlyList<Guid> titleIds)
    {
        var snapshot = new FeedSnapshot
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            ModelArtifactId = Guid.NewGuid(),
            GeneratedAt = generatedAt,
            Status = status,
        };
        db.FeedSnapshots.Add(snapshot);
        for (var i = 0; i < titleIds.Count; i++)
        {
            db.FeedItems.Add(new FeedItem
            {
                Id = Guid.NewGuid(),
                AccountId = accountId,
                FeedSnapshotId = snapshot.Id,
                Row = FeedRowKind.Hero,
                Rank = i,
                TitleId = titleIds[i],
                PredictedRating = 7m,
                FinalScore = 7m,
                WhyThisSentence = "seeded",
            });
        }

        return snapshot.Id;
    }


    private RetrainGate GateFor(Guid accountId)
    {
        var scoped = new AccountContext();
        scoped.SetAccount(accountId);
        var db = fixture.CreateContext(scoped);
        return new RetrainGate(
            db,
            new RatingCalibrationService(db, new MemoryCache(new MemoryCacheOptions())),
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [FeatureFlags.RetrainMinNewRatings] = RetrainPolicy.DefaultMinNewRatings.ToString(),
                })
                .Build());
    }

    private async Task<(Guid Id, DateTime GeneratedAt, int Count)> ActiveSnapshotAsync(Guid accountId)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var active = await db.FeedSnapshots
            .Where(s => s.AccountId == accountId && s.Status == SnapshotStatus.Active)
            .SingleAsync();
        var count = await db.FeedSnapshots.CountAsync(s => s.AccountId == accountId);
        return (active.Id, active.GeneratedAt, count);
    }

    private async Task<Dictionary<Guid, decimal>> ItemsAsync(Guid snapshotId)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        return await db.FeedItems
            .Where(i => i.FeedSnapshotId == snapshotId)
            .ToDictionaryAsync(i => i.TitleId, i => i.PredictedRating);
    }

    private async Task<List<Guid>> ItemIdsAsync(Guid snapshotId, int expected)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var ids = await db.FeedItems
            .Where(i => i.FeedSnapshotId == snapshotId)
            .OrderBy(i => i.Id)
            .Select(i => i.Id)
            .ToListAsync();
        Assert.Equal(expected, ids.Count);
        return ids;
    }

    private async Task ShiftPersistedRatingsAsync(Guid snapshotId, decimal delta)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        await db.FeedItems
            .Where(i => i.FeedSnapshotId == snapshotId)
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.PredictedRating, i => i.PredictedRating + delta));
    }

    private async Task<Guid> SeedBuildableAccountAsync()
    {
        var accountId = await SeedAccountAsync();
        await SeedLovedTitlesAsync(accountId, 12);
        await SeedEligibleTitlesAsync(80);
        await SeedTrainedArtifactAsync(accountId);
        return accountId;
    }

    private async Task<Guid> SeedAccountAsync()
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var account = new Account
        {
            Id = Guid.NewGuid(),
            TraktUserSlug = $"recalib-{Guid.NewGuid():N}",
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
            var title = NewTitle($"recalib loved {i}", popularity: 1m + (i * 0.01m));
            db.Add(title);
            db.UserRatings.Add(new UserRating
            {
                Id = Guid.NewGuid(),
                AccountId = accountId,
                TitleId = title.Id,
                SubjectType = RatingSubjectType.Movie,
                Rating = (short)(8 + (i % 3)),
                RatedAt = DateTime.UtcNow.AddDays(-30 * i),
                FirstRatedAt = DateTime.UtcNow.AddDays(-30 * i),
            });
        }

        await db.SaveChangesAsync();
    }

    private async Task SeedEligibleTitlesAsync(int count)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        for (var i = 0; i < count; i++)
        {
            db.Add(NewTitle($"recalib candidate {i}", popularity: 2m + (i * 0.001m)));
        }

        await db.SaveChangesAsync();
    }

    /// <inheritdoc cref="BuildFeedNeverEmptyTests"/>
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

    /// <summary>A real FastTree over the running schema — the build refuses to run without one.</summary>
    private async Task SeedTrainedArtifactAsync(Guid accountId)
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

    /// <summary>An artifact with no bytes — the gate and the band read only id, status and TrainedAt.</summary>
    private async Task SeedArtifactAsync(
        Guid accountId, Guid artifactId, DateTime trainedAt,
        ArtifactStatus status = ArtifactStatus.Active, int version = 1)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        db.ModelArtifacts.Add(new ModelArtifact
        {
            Id = artifactId,
            AccountId = accountId,
            Version = version,
            Algo = "FastTree",
            // Current schema version, or the global query filter hides the artifact and the gate
            // reports "no model yet" — a vacuously green test.
            FeatureSchemaJson = System.Text.Json.JsonSerializer.Serialize(
                new { version = FeatureSchema.Version, names = FeatureSchema.AllNames() }),
            TrainingRunId = Guid.NewGuid(),
            Status = status,
            TrainedAt = trainedAt,
        });
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Closed-loop pairs: a score written strictly BEFORE its rating (the leakage guard the pair
    /// query enforces), attributed to <paramref name="artifactId"/>.
    /// </summary>
    private async Task SeedCalibrationPairsAsync(
        Guid accountId, Guid artifactId, int count, decimal predicted, short actual)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var ratedAt = DateTime.UtcNow.AddHours(-1);
        for (var i = 0; i < count; i++)
        {
            var title = new Title
            {
                Id = Guid.NewGuid(),
                MediaType = MediaType.Movie,
                TraktId = Random.Shared.NextInt64(1, long.MaxValue),
                TraktSlug = string.Empty,
                Name = $"Recalibration pair {Guid.NewGuid():N}",
                Genres = [],
                CreatedAt = DateTime.UtcNow,
            };
            db.Add(title);
            db.TitleScores.Add(new TitleScore
            {
                Id = Guid.NewGuid(),
                AccountId = accountId,
                TitleId = title.Id,
                ModelArtifactId = artifactId,
                PredictedRating = predicted,
                ScoredAt = ratedAt.AddMinutes(-10),
            });
            db.UserRatings.Add(new UserRating
            {
                Id = Guid.NewGuid(),
                AccountId = accountId,
                TitleId = title.Id,
                SubjectType = RatingSubjectType.Movie,
                Rating = actual,
                RatedAt = ratedAt,
                // Older than the artifact deliberately: these pairs must convict on CALIBRATION,
                // not trip the ≥N-new-ratings rule and pass the test for the wrong reason.
                FirstRatedAt = DateTime.UtcNow.AddDays(-30),
            });
        }

        await db.SaveChangesAsync();
    }

    private async Task SeedFreshRatingsAsync(Guid accountId, int count, DateTime firstRatedAt)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        for (var i = 0; i < count; i++)
        {
            var title = new Title
            {
                Id = Guid.NewGuid(),
                MediaType = MediaType.Movie,
                TraktId = Random.Shared.NextInt64(1, long.MaxValue),
                TraktSlug = string.Empty,
                Name = $"Recalibration fresh {Guid.NewGuid():N}",
                Genres = [],
                CreatedAt = DateTime.UtcNow,
            };
            db.Add(title);
            db.UserRatings.Add(new UserRating
            {
                Id = Guid.NewGuid(),
                AccountId = accountId,
                TitleId = title.Id,
                SubjectType = RatingSubjectType.Movie,
                Rating = 7,
                RatedAt = firstRatedAt,
                FirstRatedAt = firstRatedAt,
            });
        }

        await db.SaveChangesAsync();
    }

    private async Task RunBuildAsync(Guid accountId, string? triggerReason = null, IPipelineEventHub? events = null)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var featureBuilder = new FeatureVectorBuilder(db);
        var eligibility = new EligibilityQueryBuilder(db);
        var tmdb = new OfflineTmdbClient();
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
                db, featureBuilder, new FastTreeModelEngine(),
                new MemoryCache(new MemoryCacheOptions()), NullLogger<OnlineResidualModel>.Instance),
            new RatingCalibrationService(db, new MemoryCache(new MemoryCacheOptions())),
            new DislikeSignal(db, new MemoryCache(new MemoryCacheOptions())),
            new UnavailableSearchAgent(),
            new TasteFactsBuilder(
                db,
                new DislikeSignal(db, new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions())),
                featureBuilder,
                new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions())),
            new PersonalisationContext(db, new DislikeSignal(db, new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions()))),
            new MemoryCache(new MemoryCacheOptions()),
            events ?? new RecordingEventHub(),
            new ExposureLog(db, NullLogger<ExposureLog>.Instance),
            NullLogger<BuildFeedJobHandler>.Instance);

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

    /// <summary>Captures every published frame so the SSE contract can be asserted, not assumed.</summary>
    private sealed class RecordingEventHub : IPipelineEventHub
    {
        public List<(string Type, IReadOnlyDictionary<string, object?> Data)> Frames { get; } = [];

        public void Publish(Guid accountId, string type, IReadOnlyDictionary<string, object?> data) =>
            Frames.Add((type, data));

        public async IAsyncEnumerable<PipelineSseEvent> SubscribeAsync(
            Guid accountId, long? lastEventId,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    /// <summary>TMDB port that yields nothing, so the build is deterministic over the seeded catalogue.</summary>
    private sealed class OfflineTmdbClient : ITmdbClient
    {
        public Task<TmdbTitleDetails?> GetMovieAsync(long tmdbId, CancellationToken ct = default) => Task.FromResult<TmdbTitleDetails?>(null);

        public Task<TmdbTitleDetails?> GetTvAsync(long tmdbId, CancellationToken ct = default) => Task.FromResult<TmdbTitleDetails?>(null);

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

    /// <summary>Never available, so every why stays the deterministic templater sentence.</summary>
    private sealed class UnavailableSearchAgent : ISearchAgent
    {
        public bool IsAvailable => false;

        public string ModelId => "stub";

        public Task<AgentReply> InterpretAsync(IReadOnlyList<ChatTurn> history, string message, string tasteSummary, IReadOnlyList<string> shownTitles, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<RerankResult>> RerankBatchAsync(IReadOnlyList<RerankInput> titles, string query, LensPersona persona, string? anchorName = null, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
