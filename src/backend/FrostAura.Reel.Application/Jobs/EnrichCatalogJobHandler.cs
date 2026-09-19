using System.Globalization;
using FrostAura.Reel.Application.Ml;
using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Application.Pipeline;
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
    /// that is not a prompt problem. The remaining budget here exists solely for genuinely
    /// transient transport failures — a 429, a provider outage — where the useful variable is
    /// elapsed time, and one later run is enough to catch it.
    ///
    /// The budget is spent per (title, extractor model, extractor version), not per account: the
    /// row is global, so an account whose enrich run walks past an exhausted title must not
    /// re-bill for it on behalf of everyone. Only a model change or a prompt-version bump — both
    /// of which change what the answer would BE — hands a title a fresh budget.
    /// </summary>
    private const int MaxAttributeAttempts = 2;

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
    /// The order a run works through the catalogue. For an account-scoped run the tiers are, in
    /// order: titles this account has actually been SHOWN (feed items), titles it has been SCORED
    /// (the shortlist a build persists), titles in its own LIBRARY (the training rows and loved
    /// centroids), then everything else in the shared catalogue.
    /// <para>
    /// This is what makes a capped run useful rather than arbitrary. Without it a cap of 200 over a
    /// 5,000-title catalogue ordered by id would enrich, on average, titles that have nothing to do
    /// with the account that asked — and the account would wait weeks for the features behind its
    /// own next feed. It is also what makes the sweep deterministic for a caller that knows what it
    /// seeded, which is the difference between an assertion about attempt budgets and a count of
    /// whatever else happened to be in a shared database.
    /// </para>
    /// <para>
    /// A GLOBAL run has no account to prioritise for, and takes the catalogue in SUPPLY order
    /// instead: highest <c>TmdbVoteCount</c> first, then most recent, with id as the tiebreak that
    /// keeps it deterministic. It used to be plain id order, which was fine while a global run meant
    /// the handful of rows a weekly growth sweep had just added, and became wrong the moment the
    /// catalogue bulk seed made the global backlog tens of thousands of rows deep: a capped run over
    /// that in id order enriches a uniformly random sample, so the titles an account is actually
    /// likely to be shown wait behind the ones nobody will ever see. Vote count then recency is the
    /// same axis the seed walks, and close to the order in which discovery would surface a title.
    /// </para>
    /// </summary>
    private IQueryable<Title> InPriorityOrder(IQueryable<Title> titles, Guid? accountId) =>
        accountId is { } id
            ? titles
                .OrderBy(t =>
                    db.FeedItems.Any(f => f.AccountId == id && f.TitleId == t.Id) ? 0
                    : db.TitleScores.Any(s => s.AccountId == id && s.TitleId == t.Id) ? 1
                    : db.UserRatings.Any(r => r.AccountId == id && r.TitleId == t.Id)
                        || db.WatchedTitles.Any(w => w.AccountId == id && w.TitleId == t.Id) ? 2
                    : 3)
                .ThenBy(t => t.Id)
            : titles
                .OrderByDescending(t => t.TmdbVoteCount)
                .ThenByDescending(t => t.Year ?? 0)
                .ThenBy(t => t.Id);

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

        var titles = await InPriorityOrder(work, accountId).Take(cap).ToListAsync(ct);
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
        // Eligible = not already settled BY THE CURRENT MODEL AT THE CURRENT PROMPT VERSION. A row
        // is settled when it is Done or has exhausted its retries under both. Model-scoping is what
        // lets a switch from the deterministic stub to the real model re-extract every title instead
        // of leaving stub data frozen in place; version-scoping is what lets a prompt change do the
        // same for values whose MEANING moved (see ITitleAttributeExtractor.Version). Note the
        // ordering of the two effects: an exhausted row with a stale version is eligible again,
        // which is deliberate — the reason it failed may be the reason the prompt changed.
        var modelId = extractor.ModelId;
        var version = extractor.Version;
        var pending = hydrated.Where(t => !db.TitleAttributes.Any(a =>
            a.TitleId == t.Id
            && a.ExtractorModel == modelId
            && a.ExtractorVersion >= version
            && (a.Status == AttributeExtractionStatus.Done || a.AttemptCount >= MaxAttributeAttempts)));

        var titles = await InPriorityOrder(pending, accountId).Take(cap).ToListAsync(ct);
        var total = titles.Count;
        var processed = 0;

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

            var inputs = batch.Select(t => new TitleAttributeInput(
                t.Id, t.MediaType.ToString(), t.Name, t.Year, t.Overview, t.Genres, t.Keywords)).ToList();
            var extracted = await extractor.ExtractAsync(inputs, ct);

            var batchIds = batch.Select(b => b.Id).ToList();
            var existing = await db.TitleAttributes
                .Where(a => batchIds.Contains(a.TitleId))
                .ToDictionaryAsync(a => a.TitleId, ct);

            for (var i = 0; i < batch.Length; i++)
            {
                var row = existing.GetValueOrDefault(batch[i].Id);
                if (row is null)
                {
                    row = new TitleAttributes { TitleId = batch[i].Id };
                    db.TitleAttributes.Add(row);
                }
                else if (row.ExtractorModel != modelId || row.ExtractorVersion < version)
                {
                    // A different model, or a newer prompt, is starting fresh — don't inherit the
                    // retries spent answering a different question.
                    row.AttemptCount = 0;
                }

                row.AttemptCount++;

                // Stamp model AND version BEFORE the outcome check. The eligibility query above only
                // counts attempts made at the current model and version, so a failed row that never
                // recorded one was re-picked on every single run for ever — the retry cap could not
                // engage and a permanently unextractable title billed indefinitely.
                row.ExtractorModel = modelId;
                row.ExtractorVersion = version;

                var result = extracted[i];
                if (result is null)
                {
                    // The extractor returns null only when it could not get a usable answer — a
                    // transport failure, or a response missing / mistyping a required scalar. It
                    // never substitutes a neutral value, so there is nothing to write here and the
                    // row must NOT be marked Done: a Done row is read as settled judgement for
                    // ever by every tenant's feature vector.
                    row.Status = AttributeExtractionStatus.Failed;
                    logger.LogWarning(
                        "Attribute extraction produced no usable result for {TitleId} ({Name}) on attempt {Attempt}/{Max} "
                        + "with {Model} v{Version}; row left {Status} rather than filled with defaults.",
                        batch[i].Id, batch[i].Name, row.AttemptCount, MaxAttributeAttempts, modelId, version,
                        AttributeExtractionStatus.Failed);
                    continue;
                }

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
                row.ExtractedAt = DateTime.UtcNow;
            }

            processed += batch.Length;
            job.ProgressPct = total == 0 ? 100 : Math.Round(50m + (50m * processed / total), 1);
            job.ProgressMessage = $"Reading the mood · {processed}/{total}";
            await db.SaveChangesAsync(ct);

            Publish(accountId, job);
        }

        // processed, not total: a run that stopped at a daily ceiling must not report the work it
        // selected as work it did.
        return processed;
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
