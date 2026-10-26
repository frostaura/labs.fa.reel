using FrostAura.Reel.Application.Jobs;
using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Domain.Library;
using FrostAura.Reel.Domain.Ml;
using FrostAura.Reel.Domain.Sync;
using FrostAura.Reel.Domain.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace FrostAura.Reel.Application.Pipeline;

/// <summary>
/// Builds the v2 <c>/api/sync/status</c> payload (<see cref="SyncStatusResponse"/>). Lives in the
/// application layer so the rules — which job owns the header, when progress is honest, what
/// counts as an unrecovered failure, why an account is degraded — are testable against a real
/// database without HTTP, and so other readers (auth/me's stage) can share them.
///
/// <para>Expects a context pinned to <paramref name="account"/>'s tenancy scope, as every request
/// behind <c>RequireAccount</c> is; every query also filters by account explicitly.</para>
/// </summary>
public sealed class SyncStatusQuery(IReelDbContext db, IProviderHealthReader providerHealth, IOperatorAlertReader? alerts = null)
{
    /// <summary>How far back an unrecovered failure is still reported.</summary>
    public static readonly TimeSpan FailureWindow = TimeSpan.FromDays(7);

    /// <summary>The v1 history depth, kept for <c>recentJobs</c>.</summary>
    private const int RecentJobCount = 6;

    /// <summary>Most failed rows read per poll; a kind failing faster than this is reported as at least this many.</summary>
    private const int FailureRowCap = 200;

    public async Task<SyncStatusResponse> GetAsync(Account account, int retrainThreshold, DateTime now, CancellationToken ct)
    {
        var accountId = account.Id;
        var storedStage = account.PipelineStage;

        var connection = await db.TraktConnections.AsNoTracking()
            .FirstOrDefaultAsync(c => c.AccountId == accountId, ct);
        // No connection row is the same fact as a revoked one: nothing can sync (v1 semantics).
        var connectionStatus = connection?.Status ?? ConnectionStatus.Revoked;

        var inFlight = await db.SyncJobs.AsNoTracking()
            .Where(j => j.AccountId == accountId && (j.Status == JobStatus.Pending || j.Status == JobStatus.Running))
            .ToListAsync(ct);
        var recent = await db.SyncJobs.AsNoTracking()
            .Where(j => j.AccountId == accountId)
            .OrderByDescending(j => j.EnqueuedAt)
            .Take(RecentJobCount)
            .ToListAsync(ct);

        var outboxPending = await db.TraktOutbox.CountAsync(
            o => o.AccountId == accountId && (o.Status == OutboxStatus.Pending || o.Status == OutboxStatus.Failed), ct);
        var outboxDead = await db.TraktOutbox.CountAsync(
            o => o.AccountId == accountId && o.Status == OutboxStatus.DeadLetter, ct);
        var lastListSyncAt = await db.TraktOutbox
            .Where(o => o.AccountId == accountId && o.Status == OutboxStatus.Sent && o.SentAt != null)
            .MaxAsync(o => (DateTime?)o.SentAt, ct);
        var outboxLastError = await db.TraktOutbox
            .Where(o => o.AccountId == accountId
                && (o.Status == OutboxStatus.Failed || o.Status == OutboxStatus.DeadLetter) && o.Error != null)
            .OrderByDescending(o => o.NextAttemptAt)
            .Select(o => o.Error)
            .FirstOrDefaultAsync(ct);

        var trainedAt = await db.ModelArtifacts
            .Where(a => a.AccountId == accountId && a.Status == ArtifactStatus.Active)
            .Select(a => (DateTime?)a.TrainedAt)
            .FirstOrDefaultAsync(ct);
        var newRatingsSinceTrain = trainedAt is { } since
            ? await db.UserRatings.CountAsync(r => r.AccountId == accountId && r.FirstRatedAt > since, ct)
            : 0;

        var (effectiveStage, floor, unrecognised) = await StageAsync(accountId, storedStage, connection, ct);
        var health = providerHealth.Current();
        var failures = await LastFailuresAsync(accountId, storedStage, now, ct);

        var active = inFlight
            .Where(j => !IsDead(j, now))
            .Select(j => (Job: j, Visibility: JobVisibilityPolicy.Classify(j.Kind, j.TriggerReason, storedStage)))
            .Where(x => x.Visibility != JobVisibility.Background)
            .OrderBy(x => x.Job.Status == JobStatus.Running ? 0 : 1)
            .ThenBy(x => x.Visibility == JobVisibility.UserVisible ? 0 : 1)
            .ThenByDescending(x => x.Job.EnqueuedAt)
            .Select(x => Summarise(x.Job, storedStage))
            .FirstOrDefault();

        return new SyncStatusResponse(
            ContractVersion: SyncStatusResponse.CurrentContractVersion,
            PipelineStage: effectiveStage,
            Retrain: new RetrainProgress(retrainThreshold, newRatingsSinceTrain, trainedAt is not null, trainedAt),
            ConnectionStatus: connectionStatus,
            LastDeltaSyncAt: connection?.LastDeltaSyncAt,
            LastFullReconcileAt: connection?.LastFullReconcileAt,
            ActiveJob: active,
            RecentJobs: recent.Select(j => Summarise(j, storedStage)).ToList(),
            OutboxPending: outboxPending,
            OutboxDeadLetters: outboxDead,
            LastListSyncAt: lastListSyncAt,
            OutboxLastError: outboxLastError,
            LastFailures: failures,
            ProviderHealth: health,
            DegradedReason: ResolveDegradedReason(connectionStatus, outboxDead, effectiveStage, health, unrecognised),
            NotEnoughRatings: floor,
            Alerts: OperatorAlertsFor(account));
    }

