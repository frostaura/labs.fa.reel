using System.Text.Json;
using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Domain.Sync;
using Microsoft.EntityFrameworkCore;

namespace FrostAura.Reel.Application.Jobs;

/// <summary>
/// The seed's resume checkpoint, persisted to <see cref="SyncJob.CursorJson"/> after EVERY page.
///
/// <para>
/// <paramref name="Media"/> and <paramref name="Year"/> name the partition rather than an index
/// into the plan, because the plan is rebuilt from the clock on each run and gains a year at the
/// front when the calendar turns; a stored index would then point one partition off, silently.
/// <paramref name="TitlesAdded"/> and <paramref name="PagesFetched"/> are cumulative across the
/// WHOLE plan, not per run — they are carried into each follow-on job, and they are what
/// <c>SEED_MAX_TITLES</c> is enforced against and what the progress endpoint reports.
/// </para>
/// </summary>
public sealed record CatalogSeedCursor(
    string Media, int Year, int Page, int TitlesAdded, int PagesFetched, bool Done)
{
    /// <summary>The first partition of a plan built for <paramref name="now"/>, page 1, nothing spent.</summary>
    public static CatalogSeedCursor Start(DateTime now)
    {
        var first = CatalogSeedPlan.Build(now)[0];
        return new CatalogSeedCursor(first.Media, first.Year, 1, 0, 0, false);
    }

    public static CatalogSeedCursor? Read(string? cursorJson) =>
        string.IsNullOrWhiteSpace(cursorJson) ? null : JsonSerializer.Deserialize<CatalogSeedCursor>(cursorJson);

    public string Write() => JsonSerializer.Serialize(this);
}

/// <summary>
/// Enqueue and read-back for <see cref="JobKind.SeedCatalog"/>.
///
/// <para>
/// <b>Idempotency is this class's whole job, and it cannot be delegated to the database.</b> The
/// partial unique index on <c>SyncJobs (AccountId, Kind) WHERE Status IN ('Pending','Running')</c>
/// does not dedup global jobs — Postgres treats NULL index keys as distinct — so for a null-account
/// kind the explicit in-flight check here is the only thing standing between a founder who clicks
/// twice and two concurrent walks writing the same rows.
/// </para>
/// </summary>
public static class CatalogSeedScheduler
{
    /// <summary>
    /// Below everything. Feed builds are 1–2, enrichment is 3; supply acquisition is 4 because
    /// nothing waits on it — not a user, not a model, not another job. A seed that never gets a
    /// worker slot while the system is busy is behaving correctly.
    /// </summary>
    public const short Priority = 4;

    /// <summary>
    /// Enqueues a seed run, or returns the one already in flight. <b>Saves.</b>
    /// <para>
    /// Resume is not a second code path: an interrupted plan leaves its cursor on a job that
    /// re-enqueued a successor, so "start" and "resume" are the same call. Where the caller holds a
    /// cursor from a completed-but-unfinished run it passes it in <paramref name="resumeFrom"/> and
    /// the new job continues from there instead of from the top of the plan.
    /// </para>
    /// </summary>
    public static async Task<(bool Queued, SyncJob Job)> EnqueueAsync(
        IReelDbContext db, DateTime now, CancellationToken ct, CatalogSeedCursor? resumeFrom = null)
    {
        var inFlight = await db.SyncJobs
            .Where(j => j.Kind == JobKind.SeedCatalog
                && j.AccountId == null
                && (j.Status == JobStatus.Pending || j.Status == JobStatus.Running))
            .OrderBy(j => j.EnqueuedAt)
            .FirstOrDefaultAsync(ct);
        if (inFlight is not null)
        {
            return (false, inFlight);
        }

        var job = NewJob(now, resumeFrom ?? await LastCursorAsync(db, ct));
        db.SyncJobs.Add(job);
        await db.SaveChangesAsync(ct);
        return (true, job);
    }

    /// <summary>
    /// The follow-on job a run queues for itself while the plan is unfinished. <b>Does not save.</b>
    /// <para>
    /// Deliberately NOT routed through <see cref="EnqueueAsync"/>: the caller is itself Running, so
    /// the in-flight check would see its own row and refuse. The guard that matters here is that no
    /// OTHER seed job is in flight, which the caller establishes by passing its own id.
    /// </para>
    /// </summary>
    public static async Task<bool> ContinueAsync(
        IReelDbContext db, Guid currentJobId, CatalogSeedCursor cursor, DateTime now, CancellationToken ct)
    {
        var other = await db.SyncJobs.AnyAsync(
            j => j.Kind == JobKind.SeedCatalog
                && j.AccountId == null
                && j.Id != currentJobId
                && (j.Status == JobStatus.Pending || j.Status == JobStatus.Running), ct);
        if (other)
        {
            return false;
        }

        db.SyncJobs.Add(NewJob(now, cursor));
        return true;
    }

    /// <summary>The most recently enqueued seed job, in flight or finished — what the progress endpoint reads.</summary>
    public static Task<SyncJob?> LatestAsync(IReelDbContext db, CancellationToken ct) =>
        db.SyncJobs
            .Where(j => j.Kind == JobKind.SeedCatalog && j.AccountId == null)
            .OrderByDescending(j => j.EnqueuedAt)
            .FirstOrDefaultAsync(ct);

    /// <summary>
    /// The furthest cursor on record, so a fresh enqueue after a failed run resumes rather than
    /// restarting the plan. Reads the latest job that HAS a cursor — a job that failed on its very
    /// first page has none, and falling back past it to an older, further one is correct.
    /// </summary>
    public static async Task<CatalogSeedCursor?> LastCursorAsync(IReelDbContext db, CancellationToken ct)
    {
        var cursors = await db.SyncJobs
            .Where(j => j.Kind == JobKind.SeedCatalog && j.AccountId == null && j.CursorJson != null)
            .OrderByDescending(j => j.EnqueuedAt)
            .Select(j => j.CursorJson)
            .Take(1)
            .ToListAsync(ct);
        return cursors.Count == 0 ? null : CatalogSeedCursor.Read(cursors[0]);
    }

    private static SyncJob NewJob(DateTime now, CatalogSeedCursor? cursor) => new()
    {
        Id = Guid.NewGuid(),
        AccountId = null,
        Kind = JobKind.SeedCatalog,
        Priority = Priority,
        EnqueuedAt = now,
        TriggerReason = cursor is null ? "seed-catalog" : "seed-catalog-resume",
        CursorJson = cursor?.Write(),
    };
}
