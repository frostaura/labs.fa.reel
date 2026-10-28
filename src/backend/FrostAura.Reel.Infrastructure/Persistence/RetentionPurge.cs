using FrostAura.Reel.Application.Auth;
using Microsoft.EntityFrameworkCore;

namespace FrostAura.Reel.Infrastructure.Persistence;

/// <summary>What one retention pass removed.</summary>
public sealed record RetentionPurgeResult(int SyncJobs, int TitleScores, int RefreshSessions)
{
    public int Total => SyncJobs + TitleScores + RefreshSessions;
}

/// <summary>
/// The retention rules (E-15, K-15): rows that have stopped being evidence of anything are deleted,
/// and every row a reader still needs is kept, whatever its age.
///
/// <list type="bullet">
/// <item><b>SyncJobs</b> — terminal rows that ended more than <see cref="SyncJobRetention"/> ago.
/// The newest row per (account, kind, status) is kept regardless of age: "when did enrichment last
/// succeed", "has this account ever finished a feed" and the status API's last failure per kind are
/// all read from it, and a purge must never turn "last ran 40 days ago" into "never ran".</item>
/// <item><b>TitleScores</b> — scores from an artifact that is no longer Active, scored more than
/// <see cref="SupersededScoreRetention"/> ago. Kept regardless: the newest score per (account,
/// title), because "what did the model predict before you rated it" reads the newest score older
/// than the event; and every score for a title the account has rated or removed as watched, because
/// the closed-loop accuracy, the calibrator and the retro loop all read "the score before the
/// event" for those titles, and deleting it would silently drop a pair from the track record.</item>
/// <item><b>RefreshSessions</b> — expired sessions. A QA session — one issued by the QA sign-in,
/// or rotated from one (<see cref="DevLinkSessionMarker"/>) — is kept: FD-18 excludes QA traffic from the founder's data
/// by those rows' identity, not by clock.</item>
/// </list>
///
/// <para>Raw SQL in bounded batches, so no single statement holds locks for long and a pass over a
/// large first backlog never blocks the job queue. Every rule is idempotent: the second pass the
/// same day deletes nothing.</para>
/// </summary>
public static class RetentionPurge
{
    public static readonly TimeSpan SyncJobRetention = TimeSpan.FromDays(30);

    public static readonly TimeSpan SupersededScoreRetention = TimeSpan.FromDays(14);

    /// <summary>
    /// The user-agent prefix every QA session carries: <see cref="SessionService.QaSessionMarker"/>,
    /// which the QA sign-in writes and every refresh of a QA session carries forward.
    /// </summary>
    public const string DevLinkSessionMarker = SessionService.QaSessionMarker;

    private const int BatchSize = 5_000;

    public static async Task<RetentionPurgeResult> RunAsync(ReelDbContext db, DateTime now, CancellationToken ct)
    {
        var jobsBefore = now - SyncJobRetention;
        var scoresBefore = now - SupersededScoreRetention;
        var devLink = DevLinkSessionMarker + "%";

        var jobs = await DrainAsync(
            () => db.Database.ExecuteSqlAsync(
                $"""
                 DELETE FROM "SyncJobs" WHERE "Id" IN (
                     SELECT r."Id" FROM (
                         SELECT j."Id",
                                COALESCE(j."CompletedAt", j."HeartbeatAt", j."StartedAt", j."EnqueuedAt") AS "EndedAt",
                                row_number() OVER (
                                    PARTITION BY j."AccountId", j."Kind", j."Status"
                                    ORDER BY COALESCE(j."CompletedAt", j."HeartbeatAt", j."StartedAt", j."EnqueuedAt") DESC, j."Id" DESC) AS "Newest"
                         FROM "SyncJobs" j
                         WHERE j."Status" IN ('Succeeded', 'Failed', 'Cancelled')) r
                     WHERE r."Newest" > 1 AND r."EndedAt" < {jobsBefore}
                     LIMIT {BatchSize})
                 """,
                ct),
            ct);

        var scores = await DrainAsync(
            () => db.Database.ExecuteSqlAsync(
                $"""
                 DELETE FROM "TitleScores" WHERE "Id" IN (
                     SELECT r."Id" FROM (
                         SELECT s."Id", s."AccountId", s."TitleId", s."ScoredAt", s."ModelArtifactId",
                                row_number() OVER (
                                    PARTITION BY s."AccountId", s."TitleId"
                                    ORDER BY s."ScoredAt" DESC, s."Id" DESC) AS "Newest"
                         FROM "TitleScores" s) r
                     WHERE r."Newest" > 1
                       AND r."ScoredAt" < {scoresBefore}
                       AND NOT EXISTS (
                           SELECT 1 FROM "ModelArtifacts" a WHERE a."Id" = r."ModelArtifactId" AND a."Status" = 'Active')
                       AND NOT EXISTS (
                           SELECT 1 FROM "UserRatings" u WHERE u."AccountId" = r."AccountId" AND u."TitleId" = r."TitleId")
                       AND NOT EXISTS (
                           SELECT 1 FROM "ManagedListItems" m
                           WHERE m."AccountId" = r."AccountId" AND m."TitleId" = r."TitleId" AND m."RemovedAt" IS NOT NULL)
                     LIMIT {BatchSize})
                 """,
                ct),
            ct);

        var sessions = await DrainAsync(
            () => db.Database.ExecuteSqlAsync(
                $"""
                 DELETE FROM "RefreshSessions" WHERE "Id" IN (
                     SELECT s."Id" FROM "RefreshSessions" s
                     WHERE s."ExpiresAt" < {now}
                       AND (s."UserAgent" IS NULL OR s."UserAgent" NOT LIKE {devLink})
                     LIMIT {BatchSize})
                 """,
                ct),
            ct);

        return new RetentionPurgeResult(jobs, scores, sessions);
    }

    /// <summary>Repeats a bounded delete until a batch comes back short.</summary>
    private static async Task<int> DrainAsync(Func<Task<int>> batch, CancellationToken ct)
    {
        var total = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var deleted = await batch();
            total += deleted;
            if (deleted < BatchSize)
            {
                return total;
            }
        }
    }
}