    /// <summary>
    /// The stage <c>/api/sync/status</c> reports for <paramref name="account"/> — the one every other
    /// reader of "where is this account" must print (auth/me, auth/callback, dev/link; WS-8 CR-4), so
    /// the shell and the status pill cannot disagree. It is <see cref="GetAsync"/>'s own reading, not
    /// a copy: the connection read, the unrecognised-state check (a stage a newer image wrote reads
    /// as the stored fallback, never as a cold-start park) and the ratings-floor reading.
    /// </summary>
    public async Task<PipelineStage> EffectiveStageAsync(Account account, CancellationToken ct)
    {
        var connection = await db.TraktConnections.AsNoTracking()
            .FirstOrDefaultAsync(c => c.AccountId == account.Id, ct);
        var (stage, _, _) = await StageAsync(account.Id, account.PipelineStage, connection, ct);
        return stage;
    }

    /// <summary>The effective stage, its ratings floor when it is the cold-start park, and whether the stored state is unrecognised.</summary>
    private async Task<(PipelineStage Stage, RatingsFloor? Floor, bool Unrecognised)> StageAsync(
        Guid accountId, PipelineStage storedStage, TraktConnection? connection, CancellationToken ct)
    {
        if (await HoldsUnrecognisedStateAsync(accountId, storedStage, connection, ct))
        {
            return (storedStage, null, true);
        }

        // No connection row is the same fact as a revoked one: nothing can sync (v1 semantics).
        var (stage, floor) = await ResolveStageAsync(
            accountId, storedStage, connection?.Status ?? ConnectionStatus.Revoked, ct);
        return (stage, floor, false);
    }

    /// <summary>Operator alerts are the operator's: Founder tier sees them, every other tier an empty list.</summary>
    private IReadOnlyList<OperatorAlert> OperatorAlertsFor(Account account) =>
        account.Tier == AccountTier.Founder && alerts is not null ? alerts.Current() : [];

