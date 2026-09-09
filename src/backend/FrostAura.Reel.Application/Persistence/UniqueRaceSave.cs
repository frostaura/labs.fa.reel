using FrostAura.Reel.Domain.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Npgsql;

namespace FrostAura.Reel.Application.Persistence;

/// <summary>
/// The one database race this codebase provokes deliberately, and how every caller absorbs it.
/// <para>
/// <see cref="Ranking.FeedRefreshCoordinator"/> coalesces rebuilds with a check-then-insert against
/// the PARTIAL unique index on <c>SyncJobs (AccountId, Kind) WHERE Status IN ('Pending','Running')</c>.
/// Check-then-insert is not atomic: two rapid reactions, or a reaction landing while a delta sync
/// enqueues its own rebuild, both read "nothing in flight" and both insert — and Postgres rejects
/// the loser with 23505. The index is doing exactly its job; losing is the normal outcome of the
/// coalescing design, not a failure of the change that triggered it.
/// </para>
/// <para>
/// Before this helper existed the loser's <c>DbUpdateException</c> escaped the reaction endpoints
/// unhandled, rolling back the WHOLE transaction — the rating or reaction, its managed-list row and
/// its Trakt outbox rows — so the user got a 500 and a silently dropped save during precisely the
/// queueing burst that provokes the race. The user's change is the durable thing here; the rebuild
/// enqueue is a convenience that the winning job already covers.
/// </para>
/// </summary>
public static class UniqueRaceSave
{
    /// <summary>The partial unique index that coalesces in-flight jobs (see <c>ReelDbContext</c>).</summary>
    private const string SyncJobInFlightIndex = "IX_SyncJobs_AccountId_Kind";

    private const string SyncJobsTable = "SyncJobs";

    /// <summary>Postgres 23505 = unique_violation, surfaced through EF as a DbUpdateException.</summary>
    public static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };

    /// <summary>
    /// Saves, absorbing the in-flight-job race ONCE: on a 23505 against the SyncJobs index the
    /// pending job insert(s) are detached and the save is retried, so everything else in the
    /// transaction — the reaction, the rating, the managed-list row, the outbox rows — still lands.
    /// Any other unique violation, and a second failure of the same one, propagate untouched: this
    /// swallows a coalescing collision, never a genuine constraint breach.
    /// </summary>
    public static async Task<int> SaveAbsorbingRebuildRaceAsync(this IReelDbContext db, CancellationToken ct)
    {
        try
        {
            return await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsRebuildEnqueueRace(ex))
        {
            // Detach rather than Remove: the row was never inserted, so there is nothing to delete —
            // and a DELETE here would race the job that actually won.
            foreach (var entry in ex.Entries.Where(IsPendingSyncJobInsert).ToList())
            {
                entry.State = EntityState.Detached;
            }

            return await db.SaveChangesAsync(ct);
        }
    }

    private static bool IsRebuildEnqueueRace(DbUpdateException exception) =>
        IsUniqueViolation(exception)
        // EF reports every entry in the failing command batch, so the SyncJob insert must be found
        // among them rather than assumed to be the only one.
        && exception.Entries.Any(IsPendingSyncJobInsert)
        && exception.InnerException is PostgresException { ConstraintName: SyncJobInFlightIndex }
            or PostgresException { TableName: SyncJobsTable };

    private static bool IsPendingSyncJobInsert(EntityEntry entry) =>
        entry is { State: EntityState.Added, Entity: SyncJob };
}
