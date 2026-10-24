using System.Net;
using System.Text.Json;
using FrostAura.Reel.Application.Jobs;
using FrostAura.Reel.Application.Pipeline;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Library;
using FrostAura.Reel.Domain.Sync;
using FrostAura.Reel.Domain.Tenancy;
using FrostAura.Reel.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FrostAura.Reel.Tests.IntegrationTests;

/// <summary>
/// <c>GET /api/sync/status</c>, contract v2, read over HTTP exactly as the header pill reads it.
///
/// <para>Each test seeds its own account and asserts on the wire JSON (property names and string
/// enums), because the JSON is the contract the frontend and the generated types are built on — a
/// test against the C# records would pass through a rename the client never sees.</para>
///
/// <para>The namespace is load-bearing: CI filters the integration job on
/// <c>FullyQualifiedName~IntegrationTests</c>.</para>
/// </summary>
[Collection("postgres")]
public class SyncStatusContractTests(PostgresFixture fixture, ReelApiFactory api) : IClassFixture<ReelApiFactory>
{
    /// <summary>Every v1 field, by its v1 name — v2 is additive.</summary>
    private static readonly string[] V1Fields =
    [
        "pipelineStage", "retrain", "connectionStatus", "lastDeltaSyncAt", "lastFullReconcileAt", "activeJob",
        "recentJobs", "outboxPending", "outboxDeadLetters", "lastListSyncAt", "outboxLastError",
    ];

    private static readonly string[] V2Fields =
        ["contractVersion", "lastFailures", "providerHealth", "degradedReason", "notEnoughRatings"];

    private static readonly string[] JobSummaryFields =
    [
        "id", "kind", "sseKind", "visibility", "status", "progressPct", "progressMessage", "enqueuedAt", "startedAt",
        "completedAt", "attempt", "maxAttempts", "retrying", "retryReasonCode",
    ];

    [Fact]
    public async Task The_payload_keeps_every_v1_field_and_adds_the_v2_fields_with_string_enums()
    {
        if (!fixture.Available)
        {
            return;
        }

        var (accountId, client) = await api.CreateSignedInAccountAsync($"status-shape-{Guid.NewGuid():N}");
        using (client)
        {
            await SeedConnectionAsync(accountId, ConnectionStatus.Active);
            await AddJobAsync(accountId, JobKind.FullIngest, JobStatus.Running, progress: 40, message: "Pulling history");

            var root = await GetStatusAsync(client);

            foreach (var field in V1Fields.Concat(V2Fields))
            {
                Assert.True(root.TryGetProperty(field, out _), $"missing field '{field}'");
            }

            Assert.Equal(2, root.GetProperty("contractVersion").GetInt32());
            Assert.Equal("Linked", root.GetProperty("pipelineStage").GetString());
            Assert.Equal("Active", root.GetProperty("connectionStatus").GetString());
            Assert.Equal(JsonValueKind.Null, root.GetProperty("degradedReason").ValueKind);
            Assert.Equal(JsonValueKind.Null, root.GetProperty("notEnoughRatings").ValueKind);

            var active = root.GetProperty("activeJob");
            foreach (var field in JobSummaryFields)
            {
                Assert.True(active.TryGetProperty(field, out _), $"activeJob missing field '{field}'");
            }

            Assert.Equal("FullIngest", active.GetProperty("kind").GetString());
            Assert.Equal("ingest", active.GetProperty("sseKind").GetString());
            Assert.Equal("UserVisible", active.GetProperty("visibility").GetString());
            Assert.Equal("Running", active.GetProperty("status").GetString());
            Assert.Equal(40m, active.GetProperty("progressPct").GetDecimal());
            Assert.Equal(3, active.GetProperty("maxAttempts").GetInt32());

            var health = root.GetProperty("providerHealth");
            Assert.Equal("Unmonitored", health.GetProperty("state").GetString());
            Assert.Equal(0, health.GetProperty("breakers").GetArrayLength());
        }
    }