    /// <summary>
    /// The stage the account is really in. A stored <see cref="PipelineStage.Degraded"/> on a
    /// healthy connection with fewer usable ratings than the trainer's floor is the cold-start park,
    /// not a failure: the Train handler parked it as Degraded before
    /// <see cref="PipelineStage.NotEnoughRatings"/> existed, and still may until it writes the new
    /// stage itself. Reading it here means every reader agrees on one answer either way.
    /// </summary>
    public async Task<(PipelineStage Stage, RatingsFloor? Floor)> ResolveStageAsync(
        Guid accountId, PipelineStage stored, ConnectionStatus connectionStatus, CancellationToken ct)
    {
        var candidate = stored == PipelineStage.NotEnoughRatings
            || (stored == PipelineStage.Degraded && connectionStatus == ConnectionStatus.Active);
        if (!candidate)
        {
            return (stored, null);
        }

        // The same population the trainer labels: one row per rated movie or show.
        var usable = await db.UserRatings
            .Where(r => r.AccountId == accountId
                && (r.SubjectType == RatingSubjectType.Movie || r.SubjectType == RatingSubjectType.Show))
            .Select(r => r.TitleId)
            .Distinct()
            .CountAsync(ct);

        if (usable < TrainJobHandler.MinTrainingRows)
        {
            return (PipelineStage.NotEnoughRatings, new RatingsFloor(usable, TrainJobHandler.MinTrainingRows));
        }

        // Enough ratings now: a stored NotEnoughRatings is waiting for its Train, not stuck.
        return (stored, null);
    }

