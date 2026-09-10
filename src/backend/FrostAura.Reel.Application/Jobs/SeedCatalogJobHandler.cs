using System.Globalization;
using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Application.Ranking;
using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Domain.Sync;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace FrostAura.Reel.Application.Jobs;

/// <summary>
/// The one-off catalogue bulk seed: walks TMDB discover over <see cref="CatalogSeedPlan"/> and
/// upserts every result as a global summary <c>Title</c> row.
///
/// <para>
/// <b>Why it exists.</b> The retrieval-recall probe found that only 25.5% of the titles the founder
/// loved in the last 180 days were in the catalogue at all the day before he rated them. That is a
/// SUPPLY ceiling, not a ranking or retrieval one: no pool size, no new arm and no better model can
/// return a row that does not exist. Everything else the pipeline does is downstream of this
/// number, which is why a job that nothing waits on runs below everything and still matters more
/// than most of what is above it.
/// </para>
///
/// <para>
/// <b>Summary rows only.</b> One discover page is one HTTP call for ~20 titles; hydration is one
/// call PER title and enrichment costs money per title. Separating them is what lets supply land in
/// days while the expensive follow-through drains against its own daily ceilings — see
/// <see cref="CatalogFollowThroughScheduler"/>. A seeded row is deliberately usable before it is
/// hydrated: it carries name, year, poster, vote counts and the discovery language, which is enough
/// for eligibility to reason about it.
/// </para>
///
/// <para>
/// <b>Resumable by cursor, bounded by budget, re-entrant by re-enqueue.</b> The cursor is written
/// after every page, so a crash costs at most one page. A run stops at
/// <c>SEED_PAGES_PER_RUN</c> and queues a successor carrying the cursor, so the walk is a chain of
/// bounded jobs rather than one job holding a worker slot for a week — which also means the runner's
/// 12-minute stale-heartbeat reclaim never sees it as wedged.
/// </para>
/// </summary>
public class SeedCatalogJobHandler(
    IReelDbContext db,
    ITmdbClient tmdb,
    CandidateGenerator candidates,
    IConfiguration configuration,
    ILogger<SeedCatalogJobHandler> logger) : IJobHandler
{
    public JobKind Kind => JobKind.SeedCatalog;

    public async Task ExecuteAsync(SyncJob job, CancellationToken ct)
    {
        if (job.AccountId is not null)
        {
            // The rows are tenant-shared and the plan is not shaped by anybody's preferences. An
            // account-scoped seed would be one account deciding what the catalogue contains for
            // everyone, so it is refused rather than quietly run.
            throw new InvalidOperationException("SeedCatalog is a global job and must not carry an account.");
        }

        var now = DateTime.UtcNow;
        var plan = CatalogSeedPlan.Build(now);
        var cursor = CatalogSeedCursor.Read(job.CursorJson) ?? CatalogSeedCursor.Start(now);
        var pageBudget = CatalogSeedPlan.PagesPerRun(configuration);
        var titleCeiling = CatalogSeedPlan.MaxTitles(configuration);

        if (cursor.Done)
        {
            job.ProgressPct = 100m;
            job.ProgressMessage = SeedSummary(cursor, plan.Count, plan.Count);
            await db.SaveChangesAsync(ct);
            logger.LogInformation("SeedCatalog: plan already complete ({Titles} titles over {Pages} pages).",
                cursor.TitlesAdded, cursor.PagesFetched);
            return;
        }

        // -1 means the stored partition is not in a plan built now — only reachable if FirstYear
        // moved under a running plan. Restarting at the top is safe: the upsert is idempotent, so a
        // re-walk costs pages and adds no duplicate rows.
        var index = CatalogSeedPlan.IndexOf(plan, cursor.Media, cursor.Year);
        if (index < 0)
        {
            logger.LogWarning(
                "SeedCatalog: cursor partition {Media} {Year} is not in the current plan; restarting the walk.",
                cursor.Media, cursor.Year);
            index = 0;
            cursor = cursor with { Media = plan[0].Media, Year = plan[0].Year, Page = 1 };
        }

        var pagesThisRun = 0;
        var addedThisRun = 0;
        var ceilingHit = false;

        while (!ct.IsCancellationRequested && index < plan.Count && pagesThisRun < pageBudget)
        {
            if (cursor.TitlesAdded >= titleCeiling)
            {
                ceilingHit = true;
                break;
            }

            var step = plan[index];
            var page = await tmdb.DiscoverSeedPageAsync(
                step.Movies, step.Year, CatalogSeedPlan.MinVoteCount(step.Movies, configuration), cursor.Page, ct);
            pagesThisRun++;

            var added = page.Items.Count == 0
                ? 0
                : await candidates.UpsertCandidatesAsync(page.Items, ct);
            addedThisRun += added;

            // Advance BEFORE persisting, so what lands in the cursor is where the walk resumes and
            // never the page just paid for. An empty page ends the partition even when TMDB claims
            // more: past the 500-page wall it serves nothing while still reporting total_pages.
            var lastPage = CatalogSeedPlan.LastPage(page.TotalPages);
            var partitionDone = page.Items.Count == 0 || cursor.Page >= lastPage;
            var titlesAdded = cursor.TitlesAdded + added;
            var pagesFetched = cursor.PagesFetched + 1;

            if (partitionDone)
            {
                index++;
                cursor = index < plan.Count
                    ? new CatalogSeedCursor(plan[index].Media, plan[index].Year, 1, titlesAdded, pagesFetched, false)
                    : cursor with { Page = lastPage, TitlesAdded = titlesAdded, PagesFetched = pagesFetched, Done = true };
            }
            else
            {
                cursor = cursor with { Page = cursor.Page + 1, TitlesAdded = titlesAdded, PagesFetched = pagesFetched };
            }

            job.CursorJson = cursor.Write();
            job.ProgressPct = Math.Round(100m * Math.Min(index, plan.Count) / plan.Count, 1);
            job.ProgressMessage = SeedSummary(cursor, index, plan.Count);
            await db.SaveChangesAsync(ct);
        }

        var finished = cursor.Done || index >= plan.Count;
        if (finished && !cursor.Done)
        {
            cursor = cursor with { Done = true };
            job.CursorJson = cursor.Write();
            job.ProgressPct = 100m;
            job.ProgressMessage = SeedSummary(cursor, plan.Count, plan.Count);
            await db.SaveChangesAsync(ct);
        }

        // Everything this run added is a bare summary row: no credits, no keywords, no embedding, no
        // attributes. Hand it to the follow-through scheduler rather than enqueueing hydration here,
        // so the daily ceilings are consulted in exactly one place.
        if (addedThisRun > 0)
        {
            await CatalogFollowThroughScheduler.EnqueueHydrationAsync(db, DateTime.UtcNow, ct);
        }

        if (!finished && !ceilingHit && !ct.IsCancellationRequested
            && await CatalogSeedScheduler.ContinueAsync(db, job.Id, cursor, DateTime.UtcNow, ct))
        {
            await db.SaveChangesAsync(ct);
        }

        if (ceilingHit)
        {
            logger.LogWarning(
                "SeedCatalog stopped at the SEED_MAX_TITLES guard ({Ceiling}); the plan is unfinished at {Partition} "
                + "and no successor was queued. Raise the ceiling deliberately to continue.",
                titleCeiling, CatalogSeedPlan.Describe(plan[Math.Min(index, plan.Count - 1)]));
        }

        logger.LogInformation(
            "SeedCatalog: {Pages} page(s) this run, {Added} new title(s); {TotalTitles} total over {TotalPages} page(s); "
            + "{Done}/{Steps} partitions complete.",
            pagesThisRun, addedThisRun, cursor.TitlesAdded, cursor.PagesFetched, Math.Min(index, plan.Count), plan.Count);
    }

    /// <summary>Invariant: this string is persisted on the job row and polled as an exact value.</summary>
    private static string SeedSummary(CatalogSeedCursor cursor, int stepsDone, int stepsTotal) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"Seeding the catalogue · {stepsDone}/{stepsTotal} partitions · {cursor.TitlesAdded} new titles");
}