    [Fact]
    public async Task Background_maintenance_never_owns_the_active_job()
    {
        if (!fixture.Available)
        {
            return;
        }

        var (accountId, client) = await api.CreateSignedInAccountAsync($"status-background-{Guid.NewGuid():N}");
        using (client)
        {
            await SeedConnectionAsync(accountId, ConnectionStatus.Active);
            await SetStageAsync(accountId, PipelineStage.FeedReady);

            // The H-1 shape: catalogue enrichment, a background Trakt poll, lens copy and a
            // post-onboarding retrain are all running or queued, and none of them was asked for.
            await AddJobAsync(accountId, JobKind.EnrichCatalog, JobStatus.Running, progress: 63, message: "Reading titles");
            await AddJobAsync(accountId, JobKind.DeltaSync, JobStatus.Running, trigger: "delta-poll");
            await AddJobAsync(accountId, JobKind.PersonaliseTitles, JobStatus.Pending);
            await AddJobAsync(accountId, JobKind.Train, JobStatus.Running, trigger: "nightly-reconcile");
            await AddJobAsync(accountId, JobKind.BuildFeed, JobStatus.Pending, trigger: "delta-sync");

            var root = await GetStatusAsync(client);

            Assert.Equal(JsonValueKind.Null, root.GetProperty("activeJob").ValueKind);
            // They are still listed — just never as the thing the pill reports.
            Assert.Contains(
                root.GetProperty("recentJobs").EnumerateArray(),
                j => j.GetProperty("kind").GetString() == "EnrichCatalog"
                    && j.GetProperty("visibility").GetString() == "Background");
        }
    }

    [Fact]
    public async Task A_user_sync_outranks_the_named_pass_and_the_named_pass_never_carries_a_percentage()
    {
        if (!fixture.Available)
        {
            return;
        }

        var (accountId, client) = await api.CreateSignedInAccountAsync($"status-xref-{Guid.NewGuid():N}");
        using (client)
        {
            await SeedConnectionAsync(accountId, ConnectionStatus.Active);
            await SetStageAsync(accountId, PipelineStage.FeedReady);

            // The exact row lane H sampled: a pass that already said it did nothing, at 100%.
            var xref = await AddJobAsync(
                accountId, JobKind.CrossReference, JobStatus.Running, progress: 100, message: "nothing cross-referenced this run",
                enqueuedAt: DateTime.UtcNow.AddMinutes(-5));

            var root = await GetStatusAsync(client);
            var active = root.GetProperty("activeJob");
            Assert.Equal(xref.ToString(), active.GetProperty("id").GetString());
            Assert.Equal("NamedPass", active.GetProperty("visibility").GetString());
            Assert.Equal("xref", active.GetProperty("sseKind").GetString());
            Assert.Equal(JsonValueKind.Null, active.GetProperty("progressPct").ValueKind);

            // The user presses "sync now": that is what they are watching, so it takes the header.
            using (var now = await client.PostAsync("/api/sync/now", content: null))
            {
                Assert.Equal(HttpStatusCode.Accepted, now.StatusCode);
            }

            var sync = await LatestJobAsync(accountId, JobKind.DeltaSync);
            Assert.Equal(JobVisibilityPolicy.SyncNowReason, sync.TriggerReason);
            await MarkRunningAsync(sync.Id);

            active = (await GetStatusAsync(client)).GetProperty("activeJob");
            Assert.Equal(sync.Id.ToString(), active.GetProperty("id").GetString());
            Assert.Equal("UserVisible", active.GetProperty("visibility").GetString());
        }
    }

    [Fact]
    public async Task A_retrying_job_never_reports_the_failed_attempts_progress()
    {
        if (!fixture.Available)
        {
            return;
        }

        var (accountId, client) = await api.CreateSignedInAccountAsync($"status-retry-{Guid.NewGuid():N}");
        using (client)
        {
            await SeedConnectionAsync(accountId, ConnectionStatus.Active);

            // Queued again after attempt 1 failed, still holding attempt 1's "100%".
            var queued = await AddJobAsync(
                accountId, JobKind.FullIngest, JobStatus.Pending, progress: 100, message: "Pulled everything",
                attempts: 1, error: "Response status code does not indicate success: 429 (Too Many Requests).");

            var active = (await GetStatusAsync(client)).GetProperty("activeJob");
            Assert.Equal(queued.ToString(), active.GetProperty("id").GetString());
            Assert.True(active.GetProperty("retrying").GetBoolean());
            Assert.Equal(JobFailureReason.RateLimited, active.GetProperty("retryReasonCode").GetString());
            Assert.Equal(JsonValueKind.Null, active.GetProperty("progressPct").ValueKind);
            Assert.Equal(JsonValueKind.Null, active.GetProperty("progressMessage").ValueKind);

            // Running attempt 2, on a row claimed before the runner cleared progress on claim.
            await UpdateJobAsync(queued, j =>
            {
                j.Status = JobStatus.Running;
                j.AttemptCount = 2;
                j.HeartbeatAt = DateTime.UtcNow;
            });

            active = (await GetStatusAsync(client)).GetProperty("activeJob");
            Assert.True(active.GetProperty("retrying").GetBoolean());
            Assert.Equal(2, active.GetProperty("attempt").GetInt32());
            Assert.Equal(JsonValueKind.Null, active.GetProperty("progressPct").ValueKind);

            // Once attempt 2 reports its own progress, that progress is shown.
            await UpdateJobAsync(queued, j => j.ProgressPct = 30);
            active = (await GetStatusAsync(client)).GetProperty("activeJob");
            Assert.Equal(30m, active.GetProperty("progressPct").GetDecimal());
        }
    }

