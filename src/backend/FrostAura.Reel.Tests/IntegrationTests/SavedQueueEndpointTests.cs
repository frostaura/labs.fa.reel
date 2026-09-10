using System.Text.Json;
using FrostAura.Reel.Api.Endpoints;
using FrostAura.Reel.Application.Jobs;
using FrostAura.Reel.Application.Ml;
using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Application.Ranking;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Library;
using FrostAura.Reel.Domain.Ml;
using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Domain.Providers;
using FrostAura.Reel.Domain.Sync;
using FrostAura.Reel.Domain.Tenancy;
using FrostAura.Reel.Infrastructure.Ml;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrostAura.Reel.Tests.IntegrationTests;

/// <summary>
/// The Saved queue on real Postgres. Two things are proved here that no unit test can:
///
/// <list type="bullet">
/// <item><b>The on-demand backfill actually closes the null-prediction gap.</b> Saved titles are
/// excluded from eligibility, so they are never rescored by a feed build; after a retrain, a save
/// made before it has no score under the Active artifact and used to render with no number at all.
/// On the founder's database that was the MODAL state, not an edge case. This asserts that a queue
/// larger than one request's budget still reaches zero nulls — and that it does so because scores
/// were PERSISTED, not because the second request re-did the first one's work.</item>
/// <item><b>The loop-closing prompt never fires on a title the user has already rated.</b> The
/// guard is the entire design: the same delta sync that detects a watch ingests the Trakt rating,
/// so on the founder's history all 73 conversions were already rated before Reel could ask.</item>
/// </list>
/// </summary>
[Collection("postgres")]
public class SavedQueueEndpointTests(PostgresFixture fixture)
{
    private static readonly DateTime Now = new(2026, 9, 9, 20, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task On_demand_backfill_respects_its_budget_persists_scores_and_converges_to_zero_nulls()
    {
        if (!fixture.Available)
        {
            return; // no Postgres available — covered in CI's backend-integration job
        }

        var accountId = await SeedAccountAsync();
        var titleIds = new List<Guid>();
        for (var i = 0; i < 40; i++)
        {
            titleIds.Add(await SeedTitleAsync($"saved-{i}", runtimeMinutes: 90 + i));
        }

        // Five already carry a score under the Active artifact — the state a fresh retrain leaves
        // behind: a handful of saves that happened to be in the last shortlist, and 35 that were not.
        var artifactId = await SeedActiveArtifactAsync(accountId);
        await SeedScoresAsync(accountId, artifactId, titleIds.Take(5).ToDictionary(id => id, _ => 7.0m), Now.AddDays(-1));
        await SaveAllAsync(accountId, titleIds);

        var first = await QueueAsync(accountId);

        // 35 needed a score; the budget bought 30 of them, so exactly 5 are still missing and the
        // response SAYS 5 rather than quietly rendering blanks.
        Assert.Equal(40, first.Items.Count);
        Assert.Equal(5, first.UnscoredRemaining);
        Assert.Equal(5, first.Items.Count(i => i.PredictedRating is null));

        // The budget bought real, persisted rows — this is what makes the convergence below cheap
        // rather than the same 30 titles being rescored on every page view forever.
        await using (var verify = fixture.CreateContext(Pinned(accountId)))
        {
            Assert.Equal(35, await verify.TitleScores.CountAsync(
                s => s.AccountId == accountId && s.ModelArtifactId == artifactId));
        }

        var second = await QueueAsync(accountId);

        Assert.Equal(0, second.UnscoredRemaining);
        Assert.All(second.Items, i => Assert.NotNull(i.PredictedRating));
        // Predictions are served at 1dp, on the account's own 0-10 scale.
        Assert.All(second.Items, i => Assert.InRange(i.PredictedRating!.Value, 0m, 10m));
        Assert.All(second.Items, i => Assert.Equal(Math.Round(i.PredictedRating!.Value, 1), i.PredictedRating!.Value));

        await using var final = fixture.CreateContext(Pinned(accountId));
        Assert.Equal(40, await final.TitleScores.CountAsync(
            s => s.AccountId == accountId && s.ModelArtifactId == artifactId));
    }

    [Fact]
    public async Task Tonight_pins_the_best_available_title_inside_the_evening_budget()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync(eveningBudgetMinutes: 120);
        var artifactId = await SeedActiveArtifactAsync(accountId);
        var providerId = await SeedProviderAsync("Netflix");

        // pinned      — 8.5, streams, 100 min → the only honest answer to "what do I put on"
        // tooLong     — 9.5, streams, 175 min → better, does not fit the 2h budget
        // notStreaming— 9.9, 95 min, nowhere  → the best card in the queue and unwatchable tonight
        var pinned = await SeedTitleAsync("pinned", runtimeMinutes: 100);
        var tooLong = await SeedTitleAsync("too-long", runtimeMinutes: 175);
        var notStreaming = await SeedTitleAsync("not-streaming", runtimeMinutes: 95);
        await SeedAvailabilityAsync(pinned, providerId, "US", Now.AddDays(-1));
        await SeedAvailabilityAsync(tooLong, providerId, "US", Now.AddDays(-1));
        await SeedScoresAsync(accountId, artifactId, new()
        {
            [pinned] = 8.5m,
            [tooLong] = 9.5m,
            [notStreaming] = 9.9m,
        }, Now.AddDays(-1));
        await SaveAllAsync(accountId, [pinned, tooLong, notStreaming]);

        var tonight = await QueueAsync(accountId);

        Assert.Equal("tonight", tonight.Sort);
        Assert.Equal(120, tonight.EveningBudgetMinutes);
        var pin = Assert.Single(tonight.Items, i => i.WatchTonight);
        Assert.Equal(pinned, pin.TitleId);
        Assert.Contains("1h 40m against your 2h", pin.WatchTonightReason!, StringComparison.Ordinal);
        Assert.Contains("on Netflix", pin.WatchTonightReason!, StringComparison.Ordinal);
        // Availability leads the order; the unwatchable 9.9 is last despite being the best number.
        Assert.Equal([tooLong, pinned, notStreaming], tonight.Items.Select(i => i.TitleId).ToArray());
        Assert.True(tonight.Items.Single(i => i.TitleId == pinned).AvailableNow);
        Assert.False(tonight.Items.Single(i => i.TitleId == notStreaming).AvailableNow);
        Assert.False(tonight.Items.Single(i => i.TitleId == tooLong).FitsEveningBudget);
        Assert.Equal("Netflix", tonight.Items.Single(i => i.TitleId == pinned).Providers.Single().Name);

        // A different sort re-orders the SAME set and keeps the pin: the pinned pick is a property
        // of the queue, not of the ordering the user happens to be looking at.
        var byPredicted = await QueueAsync(accountId, sort: "predicted");
        Assert.Equal("predicted", byPredicted.Sort);
        Assert.Equal([notStreaming, tooLong, pinned], byPredicted.Items.Select(i => i.TitleId).ToArray());
        Assert.Equal(pinned, Assert.Single(byPredicted.Items, i => i.WatchTonight).TitleId);
    }

