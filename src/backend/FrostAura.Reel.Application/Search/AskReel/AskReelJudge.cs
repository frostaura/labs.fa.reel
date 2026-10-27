using FrostAura.Reel.Application.Llm;
using FrostAura.Reel.Domain.Lens;
using FrostAura.Reel.Domain.Ports;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace FrostAura.Reel.Application.Search.AskReel;

/// <summary>
/// Judges the top of a turn's pool against the request: at most <see cref="MaxJudged"/> titles, in
/// batches of at most <see cref="BatchSize"/>, at most <see cref="MaxConcurrent"/> batches in flight,
/// each batch handed to the caller the moment it returns.
///
/// <para><b>Why only the top sixteen, and why concurrently.</b> The old loop judged all 24 returned
/// cards in four strictly sequential batches, about ten seconds each on a reasoning model — the
/// rerank stage alone ran 37–39 s at p50 (A-7, B-7). Judging fewer titles halves the spend against
/// the shared re-rank family ceiling (A NEW-1); running the batches together removes the queue. It
/// does not reduce the number of calls counted against the budget, which is why both halves are
/// here (M-4).</para>
///
/// <para><b>Each batch still runs through WS-1's <see cref="LlmBudgetLoop"/></b> — one loop per batch,
/// gated by a semaphore — so "a provider failure is an outcome, not an exception" and "the caller's
/// own cancellation still propagates" are the shared definitions rather than a fourth copy.</para>
///
/// <para><b>What is cached, and what is not.</b> Only a verdict whose fit came from the model is
/// cached. A fallback — a failed batch, a truncated completion, the deterministic stub — is never
/// cached: caching one replayed "A action pick aligned with your taste." as a model verdict for six
/// hours (A-8, C NEW-3, B NEW-1). The key is the COMPOSED request (<see cref="AskReelJudgeRequest"/>),
/// so a verdict is reused only where the same thing was asked in the same context.</para>
///
/// <para><b>A deadline, and no orphaned calls.</b> The client's watchdog re-arms on keepalive bytes,
/// so a provider that never answers would hold a turn open for as long as the adapter's HTTP timeout
/// allowed (D7). Judging therefore has its own deadline, <see cref="Deadline"/>, measured from the
/// start of the stage: a batch still out when it passes is a failed batch (unjudged cards, the
/// degraded flag), never a hung turn. And every batch is awaited before this returns, on every path:
/// a cancelled turn or a throwing caller cancels the siblings and waits for them, so no batch
/// releases a disposed gate or keeps spending after the turn is gone.</para>
/// </summary>
public sealed class AskReelJudge(ISearchAgent agent, IMemoryCache cache, ILogger<AskReelJudge> logger)
{
    /// <summary>The most titles one turn sends to the judge.</summary>
    public const int MaxJudged = 16;

    /// <summary>Titles per judge call.</summary>
    public const int BatchSize = 6;

    /// <summary>
    /// Titles in the FIRST judge call — the top of the pool in fused order. Smaller than
    /// <see cref="BatchSize"/> on purpose: the first card cannot reach the screen before the first
    /// batch returns, and a judge's latency is dominated by the completion it writes, roughly one
    /// verdict per title. Sixteen titles still fit the three concurrent calls (4 + 6 + 6), so the
    /// split costs no extra call and no extra round trip — it only lets the likeliest matches answer
    /// first (R1-07, §4.11's first-card budget).
    /// </summary>
    public const int FirstBatchSize = 4;

    /// <summary>Judge calls in flight at once for one turn.</summary>
    public const int MaxConcurrent = 3;

    /// <summary>How long judging may take, from the start of the stage, before an outstanding batch counts as failed.</summary>
    public static readonly TimeSpan Deadline = TimeSpan.FromSeconds(25);

    /// <summary>How long a model verdict is reused for the same account, title and request.</summary>
    public static readonly TimeSpan FitTtl = TimeSpan.FromHours(6);

    /// <summary>One batch's verdicts, row-aligned to its inputs, and whether the model produced any of them.</summary>
    /// <param name="Index">The batch's position in pool order, zero-based.</param>
    /// <param name="Inputs">The titles in this batch, in pool order.</param>
    /// <param name="Results">The verdict per title, keyed by title id.</param>
    /// <param name="Failed">True when no title in the batch carries a model fit: the batch produced nothing usable.</param>
    /// <param name="Missing">
    /// How many titles in the batch came back without a request verdict (<see cref="Ruled"/> false) —
    /// every one of them when <paramref name="Failed"/>, and possibly some when it is not: a model that
    /// answers for five titles and skips the sixth is a partly failed batch, not a clean one (R1-01).
    /// </param>
    public sealed record Batch(
        int Index, IReadOnlyList<RerankInput> Inputs, IReadOnlyDictionary<Guid, RerankResult> Results, bool Failed, int Missing = 0);

