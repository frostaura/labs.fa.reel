using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Sync;
using Microsoft.EntityFrameworkCore;

namespace FrostAura.Reel.Application.Jobs;

/// <summary>
/// Everything that decides WHEN enrichment runs and HOW FAR BEHIND it is. The handler itself only
/// decides what one run does; this decides that a run happens at all.
///
/// <para>
/// It exists because until 2026-09-09 <see cref="JobKind.EnrichCatalog"/> had exactly one enqueue
/// site in the whole backend — <c>HydrateCatalogJobHandler</c>, reachable only from <c>FullIngest</c>,
/// reachable only from the OAuth link. Enrichment therefore ran <b>once per account, at onboarding,
/// and never again</b>. Every candidate discovered afterwards — by a feed build's discover arms, by
/// the weekly catalogue sweep — was left permanently without a <see cref="TitleEmbedding"/> and
/// without <see cref="TitleAttributes"/>: invisible to the ANN retrieval arm and to rail similarity,
/// with 13 of the 92 feature slots pinned at zero while its training-row counterparts are live. The
/// measured cost on 2026-09-09 was ~1,360 titles with no embedding and ~1,400 with no Done attributes
/// out of 5,319.
/// </para>
///
/// <para>
/// Three schedulers now call in — a build that upserted new candidates, the nightly reconcile, and
/// the weekly catalogue growth sweep — so the enqueue rule, the in-flight guard and the backlog
/// query live here once rather than being re-derived at each site.
/// </para>
/// </summary>
public static class EnrichmentScheduler
{
    /// <summary>
    /// Behind feed builds (priority 1–2) on purpose. Enrichment improves the NEXT build; a user
    /// waiting on a feed must never queue behind a catalogue-wide LLM sweep.
    /// </summary>
    public const int Priority = 3;