    [Fact]
    public async Task A_running_job_whose_heartbeat_went_stale_is_not_live_work()
    {
        if (!fixture.Available)
        {
            return;
        }

        var (accountId, client) = await api.CreateSignedInAccountAsync($"status-stale-{Guid.NewGuid():N}");
        using (client)
        {
            await SeedConnectionAsync(accountId, ConnectionStatus.Active);
            await AddJobAsync(
                accountId, JobKind.FullIngest, JobStatus.Running, progress: 70,
                heartbeatAt: DateTime.UtcNow - JobRunnerLimits.StaleAfter - TimeSpan.FromMinutes(1));

            Assert.Equal(JsonValueKind.Null, (await GetStatusAsync(client)).GetProperty("activeJob").ValueKind);
        }
    }

    [Fact]
    public async Task Repeated_failures_of_a_kind_surface_in_last_failures_until_that_kind_succeeds()
    {
        if (!fixture.Available)
        {
            return;
        }

        var (accountId, client) = await api.CreateSignedInAccountAsync($"status-failures-{Guid.NewGuid():N}");
        using (client)
        {
            await SeedConnectionAsync(accountId, ConnectionStatus.Active);
            await SetStageAsync(accountId, PipelineStage.FeedReady);
            var t0 = DateTime.UtcNow.AddHours(-6);

            // An old success, then three failed passes — two with the NULL CompletedAt the old runner
            // left behind, so "when did it end" has to fall back to the heartbeat.
            await AddJobAsync(accountId, JobKind.CrossReference, JobStatus.Succeeded, enqueuedAt: t0, completedAt: t0.AddMinutes(8));
            await AddJobAsync(accountId, JobKind.CrossReference, JobStatus.Failed, enqueuedAt: t0.AddHours(1),
                heartbeatAt: t0.AddHours(1).AddMinutes(9), attempts: 3, error: "judge answered none of 3 batches");
            await AddJobAsync(accountId, JobKind.CrossReference, JobStatus.Failed, enqueuedAt: t0.AddHours(2),
                heartbeatAt: t0.AddHours(2).AddMinutes(9), attempts: 3, error: "judge answered none of 3 batches");
            await AddJobAsync(accountId, JobKind.CrossReference, JobStatus.Failed, enqueuedAt: t0.AddHours(3),
                completedAt: t0.AddHours(3).AddMinutes(10), attempts: 3,
                error: "Stalled with no heartbeat after 3 attempts.");

            // A different kind failed once and then recovered: it must not be reported.
            await AddJobAsync(accountId, JobKind.EnrichCatalog, JobStatus.Failed, enqueuedAt: t0.AddHours(1),
                completedAt: t0.AddHours(1).AddMinutes(1), attempts: 3, error: "boom");
            await AddJobAsync(accountId, JobKind.EnrichCatalog, JobStatus.Succeeded, enqueuedAt: t0.AddHours(2),
                completedAt: t0.AddHours(2).AddMinutes(1));

            var failures = (await GetStatusAsync(client)).GetProperty("lastFailures").EnumerateArray().ToList();

            var xref = Assert.Single(failures);
            Assert.Equal("CrossReference", xref.GetProperty("kind").GetString());
            Assert.Equal("xref", xref.GetProperty("sseKind").GetString());
            Assert.Equal("NamedPass", xref.GetProperty("visibility").GetString());
            Assert.Equal(3, xref.GetProperty("consecutiveFailures").GetInt32());
            Assert.Equal(3, xref.GetProperty("attempts").GetInt32());
            Assert.Equal(JobFailureReason.Stalled, xref.GetProperty("reasonCode").GetString());
            Assert.False(xref.TryGetProperty("error", out _), "free-text error must never reach the wire");

            // The next pass succeeds: the failure is recovered and drops out within one poll.
            await AddJobAsync(accountId, JobKind.CrossReference, JobStatus.Succeeded, enqueuedAt: t0.AddHours(4),
                completedAt: t0.AddHours(4).AddMinutes(7));
            Assert.Equal(0, (await GetStatusAsync(client)).GetProperty("lastFailures").GetArrayLength());
        }
    }

