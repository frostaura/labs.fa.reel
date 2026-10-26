using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Domain.Sync;
using Microsoft.EntityFrameworkCore;

namespace FrostAura.Reel.Application.Jobs;

/// <summary>
/// What one follow-through pass found and what it queued. Returned rather than logged so the
/// decision is assertable without a timer, a background host or a log scrape.
/// </summary>
/// <param name="HydrationBacklog">Rows never hydrated.</param>
/// <param name="RefreshDue">Hydrated rows due a metadata refresh (<see cref="Ingestion.MetadataRefreshPolicy.Due"/>).</param>
public sealed record CatalogFollowThrough(
    int HydrationBacklog,
    DailyAllowance HydrationAllowance,
    bool QueuedHydration,
    bool QueuedEnrichment,
    EnrichmentBacklog Enrichment,
    int RefreshDue = 0);

/// <summary>
/// Keeps the two expensive stages behind the seed actually running.
///
/// <para>
/// It exists because neither stage has a scheduler that fits a 35,000-title backlog. Hydration is
/// enqueued only from <c>FullIngest</c>'s onboarding chain and is scoped to ONE ACCOUNT'S library,
/// so a global catalogue row referenced by nobody was never hydrated by anything. Enrichment is
/// enqueued by the weekly growth sweep, a nightly reconcile and a feed build — none of which fire
/// often enough to drain a backlog that only shrinks by a ceiling's worth per day. Left alone, the
/// seed would land 35,000 summary rows that nothing ever completes.
/// </para>
///
/// <para>
/// <b>Drain before you buy.</b> The global hydration job runs at
/// <see cref="EnrichmentScheduler.Priority"/> — the same rung as enrichment and ABOVE the seed's own
/// priority 4 — because finishing supply already acquired is worth more than acquiring more of it.
/// The seed still makes progress: the runner has three general worker slots (plus two reserved) and hydration is bounded by a
/// daily ceiling it reaches long before the day ends.
/// </para>
///
/// <para>
/// <b>Nothing here spends.</b> It enqueues; the handlers consult their own budgets. The one
/// exception is the hydration allowance, which is checked BEFORE enqueueing, because a hydrate job
/// queued with no allowance left is a job that claims a worker slot to do nothing and then queues
/// itself again — the spin this class exists to avoid.
/// </para>
/// </summary>
public static class CatalogFollowThroughScheduler
{
    /// <summary>Catalogue rows with a TMDB id and no metadata — what a global hydrate run works through.</summary>
    public static IQueryable<Domain.Catalog.Title> PendingHydration(IReelDbContext db) =>
        db.Titles.Where(t => t.TmdbId != null && t.LastMetadataRefreshAt == null);

    /// <summary>
    /// Queues a global <see cref="JobKind.HydrateCatalog"/> if there is a backlog, allowance left
    /// today, and none already in flight. <b>Saves</b> when it queued. Returns whether it did.
    /// <para>
    /// The in-flight check is not optional: <c>HydrateCatalog</c>'s partial unique index keys on
    /// <c>(AccountId, Kind)</c>, and Postgres treats the NULL account of a global job as distinct
    /// from every other NULL, so the database will happily accept a hundred duplicates.
    /// </para>
    /// </summary>
    public static async Task<bool> EnqueueHydrationAsync(IReelDbContext db, DateTime now, CancellationToken ct)
    {
        var inFlight = await db.SyncJobs.AnyAsync(
            j => j.Kind == JobKind.HydrateCatalog
                && j.AccountId == null
                && (j.Status == JobStatus.Pending || j.Status == JobStatus.Running), ct);
        if (inFlight)
        {
            return false;
        }

        db.SyncJobs.Add(new SyncJob
        {
            Id = Guid.NewGuid(),
            AccountId = null,
            Kind = JobKind.HydrateCatalog,
            Priority = EnrichmentScheduler.Priority,
            EnqueuedAt = now,
            TriggerReason = "catalogue-follow-through",
        });
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// One pass: measure both backlogs, queue whichever stage has work and headroom, report what it
    /// found. <b>Saves.</b> Safe to call on any cadence — every enqueue it makes is guarded.
    /// </summary>
    public static async Task<CatalogFollowThrough> RunAsync(
        IReelDbContext db,
        CatalogHydrationBudget hydration,
        int extractorVersion,
        DateTime now,
        CancellationToken ct,
        CatalogEnrichmentBudget? enrichmentBudget = null)
    {
        var backlog = await PendingHydration(db).CountAsync(ct);
        // The metadata refresh sweep (E-11) rides the same global job and the same daily ceiling:
        // a refresh is one TMDB call, exactly like a first hydration.
        var refreshDue = await Ingestion.MetadataRefreshPolicy.Due(db, now).CountAsync(ct);
        var allowance = await hydration.AllowanceAsync(now, ct);
        var queuedHydration = (backlog > 0 || refreshDue > 0)
            && allowance.HasHeadroom
            && await EnqueueHydrationAsync(db, now, ct);

        var enrichment = await EnrichmentScheduler.BacklogAsync(db, extractorVersion, ct);
        var queuedEnrichment = false;
        if (enrichment.MissingEmbedding > 0 || enrichment.MissingAttributes > 0 || enrichment.StaleExtractorVersion > 0)
        {
            queuedEnrichment = await EnrichmentScheduler.EnqueueAsync(db, null, now, ct, enrichmentBudget);
            if (queuedEnrichment)
            {
                await db.SaveChangesAsync(ct);
            }
        }

        return new CatalogFollowThrough(backlog, allowance, queuedHydration, queuedEnrichment, enrichment, refreshDue);
    }
}
