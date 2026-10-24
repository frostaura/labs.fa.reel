using System.Text.Json;
using FrostAura.Reel.Application.Jobs;
using FrostAura.Reel.Application.Ml;
using FrostAura.Reel.Application.Pipeline;
using FrostAura.Reel.Application.Search;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Library;
using FrostAura.Reel.Domain.Ml;
using FrostAura.Reel.Domain.Sync;
using FrostAura.Reel.Domain.Tenancy;
using FrostAura.Reel.Infrastructure.Ml;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrostAura.Reel.Tests.IntegrationTests;

/// <summary>
/// The three model-artifact invariants that were being asserted in comments rather than in code:
/// a stored feature-schema version is actually VALIDATED before an artifact can be scored with, the
/// database (not a check-then-insert) is what guarantees one Active artifact per account, and an
/// account with too few ratings parks instead of wedging the pipeline behind a failing trainer.
/// Skips when REEL_TEST_CONNECTION_STRING is unset.
/// </summary>
[Collection("postgres")]
public class ModelArtifactLifecycleTests(PostgresFixture fixture)
{
    [Fact]
    public async Task An_artifact_from_an_older_feature_schema_is_not_readable_as_active()
    {
        if (!fixture.Available)
        {
            return;
        }

        // FeatureSchema.Version is written on every artifact but was never read back, and the engine
        // validates vector WIDTH only — never column ORDER. An artifact from a superseded schema must
        // therefore be unusable, not silently misread.
        var accountId = await SeedAccountAsync();
        await SeedArtifactAsync(accountId, version: 1, schemaVersion: FeatureSchema.Version - 1);

        await using var db = fixture.CreateContext(new AccountContext());
        var visible = await db.ModelArtifacts
            .FirstOrDefaultAsync(a => a.AccountId == accountId && a.Status == ArtifactStatus.Active);
        Assert.Null(visible);

        // Still on disk — the sweep has to be able to find and supersede it.
        Assert.Equal(1, await db.ModelArtifacts.IgnoreQueryFilters().CountAsync(a => a.AccountId == accountId));
    }

    [Fact]
    public async Task An_artifact_from_the_current_feature_schema_is_readable()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        var artifactId = await SeedArtifactAsync(accountId, version: 1, schemaVersion: FeatureSchema.Version);

        await using var db = fixture.CreateContext(new AccountContext());
        var visible = await db.ModelArtifacts
            .FirstOrDefaultAsync(a => a.AccountId == accountId && a.Status == ArtifactStatus.Active);