    [Fact]
    public async Task A_failure_older_than_the_window_is_not_reported()
    {
        if (!fixture.Available)
        {
            return;
        }

        var (accountId, client) = await api.CreateSignedInAccountAsync($"status-window-{Guid.NewGuid():N}");
        using (client)
        {
            await SeedConnectionAsync(accountId, ConnectionStatus.Active);
            var old = DateTime.UtcNow - SyncStatusQuery.FailureWindow - TimeSpan.FromHours(1);
            await AddJobAsync(accountId, JobKind.BuildDigest, JobStatus.Failed, enqueuedAt: old, completedAt: old, attempts: 3);

            Assert.Equal(0, (await GetStatusAsync(client)).GetProperty("lastFailures").GetArrayLength());
        }
    }

    [Theory]
    [InlineData(null, "TraktRevoked")]
    [InlineData(ConnectionStatus.Revoked, "TraktRevoked")]
    [InlineData(ConnectionStatus.RefreshFailed, "TraktRefreshFailed")]
    public async Task A_broken_trakt_link_is_the_degraded_reason_even_while_work_runs(ConnectionStatus? status, string expected)
    {
        if (!fixture.Available)
        {
            return;
        }

        var (accountId, client) = await api.CreateSignedInAccountAsync($"status-link-{Guid.NewGuid():N}");
        using (client)
        {
            if (status is { } s)
            {
                await SeedConnectionAsync(accountId, s);
            }

            await SetStageAsync(accountId, PipelineStage.FeedReady);
            await AddJobAsync(accountId, JobKind.EnrichCatalog, JobStatus.Running, progress: 12);

            var root = await GetStatusAsync(client);
            Assert.Equal(expected, root.GetProperty("degradedReason").GetString());
        }
    }

    [Fact]
    public async Task Dead_lettered_writes_are_the_degraded_reason_on_a_healthy_link()
    {
        if (!fixture.Available)
        {
            return;
        }

        var (accountId, client) = await api.CreateSignedInAccountAsync($"status-dead-{Guid.NewGuid():N}");
        using (client)
        {
            await SeedConnectionAsync(accountId, ConnectionStatus.Active);
            await SetStageAsync(accountId, PipelineStage.FeedReady);
            await WithDbAsync(db =>
            {
                db.TraktOutbox.Add(new TraktOutboxEntry
                {
                    Id = Guid.NewGuid(),
                    AccountId = accountId,
                    Status = OutboxStatus.DeadLetter,
                    EnqueuedAt = DateTime.UtcNow,
                    NextAttemptAt = DateTime.UtcNow,
                    AttemptCount = 8,
                    Error = "422",
                });
                return Task.CompletedTask;
            });

            var root = await GetStatusAsync(client);
            Assert.Equal("TraktWritesDeadLettered", root.GetProperty("degradedReason").GetString());
            Assert.Equal(1, root.GetProperty("outboxDeadLetters").GetInt32());
        }
    }

