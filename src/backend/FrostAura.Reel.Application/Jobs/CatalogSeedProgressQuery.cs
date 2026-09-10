using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Domain.Sync;
using Microsoft.EntityFrameworkCore;

namespace FrostAura.Reel.Application.Jobs;

/// <summary>
/// Everything <c>GET /api/dev/seed-catalog</c> reports, and the subset the pipeline metrics view
/// carries. One query object rather than two hand-rolled projections, because the two surfaces
/// disagreeing about how far the seed has got is exactly the kind of drift that gets a founder to
/// re-run something already done.
/// </summary>
/// <param name="MovieYearsDone">Movie partitions the walk has finished, of <paramref name="YearsPerMedium"/>.</param>
/// <param name="EstimatedDaysToEnriched">
/// Days until every seeded row is hydrated AND attributed at today's ceilings — the MAX of the two
/// stage estimates, not their sum, because they run concurrently. Null when nothing is outstanding.
/// </param>
public sealed record CatalogSeedProgress(
    Guid? JobId,
    string? Status,
    DateTime? EnqueuedAt,
    DateTime? StartedAt,
    DateTime? CompletedAt,
    int AttemptCount,
    string? Error,
    string? ProgressMessage,
    string? Media,
    int Year,
    int Page,
    int TitlesAdded,
    int PagesFetched,
    bool Done,
    int MovieYearsDone,
    int ShowYearsDone,
    int YearsPerMedium,
    int PartitionsDone,
    int PartitionsTotal,
    long PagesUsedToday,
    int HydrationBacklog,
    DailyAllowance HydrationAllowance,
    DailyAllowance ExtractAllowance,
    DailyAllowance EmbedAllowance,
    int ExtractTokensPerTitle,
    EnrichmentBacklog Enrichment,
    int? EstimatedDaysToHydrated,
    int? EstimatedDaysToAttributed,
    int? EstimatedDaysToEnriched);

/// <summary>Reads the seed's plan position, its backlogs and its ceilings in one pass.</summary>
public sealed class CatalogSeedProgressQuery(
    IReelDbContext db,
    CatalogHydrationBudget hydration,
    CatalogEnrichmentBudget enrichment)
{
    /// <summary>Days of ledger history averaged into the measured tokens-per-extraction figure.</summary>
    private const int MeasurementWindowDays = 7;

    public async Task<CatalogSeedProgress> RunAsync(int extractorVersion, DateTime now, CancellationToken ct)
    {
        var plan = CatalogSeedPlan.Build(now);
        var yearsPerMedium = plan.Count / 2;
        var job = await CatalogSeedScheduler.LatestAsync(db, ct);
        var cursor = CatalogSeedCursor.Read(job?.CursorJson);

        // Partitions done is derived from the cursor's position in a freshly-built plan rather than
        // stored, so it stays correct when the calendar turns and the plan grows a year at the front.
        var index = cursor is null ? 0 : CatalogSeedPlan.IndexOf(plan, cursor.Media, cursor.Year);
        var partitionsDone = cursor is null ? 0 : cursor.Done || index < 0 ? plan.Count : index;

        // The plan is newest-year-first and alternates movie/show, so a walk that has completed N
        // partitions has completed ceil(N/2) movie years and floor(N/2) show years.
        var movieYearsDone = Math.Min(yearsPerMedium, (partitionsDone + 1) / 2);
        var showYearsDone = Math.Min(yearsPerMedium, partitionsDone / 2);

        var today = now.Date;
        var pagesToday = await db.ExternalApiUsages
            .Where(u => u.Provider == ApiProvider.Tmdb && u.Purpose == ApiUsagePurpose.Seed && u.Day == today)
            .SumAsync(u => (long?)u.CallCount, ct) ?? 0L;

        var hydrationBacklog = await CatalogFollowThroughScheduler.PendingHydration(db).CountAsync(ct);
        var hydrationAllowance = await hydration.AllowanceAsync(now, ct);
        var extractAllowance = await enrichment.ExtractAllowanceAsync(now, ct);
        var embedAllowance = await enrichment.EmbedAllowanceAsync(now, ct);
        var tokensPerTitle = await enrichment.ExtractTokensPerTitleAsync(now, MeasurementWindowDays, ct);
        var backlog = await EnrichmentScheduler.BacklogAsync(db, extractorVersion, ct);

        var titlesPerDayHydrated = hydrationAllowance.Limit;
        // Tokens per day ÷ tokens per title = titles per day. Measured where the ledger has data,
        // assumed where it does not — and the figure used is reported alongside the estimate so the
        // arithmetic can be checked rather than believed.
        var titlesPerDayAttributed = extractAllowance.Limit / Math.Max(1, tokensPerTitle);

        var daysToHydrated = Days(hydrationBacklog, titlesPerDayHydrated);
        var daysToAttributed = Days(backlog.MissingAttributes + hydrationBacklog, titlesPerDayAttributed);
        var daysToEnriched = daysToHydrated is null && daysToAttributed is null
            ? null
            : (int?)Math.Max(daysToHydrated ?? 0, daysToAttributed ?? 0);

        return new CatalogSeedProgress(
            job?.Id,
            job?.Status.ToString(),
            job?.EnqueuedAt,
            job?.StartedAt,
            job?.CompletedAt,
            job?.AttemptCount ?? 0,
            job?.Error,
            job?.ProgressMessage,
            cursor?.Media,
            cursor?.Year ?? 0,
            cursor?.Page ?? 0,
            cursor?.TitlesAdded ?? 0,
            cursor?.PagesFetched ?? 0,
            cursor?.Done ?? false,
            movieYearsDone,
            showYearsDone,
            yearsPerMedium,
            partitionsDone,
            plan.Count,
            pagesToday,
            hydrationBacklog,
            hydrationAllowance,
            extractAllowance,
            embedAllowance,
            tokensPerTitle,
            backlog,
            daysToHydrated,
            daysToAttributed,
            daysToEnriched);
    }

    /// <summary>
    /// Whole days to clear <paramref name="backlog"/> at <paramref name="perDay"/>. Null for an
    /// empty backlog; null — not infinity and not a fabricated large number — when the ceiling is
    /// zero, because "never at this setting" is the honest answer and a number would read as a plan.
    /// </summary>
    private static int? Days(long backlog, long perDay) =>
        backlog <= 0 ? null
        : perDay <= 0 ? null
        : (int)Math.Ceiling(backlog / (double)perDay);
}
