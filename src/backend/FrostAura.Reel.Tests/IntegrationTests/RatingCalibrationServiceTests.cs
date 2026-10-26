using FrostAura.Reel.Application.Configuration;
using FrostAura.Reel.Application.Ml;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Library;
using FrostAura.Reel.Domain.Ml;
using FrostAura.Reel.Domain.Sync;
using FrostAura.Reel.Domain.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;

namespace FrostAura.Reel.Tests.IntegrationTests;

/// <summary>
/// The closed-loop query behind the calibrator, on real Postgres. The arithmetic is unit-tested in
/// <see cref="Ml.RatingCalibratorTests"/>; what can only be checked here is WHICH pairs the service
/// decides exist — and that question has three ways to go quietly wrong:
///
/// <list type="bullet">
/// <item>Pairing a rating with a score written AFTER it. That score has already seen the rating
/// through the feature vector, so the calibrator would be fit on its own output and would look far
/// better than it is.</item>
/// <item>Missing the managed-list watch conversions — on the founder's database that is the 73-row
/// set with the sharpest signal in the product (predicted 6.81, actual 5.78).</item>
/// <item>Counting a title twice because both sources return it, which double-weights it in the
/// isotonic pooling.</item>
/// <item>Losing the artifact each prediction came from, or resolving the wrong artifact as Active.
/// A prediction only means something relative to the model that made it, so without that column the
/// fit silently pools several models' levels and reads the difference between them as curvature.</item>
/// </list>
///
/// Skips when REEL_TEST_CONNECTION_STRING is unset, matching the other integration tests.
/// </summary>
[Collection("postgres")]
public class RatingCalibrationServiceTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Only_scores_that_predate_their_rating_become_pairs()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        var ratedAt = DateTime.UtcNow.AddDays(-5);

        var counted = await SeedTitleAsync();
        await SeedScoreAsync(accountId, counted, 8.4m, ratedAt.AddDays(-1)); // before → a pair
        await SeedRatingAsync(accountId, counted, 6, ratedAt);

        var leaked = await SeedTitleAsync();
        await SeedScoreAsync(accountId, leaked, 9.1m, ratedAt.AddDays(1)); // after → NOT a pair
        await SeedRatingAsync(accountId, leaked, 5, ratedAt);

        var unscored = await SeedTitleAsync();
        await SeedRatingAsync(accountId, unscored, 7, ratedAt); // never scored → NOT a pair

        var pairs = await ServiceFor(accountId).LoadPairsAsync(accountId, CancellationToken.None);

        var pair = Assert.Single(pairs);
        Assert.Equal(counted, pair.TitleId);
        Assert.Equal(8.4d, pair.Predicted, 4);
        Assert.Equal(6d, pair.Actual, 4);
    }

    [Fact]
    public async Task The_newest_score_before_the_rating_wins()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        var titleId = await SeedTitleAsync();
        var ratedAt = DateTime.UtcNow.AddDays(-2);

        // Two artifacts scored this title before the rating — the product committed to the newer
        // number, so that is the one it must be held to.
        await SeedScoreAsync(accountId, titleId, 5.0m, ratedAt.AddDays(-10));
        await SeedScoreAsync(accountId, titleId, 7.5m, ratedAt.AddDays(-1));
        await SeedRatingAsync(accountId, titleId, 6, ratedAt);

        var pairs = await ServiceFor(accountId).LoadPairsAsync(accountId, CancellationToken.None);

        Assert.Equal(7.5d, Assert.Single(pairs).Predicted, 4);
    }

    [Fact]
    public async Task Watch_conversions_contribute_pairs_and_are_de_duplicated_by_title()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        var removedAt = DateTime.UtcNow.AddDays(-3);

        // Saved via Reel, later watched, later rated — but the rating carries no score before it
        // (the score predates the LIST REMOVAL, not the rating). Only the conversion arm can find it.
        var conversionOnly = await SeedTitleAsync();
        await SeedScoreAsync(accountId, conversionOnly, 7.8m, removedAt.AddDays(-1));
        await SeedRatingAsync(accountId, conversionOnly, 4, removedAt.AddDays(-2));
        await SeedConversionAsync(accountId, conversionOnly, removedAt);

        // Reachable from BOTH arms — must appear exactly once, on the rating arm's pairing.
        var both = await SeedTitleAsync();
        await SeedScoreAsync(accountId, both, 8.0m, removedAt.AddDays(-5));
        await SeedRatingAsync(accountId, both, 5, removedAt.AddDays(-1));
        await SeedConversionAsync(accountId, both, removedAt);

        // Removed because the USER took it off the list, not because they watched it — not a
        // conversion, and with no score before its rating it must contribute nothing.
        var userRemoved = await SeedTitleAsync();
        await SeedScoreAsync(accountId, userRemoved, 9.0m, removedAt.AddDays(-1));
        await SeedRatingAsync(accountId, userRemoved, 3, removedAt.AddDays(-2));
        await SeedConversionAsync(accountId, userRemoved, removedAt, ListRemovalReason.UserRemoved);

        var pairs = await ServiceFor(accountId).LoadPairsAsync(accountId, CancellationToken.None);

        Assert.Equal(2, pairs.Count);
        Assert.Equal(7.8d, pairs.Single(p => p.TitleId == conversionOnly).Predicted, 4);
        Assert.Equal(4d, pairs.Single(p => p.TitleId == conversionOnly).Actual, 4);
        Assert.Equal(8.0d, pairs.Single(p => p.TitleId == both).Predicted, 4);
        Assert.DoesNotContain(pairs, p => p.TitleId == userRemoved);
    }

    [Fact]
    public async Task The_fitted_map_pulls_an_over_predicting_account_down()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        var ratedAt = DateTime.UtcNow.AddDays(-1);

        // 60 pairs shaped like the founder's live miscalibration: the model reads roughly a point
        // and a half high, and worse at the top of its range.
        for (var i = 0; i < 60; i++)
        {
            var titleId = await SeedTitleAsync();
            var predicted = 4m + (i * 0.1m);
            var actual = (short)Math.Clamp(Math.Round(predicted - 1m - ((predicted - 4m) * 0.3m)), 1m, 10m);
            await SeedScoreAsync(accountId, titleId, predicted, ratedAt.AddDays(-1));
            await SeedRatingAsync(accountId, titleId, actual, ratedAt);
        }

        var service = ServiceFor(accountId);
        var pairs = await service.LoadPairsAsync(accountId, CancellationToken.None);
        var calibrator = await service.GetAsync(accountId, CancellationToken.None);

        Assert.Equal(60, pairs.Count);
        Assert.Equal(CalibrationKind.Isotonic, calibrator.Kind);

        var before = pairs.Average(p => Math.Abs(p.Predicted - p.Actual));
        var after = pairs.Average(p => Math.Abs(calibrator.Calibrate(p.Predicted) - p.Actual));
        Assert.True(after < before, $"the fitted map must reduce MAE ({after} vs {before}).");
        Assert.True(calibrator.Calibrate(9d) < 9d, "an over-predicting account's high scores must come down.");

        // Ordering is untouched — this changes the number on the card, not which card is on top.
        Assert.True(calibrator.Calibrate(8d) >= calibrator.Calibrate(6d));
    }

    [Fact]
    public async Task A_new_rating_is_reflected_once_the_cache_is_invalidated()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        var scoredAt = DateTime.UtcNow.AddDays(-10);
        var ratedAt = DateTime.UtcNow.AddDays(-9);
        for (var i = 0; i < 12; i++)
        {
            var titleId = await SeedTitleAsync();
            await SeedScoreAsync(accountId, titleId, 7m, scoredAt);
            await SeedRatingAsync(accountId, titleId, 7, ratedAt);
        }

        var cache = new MemoryCache(new MemoryCacheOptions());
        var service = ServiceFor(accountId, cache);
        var first = await service.GetAsync(accountId, CancellationToken.None);
        Assert.Equal(12, first.PairCount);

        // A thirteenth rating lands. Without the eviction the endpoint performs, the cached map is
        // stale for up to ten minutes — which is exactly the "sync is instant and nothing changes"
        // complaint this whole phase exists to answer.
        var fresh = await SeedTitleAsync();
        await SeedScoreAsync(accountId, fresh, 7m, scoredAt);
        await SeedRatingAsync(accountId, fresh, 2, ratedAt);

        Assert.Equal(12, (await service.GetAsync(accountId, CancellationToken.None)).PairCount);
        service.Invalidate(accountId);
        Assert.Equal(13, (await service.GetAsync(accountId, CancellationToken.None)).PairCount);
    }

    [Fact]
    public async Task The_map_is_pinned_to_its_artifact_until_a_new_one_is_trained()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        var v1 = Guid.NewGuid();
        await SeedArtifactAsync(accountId, v1, ArtifactStatus.Active, version: 1);
        var scoredAt = DateTime.UtcNow.AddDays(-10);
        var ratedAt = DateTime.UtcNow.AddDays(-9);
        for (var i = 0; i < 12; i++)
        {
            var titleId = await SeedTitleAsync();
            await SeedScoreAsync(accountId, titleId, 7m, scoredAt, v1);
            await SeedRatingAsync(accountId, titleId, 6, ratedAt);
        }

        var pinned = await ServiceFor(accountId).GetAsync(accountId, CancellationToken.None);
        Assert.Equal(12, pinned.PairCount);

        // A thirteenth rating lands far from the prediction. A fresh process (new cache) still serves
        // the map pinned to v1 — the same artifact shows the same numbers — while the live fit sees it.
        var fresh = await SeedTitleAsync();
        await SeedScoreAsync(accountId, fresh, 7m, scoredAt, v1);
        await SeedRatingAsync(accountId, fresh, 1, ratedAt);

        var afterRestart = ServiceFor(accountId);
        var reread = await afterRestart.GetAsync(accountId, CancellationToken.None);
        Assert.Equal(12, reread.PairCount);
        Assert.Equal(pinned.Calibrate(7d), reread.Calibrate(7d));
        Assert.Equal(13, (await afterRestart.FitAsync(accountId, CancellationToken.None)).PairCount);

        await using (var db = fixture.CreateContext(new AccountContext()))
        {
            var stored = await db.ModelArtifacts.IgnoreQueryFilters().SingleAsync(a => a.Id == v1);
            Assert.NotNull(stored.CalibratorJson);
            Assert.NotNull(stored.CalibratorPinnedAt);
            await db.ModelArtifacts.IgnoreQueryFilters().Where(a => a.Id == v1)
                .ExecuteUpdateAsync(u => u.SetProperty(a => a.Status, ArtifactStatus.Superseded));
        }

        // A retrain mints v2, and v2 gets its own fit over everything known by then.
        await SeedArtifactAsync(accountId, Guid.NewGuid(), ArtifactStatus.Active, version: 2);
        Assert.Equal(13, (await ServiceFor(accountId).GetAsync(accountId, CancellationToken.None)).PairCount);
    }

    [Fact]
    public async Task Every_pair_carries_the_artifact_that_made_the_prediction()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        var artifactId = Guid.NewGuid();
        var ratedAt = DateTime.UtcNow.AddDays(-2);

        var titleId = await SeedTitleAsync();
        await SeedScoreAsync(accountId, titleId, 8.2m, ratedAt.AddDays(-1), artifactId);
        await SeedRatingAsync(accountId, titleId, 6, ratedAt);

        var pairs = await ServiceFor(accountId).LoadPairsAsync(accountId, CancellationToken.None);

        Assert.Equal(artifactId, Assert.Single(pairs).ArtifactId);
    }

    [Fact]
    public async Task Only_the_active_artifacts_pairs_are_fitted_once_it_has_enough_of_them()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        var active = Guid.NewGuid();
        var retired = Guid.NewGuid();
        await SeedArtifactAsync(accountId, retired, ArtifactStatus.Superseded, version: 1);
        await SeedArtifactAsync(accountId, active, ArtifactStatus.Active, version: 2);

        var ratedAt = DateTime.UtcNow.AddDays(-1);

        // 35 pairs from the model in service: it reads exactly one point high.
        for (var i = 0; i < 35; i++)
        {
            var titleId = await SeedTitleAsync();
            var predicted = 5m + (i * 0.1m);
            await SeedScoreAsync(accountId, titleId, predicted, ratedAt.AddDays(-1), active);
            await SeedRatingAsync(accountId, titleId, (short)Math.Clamp(Math.Round(predicted - 1m), 1m, 10m), ratedAt);
        }

        // 40 from a retired model that sat three points high. Real predictions, but of a model
        // nobody is being shown any more — including them would drag the map toward a level that no
        // longer exists.
        for (var i = 0; i < 40; i++)
        {
            var titleId = await SeedTitleAsync();
            var predicted = 6m + (i * 0.1m);
            await SeedScoreAsync(accountId, titleId, predicted, ratedAt.AddDays(-1), retired);
            await SeedRatingAsync(accountId, titleId, (short)Math.Clamp(Math.Round(predicted - 3m), 1m, 10m), ratedAt);
        }

        var service = ServiceFor(accountId);
        Assert.Equal(active, await service.ActiveArtifactIdAsync(accountId, CancellationToken.None));
        Assert.Equal(75, (await service.LoadPairsAsync(accountId, CancellationToken.None)).Count);

        var calibrator = await service.GetAsync(accountId, CancellationToken.None);

        Assert.Equal(35, calibrator.PairCount);
        Assert.True(Math.Abs(calibrator.Calibrate(7d) - 6d) < 0.4d,
            $"the map must describe the ACTIVE model's one-point bias, not the pool's ({calibrator.Calibrate(7d)}).");
    }

    [Fact]
    public async Task Two_artifacts_at_different_levels_are_de_meaned_before_they_are_pooled()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        var active = Guid.NewGuid();
        var retired = Guid.NewGuid();
        await SeedArtifactAsync(accountId, retired, ArtifactStatus.Superseded, version: 1);
        await SeedArtifactAsync(accountId, active, ArtifactStatus.Active, version: 2);

        var ratedAt = DateTime.UtcNow.AddDays(-1);

        // Neither artifact reaches 30 pairs on its own, so both are kept — but the retired one sat
        // two points higher for the same taste. De-meaned, they describe one relationship; pooled
        // raw, the overlap contradicts itself and the map flattens.
        foreach (var (artifactId, offset, count) in new[] { (active, 0m, 15), (retired, 2m, 15) })
        {
            for (var i = 0; i < count; i++)
            {
                var titleId = await SeedTitleAsync();
                var level = 6m + (i * 0.2m);
                await SeedScoreAsync(accountId, titleId, level + offset, ratedAt.AddDays(-1), artifactId);
                await SeedRatingAsync(accountId, titleId, (short)Math.Round(level - 1m), ratedAt);
            }
        }

        var calibrator = await ServiceFor(accountId).GetAsync(accountId, CancellationToken.None);

        Assert.Equal(30, calibrator.PairCount);
        Assert.True(Math.Abs(calibrator.Calibrate(7d) - 6d) < 0.6d,
            $"both artifacts describe y = x − 1 on the active level ({calibrator.Calibrate(7d)}).");
        Assert.True(calibrator.Calibrate(8d) > calibrator.Calibrate(6d), "the levelled fit must still differentiate.");
    }

    [Fact]
    public async Task CALIBRATOR_MODE_pins_the_selection_rule()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        var ratedAt = DateTime.UtcNow.AddDays(-1);
        for (var i = 0; i < 40; i++)
        {
            var titleId = await SeedTitleAsync();
            var predicted = 4m + (i * 0.15m);
            await SeedScoreAsync(accountId, titleId, predicted, ratedAt.AddDays(-1));
            await SeedRatingAsync(accountId, titleId, (short)Math.Clamp(Math.Round(predicted - 1m), 1m, 10m), ratedAt);
        }

        Assert.Equal(CalibratorMode.Auto, ServiceFor(accountId).Mode);

        var off = await ServiceFor(accountId, mode: "off").GetAsync(accountId, CancellationToken.None);
        Assert.Equal(CalibrationKind.Identity, off.Kind);
        Assert.Equal(8d, off.Calibrate(8d), 6);

        var shift = await ServiceFor(accountId, mode: "SHIFT").GetAsync(accountId, CancellationToken.None);
        Assert.Equal(CalibrationKind.BiasShift, shift.Kind);

        // Nonsense degrades to the documented default rather than silently disabling calibration.
        Assert.Equal(CalibratorMode.Auto, ServiceFor(accountId, mode: "sideways").Mode);
    }

    private RatingCalibrationService ServiceFor(Guid accountId, IMemoryCache? cache = null, string? mode = null)
    {
        var scoped = new AccountContext();
        scoped.SetAccount(accountId);
        return new RatingCalibrationService(
            fixture.CreateContext(scoped),
            cache ?? new MemoryCache(new MemoryCacheOptions()),
            mode is null
                ? null
                : new ConfigurationBuilder()
                    .AddInMemoryCollection(new Dictionary<string, string?> { [FeatureFlags.CalibratorMode] = mode })
                    .Build());
    }

    private async Task<Guid> SeedAccountAsync()
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var account = new Account
        {
            Id = Guid.NewGuid(),
            TraktUserSlug = $"calib-{Guid.NewGuid():N}",
            CreatedAt = DateTime.UtcNow,
            LastSeenAt = DateTime.UtcNow,
            PipelineStageChangedAt = DateTime.UtcNow,
        };
        db.Add(account);
        await db.SaveChangesAsync();
        return account.Id;
    }

    /// <summary>
    /// A catalogue row this fixture can hang a score and a rating off, and NOTHING ELSE. The pair
    /// query touches only ids, so the title needs no metadata — and these tests seed dozens of rows
    /// into a catalogue every other integration test in the "postgres" collection shares. Given a
    /// poster, a TMDB id and a popularity, they were eligible enough to crowd real expectations out
    /// of the search suites' result sets. No poster and no TMDB id keeps them off every suggestion
    /// and search lane by construction, which is what a fixture that is not about the catalogue
    /// should be.
    /// </summary>
    private async Task<Guid> SeedTitleAsync()
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var title = new Title
        {
            Id = Guid.NewGuid(),
            MediaType = MediaType.Movie,
            TraktId = Random.Shared.NextInt64(1, long.MaxValue),
            TraktSlug = string.Empty,
            Name = $"Calibration Fixture {Guid.NewGuid():N}",
            Genres = [],
            CreatedAt = DateTime.UtcNow,
        };
        db.Add(title);
        await db.SaveChangesAsync();
        return title.Id;
    }

    private async Task SeedScoreAsync(
        Guid accountId, Guid titleId, decimal predicted, DateTime scoredAt, Guid? artifactId = null)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        db.TitleScores.Add(new TitleScore
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            TitleId = titleId,
            // A distinct artifact id per score row unless the test says otherwise: the QUERY
            // deliberately does not filter on the Active artifact, because a prediction the product
            // served under an older model is still a prediction it made. Deciding which of those
            // pairs are comparable is the calibrator's job, not the query's.
            ModelArtifactId = artifactId ?? Guid.NewGuid(),
            PredictedRating = predicted,
            ScoredAt = scoredAt,
        });
        await db.SaveChangesAsync();
    }

    private async Task SeedArtifactAsync(Guid accountId, Guid artifactId, ArtifactStatus status, int version)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        db.ModelArtifacts.Add(new ModelArtifact
        {
            Id = artifactId,
            AccountId = accountId,
            Version = version,
            Status = status,
            // The schema version must be current or the global query filter hides the artifact and
            // ActiveArtifactIdAsync reports null — the deliberate "no model yet" degradation, and a
            // vacuously green test if it is seeded stale.
            FeatureSchemaJson = System.Text.Json.JsonSerializer.Serialize(
                new { version = FeatureSchema.Version, names = FeatureSchema.AllNames() }),
            TrainingRunId = Guid.NewGuid(),
            TrainedAt = DateTime.UtcNow.AddDays(-version),
        });
        await db.SaveChangesAsync();
    }

    private async Task SeedRatingAsync(Guid accountId, Guid titleId, short rating, DateTime ratedAt)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        db.UserRatings.Add(new UserRating
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            TitleId = titleId,
            SubjectType = RatingSubjectType.Movie,
            Rating = rating,
            RatedAt = ratedAt,
            SyncedAt = ratedAt,
        });
        await db.SaveChangesAsync();
    }

    private async Task SeedConversionAsync(
        Guid accountId, Guid titleId, DateTime removedAt, ListRemovalReason reason = ListRemovalReason.Watched)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        db.ManagedListItems.Add(new ManagedListItem
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            TitleId = titleId,
            AddedAt = removedAt.AddDays(-30),
            RemovedAt = removedAt,
            RemovalReason = reason,
        });
        await db.SaveChangesAsync();
    }
}