    [Fact]
    public async Task A_degraded_account_below_the_rating_floor_reads_as_not_enough_ratings_with_the_counts()
    {
        if (!fixture.Available)
        {
            return;
        }

        var (accountId, client) = await api.CreateSignedInAccountAsync($"status-floor-{Guid.NewGuid():N}");
        using (client)
        {
            await SeedConnectionAsync(accountId, ConnectionStatus.Active);
            // The Train handler parks a cold-start account as Degraded today.
            await SetStageAsync(accountId, PipelineStage.Degraded);

            // Four rated movies, plus an episode rating and a second rating of the same show's
            // season — neither of which trains, so the usable count is 4, not 6.
            var show = await SeedTitleAsync(MediaType.Show);
            for (var i = 0; i < 4; i++)
            {
                await SeedRatingAsync(accountId, await SeedTitleAsync(MediaType.Movie), RatingSubjectType.Movie);
            }

            await SeedRatingAsync(accountId, show, RatingSubjectType.Episode, season: 1, episode: 1);
            await SeedRatingAsync(accountId, show, RatingSubjectType.Season, season: 1);

            var root = await GetStatusAsync(client);
            Assert.Equal("NotEnoughRatings", root.GetProperty("pipelineStage").GetString());
            Assert.Equal("NotEnoughRatings", root.GetProperty("degradedReason").GetString());
            var floor = root.GetProperty("notEnoughRatings");
            Assert.Equal(4, floor.GetProperty("usableRatings").GetInt32());
            Assert.Equal(TrainJobHandler.MinTrainingRows, floor.GetProperty("required").GetInt32());

            // Same account, enough ratings: the Degraded stage is a real failure again.
            for (var i = 0; i < TrainJobHandler.MinTrainingRows - 4; i++)
            {
                await SeedRatingAsync(accountId, await SeedTitleAsync(MediaType.Movie), RatingSubjectType.Movie);
            }

            root = await GetStatusAsync(client);
            Assert.Equal("Degraded", root.GetProperty("pipelineStage").GetString());
            Assert.Equal("PipelineFailed", root.GetProperty("degradedReason").GetString());
            Assert.Equal(JsonValueKind.Null, root.GetProperty("notEnoughRatings").ValueKind);
        }
    }

    [Fact]
    public async Task A_stored_not_enough_ratings_stage_reads_back_as_itself()
    {
        if (!fixture.Available)
        {
            return;
        }

        var (accountId, client) = await api.CreateSignedInAccountAsync($"status-stored-floor-{Guid.NewGuid():N}");
        using (client)
        {
            await SeedConnectionAsync(accountId, ConnectionStatus.Active);
            await SetStageAsync(accountId, PipelineStage.NotEnoughRatings);
            await SeedRatingAsync(accountId, await SeedTitleAsync(MediaType.Show), RatingSubjectType.Show);

            var root = await GetStatusAsync(client);
            Assert.Equal("NotEnoughRatings", root.GetProperty("pipelineStage").GetString());
            Assert.Equal(1, root.GetProperty("notEnoughRatings").GetProperty("usableRatings").GetInt32());

            // A Train queued for this account is what it is waiting for, so it is user-visible.
            await AddJobAsync(accountId, JobKind.Train, JobStatus.Pending, trigger: "delta-sync");
            var active = (await GetStatusAsync(client)).GetProperty("activeJob");
            Assert.Equal("Train", active.GetProperty("kind").GetString());
            Assert.Equal("UserVisible", active.GetProperty("visibility").GetString());
        }
    }

    [Fact]
    public async Task A_degraded_account_with_a_revoked_link_is_not_mistaken_for_a_cold_start()
    {
        if (!fixture.Available)
        {
            return;
        }

        var (accountId, client) = await api.CreateSignedInAccountAsync($"status-revoked-floor-{Guid.NewGuid():N}");
        using (client)
        {
            await SeedConnectionAsync(accountId, ConnectionStatus.Revoked);
            await SetStageAsync(accountId, PipelineStage.Degraded);

            var root = await GetStatusAsync(client);
            Assert.Equal("Degraded", root.GetProperty("pipelineStage").GetString());
            Assert.Equal("TraktRevoked", root.GetProperty("degradedReason").GetString());
            Assert.Equal(JsonValueKind.Null, root.GetProperty("notEnoughRatings").ValueKind);
        }
    }