        Assert.NotNull(visible);
        Assert.Equal(artifactId, visible!.Id);
    }

    [Fact]
    public async Task The_database_refuses_a_second_active_artifact_for_one_account()
    {
        if (!fixture.Available)
        {
            return;
        }

        // Two Active artifacts would let OnDemandScorer and FeedPaginator — both of which pick the
        // Active row with an UNORDERED FirstOrDefault — disagree request to request. The partial
        // unique index is what makes that unrepresentable.
        var accountId = await SeedAccountAsync();
        await SeedArtifactAsync(accountId, version: 1, schemaVersion: FeatureSchema.Version);

        var error = await Assert.ThrowsAsync<DbUpdateException>(
            () => SeedArtifactAsync(accountId, version: 2, schemaVersion: FeatureSchema.Version));

        Assert.True(TrainJobHandler.IsUniqueViolation(error), $"expected a unique violation, got {error.InnerException?.Message}");
    }

    [Fact]
    public async Task Training_an_account_with_no_ratings_parks_it_instead_of_throwing()
    {
        if (!fixture.Available)
        {
            return;
        }

        // ML.NET FastTree throws "All instances skipped due to missing features" on an empty matrix.
        // The old code had already moved the account to Training before that throw, so it sat behind
        // a permanent spinner while the job burned its retries and every later sync re-enqueued
        // another certain failure. Real Trakt users who watch without rating land here.
        var accountId = await SeedAccountAsync();
        var job = new SyncJob
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            Kind = JobKind.Train,
            EnqueuedAt = DateTime.UtcNow,
        };

        await RunTrainAsync(accountId, job);

        await using var db = fixture.CreateContext(new AccountContext());
        var account = await db.Accounts.FirstAsync(a => a.Id == accountId);

        Assert.NotEqual(PipelineStage.Training, account.PipelineStage);
        Assert.Equal(PipelineStage.Degraded, account.PipelineStage);
        Assert.False(await db.ModelArtifacts.IgnoreQueryFilters().AnyAsync(a => a.AccountId == accountId));
        Assert.False(await db.SyncJobs.AnyAsync(j => j.AccountId == accountId && j.Kind == JobKind.Evaluate));
        Assert.NotNull(job.ProgressMessage);
        Assert.Contains("Not enough ratings", job.ProgressMessage!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Training_below_the_row_floor_parks_rather_than_fitting_a_degenerate_model()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        for (var i = 0; i < TrainJobHandler.MinTrainingRows - 1; i++)
        {
            var titleId = await SeedTitleAsync($"Sparse {i}");
            await SeedRatingAsync(accountId, titleId, (short)(6 + (i % 4)));
        }

        var job = new SyncJob { Id = Guid.NewGuid(), AccountId = accountId, Kind = JobKind.Train, EnqueuedAt = DateTime.UtcNow };
        await RunTrainAsync(accountId, job);

        await using var db = fixture.CreateContext(new AccountContext());
        Assert.False(await db.ModelArtifacts.IgnoreQueryFilters().AnyAsync(a => a.AccountId == accountId));
        Assert.Equal(PipelineStage.Degraded, (await db.Accounts.FirstAsync(a => a.Id == accountId)).PipelineStage);
    }

    [Fact]
    public async Task Training_at_or_above_the_floor_produces_exactly_one_active_artifact()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        for (var i = 0; i < TrainJobHandler.MinTrainingRows + 4; i++)
        {
            var titleId = await SeedTitleAsync($"Trainable {i}");
            await SeedRatingAsync(accountId, titleId, (short)(4 + (i % 7)));
        }

        // Twice in a row: the second run must supersede the first, not collide with the unique index.
        // A new rating lands between them — a retrain over unchanged inputs is skipped by design
        // (see Same_inputs_never_retrain…), and that is not what this test is about.
        await RunTrainAsync(accountId, new SyncJob { Id = Guid.NewGuid(), AccountId = accountId, Kind = JobKind.Train, EnqueuedAt = DateTime.UtcNow });
        await SeedRatingAsync(accountId, await SeedTitleAsync("Trainable late"), 9);
        await RunTrainAsync(accountId, new SyncJob { Id = Guid.NewGuid(), AccountId = accountId, Kind = JobKind.Train, EnqueuedAt = DateTime.UtcNow });

        await using var db = fixture.CreateContext(new AccountContext());
        var artifacts = await db.ModelArtifacts.IgnoreQueryFilters()
            .Where(a => a.AccountId == accountId)
            .ToListAsync();

        Assert.Equal(2, artifacts.Count);
        Assert.Equal(1, artifacts.Count(a => a.Status == ArtifactStatus.Active));
        Assert.Equal([1, 2], artifacts.OrderBy(a => a.Version).Select(a => a.Version));

        // And the artifact it left behind is readable — i.e. stamped with the running schema version.
        Assert.NotNull(await db.ModelArtifacts.FirstOrDefaultAsync(a => a.AccountId == accountId && a.Status == ArtifactStatus.Active));
    }

    [Fact]
    public async Task Same_inputs_never_retrain_and_the_active_model_is_left_alone()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedTrainableAccountAsync();
        await RunTrainAsync(accountId, NewTrainJob(accountId));
        var first = await ActiveAsync(accountId);
        int runsAfterFirst;
        await using (var count = fixture.CreateContext(new AccountContext()))
        {
            runsAfterFirst = await count.TrainingRuns.CountAsync(r => r.AccountId == accountId);
        }

        // The enrich-chain shape: three more Train jobs, nothing new rated, nothing re-extracted.
        SyncJob? last = null;
        for (var i = 0; i < 3; i++)
        {
            last = NewTrainJob(accountId);
            await RunTrainAsync(accountId, last);
        }

        var active = await ActiveAsync(accountId);
        Assert.Equal(first.Id, active.Id);
        Assert.Equal(1, active.Version);
        await using var db = fixture.CreateContext(new AccountContext());
        Assert.Equal(1, await db.ModelArtifacts.IgnoreQueryFilters().CountAsync(a => a.AccountId == accountId));
        // The skip comes before the eval harness, so no ledger row is written for a non-event either.
        Assert.Equal(runsAfterFirst, await db.TrainingRuns.CountAsync(r => r.AccountId == accountId));
        Assert.Contains("Nothing new to learn from", last!.ProgressMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_new_rating_always_retrains_even_minutes_later()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedTrainableAccountAsync();
        await RunTrainAsync(accountId, NewTrainJob(accountId));
        await SeedRatingAsync(accountId, await SeedTitleAsync("Just rated"), 10);

        await RunTrainAsync(accountId, NewTrainJob(accountId));

        Assert.Equal(2, (await ActiveAsync(accountId)).Version);
    }

    [Fact]
    public async Task Catalogue_drift_alone_waits_out_the_day_then_retrains_once()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedTrainableAccountAsync();
        await RunTrainAsync(accountId, NewTrainJob(accountId));

        // An enrich pass re-extracts a rated title: a real input change, but not a new label.
        await using (var db = fixture.CreateContext(new AccountContext()))
        {
            var ratedTitle = await db.UserRatings.Where(r => r.AccountId == accountId).Select(r => r.TitleId).FirstAsync();
            db.TitleAttributes.Add(new TitleAttributes
            {
                TitleId = ratedTitle,
                Status = AttributeExtractionStatus.Done,
                ExtractorVersion = 3,
                ExtractorModel = "test",
                ExtractedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        await RunTrainAsync(accountId, NewTrainJob(accountId));
        Assert.Equal(1, (await ActiveAsync(accountId)).Version); // debounced: v1 is minutes old

        // Twenty-three hours on it still waits: catalogue drift with no new rating is folded in at
        // most once a day, because every promotion re-deals the feed with a paid Fresh build.
        await AgeActiveAsync(accountId, TimeSpan.FromHours(23));
        await RunTrainAsync(accountId, NewTrainJob(accountId));
        Assert.Equal(1, (await ActiveAsync(accountId)).Version);

        // A day later the same drift is worth one refit.
        await using (var db = fixture.CreateContext(new AccountContext()))
        {
            var artifact = await db.ModelArtifacts.SingleAsync(a => a.AccountId == accountId && a.Status == ArtifactStatus.Active);
            artifact.TrainedAt = DateTime.UtcNow - TrainJobHandler.DriftDebounce - TimeSpan.FromMinutes(1);
            await db.SaveChangesAsync();
        }

        await RunTrainAsync(accountId, NewTrainJob(accountId));
        Assert.Equal(2, (await ActiveAsync(accountId)).Version);

        // And having absorbed it, the next one has nothing to learn.
        await RunTrainAsync(accountId, NewTrainJob(accountId));
        Assert.Equal(2, (await ActiveAsync(accountId)).Version);
    }

    [Fact]
    public async Task Trakt_vote_ticks_and_metadata_refreshes_are_not_new_inputs()
    {
        if (!fixture.Available)
        {
            return;
        }

        // Every Trakt ingest rewrites the vote counts and every metadata refresh stamps a time; the
        // hash used to include both raw, so a steady account retrained once per sync after the
        // debounce. Neither is something the model would learn from at that resolution.
        var accountId = await SeedTrainableAccountAsync();
        await SetCrowdAsync(accountId, votes: 40_000, rating: 7.81m);
        await RunTrainAsync(accountId, NewTrainJob(accountId));

        await SetCrowdAsync(accountId, votes: 40_012, rating: 7.84m, refreshedAt: DateTime.UtcNow);
        await AgeActiveAsync(accountId, TrainJobHandler.DriftDebounce + TimeSpan.FromHours(1));
        var job = NewTrainJob(accountId);
        await RunTrainAsync(accountId, job);

        Assert.Equal(1, (await ActiveAsync(accountId)).Version);
        Assert.Contains("Nothing new to learn from", job.ProgressMessage, StringComparison.Ordinal);

        // A real move — the crowd rating shifts a full tenth, the votes double — is an input change.
        await SetCrowdAsync(accountId, votes: 80_000, rating: 7.95m);
        await RunTrainAsync(accountId, NewTrainJob(accountId));
        Assert.Equal(2, (await ActiveAsync(accountId)).Version);
    }

    [Fact]
    public async Task A_model_trained_before_input_hashes_skips_when_nothing_was_said_since_and_is_stamped()
    {
        if (!fixture.Available)
        {
            return;
        }

        // The deploy that introduced hashes: the Active artifact has none. Reading "no hash" as
        // "changed" made the first retrain after deploy always train and promote on 0 new ratings,
        // and force a second paid Fresh build (purge build -> enrich -> Train -> model-trained build).
        var accountId = await SeedTrainableAccountAsync();
        await RunTrainAsync(accountId, NewTrainJob(accountId));
        await StripHashesAsync(accountId);

        var job = NewTrainJob(accountId);
        await RunTrainAsync(accountId, job);

        var active = await ActiveAsync(accountId);
        Assert.Equal(1, active.Version);
        Assert.Contains("Nothing new to learn from", job.ProgressMessage, StringComparison.Ordinal);
        Assert.NotNull(TrainingInputHash.ReadFrom(active.FeatureSchemaJson)); // stamped for every later check
        Assert.Contains("\"version\"", active.FeatureSchemaJson, StringComparison.Ordinal); // nothing else lost

        // From then on it is the ordinary hash comparison: a new rating retrains.
        await SeedRatingAsync(accountId, await SeedTitleAsync("Rated after the deploy"), 9);
        await RunTrainAsync(accountId, NewTrainJob(accountId));
        Assert.Equal(2, (await ActiveAsync(accountId)).Version);
    }

    [Fact]
    public async Task A_model_trained_before_input_hashes_retrains_when_the_user_reacted_since()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedTrainableAccountAsync();
        await RunTrainAsync(accountId, NewTrainJob(accountId));
        await StripHashesAsync(accountId);
        await AgeActiveAsync(accountId, TimeSpan.FromHours(2));

        await using (var db = fixture.CreateContext(new AccountContext()))
        {
            var titleId = await db.UserRatings.Where(r => r.AccountId == accountId).Select(r => r.TitleId).FirstAsync();
            db.UserTitleReactions.Add(new UserTitleReaction
            {
                Id = Guid.NewGuid(),
                AccountId = accountId,
                TitleId = titleId,
                Kind = ReactionKind.NotInterested,
                CreatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        await RunTrainAsync(accountId, NewTrainJob(accountId));
        Assert.Equal(2, (await ActiveAsync(accountId)).Version);
    }

    [Theory]
    [InlineData(40_000, 40_012, true)]
    [InlineData(1_000, 1_040, true)]
    [InlineData(1_000, 2_000, false)]
    [InlineData(0, 3, false)]
    public void Vote_counts_hash_at_the_resolution_the_feature_reads(int before, int after, bool same)
    {
        Assert.Equal(same, TrainingInputHash.VotesBucket(before) == TrainingInputHash.VotesBucket(after));
    }

    [Fact]
    public async Task An_onboarding_account_is_never_skipped()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedTrainableAccountAsync();
        await RunTrainAsync(accountId, NewTrainJob(accountId));

        // A re-ingest put the account back mid-pipeline: skipping would leave it behind a spinner.
        await using (var db = fixture.CreateContext(new AccountContext()))
        {
            var account = await db.Accounts.SingleAsync(a => a.Id == accountId);
            account.PipelineStage = PipelineStage.Extracting;
            await db.SaveChangesAsync();
        }

        await RunTrainAsync(accountId, NewTrainJob(accountId));

        Assert.Equal(2, (await ActiveAsync(accountId)).Version);
        await using var verify = fixture.CreateContext(new AccountContext());
        Assert.Equal(PipelineStage.Evaluated, (await verify.Accounts.SingleAsync(a => a.Id == accountId)).PipelineStage);
    }

    private static SyncJob NewTrainJob(Guid accountId) =>
        new() { Id = Guid.NewGuid(), AccountId = accountId, Kind = JobKind.Train, EnqueuedAt = DateTime.UtcNow };

    private async Task<Guid> SeedTrainableAccountAsync()
    {
        var accountId = await SeedAccountAsync();
        for (var i = 0; i < TrainJobHandler.MinTrainingRows + 4; i++)
        {
            await SeedRatingAsync(accountId, await SeedTitleAsync($"Hygiene {i}"), (short)(4 + (i % 7)));
        }

        return accountId;
    }

    private async Task AgeActiveAsync(Guid accountId, TimeSpan age)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var artifact = await db.ModelArtifacts.SingleAsync(a => a.AccountId == accountId && a.Status == ArtifactStatus.Active);
        artifact.TrainedAt = DateTime.UtcNow - age;
        await db.SaveChangesAsync();
    }

    private async Task StripHashesAsync(Guid accountId)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var artifact = await db.ModelArtifacts.SingleAsync(a => a.AccountId == accountId && a.Status == ArtifactStatus.Active);
        var doc = System.Text.Json.Nodes.JsonNode.Parse(artifact.FeatureSchemaJson)!.AsObject();
        doc.Remove("labelHash");
        doc.Remove("inputHash");
        artifact.FeatureSchemaJson = doc.ToJsonString();
        await db.SaveChangesAsync();
        Assert.Null(TrainingInputHash.ReadFrom(artifact.FeatureSchemaJson));
    }

    private async Task SetCrowdAsync(Guid accountId, int votes, decimal rating, DateTime? refreshedAt = null)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var ids = await db.UserRatings.Where(r => r.AccountId == accountId).Select(r => r.TitleId).ToListAsync();
        foreach (var title in await db.Titles.Where(t => ids.Contains(t.Id)).ToListAsync())
        {
            title.TraktVotes = votes;
            title.TraktRating = rating;
            title.LastMetadataRefreshAt = refreshedAt ?? title.LastMetadataRefreshAt;
        }

        await db.SaveChangesAsync();
    }

    private async Task<ModelArtifact> ActiveAsync(Guid accountId)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        return await db.ModelArtifacts.SingleAsync(a => a.AccountId == accountId && a.Status == ArtifactStatus.Active);
    }

    private async Task RunTrainAsync(Guid accountId, SyncJob job)
    {
        var scoped = new AccountContext();
        scoped.SetAccount(accountId);
        await using var db = fixture.CreateContext(scoped);
        var featureBuilder = new FeatureVectorBuilder(db);
        var engine = new FastTreeModelEngine();
        var handler = new TrainJobHandler(
            db,
            featureBuilder,
            engine,
            new EvalHarness(
                db,
                featureBuilder,
                engine,
                new EligibilityQueryBuilder(db),
                new NoopEventHub(),
                NullLogger<EvalHarness>.Instance),
            new PersonAffinityWriter(db, NullLogger<PersonAffinityWriter>.Instance),
            new NoopEventHub(),
            NullLogger<TrainJobHandler>.Instance);
        await handler.ExecuteAsync(job, CancellationToken.None);
    }

    private async Task<Guid> SeedAccountAsync()
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var account = new Account
        {
            Id = Guid.NewGuid(),
            TraktUserSlug = $"artifact-{Guid.NewGuid():N}",
            CreatedAt = DateTime.UtcNow,
            LastSeenAt = DateTime.UtcNow,
            PipelineStage = PipelineStage.Extracting,
            PipelineStageChangedAt = DateTime.UtcNow,
        };
        db.Add(account);
        await db.SaveChangesAsync();
        return account.Id;
    }

    private async Task<Guid> SeedTitleAsync(string name)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var title = new Title
        {
            Id = Guid.NewGuid(),
            MediaType = MediaType.Movie,
            TmdbId = Random.Shared.NextInt64(1, long.MaxValue),
            TraktSlug = string.Empty,
            Name = name,
            Language = "en",
            // No poster: keeps these fixtures out of every discovery lane (PosterPath != null) and
            // therefore out of the other tests sharing this database.
            PosterPath = null,
            Genres = ["drama"],
            Year = 2005,
            RuntimeMinutes = 100,
            TraktRating = 7m,
            TmdbPopularity = 10m,
            CreatedAt = DateTime.UtcNow,
        };
        db.Add(title);
        await db.SaveChangesAsync();
        return title.Id;
    }

    private async Task SeedRatingAsync(Guid accountId, Guid titleId, short rating)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        db.UserRatings.Add(new UserRating
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            TitleId = titleId,
            SubjectType = RatingSubjectType.Movie,
            Rating = rating,
            RatedAt = DateTime.UtcNow.AddDays(-30),
            SyncedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private async Task<Guid> SeedArtifactAsync(Guid accountId, int version, int schemaVersion)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var artifact = new ModelArtifact
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            Version = version,
            Algo = "FastTree",
            ArtifactBytes = [1, 2, 3],
            FeatureSchemaJson = JsonSerializer.Serialize(new { version = schemaVersion, names = FeatureSchema.AllNames() }),
            TrainingRunId = Guid.Empty,
            Status = ArtifactStatus.Active,
            TrainedAt = DateTime.UtcNow,
        };
        db.ModelArtifacts.Add(artifact);
        await db.SaveChangesAsync();
        return artifact.Id;
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