    /// <summary>What judging did overall.</summary>
    /// <param name="Batches">How many batches ran.</param>
    /// <param name="FailedBatches">How many of them produced no model verdict at all.</param>
    /// <param name="Results">Every verdict, by title.</param>
    /// <param name="Judged">How many titles were sent to the judge.</param>
    /// <param name="Missing">How many of those came back without a request verdict, failed batches included.</param>
    public sealed record Summary(
        int Batches, int FailedBatches, IReadOnlyDictionary<Guid, RerankResult> Results, int Judged = 0, int Missing = 0)
    {
        /// <summary>Whether the model ruled on at least one title.</summary>
        public bool JudgedAny => Results.Values.Any(r => r.FitSource == ResultSource.Llm);
    }

    /// <summary>
    /// Whether a verdict is the model's ruling on the REQUEST: a model-sourced fit AND a
    /// <c>satisfies</c> value. A fallback entry (a skipped or malformed title, a failed batch) and a
    /// model entry that omitted <c>satisfies</c> are both unjudged — the gate cannot place either.
    /// </summary>
    public static bool Ruled(RerankResult? verdict) =>
        verdict is { FitSource: ResultSource.Llm, Satisfies: not null };

    /// <summary>
    /// The pool cut into judge calls: <see cref="FirstBatchSize"/> titles first, then
    /// <see cref="BatchSize"/> at a time, in pool order. Internal so the split is pinned by tests.
    /// </summary>
    internal static List<List<RerankInput>> Split(IReadOnlyList<RerankInput> judged)
    {
        var batches = new List<List<RerankInput>>();
        var offset = 0;
        while (offset < judged.Count)
        {
            var size = batches.Count == 0 ? FirstBatchSize : BatchSize;
            batches.Add(judged.Skip(offset).Take(size).ToList());
            offset += size;
        }

        return batches;
    }

    /// <summary>How many judge calls a pool of <paramref name="count"/> titles takes.</summary>
    public static int BatchCount(int count)
    {
        var judged = Math.Min(count, MaxJudged);
        return judged <= 0 ? 0
            : judged <= FirstBatchSize ? 1
            : 1 + ((judged - FirstBatchSize + BatchSize - 1) / BatchSize);
    }

    /// <summary>The judge's deadline; a test may shorten it. Never longer than <see cref="Deadline"/> in production.</summary>
    internal TimeSpan StageDeadline { get; init; } = Deadline;

    /// <summary>
    /// Judges <paramref name="pool"/> (already cut to <see cref="MaxJudged"/> by the caller or here)
    /// and calls <paramref name="onBatch"/> once per batch, in the order batches RETURN, one call at
    /// a time — so the caller can emit to a single stream without a lock of its own.
    /// </summary>
    public async Task<Summary> JudgeAsync(
        Guid accountId,
        string query,
        LensPersona persona,
        IReadOnlyList<RerankInput> pool,
        string usagePurpose,
        Func<Batch, Task> onBatch,
        CancellationToken ct)
    {
        var judged = pool.Take(MaxJudged).ToList();
        if (judged.Count == 0)
        {
            return new Summary(0, 0, new Dictionary<Guid, RerankResult>());
        }

        var qnorm = NormalizeQuery(query);
        var batches = Split(judged);

        var deadlineAt = DateTime.UtcNow + StageDeadline;
        using var gate = new SemaphoreSlim(MaxConcurrent, MaxConcurrent);
        using var siblings = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var pending = batches
            .Select((inputs, index) => RunBatchAsync(
                index, inputs, accountId, query, qnorm, persona, usagePurpose, gate, deadlineAt, siblings.Token))
            .ToList();

        var all = new Dictionary<Guid, RerankResult>();
        var failed = 0;
        var missing = 0;
        try
        {
            while (pending.Count > 0)
            {
                var finished = await Task.WhenAny(pending);
                pending.Remove(finished);
                var batch = await finished;
                if (batch.Failed)
                {
                    failed++;
                }

                missing += batch.Missing;

                foreach (var (id, result) in batch.Results)
                {
                    all[id] = result;
                }

                await onBatch(batch);
            }
        }
        finally
        {
            // Whatever ended the loop early — the turn being cancelled, or the caller's own handler
            // throwing — the batches still out are cancelled and AWAITED before the gate and the
            // token source are disposed. Their outcomes are discarded; their exceptions are observed.
            if (pending.Count > 0)
            {
                await siblings.CancelAsync();
                try
                {
                    await Task.WhenAll(pending);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    logger.LogDebug(ex, "Ask Reel judge: {Count} batch(es) abandoned with the turn.", pending.Count);
                }
            }
        }

        return new Summary(batches.Count, failed, all, judged.Count, missing);
    }