    [Fact]
    public async Task Another_accounts_jobs_never_reach_the_payload()
    {
        if (!fixture.Available)
        {
            return;
        }

        var other = await api.SeedAccountAsync($"status-other-{Guid.NewGuid():N}");
        await AddJobAsync(other, JobKind.FullIngest, JobStatus.Running, progress: 50);
        await AddJobAsync(other, JobKind.Train, JobStatus.Failed, attempts: 3, completedAt: DateTime.UtcNow);

        var (accountId, client) = await api.CreateSignedInAccountAsync($"status-self-{Guid.NewGuid():N}");
        using (client)
        {
            await SeedConnectionAsync(accountId, ConnectionStatus.Active);
            var root = await GetStatusAsync(client);

            Assert.Equal(JsonValueKind.Null, root.GetProperty("activeJob").ValueKind);
            Assert.Equal(0, root.GetProperty("recentJobs").GetArrayLength());
            Assert.Equal(0, root.GetProperty("lastFailures").GetArrayLength());
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────

    private static async Task<JsonElement> GetStatusAsync(HttpClient client)
    {
        using var response = await client.GetAsync("/api/sync/status");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.Clone();
    }

    private async Task WithDbAsync(Func<ReelDbContext, Task> work)
    {
        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ReelDbContext>();
        await work(db);
        await db.SaveChangesAsync();
    }

    private Task SeedConnectionAsync(Guid accountId, ConnectionStatus status) => WithDbAsync(db =>
    {
        db.TraktConnections.Add(new TraktConnection
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            Status = status,
            ExpiresAt = DateTime.UtcNow.AddDays(30),
        });
        return Task.CompletedTask;
    });

    private Task SetStageAsync(Guid accountId, PipelineStage stage) => WithDbAsync(async db =>
    {
        var account = await db.Accounts.FirstAsync(a => a.Id == accountId);
        account.PipelineStage = stage;
        account.PipelineStageChangedAt = DateTime.UtcNow;
    });

    private async Task<Guid> AddJobAsync(
        Guid accountId, JobKind kind, JobStatus status, decimal? progress = null, string? message = null,
        string? trigger = null, int attempts = 0, string? error = null, DateTime? enqueuedAt = null,
        DateTime? completedAt = null, DateTime? heartbeatAt = null)
    {
        var id = Guid.NewGuid();
        var enqueued = enqueuedAt ?? DateTime.UtcNow;
        var started = status == JobStatus.Pending && attempts == 0 ? (DateTime?)null : enqueued;
        await WithDbAsync(db =>
        {
            db.SyncJobs.Add(new SyncJob
            {
                Id = id,
                AccountId = accountId,
                Kind = kind,
                Status = status,
                Priority = 2,
                EnqueuedAt = enqueued,
                StartedAt = started,
                HeartbeatAt = heartbeatAt ?? (status == JobStatus.Running ? DateTime.UtcNow : started),
                CompletedAt = completedAt,
                AttemptCount = status == JobStatus.Running && attempts == 0 ? 1 : attempts,
                ProgressPct = progress,
                ProgressMessage = message,
                TriggerReason = trigger,
                Error = error,
            });
            return Task.CompletedTask;
        });
        return id;
    }

    private Task UpdateJobAsync(Guid jobId, Action<SyncJob> change) => WithDbAsync(async db =>
        change(await db.SyncJobs.FirstAsync(j => j.Id == jobId)));

    private Task MarkRunningAsync(Guid jobId) => UpdateJobAsync(jobId, j =>
    {
        j.Status = JobStatus.Running;
        j.StartedAt = DateTime.UtcNow;
        j.HeartbeatAt = DateTime.UtcNow;
        j.AttemptCount = 1;
    });

    private async Task<SyncJob> LatestJobAsync(Guid accountId, JobKind kind)
    {
        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ReelDbContext>();
        return await db.SyncJobs.AsNoTracking()
            .Where(j => j.AccountId == accountId && j.Kind == kind)
            .OrderByDescending(j => j.EnqueuedAt)
            .FirstAsync();
    }

    private async Task<Guid> SeedTitleAsync(MediaType media)
    {
        var id = Guid.NewGuid();
        await WithDbAsync(db =>
        {
            db.Add(new Title
            {
                Id = id,
                MediaType = media,
                TmdbId = Random.Shared.NextInt64(1, long.MaxValue),
                TraktId = Random.Shared.NextInt64(1, long.MaxValue),
                TraktSlug = string.Empty,
                Name = $"{media} {Guid.NewGuid():N}",
                CreatedAt = DateTime.UtcNow,
            });
            return Task.CompletedTask;
        });
        return id;
    }

    private Task SeedRatingAsync(Guid accountId, Guid titleId, RatingSubjectType subject, int season = 0, int episode = 0) =>
        WithDbAsync(db =>
        {
            var now = DateTime.UtcNow;
            db.UserRatings.Add(new UserRating
            {
                Id = Guid.NewGuid(),
                AccountId = accountId,
                TitleId = titleId,
                SubjectType = subject,
                SeasonNumber = season,
                EpisodeNumber = episode,
                Rating = 7,
                RatedAt = now,
                FirstRatedAt = now,
                SyncedAt = now,
            });
            return Task.CompletedTask;
        });
}
