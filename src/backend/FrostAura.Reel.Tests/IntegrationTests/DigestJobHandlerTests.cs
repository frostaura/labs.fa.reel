using System.Text.Json;
using FrostAura.Reel.Application.Jobs;
using FrostAura.Reel.Application.Ml;
using FrostAura.Reel.Application.Ranking;
using FrostAura.Reel.Application.Search;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Feed;
using FrostAura.Reel.Domain.Library;
using FrostAura.Reel.Domain.Ml;
using FrostAura.Reel.Domain.Notifications;
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
/// BuildDigest on real Postgres: the trailing-7-day window, the ≥7.0 predicted floor, the
/// zero-footprint eligibility rule, the new-on-provider suppression against last week's digest,
/// the (AccountId, WeekStart) upsert, and the push-subscription lifecycle (success resets,
/// gone deletes, fifth consecutive failure prunes). Skips when REEL_TEST_CONNECTION_STRING is
/// unset. Every candidate is pre-scored under the Active artifact so the on-demand scorer
/// stays un-invoked — these tests pin the digest's set logic, not the model.
/// </summary>
[Collection("postgres")]
public class DigestJobHandlerTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Window_floor_and_footprint_gate_the_items_and_reruns_upsert()
    {
        if (!fixture.Available)
        {
            return;
        }

        var now = DateTime.UtcNow;
        var accountId = await SeedAccountAsync();

        // Six titles spanning every gate:
        //   freshShow    — show, first aired 2 days ago, 9.0  → in, "new-release"
        //   freshMovie   — movie, released 3 days ago, 8.5  → in, "new-release"
        //   staleMovie   — released 30 days ago, 9.9        → out: outside the window
        //   lowScore     — released 2 days ago, 6.9         → out: under the 7.0 floor
        //   watched      — released 1 day ago, 9.9, watched → out: interaction footprint
        //   onProvider   — old title, fresh flatrate, 8.0   → in, "new-on-provider"
        var freshShow = await SeedTitleAsync(MediaType.Show, firstAiredAt: now.AddDays(-2));
        var freshMovie = await SeedTitleAsync(MediaType.Movie, releasedAt: now.AddDays(-3));
        var staleMovie = await SeedTitleAsync(MediaType.Movie, releasedAt: now.AddDays(-30));
        var lowScore = await SeedTitleAsync(MediaType.Movie, releasedAt: now.AddDays(-2));
        var watched = await SeedTitleAsync(MediaType.Movie, releasedAt: now.AddDays(-1));
        var onProvider = await SeedTitleAsync(MediaType.Movie, releasedAt: now.AddYears(-2));

        await SeedWatchedAsync(accountId, watched);
        await SeedAvailabilityAsync(onProvider, "US", now.AddDays(-1));
        await SeedActiveArtifactAndScoresAsync(accountId, new()
        {
            [freshShow] = 9.0m,
            [freshMovie] = 8.5m,
            [staleMovie] = 9.9m,
            [lowScore] = 6.9m,
            [watched] = 9.9m,
            [onProvider] = 8.0m,
        });

        await RunDigestAsync(accountId, new FakePushSender());
        var items = await ReadDigestItemsAsync(accountId);

        Assert.Equal(new[] { freshShow, freshMovie, onProvider }, items.Select(i => i.TitleId).ToArray());
        Assert.Equal(DigestReasons.NewRelease, items[0].Reason);
        Assert.Equal(DigestReasons.NewRelease, items[1].Reason);
        Assert.Equal(DigestReasons.NewOnProvider, items[2].Reason);
        Assert.Equal(9.0m, items[0].PredictedRating);

        // A re-run inside the same ISO week refreshes the one row rather than adding a second.
        await RunDigestAsync(accountId, new FakePushSender());
        await using var db = fixture.CreateContext(Pinned(accountId));
        Assert.Equal(1, await db.AccountDigests.CountAsync(d => d.AccountId == accountId));
    }

    [Fact]
    public async Task Last_weeks_provider_items_stay_suppressed_and_push_lifecycle_runs()
    {
        if (!fixture.Available)
        {
            return;
        }

        var now = DateTime.UtcNow;
        var accountId = await SeedAccountAsync();

        // Both titles are old catalogue with a freshly fetched flatrate row; only the one absent
        // from LAST week's digest may ship — re-fetched availability must not recycle items.
        var shownLastWeek = await SeedTitleAsync(MediaType.Movie, releasedAt: now.AddYears(-3));
        var genuinelyNew = await SeedTitleAsync(MediaType.Movie, releasedAt: now.AddYears(-3));
        await SeedAvailabilityAsync(shownLastWeek, "US", now.AddDays(-2));
        await SeedAvailabilityAsync(genuinelyNew, "US", now.AddDays(-2));
        await SeedActiveArtifactAndScoresAsync(accountId, new()
        {
            [shownLastWeek] = 9.5m,
            [genuinelyNew] = 8.0m,
        });
        await SeedPreviousWeekDigestAsync(accountId, shownLastWeek, now);

        var healthy = await SeedSubscriptionAsync(accountId, "https://push.example/healthy");
        var dead = await SeedSubscriptionAsync(accountId, "https://push.example/dead");
        var flaky = await SeedSubscriptionAsync(accountId, "https://push.example/flaky", failCount: 4);
        var sender = new FakePushSender
        {
            Outcomes =
            {
                ["https://push.example/dead"] = WebPushOutcome.Gone,
                ["https://push.example/flaky"] = WebPushOutcome.Failure,
            },
        };

        await RunDigestAsync(accountId, sender);

        var items = await ReadDigestItemsAsync(accountId);
        Assert.Equal(new[] { genuinelyNew }, items.Select(i => i.TitleId).ToArray());

        // One send per subscription; the payload advertises the single fresh pick.
        Assert.Equal(3, sender.Sent.Count);
        Assert.Contains("1 new high-for-you", sender.Sent[0].PayloadJson);

        await using var db = fixture.CreateContext(Pinned(accountId));
        var survivor = await db.PushSubscriptions.SingleAsync(s => s.AccountId == accountId);
        Assert.Equal(healthy, survivor.Id);
        Assert.NotNull(survivor.LastSuccessAt);
        Assert.Equal(0, survivor.FailCount);

        // Gone deletes immediately; the flaky one just crossed the 5-failure prune line.
        Assert.False(await db.PushSubscriptions.AnyAsync(s => s.Id == dead));
        Assert.False(await db.PushSubscriptions.AnyAsync(s => s.Id == flaky));
    }

    // ── Harness ─────────────────────────────────────────────────────────────────────────────

    private sealed class FakePushSender : IWebPushSender
    {
        public Dictionary<string, WebPushOutcome> Outcomes { get; } = [];

        public List<(string Endpoint, string PayloadJson)> Sent { get; } = [];

        public Task<WebPushResult> SendAsync(
            string endpoint, string p256dh, string auth, string payloadJson, CancellationToken ct)
        {
            Sent.Add((endpoint, payloadJson));
            var outcome = Outcomes.GetValueOrDefault(endpoint, WebPushOutcome.Success);
            return Task.FromResult(new WebPushResult(outcome, outcome == WebPushOutcome.Failure ? "boom" : null));
        }
    }

    private static AccountContext Pinned(Guid accountId)
    {
        var context = new AccountContext();
        context.SetAccount(accountId);
        return context;
    }

    /// <summary>Runs the handler exactly as the job runner does: context pinned to the account.</summary>
    private async Task RunDigestAsync(Guid accountId, FakePushSender sender)
    {
        await using var db = fixture.CreateContext(Pinned(accountId));
        var featureBuilder = new FeatureVectorBuilder(db);
        var engine = new FastTreeModelEngine();
        var scorer = new OnDemandScorer(
            db,
            featureBuilder,
            engine,
            new OnlineResidualModel(
                db, featureBuilder, engine,
                new MemoryCache(new MemoryCacheOptions()), NullLogger<OnlineResidualModel>.Instance),
            new DislikeSignal(db, new MemoryCache(new MemoryCacheOptions())));
        var handler = new BuildDigestJobHandler(
            db, new EligibilityQueryBuilder(db), scorer, sender, NullLogger<BuildDigestJobHandler>.Instance);

        await handler.ExecuteAsync(
            new SyncJob { Id = Guid.NewGuid(), AccountId = accountId, Kind = JobKind.BuildDigest },
            CancellationToken.None);
    }

    private async Task<List<DigestItem>> ReadDigestItemsAsync(Guid accountId)
    {
        // Computed client-side: DigestWeek.MondayOf has no SQL translation.
        var weekStart = DigestWeek.MondayOf(DateTime.UtcNow);
        await using var db = fixture.CreateContext(Pinned(accountId));
        var digest = await db.AccountDigests.SingleAsync(
            d => d.AccountId == accountId && d.WeekStart == weekStart);
        return JsonSerializer.Deserialize<List<DigestItem>>(digest.ItemsJson) ?? [];
    }

    private async Task<Guid> SeedAccountAsync()
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var account = new Account
        {
            Id = Guid.NewGuid(),
            TraktUserSlug = $"digest-{Guid.NewGuid():N}",
            DisplayName = "Digest Fixture",
            CreatedAt = DateTime.UtcNow,
            LastSeenAt = DateTime.UtcNow,
            PipelineStageChangedAt = DateTime.UtcNow,
        };
        db.Add(account);
        await db.SaveChangesAsync();
        return account.Id;
    }

    private async Task<Guid> SeedTitleAsync(
        MediaType mediaType, DateTime? releasedAt = null, DateTime? firstAiredAt = null)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var title = new Title
        {
            Id = Guid.NewGuid(),
            MediaType = mediaType,
            TmdbId = Random.Shared.NextInt64(1, long.MaxValue),
            TraktSlug = string.Empty,
            Name = $"digest-fixture {Guid.NewGuid():N}",
            Language = "en",
            PosterPath = "/p.jpg",
            Genres = ["drama"],
            ReleasedAt = releasedAt,
            FirstAiredAt = firstAiredAt,
            // Bottom of the popularity order so these fixtures never displace another
            // integration test's top-N assertions on the shared catalogue.
            TmdbPopularity = 0m,
            CreatedAt = DateTime.UtcNow,
        };
        db.Add(title);
        await db.SaveChangesAsync();
        return title.Id;
    }

    private async Task SeedWatchedAsync(Guid accountId, Guid titleId)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        db.WatchedTitles.Add(new WatchedTitle
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            TitleId = titleId,
            Plays = 1,
            IsFullyWatched = true,
            FirstSyncedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private async Task SeedAvailabilityAsync(Guid titleId, string region, DateTime fetchedAt)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        db.TitleAvailabilities.Add(new TitleAvailability
        {
            Id = Guid.NewGuid(),
            TitleId = titleId,
            Region = region,
            ProviderId = Guid.NewGuid(),
            Kind = AvailabilityKind.Flatrate,
            FetchedAt = fetchedAt,
        });
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// One genuinely trained Active artifact plus a persisted TitleScore per entry. The model is
    /// REAL (small FastTree over the live schema, labels pinned near 5.0) because the catalogue is
    /// shared across the collection: other suites' fresh titles become unscored candidates for
    /// this account, the handler on-demand-scores them, and their ~5.0 predictions fall under the
    /// 7.0 floor — so the assertions stay exact without owning the whole catalogue.
    /// </summary>
    private async Task SeedActiveArtifactAndScoresAsync(Guid accountId, Dictionary<Guid, decimal> scores)
    {
        var rng = new Random(11);
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
            TrainedAt = DateTime.UtcNow,
            // The schema-compat shadow column projects 'version' out of this jsonb; without a
            // current version the artifact is invisible to every Active-artifact read.
            FeatureSchemaJson = JsonSerializer.Serialize(new { version = FeatureSchema.Version, names = FeatureSchema.AllNames() }),
        };
        db.Add(artifact);
        foreach (var (titleId, predicted) in scores)
        {
            db.TitleScores.Add(new TitleScore
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

    private async Task SeedPreviousWeekDigestAsync(Guid accountId, Guid titleId, DateTime now)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        db.AccountDigests.Add(new AccountDigest
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            WeekStart = DigestWeek.MondayOf(now).AddDays(-7),
            GeneratedAt = now.AddDays(-7),
            ItemsJson = JsonSerializer.Serialize(
                new List<DigestItem> { new(titleId, 9.5m, DigestReasons.NewOnProvider) }),
        });
        await db.SaveChangesAsync();
    }

    private async Task<Guid> SeedSubscriptionAsync(Guid accountId, string endpoint, int failCount = 0)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var subscription = new PushSubscription
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            Endpoint = endpoint,
            P256dh = "p256dh-fixture",
            Auth = "auth-fixture",
            FailCount = failCount,
            CreatedAt = DateTime.UtcNow,
        };
        db.PushSubscriptions.Add(subscription);
        await db.SaveChangesAsync();
        return subscription.Id;
    }
}