    [Fact]
    public async Task Recently_watched_offers_only_unrated_in_window_conversions_that_carry_a_prediction()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        var artifactId = await SeedActiveArtifactAsync(accountId);

        var unrated = await SeedTitleAsync("unrated-conversion");
        var alreadyRated = await SeedTitleAsync("already-rated");
        var outOfWindow = await SeedTitleAsync("out-of-window");
        var noPrediction = await SeedTitleAsync("no-prediction");
        var userRemoved = await SeedTitleAsync("user-removed");

        await SeedRemovedAsync(accountId, unrated, Now.AddDays(-3), ListRemovalReason.Watched);
        await SeedRemovedAsync(accountId, alreadyRated, Now.AddDays(-3), ListRemovalReason.Watched);
        await SeedRemovedAsync(accountId, outOfWindow, Now.AddDays(-30), ListRemovalReason.Watched);
        await SeedRemovedAsync(accountId, noPrediction, Now.AddDays(-2), ListRemovalReason.Watched);
        await SeedRemovedAsync(accountId, userRemoved, Now.AddDays(-1), ListRemovalReason.UserRemoved);

        await SeedScoresAsync(accountId, artifactId, new()
        {
            [alreadyRated] = 8.0m,
            [outOfWindow] = 8.0m,
            [userRemoved] = 8.0m,
        }, Now.AddDays(-40));
        // The prediction Reel committed to before the watch...
        await SeedScoresAsync(accountId, artifactId, new() { [unrated] = 8.4m }, Now.AddDays(-10));
        // ...and a rescore AFTER it, which is hindsight and must not be the number the prompt shows.
        // Under a different artifact id, because (account, title, artifact) is unique — which is
        // also how it happens in life: the second number comes from the next retrain.
        await SeedScoresAsync(accountId, Guid.NewGuid(), new() { [unrated] = 5.1m }, Now.AddDays(-1));
        // `noPrediction` was only ever scored after its removal: no promise was made, so there is
        // nothing to settle and the strip stays silent rather than inventing a guess.
        await SeedScoresAsync(accountId, artifactId, new() { [noPrediction] = 9.0m }, Now.AddDays(-1));