    /// <summary>
    /// Whether the stage or the connection status the account was read with is a fallback for a
    /// value this build does not know (written by a newer image, then rolled back). Those read as
    /// <see cref="PipelineStage.Degraded"/> and <see cref="ConnectionStatus.Revoked"/>: inert, but
    /// on their own they would tell a user with a healthy link to reconnect, or report a failure that
    /// never happened. Only those two readings are checked against the stored text, so a healthy
    /// account costs nothing extra.
    /// </summary>
    private async Task<bool> HoldsUnrecognisedStateAsync(
        Guid accountId, PipelineStage storedStage, TraktConnection? connection, CancellationToken ct)
    {
        if (storedStage == PipelineStage.Degraded)
        {
            var stored = await db.Database
                .SqlQuery<string>($"""SELECT "PipelineStage" AS "Value" FROM "Accounts" WHERE "Id" = {accountId}""")
                .FirstOrDefaultAsync(ct);
            if (stored is not null && !Enum.GetNames<PipelineStage>().Contains(stored, StringComparer.Ordinal))
            {
                return true;
            }
        }

        if (connection?.Status == ConnectionStatus.Revoked)
        {
            var stored = await db.Database
                .SqlQuery<string>($"""SELECT "Status" AS "Value" FROM "TraktConnections" WHERE "Id" = {connection.Id}""")
                .FirstOrDefaultAsync(ct);
            if (stored is not null && !Enum.GetNames<ConnectionStatus>().Contains(stored, StringComparer.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The single reason the account is not healthy, most actionable first; null when it is.</summary>
    /// <param name="connectionStatus">The connection as read (an unreadable one reads as Revoked).</param>
    /// <param name="outboxDeadLetters">Dead-lettered Trakt writes.</param>
    /// <param name="effectiveStage">The stage after the cold-start reading.</param>
    /// <param name="health">Provider health.</param>
    /// <param name="unrecognisedState">
    /// The stage or connection was written by a newer image. Checked first, because every reason
    /// below would be read off a fallback and tell the user something false.
    /// </param>
    /// <returns>The reason, or null when the account is healthy.</returns>
    public static DegradedReason? ResolveDegradedReason(
        ConnectionStatus connectionStatus,
        int outboxDeadLetters,
        PipelineStage effectiveStage,
        ProviderHealthSnapshot health,
        bool unrecognisedState = false)
    {
        if (unrecognisedState)
        {
            return DegradedReason.UnrecognisedState;
        }

        if (connectionStatus == ConnectionStatus.Revoked)
        {
            return DegradedReason.TraktRevoked;
        }

        if (connectionStatus == ConnectionStatus.RefreshFailed)
        {
            return DegradedReason.TraktRefreshFailed;
        }

        if (outboxDeadLetters > 0)
        {
            return DegradedReason.TraktWritesDeadLettered;
        }

        if (effectiveStage == PipelineStage.NotEnoughRatings)
        {
            return DegradedReason.NotEnoughRatings;
        }

        if (health.State == ProviderHealthState.Down)
        {
            return DegradedReason.ProviderDown;
        }

        return effectiveStage == PipelineStage.Degraded ? DegradedReason.PipelineFailed : null;
    }

    /// <summary>
    /// For each kind whose latest terminal run in the window failed: that failure, and how many
    /// failures have piled up since the kind last succeeded. Ordered most recent first.
    /// </summary>
    private async Task<IReadOnlyList<JobFailure>> LastFailuresAsync(
        Guid accountId, PipelineStage storedStage, DateTime now, CancellationToken ct)
    {
        var windowStart = now - FailureWindow;

        // CompletedAt is set on every success, so a success's end is exact. Failed rows written
        // before the runner fix carry a NULL CompletedAt; their end is the last sign of life.
        var lastSuccess = await db.SyncJobs.AsNoTracking()
            .Where(j => j.AccountId == accountId && j.Status == JobStatus.Succeeded && j.EnqueuedAt >= windowStart)
            .GroupBy(j => j.Kind)
            .Select(g => new { Kind = g.Key, At = g.Max(j => j.CompletedAt ?? j.HeartbeatAt ?? j.StartedAt ?? j.EnqueuedAt) })
            .ToDictionaryAsync(x => x.Kind, x => x.At, ct);

        var failed = await db.SyncJobs.AsNoTracking()
            .Where(j => j.AccountId == accountId && j.Status == JobStatus.Failed && j.EnqueuedAt >= windowStart)
            .OrderByDescending(j => j.EnqueuedAt)
            .Take(FailureRowCap)
            .Select(j => new
            {
                j.Kind,
                End = j.CompletedAt ?? j.HeartbeatAt ?? j.StartedAt ?? j.EnqueuedAt,
                j.AttemptCount,
                j.Error,
            })
            .ToListAsync(ct);

        return failed
            .Where(f => !lastSuccess.TryGetValue(f.Kind, out var succeededAt) || f.End > succeededAt)
            .GroupBy(f => f.Kind)
            .Select(g =>
            {
                var latest = g.OrderByDescending(f => f.End).First();
                return new JobFailure(
                    latest.Kind,
                    JobVisibilityPolicy.SseKind(latest.Kind),
                    JobVisibilityPolicy.Classify(latest.Kind, null, storedStage),
                    latest.End,
                    g.Count(),
                    latest.AttemptCount,
                    JobFailureReason.Classify(latest.Error));
            })
            .OrderByDescending(f => f.FailedAt)
            .ToList();
    }

    /// <summary>A Running row whose heartbeat went stale is a dead attempt awaiting reclaim, not live work.</summary>
    private static bool IsDead(SyncJob job, DateTime now) =>
        job.Status == JobStatus.Running && job.HeartbeatAt is { } beat && beat < now - JobRunnerLimits.StaleAfter;

    public static SyncJobSummary Summarise(SyncJob job, PipelineStage storedStage)
    {
        var visibility = JobVisibilityPolicy.Classify(job.Kind, job.TriggerReason, storedStage);
        var inFlight = job.Status is JobStatus.Pending or JobStatus.Running;

        // An attempt has already failed when a queued job has been claimed before, or a running one
        // is past its first claim. The runner keeps the last attempt's error on the row until it
        // succeeds, which is what the reason code reads.
        var retrying = inFlight
            && ((job.Status == JobStatus.Pending && job.AttemptCount > 0) || job.AttemptCount > 1);

        // Honest progress only: nothing for a queued job (first time or retry), and never a
        // denominator for the named pass. A retry's inherited percentage is the H-1 lie.
        // A retry that somehow still holds a finished attempt's 100 (a row claimed before the
        // runner cleared progress on claim) reports nothing rather than a finished job.
        var showProgress = visibility != JobVisibility.NamedPass
            && job.Status != JobStatus.Pending
            && !(retrying && job.ProgressPct >= 100);

        return new SyncJobSummary(
            Id: job.Id,
            Kind: job.Kind,
            SseKind: JobVisibilityPolicy.SseKind(job.Kind),
            Visibility: visibility,
            Status: job.Status,
            ProgressPct: showProgress ? job.ProgressPct : null,
            ProgressMessage: job.Status == JobStatus.Pending ? null : job.ProgressMessage,
            EnqueuedAt: job.EnqueuedAt,
            StartedAt: job.StartedAt,
            CompletedAt: job.CompletedAt,
            Attempt: job.AttemptCount,
            MaxAttempts: JobRunnerLimits.MaxAttempts,
            Retrying: retrying,
            RetryReasonCode: retrying ? JobFailureReason.Classify(job.Error) : null);
    }
}