    private async Task<Batch> RunBatchAsync(
        int index,
        IReadOnlyList<RerankInput> inputs,
        Guid accountId,
        string query,
        string qnorm,
        LensPersona persona,
        string usagePurpose,
        SemaphoreSlim gate,
        DateTime deadlineAt,
        CancellationToken ct)
    {
        var results = new Dictionary<Guid, RerankResult>();
        var uncached = new List<RerankInput>();
        var salt = agent.CacheSalt(usagePurpose);
        var batchIds = inputs.Select(i => i.TitleId).ToList();
        foreach (var input in inputs)
        {
            if (cache.TryGetValue<RerankResult>(FitKey(salt, accountId, input.TitleId, qnorm, batchIds), out var hit)
                && hit is { FitSource: ResultSource.Llm })
            {
                results[input.TitleId] = hit;
            }
            else
            {
                uncached.Add(input);
            }
        }

        if (uncached.Count > 0)
        {
            await gate.WaitAsync(ct);
            try
            {
                // The ceiling is what is left of the stage's deadline, so a batch that waited for the
                // gate gets less, not a fresh allowance. A batch with nothing left is not attempted.
                var remaining = deadlineAt - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero)
                {
                    logger.LogWarning(
                        "Ask Reel judge batch {Batch} skipped: the judging deadline passed; its {Count} titles are unjudged.",
                        index, uncached.Count);
                    return Of(index, inputs, results);
                }

                using var loop = new LlmBudgetLoop(
                    new LlmBudgetOptions(Timeout.InfiniteTimeSpan, remaining, BatchSize), ct);
                var stop = await loop.RunAsync(uncached, async (slice, sliceCt) =>
                {
                    var answered = await agent.RerankBatchAsync(slice, query, persona, anchorName: null, usagePurpose, sliceCt);
                    foreach (var result in answered)
                    {
                        results[result.TitleId] = result;
                        if (result.FitSource == ResultSource.Llm)
                        {
                            cache.Set(FitKey(salt, accountId, result.TitleId, qnorm, batchIds), result, FitTtl);
                        }
                    }
                });

                if (stop is not LlmBudgetStop.Completed)
                {
                    logger.LogWarning(
                        "Ask Reel judge batch {Batch} stopped ({Stop}); its {Count} titles are unjudged.",
                        index, stop, uncached.Count);
                }
            }
            finally
            {
                gate.Release();
            }
        }

        return Of(index, inputs, results);
    }

    /// <summary>A batch's outcome: failed when the model ruled on none of it, missing counted title by title.</summary>
    private static Batch Of(int index, IReadOnlyList<RerankInput> inputs, IReadOnlyDictionary<Guid, RerankResult> results) =>
        new(
            index,
            inputs,
            results,
            Failed: !inputs.Any(i => results.TryGetValue(i.TitleId, out var r) && r.FitSource == ResultSource.Llm),
            Missing: inputs.Count(i => !Ruled(results.GetValueOrDefault(i.TitleId))));

    /// <summary>The query as cached: lower-cased, whitespace collapsed.</summary>
    internal static string NormalizeQuery(string query) =>
        string.Join(' ', query.ToLowerInvariant().Split([' ', '\t', '\n'], StringSplitOptions.RemoveEmptyEntries));

    /// <summary>
    /// The per-(account, title, request) verdict key, in the one shape every re-rank cache shares
    /// (<see cref="RerankCacheKey.For"/>, WS-1 CR-4): the agent's salt for the purpose — result
    /// version, model, the effort it runs at and the prompt and schema fingerprint — the account, the
    /// title, the request in its prompt generation, and the batch the title was judged among, because
    /// the prompt spreads fits across the batch. A verdict from another prompt, model, effort or
    /// company misses rather than serving as if nothing had changed.
    /// </summary>
    internal static string FitKey(string salt, Guid accountId, Guid titleId, string qnorm, IEnumerable<Guid> batch) =>
        RerankCacheKey.For("askfit", salt, accountId, titleId, $"{AskReelPromptVersion.Current}|{qnorm}", batch);
}