        await SeedRatingAsync(accountId, alreadyRated, 6, Now.AddDays(-2));

        var recent = await RecentlyWatchedAsync(accountId);

        var item = Assert.Single(recent.Items);
        Assert.Equal(unrated, item.TitleId);
        Assert.Equal(8.4m, item.PredictedRating);

        // And once the user answers, the prompt stops offering it — the loop is closed, not looped.
        await SeedRatingAsync(accountId, unrated, 6, Now.AddHours(-1));
        Assert.Empty((await RecentlyWatchedAsync(accountId)).Items);
    }

    // ── Invocation ──────────────────────────────────────────────────────────────────────────

    private async Task<SavedEndpoints.SavedQueueResponse> QueueAsync(Guid accountId, string? sort = null)
    {
        await using var db = fixture.CreateContext(Pinned(accountId));
        var account = await db.Accounts.SingleAsync(a => a.Id == accountId);
        return await SavedEndpoints.QueueAsync(
            sort, account, db, Scorer(db), Calibration(db), Now, CancellationToken.None);
    }

    private async Task<SavedEndpoints.RecentlyWatchedResponse> RecentlyWatchedAsync(Guid accountId)
    {
        await using var db = fixture.CreateContext(Pinned(accountId));
        return await SavedEndpoints.RecentlyWatchedAsync(
            accountId, db, Calibration(db), Now, CancellationToken.None);
    }

    private static OnDemandScorer Scorer(IReelDbContext db)
    {
        var featureBuilder = new FeatureVectorBuilder(db);
        var engine = new FastTreeModelEngine();
        return new OnDemandScorer(
            db,
            featureBuilder,
            engine,
            new OnlineResidualModel(
                db, featureBuilder, engine,
                new MemoryCache(new MemoryCacheOptions()), NullLogger<OnlineResidualModel>.Instance),
            new DislikeSignal(db, new MemoryCache(new MemoryCacheOptions())),
            Calibration(db));
    }

    private static RatingCalibrationService Calibration(IReelDbContext db) =>
        new(db, new MemoryCache(new MemoryCacheOptions()));

    private static AccountContext Pinned(Guid accountId)
    {
        var context = new AccountContext();
        context.SetAccount(accountId);
        return context;
    }

    // ── Seeding ─────────────────────────────────────────────────────────────────────────────

    private async Task<Guid> SeedAccountAsync(int? eveningBudgetMinutes = null)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var account = new Account
        {
            Id = Guid.NewGuid(),
            TraktUserSlug = $"saved-queue-{Guid.NewGuid():N}",
            DisplayName = "Saved Queue Fixture",
            Region = "US",
            CreatedAt = Now,
            LastSeenAt = Now,
            PipelineStageChangedAt = Now,
            Settings = new AccountSettings { EveningBudgetMinutes = eveningBudgetMinutes },
        };
        db.Add(account);
        await db.SaveChangesAsync();
        return account.Id;
    }

    private async Task<Guid> SeedTitleAsync(string name, int? runtimeMinutes = null)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var title = new Title
        {
            Id = Guid.NewGuid(),
            MediaType = MediaType.Movie,
            TmdbId = Random.Shared.NextInt64(1, long.MaxValue),
            TraktId = Random.Shared.NextInt64(1, long.MaxValue),
            TraktSlug = $"{name}-{Guid.NewGuid():N}",
            Name = $"{name} {Guid.NewGuid():N}",
            Language = "en",
            PosterPath = "/p.jpg",
            Genres = ["drama"],
            Year = 2020,
            ReleasedAt = Now.AddYears(-5),
            RuntimeMinutes = runtimeMinutes,
            // Bottom of the popularity order so these fixtures never displace another suite's
            // top-N assertions on the shared catalogue.
            TmdbPopularity = 0m,
            CreatedAt = Now,
        };
        db.Add(title);
        await db.SaveChangesAsync();
        return title.Id;
    }

    private async Task SaveAllAsync(Guid accountId, IEnumerable<Guid> titleIds)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var offset = 0;
        foreach (var titleId in titleIds)
        {
            db.UserTitleReactions.Add(new UserTitleReaction
            {
                Id = Guid.NewGuid(),
                AccountId = accountId,
                TitleId = titleId,
                Kind = ReactionKind.SaveForLater,
                // Distinct save times, so "newest first" is a real order rather than a tie.
                CreatedAt = Now.AddDays(-offset++),
            });
        }

        await db.SaveChangesAsync();
    }

    private async Task<Guid> SeedProviderAsync(string name)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var provider = new StreamingProvider
        {
            Id = Guid.NewGuid(),
            TmdbProviderId = Random.Shared.Next(1, int.MaxValue),
            Name = name,
            LogoPath = "/logo.jpg",
            DisplayPriority = 1,
        };
        db.Add(provider);
        await db.SaveChangesAsync();
        return provider.Id;
    }

    private async Task SeedAvailabilityAsync(Guid titleId, Guid providerId, string region, DateTime fetchedAt)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        db.TitleAvailabilities.Add(new TitleAvailability
        {
            Id = Guid.NewGuid(),
            TitleId = titleId,
            Region = region,
            ProviderId = providerId,
            Kind = AvailabilityKind.Flatrate,
            FetchedAt = fetchedAt,
        });
        await db.SaveChangesAsync();
    }

    /// <summary>A small FastTree over the live schema, Active — the same shape the other suites use.</summary>
    private async Task<Guid> SeedActiveArtifactAsync(Guid accountId)
    {
        var rng = new Random(13);
        var rows = new List<float[]>();
        var labels = new List<float>();
        for (var i = 0; i < 200; i++)
        {
            var v = new float[FeatureSchema.VectorLength];
            for (var j = 0; j < v.Length; j++)
            {
                v[j] = (float)rng.NextDouble();
            }

            rows.Add(v);
            labels.Add(Math.Clamp(5f + (v[0] - 0.5f), 1f, 10f));
        }

        var trained = new FastTreeModelEngine().Train(
            new TrainingMatrix(rows.ToArray(), labels.ToArray(), FeatureSchema.AllNames()),
            TrainJobHandler.DefaultHyperparams);

        await using var db = fixture.CreateContext(new AccountContext());
        var artifact = new ModelArtifact
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            Version = 1,
            Algo = "FastTree",
            ArtifactBytes = trained.ArtifactBytes,
            TrainingRunId = Guid.NewGuid(),
            Status = ArtifactStatus.Active,
            TrainedAt = Now.AddDays(-2),
            // The schema-compat shadow column projects 'version' out of this jsonb; without a
            // current version the artifact is invisible to every Active-artifact read.
            FeatureSchemaJson = JsonSerializer.Serialize(
                new { version = FeatureSchema.Version, names = FeatureSchema.AllNames() }),
        };
        db.Add(artifact);
        await db.SaveChangesAsync();
        return artifact.Id;
    }

    private async Task SeedScoresAsync(
        Guid accountId, Guid artifactId, Dictionary<Guid, decimal> scores, DateTime scoredAt)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        foreach (var (titleId, predicted) in scores)
        {
            db.TitleScores.Add(new TitleScore
            {
                Id = Guid.NewGuid(),
                AccountId = accountId,
                TitleId = titleId,
                ModelArtifactId = artifactId,
                PredictedRating = predicted,
                ScoredAt = scoredAt,
            });
        }

        await db.SaveChangesAsync();
    }

    private async Task SeedRemovedAsync(Guid accountId, Guid titleId, DateTime removedAt, ListRemovalReason reason)
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
            FirstRatedAt = ratedAt,
        });
        await db.SaveChangesAsync();
    }
}
