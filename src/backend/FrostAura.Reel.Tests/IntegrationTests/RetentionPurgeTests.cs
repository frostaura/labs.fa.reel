using FrostAura.Reel.Api.Endpoints;
using FrostAura.Reel.Domain.Auth;
using FrostAura.Reel.Domain.Library;
using FrostAura.Reel.Domain.Ml;
using FrostAura.Reel.Domain.Sync;
using FrostAura.Reel.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FrostAura.Reel.Tests.IntegrationTests;

/// <summary>
/// Retention (E-15, K-15): old rows go, and every row a reader still needs stays whatever its age.
/// On the runner collection's own database, because a purge is global and the shared integration
/// database holds other tests' rows.
/// </summary>
[Collection("job-runner")]
public sealed class RetentionPurgeTests(JobRunnerDatabaseFixture fixture)
{
    [Fact]
    public async Task A_pass_deletes_only_rows_nothing_reads_and_a_second_pass_deletes_nothing_of_them()
    {
        if (!fixture.Available)
        {
            return;
        }

        var now = DateTime.UtcNow;
        var account = Guid.NewGuid();
        var seeded = new Seeded();

        await using (var db = fixture.CreateContext())
        {
            // ── SyncJobs ──
            seeded.JobOldest = Job(db, account, JobKind.Train, JobStatus.Succeeded, completed: now.AddDays(-40));
            seeded.JobOld = Job(db, account, JobKind.Train, JobStatus.Succeeded, completed: now.AddDays(-35));
            seeded.JobRecent = Job(db, account, JobKind.Train, JobStatus.Succeeded, completed: now.AddDays(-1));
            // The only row of its (account, kind, status): "last failed 60 days ago" must survive.
            seeded.JobOnlyFailure = Job(db, account, JobKind.BuildFeed, JobStatus.Failed, completed: now.AddDays(-60));
            // Pre-fix Failed rows with no CompletedAt end at their last sign of life.
            seeded.JobFailedNoCompletion = Job(db, account, JobKind.DeltaSync, JobStatus.Failed, completed: null, heartbeat: now.AddDays(-45));
            Job(db, account, JobKind.DeltaSync, JobStatus.Failed, completed: now.AddDays(-2));
            // Not terminal: never purged.
            seeded.JobPendingOld = Job(db, account, JobKind.Evaluate, JobStatus.Pending, completed: null, enqueued: now.AddDays(-90));

            // ── TitleScores ──
            var active = Artifact(db, account, ArtifactStatus.Active, 3);
            var superseded = Artifact(db, account, ArtifactStatus.Superseded, 2);
            var older = Artifact(db, account, ArtifactStatus.Superseded, 1);
            var (replaced, onlyScore, rated, watched, young, activeOld) =
                (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

            seeded.ScoreReplaced = Score(db, account, replaced, superseded, now.AddDays(-20));
            Score(db, account, replaced, active, now.AddDays(-1));
            seeded.ScoreOnly = Score(db, account, onlyScore, superseded, now.AddDays(-20));
            seeded.ScoreRated = Score(db, account, rated, superseded, now.AddDays(-20));
            Score(db, account, rated, active, now.AddDays(-1));
            db.UserRatings.Add(new UserRating { Id = Guid.NewGuid(), AccountId = account, TitleId = rated, SubjectType = RatingSubjectType.Movie, Rating = 8, RatedAt = now.AddDays(-3), FirstRatedAt = now.AddDays(-3), SyncedAt = now });
            seeded.ScoreWatched = Score(db, account, watched, superseded, now.AddDays(-20));
            Score(db, account, watched, active, now.AddDays(-1));
            db.ManagedListItems.Add(new ManagedListItem { Id = Guid.NewGuid(), AccountId = account, TitleId = watched, AddedAt = now.AddDays(-30), RemovedAt = now.AddDays(-4), RemovalReason = ListRemovalReason.Watched });
            seeded.ScoreYoung = Score(db, account, young, superseded, now.AddDays(-5));
            Score(db, account, young, active, now.AddDays(-1));
            // An Active artifact's score is never superseded, however old and however outranked.
            seeded.ScoreActiveOld = Score(db, account, activeOld, active, now.AddDays(-20));
            Score(db, account, activeOld, older, now.AddDays(-1));

            // ── RefreshSessions ──
            seeded.SessionExpired = Session(db, account, now.AddDays(-1), "Mozilla/5.0");
            seeded.SessionExpiredNoAgent = Session(db, account, now.AddDays(-1), null);
            seeded.SessionDevLink = Session(db, account, now.AddDays(-1), $"{DevEndpoints.DevLinkSessionMarker} probe");
            seeded.SessionLive = Session(db, account, now.AddDays(10), "Mozilla/5.0");

            await db.SaveChangesAsync();
        }

        await using (var db = fixture.CreateContext())
        {
            var result = await RetentionPurge.RunAsync(db, now, CancellationToken.None);
            Assert.True(result.SyncJobs >= 3 && result.TitleScores >= 1 && result.RefreshSessions >= 2, $"removed {result}");
        }

        await AssertSurvivorsAsync(seeded);

        // Idempotent: a second pass finds nothing more among these rows.
        await using (var db = fixture.CreateContext())
        {
            await RetentionPurge.RunAsync(db, now, CancellationToken.None);
        }

        await AssertSurvivorsAsync(seeded);

        await using (var db = fixture.CreateContext())
        {
            // The queued row would otherwise sit in the runner collection's queue.
            await db.SyncJobs.Where(j => j.AccountId == account).ExecuteDeleteAsync();
        }
    }

    [Fact]
    public void The_kept_QA_session_marker_is_the_one_the_QA_sign_in_writes() =>
        Assert.Equal(DevEndpoints.DevLinkSessionMarker, RetentionPurge.DevLinkSessionMarker);

    private async Task AssertSurvivorsAsync(Seeded seeded)
    {
        await using var db = fixture.CreateContext();
        var jobs = await db.SyncJobs.IgnoreQueryFilters().Select(j => j.Id).ToListAsync();
        Assert.DoesNotContain(seeded.JobOldest, jobs);
        Assert.DoesNotContain(seeded.JobOld, jobs);
        Assert.DoesNotContain(seeded.JobFailedNoCompletion, jobs);
        Assert.Contains(seeded.JobRecent, jobs);
        Assert.Contains(seeded.JobOnlyFailure, jobs);
        Assert.Contains(seeded.JobPendingOld, jobs);

        var scores = await db.TitleScores.IgnoreQueryFilters().Select(s => s.Id).ToListAsync();
        Assert.DoesNotContain(seeded.ScoreReplaced, scores);
        Assert.Contains(seeded.ScoreOnly, scores);
        Assert.Contains(seeded.ScoreRated, scores);
        Assert.Contains(seeded.ScoreWatched, scores);
        Assert.Contains(seeded.ScoreYoung, scores);
        Assert.Contains(seeded.ScoreActiveOld, scores);

        var sessions = await db.RefreshSessions.IgnoreQueryFilters().Select(s => s.Id).ToListAsync();
        Assert.DoesNotContain(seeded.SessionExpired, sessions);
        Assert.DoesNotContain(seeded.SessionExpiredNoAgent, sessions);
        Assert.Contains(seeded.SessionDevLink, sessions);
        Assert.Contains(seeded.SessionLive, sessions);
    }

    private static Guid Job(
        ReelDbContext db, Guid account, JobKind kind, JobStatus status, DateTime? completed, DateTime? heartbeat = null, DateTime? enqueued = null)
    {
        var id = Guid.NewGuid();
        var ended = completed ?? heartbeat ?? enqueued ?? DateTime.UtcNow;
        db.SyncJobs.Add(new SyncJob
        {
            Id = id,
            AccountId = account,
            Kind = kind,
            Status = status,
            EnqueuedAt = enqueued ?? ended.AddMinutes(-5),
            StartedAt = status == JobStatus.Pending ? null : ended.AddMinutes(-4),
            HeartbeatAt = heartbeat,
            CompletedAt = completed,
            AttemptCount = status == JobStatus.Pending ? 0 : 1,
        });
        return id;
    }

    private static Guid Artifact(ReelDbContext db, Guid account, ArtifactStatus status, int version)
    {
        var id = Guid.NewGuid();
        db.ModelArtifacts.Add(new ModelArtifact
        {
            Id = id,
            AccountId = account,
            Version = version,
            Status = status,
            TrainingRunId = Guid.NewGuid(),
            TrainedAt = DateTime.UtcNow.AddDays(-version),
        });
        return id;
    }

    private static Guid Score(ReelDbContext db, Guid account, Guid title, Guid artifact, DateTime scoredAt)
    {
        var id = Guid.NewGuid();
        db.TitleScores.Add(new TitleScore { Id = id, AccountId = account, TitleId = title, ModelArtifactId = artifact, PredictedRating = 7.1m, ScoredAt = scoredAt });
        return id;
    }

    private static Guid Session(ReelDbContext db, Guid account, DateTime expiresAt, string? userAgent)
    {
        var id = Guid.NewGuid();
        db.RefreshSessions.Add(new RefreshSession
        {
            Id = id,
            AccountId = account,
            TokenHash = Guid.NewGuid().ToString("N"),
            CreatedAt = expiresAt.AddDays(-30),
            ExpiresAt = expiresAt,
            UserAgent = userAgent,
        });
        return id;
    }

    private sealed class Seeded
    {
        public Guid JobOldest { get; set; }

        public Guid JobOld { get; set; }

        public Guid JobRecent { get; set; }

        public Guid JobOnlyFailure { get; set; }

        public Guid JobFailedNoCompletion { get; set; }

        public Guid JobPendingOld { get; set; }

        public Guid ScoreReplaced { get; set; }

        public Guid ScoreOnly { get; set; }

        public Guid ScoreRated { get; set; }

        public Guid ScoreWatched { get; set; }

        public Guid ScoreYoung { get; set; }

        public Guid ScoreActiveOld { get; set; }

        public Guid SessionExpired { get; set; }

        public Guid SessionExpiredNoAgent { get; set; }

        public Guid SessionDevLink { get; set; }

        public Guid SessionLive { get; set; }
    }
}