    /// <summary>
    /// Adds a Pending <see cref="JobKind.EnrichCatalog"/> unless one is already Pending or Running
    /// for the same owner, and returns whether it added one. <b>Does not save</b> — the caller owns
    /// the transaction, and every caller commits through an absorber because check-then-insert
    /// against the partial unique index on <c>SyncJobs (AccountId, Kind) WHERE Status IN
    /// ('Pending','Running')</c> is not atomic.
    /// <para>
    /// <paramref name="accountId"/> is null for a GLOBAL sweep — catalogue rows are tenant-shared,
    /// so work discovered by the weekly growth pass belongs to no account. Postgres treats NULL
    /// index keys as distinct, so the partial index does <b>not</b> dedup global jobs; for those the
    /// check below is the only dedup there is, exactly as the index's own comment states.
    /// </para>
    /// <para>
    /// <b>Nothing is queued while the day's ceiling is spent.</b> With a <paramref name="budget"/>,
    /// a run is queued only if it could buy something today: extraction headroom, or embedding
    /// headroom with a title still missing its vector. Without the check the follow-through pass
    /// queued a global run every 15 minutes after the ceiling was hit, each one opening only to log
    /// "paused for today's token ceiling" (E-15). Callers that do not pass a budget keep the old
    /// behaviour; every caller should pass one.
    /// </para>
    /// </summary>
    public static async Task<bool> EnqueueAsync(
        IReelDbContext db, Guid? accountId, DateTime now, CancellationToken ct, CatalogEnrichmentBudget? budget = null)
    {
        if (budget is not null && !await CanSpendTodayAsync(db, budget, now, ct))
        {
            return false;
        }

        var inFlight = db.SyncJobs.Where(j =>
            j.Kind == JobKind.EnrichCatalog
            && (j.Status == JobStatus.Pending || j.Status == JobStatus.Running));

        // Branched rather than written as `j.AccountId == accountId`: the null case must compile to
        // IS NULL, and relying on the provider's null-semantics rewrite for a captured null is the
        // kind of implicit that silently enqueues a duplicate global job every week.
        var already = accountId is { } id
            ? await inFlight.AnyAsync(j => j.AccountId == id, ct)
            : await inFlight.AnyAsync(j => j.AccountId == null, ct);
        if (already)
        {
            return false;
        }

        db.SyncJobs.Add(new SyncJob
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            Kind = JobKind.EnrichCatalog,
            Priority = Priority,
            EnqueuedAt = now,
        });
        return true;
    }

    /// <summary>
    /// Whether an enrich run queued now could spend anything today: extraction has headroom, or
    /// embedding has headroom and some hydrated title still lacks a vector.
    /// </summary>
    public static async Task<bool> CanSpendTodayAsync(
        IReelDbContext db, CatalogEnrichmentBudget budget, DateTime now, CancellationToken ct)
    {
        if ((await budget.ExtractAllowanceAsync(now, ct)).HasHeadroom)
        {
            return true;
        }

        return (await budget.EmbedAllowanceAsync(now, ct)).HasHeadroom
            && await db.Titles.AnyAsync(
                t => t.LastMetadataRefreshAt != null && !db.TitleEmbeddings.Any(e => e.TitleId == t.Id), ct);
    }

    /// <summary>
    /// The <c>ExtractorVersion</c> invalidation sweep — the attribute-side analogue of the
    /// <c>FeatureSchema</c> artifact sweep. Rows written by an older prompt generation are put back
    /// to Pending with a fresh attempt budget so the handler re-extracts them.
    /// <para>
    /// Capped at <paramref name="cap"/> per pass <b>deliberately</b>: a version bump invalidates the
    /// whole stored catalogue at once, and an uncapped requeue would hand the next run a bill for
    /// every title in the database in one night. At the default cap the backlog drains over days.
    /// The <c>(Status, AttemptCount)</c> predicate makes each row self-terminating — a row already
    /// reset is not re-written on the next pass, so the sweep converges instead of churning.
    /// </para>
    /// Returns how many rows were reset. <b>Does not save.</b>
    /// </summary>
    public static async Task<int> RequeueStaleExtractorVersionsAsync(
        IReelDbContext db, int currentVersion, int cap, CancellationToken ct)
    {
        var stale = await db.TitleAttributes
            .Where(a => a.ExtractorVersion < currentVersion
                && (a.Status != AttributeExtractionStatus.Pending || a.AttemptCount != 0))
            .OrderBy(a => a.TitleId)
            .Take(cap)
            .ToListAsync(ct);

        foreach (var row in stale)
        {
            row.Status = AttributeExtractionStatus.Pending;
            row.AttemptCount = 0;
        }

        return stale.Count;
    }

    /// <summary>
    /// How far behind enrichment is, right now. Global by construction — catalogue rows are
    /// tenant-shared, so "how many titles lack an embedding" is one number for the whole system and
    /// not a per-account one.
    /// <para>
    /// Scoped to HYDRATED titles because that is the sweep's own scope: an unhydrated row has no
    /// overview to embed or reason over, so counting it as a gap would report a backlog that no
    /// amount of enrichment could ever close.
    /// </para>
    /// </summary>
    public static async Task<EnrichmentBacklog> BacklogAsync(
        IReelDbContext db, int currentVersion, CancellationToken ct)
    {
        var hydrated = db.Titles.Where(t => t.LastMetadataRefreshAt != null);

        return new EnrichmentBacklog(
            await hydrated.CountAsync(t => !db.TitleEmbeddings.Any(e => e.TitleId == t.Id), ct),
            await hydrated.CountAsync(t => !db.TitleAttributes.Any(a =>
                a.TitleId == t.Id && a.Status == AttributeExtractionStatus.Done), ct),
            await db.TitleAttributes.CountAsync(a => a.ExtractorVersion < currentVersion, ct),
            // Both account-scoped and global runs count: the SyncJob query filter admits null-account
            // rows, and "when did enrichment last finish" is a property of the catalogue, not a tenant.
            await db.SyncJobs
                .Where(j => j.Kind == JobKind.EnrichCatalog && j.Status == JobStatus.Succeeded)
                .MaxAsync(j => (DateTime?)j.CompletedAt, ct));
    }
}

/// <summary>
/// The enrichment gap, as four numbers. <paramref name="StaleExtractorVersion"/> counts rows whose
/// stored values predate the running prompt generation — they are present and Done, and still wrong.
/// </summary>
public record EnrichmentBacklog(
    int MissingEmbedding,
    int MissingAttributes,
    int StaleExtractorVersion,
    DateTime? LastRunAt);
