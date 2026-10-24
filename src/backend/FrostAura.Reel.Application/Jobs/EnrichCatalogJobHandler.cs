using System.Globalization;
using FrostAura.Reel.Application.Ml;
using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Application.Pipeline;
using FrostAura.Reel.Application.Search;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Domain.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Pgvector;

namespace FrostAura.Reel.Application.Jobs;

/// <summary>
/// Enriches the catalog with the two LLM-derived feature families the model reads but nothing else
/// produces: plot/tone <see cref="TitleEmbedding"/>s (one vector space with NL search) and structured
/// <see cref="TitleAttributes"/>. Both are global catalog rows — one enrichment serves every tenant,
/// so the work compounds across accounts.
///
/// <para>
/// Runs after hydration (needs overview/genres/keywords) and before training (which consumes the
/// results). When neither provider is configured it is a clean no-op that still chains to Train —
/// the model trains on metadata + affinity features alone, exactly as it does today, and these
/// features light up the moment the keys land.
/// </para>
///
/// <para>
/// <b>Bounded, prioritised, resumable-by-construction.</b> One run touches at most
/// <c>ENRICH_MAX_TITLES_PER_RUN</c> titles (default 200), taken in an order that puts the requesting
/// account's own titles first — what it was shown, what it was scored, then its library — and the
/// rest of the shared catalogue after. Three things follow from that. Scheduling can now be
/// aggressive (a build, a night, a weekly sweep) without any one run billing for the whole world.
/// The account that triggered the run gets the features that affect ITS next feed first, rather
/// than whatever sorted earliest by id. And there is no cursor any more: both selection queries
/// exclude work that is already done, so an interrupted run simply re-selects what is left, which
/// is what a cursor was emulating.
/// </para>
///
/// <para>
/// <b>Account is optional.</b> A null <c>AccountId</c> is a global sweep — catalogue rows belong to
/// no tenant, and the weekly growth pass and the catalogue bulk seed both discover titles on behalf
/// of nobody. A global run takes the catalogue in supply order rather than an account's priority
/// tiers (there is no account to prioritise for), publishes no SSE (no one is watching) and chains
/// no Train (there is no model to retrain).
/// </para>
///
/// <para>
/// <b>Two ceilings, not one.</b> <c>ENRICH_MAX_TITLES_PER_RUN</c> bounds a RUN;
/// <c>EXTRACT_DAILY_TOKENS</c> and <c>EMBED_DAILY_TOKENS</c> bound a DAY, system-wide, against the
/// token ledger. The per-run cap alone was sufficient while enrichment ran a few times a day over a
/// 5,000-title catalogue; once the bulk seed made the backlog tens of thousands deep and the
/// follow-through scheduler started queueing runs continuously, a per-run cap bounds nothing at all.
/// See <see cref="CatalogEnrichmentBudget"/> for the cost arithmetic behind the numbers.
/// </para>
/// </summary>
public class EnrichCatalogJobHandler(
    IReelDbContext db,
    IEmbeddingProvider embeddings,
    ITitleAttributeExtractor extractor,
    IPipelineEventHub events,
    IConfiguration configuration,
    CatalogEnrichmentBudget budget,
    ILogger<EnrichCatalogJobHandler> logger) : IJobHandler
{
    public JobKind Kind => JobKind.EnrichCatalog;

    private const int EmbedBatch = 64;
    private const int AttributeBatch = 24; // the extractor parallelizes within a batch (bounded by OPENROUTER_MAX_CONCURRENCY)

    /// <summary>
    /// Ceiling on titles touched per phase per run, and the reason the job can be scheduled from
    /// three places without any of them being able to spend without limit.
    /// <para>
    /// The number is a spend decision, not a performance one: at 200 titles a run and a nightly
    /// pass, a 1,360-title embedding gap closes in a week, and a prompt-version bump that
    /// invalidates the whole catalogue drains over days rather than presenting one night's bill for
    /// every title in the database. Raise it to catch up faster; it is read fresh on every run.
    /// </para>
    /// </summary>
    public const string MaxTitlesPerRunKey = "ENRICH_MAX_TITLES_PER_RUN";

    public const int DefaultMaxTitlesPerRun = 200;

    /// <summary>
    /// The per-run cap. Read by indexer and parsed here rather than through <c>GetValue&lt;int&gt;</c>
    /// because this project references Configuration.Abstractions only — the binder is not a
    /// dependency worth adding for one integer. A missing, unparseable or non-positive value falls
    /// back to the default: a typo in an env var must not silently disable enrichment.
    /// </summary>
    public static int MaxTitlesPerRun(IConfiguration configuration) =>
        int.TryParse(configuration[MaxTitlesPerRunKey], NumberStyles.Integer, CultureInfo.InvariantCulture, out var cap)
        && cap > 0
            ? cap
            : DefaultMaxTitlesPerRun;

    /// <summary>
    /// How many enrich RUNS may spend money on one title before it is left alone.
    ///
    /// Deliberately 2, not 3. Nothing here varies between runs — same title, same prompt,
    /// temperature 0 — so a third identical request buys no incremental chance of success, only
    /// a third bill. The variation that does help now lives where it can actually be targeted:
    /// the extractor detects WHY a response was unusable and, for structural failures only,
    /// re-asks once within the same attempt with a materially different prompt (named offending
    /// fields, or a compacted input plus a wider token budget for a truncation). What survives
    /// that is not a prompt problem, and the second run is its one later chance.
    ///
    /// Transport failures — a dropped connection, a body read that died after the headers, a 429,
    /// an outage — do NOT spend this budget at all: they back off by time instead (see
    /// <see cref="TransportBackoff"/>). Charging them is what left 2,038 titles Failed on a laptop
    /// host that slept through most of its runs (E-6).
    ///
    /// The budget is spent per (title, extractor model, extractor version), not per account: the
    /// row is global, so an account whose enrich run walks past an exhausted title must not
    /// re-bill for it on behalf of everyone. Only a model change or a prompt-version bump — both
    /// of which change what the answer would BE — hands a title a fresh budget.
    /// </summary>
    private const int MaxAttributeAttempts = 2;

    /// <summary>
    /// How long a batch claim holds. Long enough for a slow batch (24 titles at bounded concurrency
    /// against a reasoning model), short enough that a run killed mid-batch frees its titles within
    /// the hour without anyone cleaning up.
    /// </summary>
    public static readonly TimeSpan ClaimLease = TimeSpan.FromMinutes(30);

    /// <summary>The ceiling on a transport failure's backoff — a title is retried at least daily.</summary>
    public static readonly TimeSpan MaxTransportBackoff = TimeSpan.FromHours(24);

    /// <summary>
    /// The wait after a failed re-extraction of a row that already holds an answer. That row cannot
    /// be charged against the current model's budget without being demoted, so time bounds its spend
    /// instead: at most one billed attempt per title per week.
    /// </summary>
    public static readonly TimeSpan AnsweredRowRetryDelay = TimeSpan.FromDays(7);

    public async Task ExecuteAsync(SyncJob job, CancellationToken ct)
    {
        var accountId = job.AccountId;
        var cap = MaxTitlesPerRun(configuration);

        // Every hydrated title — both the watched/rated LIBRARY (training label rows + loved
        // centroids) AND the unwatched CANDIDATES that discovery surfaces. Serving needs the
        // candidates embedded/attributed too: semantic search and feed similarity only ever
        // operate on unseen titles, so scoping to the library alone leaves search with nothing.
        // Catalog rows are global, so this work is shared across every tenant.
        var hydrated = db.Titles.Where(t => t.LastMetadataRefreshAt != null);

        var embedded = 0;
        if (embeddings.IsAvailable)
        {
            embedded = await EmbedAsync(job, accountId, hydrated, cap, ct);
        }

        var extracted = 0;
        if (extractor.IsAvailable)
        {
            extracted = await ExtractAsync(job, accountId, hydrated, cap, ct);
        }

        if (!embeddings.IsAvailable && !extractor.IsAvailable)
        {
            logger.LogInformation(
                "EnrichCatalog no-op for {AccountId}: no embedding or extractor provider configured.", accountId);
            job.ProgressMessage = "skipped — no LLM provider configured";
        }

        if (accountId is { } owner)
        {
            await ChainTrainAsync(owner, ct);
            events.Publish(owner, PipelineEventTypes.JobCompleted, new Dictionary<string, object?> { ["kind"] = "enrich" });
        }

        logger.LogInformation(
            "EnrichCatalog completed for {AccountId}: {Embedded} title(s) embedded, {Extracted} attributed (cap {Cap}).",
            accountId, embedded, extracted, cap);
    }

    /// <summary>
    /// The order a run works through the catalogue.
    /// <para>
    /// <b>Never-attributed first, always.</b> A title with no Done attributes at the current prompt
    /// generation is invisible (or wrong in meaning) to every feature that reads them; a title
    /// holding an older MODEL's Done answer at the current generation is fully usable.
    /// After the 2026-10-14 model switch the daily ceiling was spent partly re-attributing 5,199
    /// titles that already had attributes while ~24k had none (E-8, NEW-3), so re-extraction now
    /// waits behind the whole never-attributed backlog, on every run, account or global.
    /// </para>
    /// <para>
    /// Then, for an account-scoped run, the tiers: titles this account has actually been SHOWN (feed
    /// items), titles it has been SCORED, titles in its own LIBRARY, then its ELIGIBLE POOL (the
    /// unseen titles its standing conditions admit — what its next feed can draw from), then the
    /// rest of the shared catalogue. Within a tier, supply order: highest <c>TmdbVoteCount</c>
    /// first, id as the tiebreak that keeps it deterministic. Tier 3 used to be the whole rest of
    /// the catalogue ordered by id — a uniformly random sample — which is the same defect the global
    /// path had already fixed.
    /// </para>
    /// <para>
    /// A GLOBAL run has no account to prioritise for, and takes the catalogue in SUPPLY order:
    /// highest <c>TmdbVoteCount</c> first, then most recent, then id.
    /// </para>
    /// </summary>
    private IQueryable<Title> InPriorityOrder(IQueryable<Title> titles, Guid? accountId, int extractorVersion)
    {
        // "Attributed" means a Done answer at the CURRENT prompt generation, under any model. An
        // older model's answer is usable; an older generation's is wrong in meaning (that is what a
        // version bump says), so it waits in the never-attributed queue rather than behind it.
        var neverAttributedFirst = titles.OrderBy(t =>
            db.TitleAttributes.Any(a => a.TitleId == t.Id
                && a.Status == AttributeExtractionStatus.Done
                && a.ExtractorVersion >= extractorVersion) ? 1 : 0);

        if (accountId is not { } id)
        {
            return neverAttributedFirst
                .ThenByDescending(t => t.TmdbVoteCount)
                .ThenByDescending(t => t.Year ?? 0)
                .ThenBy(t => t.Id);
        }

        var eligiblePool = new EligibilityQueryBuilder(db).EligibleTitles(id).Select(e => e.Id);
        return neverAttributedFirst
            .ThenBy(t =>
                db.FeedItems.Any(f => f.AccountId == id && f.TitleId == t.Id) ? 0
                : db.TitleScores.Any(s => s.AccountId == id && s.TitleId == t.Id) ? 1
                : db.UserRatings.Any(r => r.AccountId == id && r.TitleId == t.Id)
                    || db.WatchedTitles.Any(w => w.AccountId == id && w.TitleId == t.Id) ? 2
                : eligiblePool.Contains(t.Id) ? 3
                : 4)
            .ThenByDescending(t => t.TmdbVoteCount)
            .ThenBy(t => t.Id);
    }

    private async Task<int> EmbedAsync(
        SyncJob job, Guid? accountId, IQueryable<Title> hydrated, int cap, CancellationToken ct)
    {
        // Embed those MISSING a vector, plus those whose model moved or whose metadata was refreshed
        // after the vector was written. The drift test that actually decides whether to spend is the
        // SourceTextHash comparison below, in C# — EmbeddingText.Build has no SQL translation — so
        // LastMetadataRefreshAt is the cheap SQL-side proxy that narrows the scan to rows that COULD
        // have drifted. Re-embedding on real change stops a title being frozen with a stale vector;
        // the live search path writes through the same helper, so the two never disagree.
        var model = EmbeddingText.Model;
        var work = hydrated.Where(t =>
            !db.TitleEmbeddings.Any(e => e.TitleId == t.Id)
            || db.TitleEmbeddings.Any(e => e.TitleId == t.Id
                && (e.EmbeddingModel != model || e.CreatedAt < t.LastMetadataRefreshAt)));

        var titles = await InPriorityOrder(work, accountId, extractor.Version).Take(cap).ToListAsync(ct);
        var total = titles.Count;
        var processed = 0;

        foreach (var batch in titles.Chunk(EmbedBatch))
        {
            if (ct.IsCancellationRequested)
            {
                break;
            }

            // Re-read per batch, not once per run: the ledger's own in-flight counters move as this
            // loop spends, so a run that starts inside the ceiling stops the moment it crosses it
            // rather than finishing whatever it had already selected.
            var allowance = await budget.EmbedAllowanceAsync(DateTime.UtcNow, ct);
            if (!allowance.HasHeadroom)
            {
                logger.LogInformation(
                    "Embedding stopped at the daily token ceiling ({Used}/{Limit}) after {Processed}/{Total} title(s).",
                    allowance.Used, allowance.Limit, processed, total);
                job.ProgressMessage = "Learning plots · paused for today's token ceiling";
                await db.SaveChangesAsync(ct);
                break;
            }

            var batchIds = batch.Select(t => t.Id).ToList();
            var existing = await db.TitleEmbeddings
                .Where(e => batchIds.Contains(e.TitleId))
                .ToDictionaryAsync(e => e.TitleId, ct);

            var stale = new List<(Title Title, string Text, string Hash, TitleEmbedding? Row)>();
            foreach (var title in batch)
            {
                var text = EmbeddingText.Build(title);
                var hash = EmbeddingText.Hash(text);
                var row = existing.GetValueOrDefault(title.Id);
                if (row is null || row.SourceTextHash != hash || row.EmbeddingModel != model)
                {
                    stale.Add((title, text, hash, row));
                }
                else
                {
                    // Selected by the SQL proxy — a metadata refresh landed after this vector was
                    // written — but the embedding TEXT did not actually change, so there is nothing
                    // to buy. Re-stamp the verification time anyway: without it the row matches the
                    // proxy on every future run and permanently occupies slots in the per-run cap
                    // that unenriched titles need.
                    row.CreatedAt = DateTime.UtcNow;
                }
            }

            if (stale.Count > 0)
            {
                var vectors = await embeddings.EmbedAsync(stale.Select(s => s.Text).ToList(), ct);
                for (var i = 0; i < stale.Count; i++)
                {
                    var (title, _, hash, row) = stale[i];
                    if (row is null)
                    {
                        db.TitleEmbeddings.Add(new TitleEmbedding
                        {
                            TitleId = title.Id,
                            Embedding = new Vector(vectors[i]),
                            EmbeddingModel = model,
                            SourceTextHash = hash,
                            CreatedAt = DateTime.UtcNow,
                        });
                    }
                    else
                    {
                        row.Embedding = new Vector(vectors[i]);
                        row.EmbeddingModel = model;
                        row.SourceTextHash = hash;
                        row.CreatedAt = DateTime.UtcNow;
                    }
                }
            }

            processed += batch.Length;
            job.ProgressPct = total == 0 ? 0 : Math.Round(50m * processed / total, 1);
            // Same text on the poll (ProgressMessage) and the live SSE event so the pill never
            // flickers between two phrasings.
            job.ProgressMessage = $"Learning plots · {processed}/{total}";
            await db.SaveChangesAsync(ct);

            Publish(accountId, job);
        }

        // processed, not total: a run that stopped at a daily ceiling must not report the work it
        // selected as work it did.
        return processed;
    }

    private async Task<int> ExtractAsync(
        SyncJob job, Guid? accountId, IQueryable<Title> hydrated, int cap, CancellationToken ct)
    {
        var modelId = extractor.ModelId;
        var version = extractor.Version;
        var runId = job.Id;

        var titles = await InPriorityOrder(PendingExtraction(hydrated, modelId, version, runId, DateTime.UtcNow), accountId, version)
            .Take(cap)
            .ToListAsync(ct);
        var total = titles.Count;
        var processed = 0;
        var tally = new ExtractionTally();

        foreach (var batch in titles.Chunk(AttributeBatch))
        {
            if (ct.IsCancellationRequested)
            {
                break;
            }

            // The money gate. Extraction is the only stage whose cost scales with the catalogue at a
            // price worth arguing about, so this is the number that decides how fast a 35,000-title
            // seed becomes usable — and the one that must never be crossed silently. Checked per
            // batch for the same reason as embedding: a run that started inside the budget can spend
            // past it in a single window.
            var allowance = await budget.ExtractAllowanceAsync(DateTime.UtcNow, ct);
            if (!allowance.HasHeadroom)
            {
                logger.LogInformation(
                    "Attribute extraction stopped at the daily token ceiling ({Used}/{Limit}) after {Processed}/{Total} title(s).",
                    allowance.Used, allowance.Limit, processed, total);
                job.ProgressMessage = "Reading the mood · paused for today's token ceiling";
                await db.SaveChangesAsync(ct);
                break;
            }

            // Claim before spending. Two enrich runs (global and account, or a revived job and its
            // live original) selected overlapping titles 24 times in ten days; whichever failed last
            // overwrote the other's Done answer with Failed. The claim makes the batch this run's
            // alone, and re-checks eligibility under the row lock, so a title another run settled a
            // moment ago is not bought twice.
            var claimed = await ClaimAsync(batch.Select(t => t.Id).ToList(), runId, modelId, version, ct);
            var work = batch.Where(t => claimed.Contains(t.Id)).ToList();
            tally.SkippedClaimed += batch.Length - work.Count;

            if (work.Count > 0)
            {
                try
                {
                    var stop = await ExtractBatchAsync(work, modelId, version, tally, ct);
                    if (stop)
                    {
                        processed += batch.Length;
                        break;
                    }
                }
                finally
                {
                    await ReleaseAsync(work.Select(t => t.Id).ToList(), runId);
                }
            }

            processed += batch.Length;
            job.ProgressPct = total == 0 ? 100 : Math.Round(50m + (50m * processed / total), 1);
            job.ProgressMessage = $"Reading the mood · {processed}/{total}";
            await db.SaveChangesAsync(ct);

            Publish(accountId, job);
        }

        // Separate counters, because "Failed" used to be one number that hid a network outage
        // inside what read like a model-quality problem.
        logger.LogInformation(
            "Attribute extraction for {AccountId} with {Model} v{Version}: {Done} answered, {Transport} transport failure(s) "
            + "(backed off, not charged), {Parse} parse failure(s), {Unclassified} unclassified failure(s), "
            + "{Skipped} skipped as claimed by another run.",
            accountId, modelId, version, tally.Done, tally.Transport, tally.Parse, tally.Unclassified, tally.SkippedClaimed);

        // processed, not total: a run that stopped at a daily ceiling must not report the work it
        // selected as work it did.
        return processed;
    }

    /// <summary>
    /// Titles this run may extract right now. Eligible = not already settled BY THE CURRENT MODEL AT
    /// THE CURRENT PROMPT VERSION (Done, or its attempt budget spent under both), not inside a
    /// failure's time gate, and not held by another live run. Model-scoping is what lets a switch
    /// from the deterministic stub to the real model re-extract every title; version-scoping is what
    /// lets a prompt change do the same for values whose MEANING moved (see
    /// ITitleAttributeExtractor.Version). An exhausted row with a stale version is eligible again,
    /// deliberately — the reason it failed may be the reason the prompt changed.
    /// </summary>
    private IQueryable<Title> PendingExtraction(
        IQueryable<Title> hydrated, string modelId, int version, Guid runId, DateTime now) =>
        hydrated.Where(t => !db.TitleAttributes.Any(a =>
            a.TitleId == t.Id
            && ((a.ExtractorModel == modelId
                    && a.ExtractorVersion >= version
                    && (a.Status == AttributeExtractionStatus.Done || a.AttemptCount >= MaxAttributeAttempts))
                || a.RetryAfter > now
                || (a.ClaimedUntil > now && a.ClaimedBy != runId))));

    /// <summary>
    /// Claims <paramref name="titleIds"/> for this run and returns the ones it now holds.
    /// <para>
    /// A never-attributed title has no row to claim, so one is created first as an unanswered
    /// Pending placeholder stamped with the current model and version (so it is neither "stale" to
    /// the version sweep nor "settled" to selection). Every reader of attributes filters on Done or
    /// on a non-null tone, so a placeholder is invisible everywhere else. The claim itself is one
    /// conditional UPDATE: Postgres re-evaluates its predicate against the committed row when two
    /// runs race for the same title, so exactly one of them wins it.
    /// </para>
    /// </summary>
    private async Task<HashSet<Guid>> ClaimAsync(
        List<Guid> titleIds, Guid runId, string modelId, int version, CancellationToken ct)
    {
        var existing = await db.TitleAttributes
            .Where(a => titleIds.Contains(a.TitleId))
            .Select(a => a.TitleId)
            .ToListAsync(ct);
        var missing = titleIds.Except(existing).ToList();
        if (missing.Count > 0)
        {
            var placeholders = missing
                .Select(id => new TitleAttributes
                {
                    TitleId = id,
                    ExtractorModel = modelId,
                    ExtractorVersion = version,
                    Status = AttributeExtractionStatus.Pending,
                })
                .ToList();
            db.TitleAttributes.AddRange(placeholders);
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                // A concurrent run created some of the same rows first. They exist now, which is all
                // the claim below needs; forget ours rather than retrying the insert.
                foreach (var placeholder in placeholders)
                {
                    db.TitleAttributes.Entry(placeholder).State = EntityState.Detached;
                }
            }
        }

        var now = DateTime.UtcNow;
        var until = now + ClaimLease;
        await db.TitleAttributes
            .Where(a => titleIds.Contains(a.TitleId)
                && (a.ClaimedUntil == null || a.ClaimedUntil <= now || a.ClaimedBy == runId)
                && !(a.ExtractorModel == modelId
                    && a.ExtractorVersion >= version
                    && (a.Status == AttributeExtractionStatus.Done || a.AttemptCount >= MaxAttributeAttempts))
                && (a.RetryAfter == null || a.RetryAfter <= now))
            .ExecuteUpdateAsync(set => set
                .SetProperty(a => a.ClaimedBy, runId)
                .SetProperty(a => a.ClaimedUntil, until), ct);

        return (await db.TitleAttributes
                .Where(a => titleIds.Contains(a.TitleId) && a.ClaimedBy == runId && a.ClaimedUntil > now)
                .Select(a => a.TitleId)
                .ToListAsync(ct))
            .ToHashSet();
    }

    /// <summary>
    /// Hands this run's titles back. Best effort and deliberately not cancellable: a run being shut
    /// down should still let go of what it held, and if it cannot, the lease expires on its own.
    /// </summary>
    private async Task ReleaseAsync(List<Guid> titleIds, Guid runId)
    {
        try
        {
            await db.TitleAttributes
                .Where(a => titleIds.Contains(a.TitleId) && a.ClaimedBy == runId)
                .ExecuteUpdateAsync(set => set
                    .SetProperty(a => a.ClaimedBy, (Guid?)null)
                    .SetProperty(a => a.ClaimedUntil, (DateTime?)null), CancellationToken.None);
        }
        catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException)
        {
            logger.LogWarning(ex, "Could not release {Count} enrichment claim(s) for run {RunId}; they lapse with the lease.", titleIds.Count, runId);
        }
    }

    /// <summary>
    /// One claimed batch: ask the model, then write each title's outcome. Returns true when the run
    /// should stop — the whole batch failed in transport, which a later batch in the same minute
    /// will not fix.
    /// </summary>
    private async Task<bool> ExtractBatchAsync(
        List<Title> work, string modelId, int version, ExtractionTally tally, CancellationToken ct)
    {
        var inputs = work.Select(t => new TitleAttributeInput(
            t.Id, t.MediaType.ToString(), t.Name, t.Year, t.Overview, t.Genres, t.Keywords)).ToList();

        IReadOnlyList<AttributeExtractionOutcome> outcomes;
        var wholeBatchTransport = false;
        try
        {
            outcomes = await ExtractClassifiedAsync(inputs, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The port allows a throw only for a whole-batch transport failure.
            logger.LogWarning(ex, "Attribute extraction batch of {Count} failed in transport; backing off and stopping this run.", work.Count);
            outcomes = work.Select(_ => AttributeExtractionOutcome.Failed(AttributeFailureClass.Transport)).ToList();
            wholeBatchTransport = true;
        }

        var batchIds = work.Select(b => b.Id).ToList();
        var rows = await db.TitleAttributes
            .Where(a => batchIds.Contains(a.TitleId))
            .ToDictionaryAsync(a => a.TitleId, ct);
        var now = DateTime.UtcNow;

        for (var i = 0; i < work.Count; i++)
        {
            var title = work[i];
            var row = rows.GetValueOrDefault(title.Id);
            if (row is null)
            {
                // Claimed rows exist by construction; a row deleted underneath the run is not this
                // run's to resurrect.
                continue;
            }

            // Attempts are counted per (model, version): a different model, or a newer prompt, is
            // answering a different question and starts with a fresh budget.
            var sameQuestion = row.ExtractorModel == modelId && row.ExtractorVersion >= version;
            var attempts = sameQuestion ? row.AttemptCount + 1 : 1;
            var outcome = outcomes[i];

            if (outcome.Attributes is { } result)
            {
                row.Darkness = result.Darkness;
                row.Pacing = result.Pacing;
                row.Complexity = result.Complexity;
                row.EmotionalIntensity = result.EmotionalIntensity;
                row.Humor = result.Humor;
                row.Optimism = result.Optimism;
                row.EnsembleVsSolo = result.EnsembleVsSolo;
                row.Tone = result.Tone;
                row.Era = result.Era;
                row.Themes = result.Themes.ToArray();
                row.RawJson = result.RawJson;
                row.Status = AttributeExtractionStatus.Done;
                row.ExtractedAt = now;
                row.ExtractorModel = modelId;
                row.ExtractorVersion = version;
                row.AttemptCount = attempts;
                row.LastAttemptAt = now;
                row.TransportFailures = 0;
                row.RetryAfter = null;
                tally.Done++;
                continue;
            }

            await WriteFailureAsync(row, outcome.Failure, attempts, modelId, version, now, tally, ct);
        }

        await db.SaveChangesAsync(ct);
        return wholeBatchTransport;
    }

    /// <summary>
    /// A failure, written so that it can never demote an answer.
    /// <para>
    /// The extractor returns no answer only when it could not get a usable one, and never
    /// substitutes a neutral value — so nothing is written to the attribute columns, ever. What
    /// changes is bookkeeping, and every status-changing write is a conditional UPDATE in SQL
    /// (<c>WHERE "Status" &lt;&gt; 'Done' AND "RawJson" IS NULL</c>) rather than a write through the
    /// entity loaded after the slow model call: 1,077 rows on the founder's catalogue held a complete
    /// answer under a Failed status because a stale run's failure landed on top of another run's
    /// success (NEW-1/NEW-2). A row that already holds an answer keeps it and is time-gated instead.
    /// </para>
    /// </summary>
    private async Task WriteFailureAsync(
        TitleAttributes row, AttributeFailureClass failure, int attempts, string modelId, int version,
        DateTime now, ExtractionTally tally, CancellationToken ct)
    {
        var titleId = row.TitleId;
        if (failure == AttributeFailureClass.Transport)
        {
            tally.Transport++;
            var failures = row.TransportFailures + 1;
            var retryAfter = now + TransportBackoff(failures);
            await db.TitleAttributes
                .Where(a => a.TitleId == titleId)
                .ExecuteUpdateAsync(set => set
                    .SetProperty(a => a.TransportFailures, a => a.TransportFailures + 1)
                    .SetProperty(a => a.RetryAfter, retryAfter)
                    .SetProperty(a => a.LastAttemptAt, now), ct);
            logger.LogWarning(
                "Attribute extraction for {TitleId} failed in transport ({Failures} in a row); retrying after {RetryAfter:O}, attempt budget untouched.",
                titleId, failures, retryAfter);
            return;
        }

        if (failure == AttributeFailureClass.Parse)
        {
            tally.Parse++;
        }
        else
        {
            tally.Unclassified++;
        }

        var demotable = await db.TitleAttributes
            .Where(a => a.TitleId == titleId
                && a.Status != AttributeExtractionStatus.Done
                && a.RawJson == null)
            .ExecuteUpdateAsync(set => set
                .SetProperty(a => a.Status, AttributeExtractionStatus.Failed)
                .SetProperty(a => a.AttemptCount, attempts)
                // Stamped with the outcome: selection counts only attempts made at the current model
                // and version, so a failed row that never recorded one was re-picked for ever.
                .SetProperty(a => a.ExtractorModel, modelId)
                .SetProperty(a => a.ExtractorVersion, version)
                .SetProperty(a => a.LastAttemptAt, now)
                .SetProperty(a => a.RetryAfter, (DateTime?)null), ct);

        if (demotable > 0)
        {
            logger.LogWarning(
                "Attribute extraction produced no usable result for {TitleId} on attempt {Attempt}/{Max} with {Model} v{Version} "
                + "({Failure}); row left Failed rather than filled with defaults.",
                titleId, attempts, MaxAttributeAttempts, modelId, version, failure);
            return;
        }

        // The row holds an answer — Done under some model, or an older generation's values awaiting
        // re-extraction. It keeps them, and waits a week before this model is asked again.
        var retry = now + AnsweredRowRetryDelay;
        await db.TitleAttributes
            .Where(a => a.TitleId == titleId)
            .ExecuteUpdateAsync(set => set
                .SetProperty(a => a.RetryAfter, retry)
                .SetProperty(a => a.LastAttemptAt, now), ct);
        logger.LogWarning(
            "Re-extraction of {TitleId} with {Model} v{Version} failed ({Failure}); its existing answer is kept, next try after {RetryAfter:O}.",
            titleId, modelId, version, failure, retry);
    }

    private async Task<IReadOnlyList<AttributeExtractionOutcome>> ExtractClassifiedAsync(
        IReadOnlyList<TitleAttributeInput> inputs, CancellationToken ct)
    {
        if (extractor is IClassifyingAttributeExtractor classifying)
        {
            return await classifying.ExtractClassifiedAsync(inputs, ct);
        }

        var results = await extractor.ExtractAsync(inputs, ct);
        return results
            .Select(r => r is null
                ? AttributeExtractionOutcome.Failed(AttributeFailureClass.Unclassified)
                : AttributeExtractionOutcome.Success(r))
            .ToList();
    }

    /// <summary>
    /// How long a title waits after its <paramref name="consecutiveFailures"/>-th transport failure in
    /// a row: 30 minutes, doubling, capped at a day. A sleeping laptop host turned "one later run"
    /// into another dropped connection; a doubling wait spreads the retries across real time.
    /// </summary>
    public static TimeSpan TransportBackoff(int consecutiveFailures)
    {
        var exponent = Math.Clamp(consecutiveFailures - 1, 0, 10);
        var wait = TimeSpan.FromMinutes(30 * Math.Pow(2, exponent));
        return wait > MaxTransportBackoff ? MaxTransportBackoff : wait;
    }

    private sealed class ExtractionTally
    {
        public int Done { get; set; }

        public int Transport { get; set; }

        public int Parse { get; set; }

        public int Unclassified { get; set; }

        public int SkippedClaimed { get; set; }
    }

    /// <summary>Progress SSE, skipped for a global run — there is no account subscribed to it.</summary>
    private void Publish(Guid? accountId, SyncJob job)
    {
        if (accountId is not { } owner)
        {
            return;
        }

        events.Publish(owner, PipelineEventTypes.JobProgress, new Dictionary<string, object?>
        {
            ["kind"] = "enrich",
            ["pct"] = job.ProgressPct,
            ["message"] = job.ProgressMessage,
        });
    }

    private async Task ChainTrainAsync(Guid accountId, CancellationToken ct)
    {
        var hasTrain = await db.SyncJobs.AnyAsync(
            j => j.AccountId == accountId && (j.Kind == JobKind.Train || j.Kind == JobKind.Evaluate)
                && (j.Status == JobStatus.Pending || j.Status == JobStatus.Running), ct);
        if (!hasTrain)
        {
            db.SyncJobs.Add(new SyncJob
            {
                Id = Guid.NewGuid(),
                AccountId = accountId,
                Kind = JobKind.Train,
                Priority = 1,
                EnqueuedAt = DateTime.UtcNow,
            });

            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex) when (TrainJobHandler.IsUniqueViolation(ex))
            {
                // Check-then-insert, and this is the loser of the race: a concurrent enqueue (the
                // manual /model/train endpoint, or the nightly reconcile) already queued a Train
                // job, and the partial unique index on SyncJobs(AccountId, Kind) WHERE Status IN
                // ('Pending','Running') correctly refused the duplicate. Losing that race means the
                // work is already scheduled, which is exactly the desired end state — so it is not
                // an error to propagate and fail the enrich job over.
                db.SyncJobs.Local.Clear();
                logger.LogDebug("Train already queued for {AccountId} by a concurrent enqueue.", accountId);
            }
        }
    }
}
