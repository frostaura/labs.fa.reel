using System.Text.Json;
using FrostAura.Reel.Application.Ingestion;
using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Application.Pipeline;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
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
    CatalogHydrationBudget budget,
    IConfiguration configuration,
    ILogger<HydrateCatalogJobHandler> logger,
    CatalogEnrichmentBudget? enrichmentBudget = null) : IJobHandler
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
        // A null account is the GLOBAL catalogue sweep the seed's follow-through queues. It is a
        // genuinely different walk — a different pending set (the whole catalogue, not one library),
        // a different order (supply priority, not id) and a daily ceiling — so it gets its own
        // method rather than a handful of `accountId is null` branches threaded through logic whose
        // every line is load-bearing for the account path.
        if (job.AccountId is null)
        {
            await ExecuteGlobalAsync(job, ct);
            return;
        }

        var accountId = job.AccountId ?? throw new InvalidOperationException("HydrateCatalog requires an account.");
        var cursor = job.CursorJson is null
            ? new Cursor(null, 1, null)
            : JsonSerializer.Deserialize<Cursor>(job.CursorJson) ?? new Cursor(null, 1, null);

        var referenced = db.WatchedTitles.Where(w => w.AccountId == accountId).Select(w => w.TitleId)
            .Union(db.UserRatings.Where(r => r.AccountId == accountId).Select(r => r.TitleId));

        IOrderedQueryable<Title> Pending() => db.Titles
            .Where(t => referenced.Contains(t.Id) && t.TmdbId != null && t.LastMetadataRefreshAt == null)
            .OrderBy(t => t.Id);

        // The account's own region steers which age certification TMDB's per-region block resolves
        // to; TitleHydrator falls back to US when it is absent or TMDB has no entry for it. The row
        // itself stays global — this only picks which of several labels the catalogue stores.
        var region = await db.Accounts.Where(a => a.Id == accountId).Select(a => a.Region).FirstOrDefaultAsync(ct);

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

                var result = await hydrator.HydrateBatchResilientAsync(batch, ct, region);
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

    /// <summary>The global sweep's checkpoint — the poison-pill counter, and what this chain has hydrated.</summary>
    private sealed record GlobalCursor(Dictionary<string, int>? Failures, int Hydrated);

    /// <summary>
    /// The GLOBAL catalogue sweep: hydrates rows that belong to no account, in the order an
    /// account's discovery would surface them, bounded by <c>HYDRATE_MAX_TITLES_PER_DAY</c>.
    ///
    /// <para>
    /// <b>Priority order is the entire point of a capped run.</b> The seed lands tens of thousands
    /// of rows and a day's ceiling covers a small fraction of them, so WHICH fraction decides
    /// whether the recall gap closes this week or in two months. Highest <c>TmdbVoteCount</c> first,
    /// then most recent: that is the same axis the seed walks and very close to the order in which
    /// eligibility and the popularity arm would actually surface a title. Id order — what the
    /// account path uses, correctly, because a library is small and must be covered entirely —
    /// would hydrate a uniformly random sample and leave the titles most likely to be rated waiting
    /// behind the obscure ones.
    /// </para>
    ///
    /// <para>
    /// <b>No page cursor, by construction.</b> The pending query excludes what is already hydrated,
    /// so an interrupted run simply re-selects what is left — the same reasoning that removed the
    /// cursor from <c>EnrichCatalog</c>. What the cursor still carries is the failure counter, which
    /// must survive a crash or a permanently-broken title would be retried for ever.
    /// </para>
    /// </summary>
    private async Task ExecuteGlobalAsync(SyncJob job, CancellationToken ct)
    {
        var cursor = job.CursorJson is null
            ? new GlobalCursor(null, 0)
            : JsonSerializer.Deserialize<GlobalCursor>(job.CursorJson) ?? new GlobalCursor(null, 0);
        var failures = cursor.Failures ?? [];

        // Retire anything that spent its budget on an earlier run BEFORE selecting work, so a
        // permanently-broken title stops occupying a slot in today's ceiling. Stamping is what the
        // 404 path already does: no metadata is worse than none only if it also blocks everything
        // behind it.
        var spent = failures.Where(f => f.Value >= MaxTitleAttempts).Select(f => f.Key).ToList();
        if (spent.Count > 0)
        {
            var spentIds = spent.Select(Guid.Parse).ToList();
            foreach (var title in await db.Titles.Where(t => spentIds.Contains(t.Id)).ToListAsync(ct))
            {
                title.LastMetadataRefreshAt = DateTime.UtcNow;
            }

            foreach (var key in spent)
            {
                failures.Remove(key);
            }

            logger.LogWarning("Global hydration retired {Count} title(s) past the attempt budget.", spent.Count);
            await db.SaveChangesAsync(ct);
        }

        var allowance = await budget.AllowanceAsync(DateTime.UtcNow, ct);
        // Titles hydrated by an account-scoped run earlier today count against the same ceiling —
        // it bounds TMDB calls against the shared catalogue, not calls made by this one job.
        var remaining = (int)Math.Min(int.MaxValue, allowance.Remaining);

        // Titles are GLOBAL rows, so there is no account whose region could steer the certification
        // block. The deployment's default region is the honest stand-in, and TitleHydrator falls
        // back to US when it is absent.
        var region = configuration["GEOIP_DEFAULT_REGION"] ?? TitleHydrator.FallbackCertificationRegion;

        // Failed titles are excluded rather than re-selected: without this the same batch comes back
        // on the next iteration and burns its whole attempt budget inside one run, which turns a
        // transient TMDB blip into a wholesale retirement.
        var skip = failures.Keys.Select(Guid.Parse).ToList();
        var now = DateTime.UtcNow;

        IQueryable<Title> Pending() => CatalogFollowThroughScheduler.PendingHydration(db)
            .Where(t => !skip.Contains(t.Id))
            .OrderByDescending(t => t.TmdbVoteCount)
            .ThenByDescending(t => t.Year ?? 0)
            .ThenBy(t => t.Id);

        // The metadata refresh sweep (E-11, FD-9) shares this run and this ceiling. The day's
        // allowance goes first to rows past the rolling interval — oldest first, because they are
        // the ones closing on TMDB's six-month cache limit — then to recent releases due their weekly
        // refresh, and only then to rows never hydrated. The refresh load is bounded (see
        // MetadataRefreshPolicy.Project), so the seed backlog still gets most of every day.
        IQueryable<Title> Overdue() => MetadataRefreshPolicy.Overdue(db, now)
            .Where(t => !skip.Contains(t.Id))
            .OrderBy(t => t.LastMetadataRefreshAt)
            .ThenBy(t => t.Id);
        IQueryable<Title> RecentDue() => MetadataRefreshPolicy.RecentDue(db, now)
            .Where(t => !skip.Contains(t.Id))
            .OrderBy(t => t.LastMetadataRefreshAt)
            .ThenBy(t => t.Id);

        async Task<List<Title>> NextBatchAsync(int size)
        {
            var batch = await Overdue().Take(size).ToListAsync(ct);
            if (batch.Count < size)
            {
                batch.AddRange(await RecentDue().Take(size - batch.Count).ToListAsync(ct));
            }

            if (batch.Count < size)
            {
                batch.AddRange(await Pending().Take(size - batch.Count).ToListAsync(ct));
            }

            return batch;
        }

        var refreshDue = await MetadataRefreshPolicy.Due(db, now).CountAsync(ct);
        var backlog = await Pending().CountAsync(ct) + refreshDue;
        var processed = 0;
        var deadBatches = 0;

        while (!ct.IsCancellationRequested && processed < remaining)
        {
            var batch = await NextBatchAsync(Math.Min(SaveBatchSize, remaining - processed));
            if (batch.Count == 0)
            {
                break;
            }

            var result = await hydrator.HydrateBatchResilientAsync(batch, ct, region);
            processed += result.Hydrated;

            foreach (var title in result.Failed)
            {
                var key = title.Id.ToString();
                failures[key] = failures.GetValueOrDefault(key) + 1;
                skip.Add(title.Id);
            }

            // Saved unconditionally and before anything below can throw — the batch's hydrations and
            // its failure counts are durable even if the outage guard fires on this very iteration.
            job.CursorJson = JsonSerializer.Serialize(
                new GlobalCursor(failures.Count == 0 ? null : failures, cursor.Hydrated + processed));
            job.ProgressPct = backlog == 0 ? 100m : Math.Min(100m, Math.Round(100m * processed / backlog, 1));
            job.ProgressMessage = $"hydrated {processed}/{Math.Min(remaining, backlog)} catalogue titles today";
            await db.SaveChangesAsync(ct);

            deadBatches = result.Hydrated == 0 && batch.Count > 1 ? deadBatches + 1 : 0;
            if (deadBatches >= OutageBatchThreshold)
            {
                throw new InvalidOperationException(
                    $"TMDB hydration failed for {deadBatches} consecutive full batches with no successes — treating as an upstream outage rather than retiring the titles.");
            }
        }

        // Newly hydrated rows have an overview and keywords and nothing derived from them. Enrich is
        // a clean no-op without LLM keys, so queueing it is safe regardless of configuration.
        if (processed > 0 && await EnrichmentScheduler.EnqueueAsync(db, null, DateTime.UtcNow, ct, enrichmentBudget))
        {
            await db.SaveChangesAsync(ct);
        }

        // Whether the configured ceiling can keep every cached TMDB record inside the six-month
        // limit, said out loud on every run so a cap lowered in the environment is visible at once.
        var projection = MetadataRefreshPolicy.Project(
            await db.Titles.CountAsync(t => t.TmdbId != null, ct),
            await MetadataRefreshPolicy.Recent(db, now).CountAsync(ct),
            budget.DailyTitles);
        if (projection.InsideCacheLimit)
        {
            logger.LogInformation("Metadata refresh sweep: {Projection}.", projection.Describe());
        }
        else
        {
            logger.LogWarning("Metadata refresh sweep: {Projection}.", projection.Describe());
        }

        logger.LogInformation(
            "Global HydrateCatalog: {Processed} title(s) hydrated or refreshed of a {Backlog} backlog ({RefreshDue} due a refresh); "
            + "daily allowance {Used}/{Limit}, {Failing} title(s) carrying failures.",
            processed, backlog, refreshDue, allowance.Used, allowance.Limit, failures.Count);
    }
}
