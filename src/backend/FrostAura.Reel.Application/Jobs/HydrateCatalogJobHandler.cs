using System.Text.Json;
using FrostAura.Reel.Application.Ingestion;
using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Application.Pipeline;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FrostAura.Reel.Application.Jobs;

/// <summary>
/// TMDB hydration for every title the account's library references (delegating to the shared
/// TitleHydrator). Global catalog rows — one hydration serves every tenant. Cursor = last
/// processed title id, so crash-resume is exact. Chains into Train once the library is rich.
///
/// Two properties make this job actually finish its library, both of which it previously lacked:
///   * A failing title is isolated, counted, and retired after <see cref="MaxTitleAttempts"/>
///     attempts, instead of throwing out of the batch before SaveChangesAsync and discarding the
///     work already done for its 24 healthy neighbours.
///   * The cursor is swept more than once. The cursor orders by Guid, so a title that fails, or
///     that a concurrent DeltaSync inserts with a Guid below the cursor, sits *behind* a forward-
///     only walk forever — the job then reports "100%, succeeded" with a large share of the
///     library never hydrated, and because EnrichCatalog scopes to LastMetadataRefreshAt != null,
///     those titles are silently excluded from embeddings and LLM attributes too.
/// </summary>
public class HydrateCatalogJobHandler(
    IReelDbContext db,
    TitleHydrator hydrator,
    IPipelineEventHub events,
    ILogger<HydrateCatalogJobHandler> logger) : IJobHandler
{
    public JobKind Kind => JobKind.HydrateCatalog;

    /// <summary>
    /// Resume checkpoint. <paramref name="Failures"/> maps title id → failed attempts and is the
    /// poison-pill counter: it survives crash-resume and the job runner's own retries, so a title
    /// that is permanently broken at TMDB is retired rather than re-tried until the job dies.
    /// </summary>
    private sealed record Cursor(Guid? LastTitleId, int Pass, Dictionary<string, int>? Failures);

    private const int SaveBatchSize = 25;

    /// <summary>Failed hydrations of one title before it is retired (stamped, so it stops blocking).</summary>
    private const int MaxTitleAttempts = 3;

    /// <summary>Sweeps of the pending set. Must exceed <see cref="MaxTitleAttempts"/> — one attempt per title per pass.</summary>
    private const int MaxPasses = 4;

    /// <summary>Consecutive all-failed batches, before any title has hydrated, that mean "TMDB is down" rather than "this title is bad".</summary>
    private const int OutageBatchThreshold = 2;

    public async Task ExecuteAsync(SyncJob job, CancellationToken ct)
    {
        var accountId = job.AccountId ?? throw new InvalidOperationException("HydrateCatalog requires an account.");
        var cursor = job.CursorJson is null
            ? new Cursor(null, 1, null)
            : JsonSerializer.Deserialize<Cursor>(job.CursorJson) ?? new Cursor(null, 1, null);

        var referenced = db.WatchedTitles.Where(w => w.AccountId == accountId).Select(w => w.TitleId)
            .Union(db.UserRatings.Where(r => r.AccountId == accountId).Select(r => r.TitleId));

        IOrderedQueryable<Title> Pending() => db.Titles
            .Where(t => referenced.Contains(t.Id) && t.TmdbId != null && t.LastMetadataRefreshAt == null)
            .OrderBy(t => t.Id);

        var failures = cursor.Failures ?? [];
        var lastTitleId = cursor.LastTitleId;
        var pass = Math.Max(1, cursor.Pass);

        var total = await Pending().CountAsync(ct);
        var processed = 0;
        var retired = 0;
        var deadBatches = 0;

        while (!ct.IsCancellationRequested && pass <= MaxPasses)
        {
            while (!ct.IsCancellationRequested)
            {
                var batch = await Pending()
                    .Where(t => lastTitleId == null || t.Id > lastTitleId)
                    .Take(SaveBatchSize)
                    .ToListAsync(ct);
                if (batch.Count == 0)
                {
                    break;
                }

                var result = await hydrator.HydrateBatchResilientAsync(batch, ct);
                processed += result.Hydrated;

                foreach (var title in result.Failed)
                {
                    var key = title.Id.ToString();
                    var attempts = failures.GetValueOrDefault(key) + 1;
                    if (attempts < MaxTitleAttempts)
                    {
                        failures[key] = attempts;
                        continue;
                    }

                    // Retire it: stamping is what the 404 path already does for a title TMDB cannot
                    // serve. Leaving it un-stamped would keep it pending forever AND keep it out of
                    // enrichment, which is strictly worse than admitting we have no metadata for it.
                    title.LastMetadataRefreshAt = DateTime.UtcNow;
                    failures.Remove(key);
                    retired++;
                    logger.LogWarning(
                        "Retiring title {TitleId} from hydration after {Attempts} failed TMDB attempts.",
                        title.Id,
                        attempts);
                }

                // Save FIRST, unconditionally: whatever this batch achieved — hydrations, retirements,
                // one more failed attempt against the counter — is durable before anything below is
                // allowed to throw. That ordering is the whole defect: the old code reached
                // SaveChangesAsync only if all 25 titles survived.
                lastTitleId = batch[^1].Id;
                job.CursorJson = JsonSerializer.Serialize(new Cursor(lastTitleId, pass, failures.Count == 0 ? null : failures));
                job.ProgressPct = total == 0 ? 100m : Math.Min(100m, Math.Round(100m * processed / total, 1));
                job.ProgressMessage = $"hydrated {processed}/{total} titles";
                await db.SaveChangesAsync(ct);

                events.Publish(accountId, PipelineEventTypes.JobProgress, new Dictionary<string, object?>
                {
                    ["kind"] = "hydrate",
                    ["pct"] = job.ProgressPct,
                    ["message"] = $"Enriching your library · {processed}/{total}",
                });

                // Two consecutive multi-title batches in which nothing at all hydrated is an
                // upstream outage, not 50 simultaneously-poisonous titles. Fail the job so the
                // runner retries it later from this exact cursor (failure counts included), rather
                // than burning every title's attempt budget and retiring the library wholesale.
                deadBatches = result.Hydrated == 0 && batch.Count > 1 ? deadBatches + 1 : 0;
                if (deadBatches >= OutageBatchThreshold)
                {
                    throw new InvalidOperationException(
                        $"TMDB hydration failed for {deadBatches} consecutive full batches with no successes — treating as an upstream outage rather than retiring the titles.");
                }
            }

            // The forward walk is exhausted, but "exhausted" only means "nothing left ABOVE the
            // cursor". Anything still pending sorts below it — a title that failed earlier in this
            // pass, one a concurrent DeltaSync inserted with a lower Guid, or (on a resumed job)
            // the entire library, because the cursor was already past the end. That is how a run
            // reported "100%, succeeded" while leaving a quarter of the library unhydrated.
            // Rewind and sweep again. Bounded by MaxPasses, and each sweep sees a strictly smaller
            // pending set because every title either hydrates or burns one of its attempts.
            if (ct.IsCancellationRequested || await Pending().CountAsync(ct) == 0)
            {
                break;
            }

            pass++;
            lastTitleId = null;
            job.CursorJson = JsonSerializer.Serialize(new Cursor(null, pass, failures.Count == 0 ? null : failures));
            await db.SaveChangesAsync(ct);
        }

        // Library hydrated → enrich it (embeddings + LLM attributes), which then chains to Train.
        // EnrichCatalog is a clean no-op without LLM keys, so the chain reaches Train regardless.
        var hasDownstream = await db.SyncJobs.AnyAsync(
            j => j.AccountId == accountId
                && (j.Kind == JobKind.EnrichCatalog || j.Kind == JobKind.Train || j.Kind == JobKind.Evaluate)
                && (j.Status == JobStatus.Pending || j.Status == JobStatus.Running), ct);
        if (!hasDownstream)
        {
            db.SyncJobs.Add(new SyncJob
            {
                Id = Guid.NewGuid(),
                AccountId = accountId,
                Kind = JobKind.EnrichCatalog,
                Priority = 1,
                EnqueuedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync(ct);
        }

        events.Publish(accountId, PipelineEventTypes.JobCompleted, new Dictionary<string, object?> { ["kind"] = "hydrate" });

        var stranded = await Pending().CountAsync(ct);
        logger.LogInformation(
            "HydrateCatalog completed for {AccountId}: {Processed}/{Total} titles in {Passes} pass(es); {Retired} retired, {Stranded} still pending.",
            accountId,
            processed,
            total,
            pass,
            retired,
            stranded);
    }
}
