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
/// hours (A-8, C NEW-3, B NEW-1).</para>
/// </summary>
public sealed class AskReelJudge(ISearchAgent agent, IMemoryCache cache, ILogger<AskReelJudge> logger)
{
    /// <summary>The most titles one turn sends to the judge.</summary>
    public const int MaxJudged = 16;

    /// <summary>Titles per judge call.</summary>
    public const int BatchSize = 6;

    /// <summary>Judge calls in flight at once for one turn.</summary>
    public const int MaxConcurrent = 3;

    /// <summary>How long a model verdict is reused for the same account, title and request.</summary>
    public static readonly TimeSpan FitTtl = TimeSpan.FromHours(6);

    /// <summary>One batch's verdicts, row-aligned to its inputs, and whether the model produced any of them.</summary>
    /// <param name="Index">The batch's position in pool order, zero-based.</param>
    /// <param name="Inputs">The titles in this batch, in pool order.</param>
    /// <param name="Results">The verdict per title, keyed by title id.</param>
    /// <param name="Failed">True when no title in the batch carries a model fit: the batch produced nothing usable.</param>
    public sealed record Batch(int Index, IReadOnlyList<RerankInput> Inputs, IReadOnlyDictionary<Guid, RerankResult> Results, bool Failed);

    /// <summary>What judging did overall.</summary>
    /// <param name="Batches">How many batches ran.</param>
    /// <param name="FailedBatches">How many of them produced no model verdict at all.</param>
    /// <param name="Results">Every verdict, by title.</param>
    public sealed record Summary(int Batches, int FailedBatches, IReadOnlyDictionary<Guid, RerankResult> Results);

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
        var batches = new List<List<RerankInput>>();
        for (var offset = 0; offset < judged.Count; offset += BatchSize)
        {
            batches.Add(judged.Skip(offset).Take(BatchSize).ToList());
        }

        using var gate = new SemaphoreSlim(MaxConcurrent, MaxConcurrent);
        var pending = batches
            .Select((inputs, index) => RunBatchAsync(index, inputs, accountId, query, qnorm, persona, usagePurpose, gate, ct))
            .ToList();

        var all = new Dictionary<Guid, RerankResult>();
        var failed = 0;
        while (pending.Count > 0)
        {
            var finished = await Task.WhenAny(pending);
            pending.Remove(finished);
            var batch = await finished;
            if (batch.Failed)
            {
                failed++;
            }

            foreach (var (id, result) in batch.Results)
            {
                all[id] = result;
            }

            await onBatch(batch);
        }

        return new Summary(batches.Count, failed, all);
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
        CancellationToken ct)
    {
        var results = new Dictionary<Guid, RerankResult>();
        var uncached = new List<RerankInput>();
        foreach (var input in inputs)
        {
            if (cache.TryGetValue<RerankResult>(FitKey(accountId, input.TitleId, qnorm, agent.ModelId), out var hit)
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
                using var loop = new LlmBudgetLoop(LlmBudgetOptions.Unbounded(BatchSize), ct);
                var stop = await loop.RunAsync(uncached, async (slice, sliceCt) =>
                {
                    var answered = await agent.RerankBatchAsync(slice, query, persona, anchorName: null, usagePurpose, sliceCt);
                    foreach (var result in answered)
                    {
                        results[result.TitleId] = result;
                        if (result.FitSource == ResultSource.Llm)
                        {
                            cache.Set(FitKey(accountId, result.TitleId, qnorm, agent.ModelId), result, FitTtl);
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

        var failed = !inputs.Any(i => results.TryGetValue(i.TitleId, out var r) && r.FitSource == ResultSource.Llm);
        return new Batch(index, inputs, results, failed);
    }

    /// <summary>The query as cached: lower-cased, whitespace collapsed.</summary>
    internal static string NormalizeQuery(string query) =>
        string.Join(' ', query.ToLowerInvariant().Split([' ', '\t', '\n'], StringSplitOptions.RemoveEmptyEntries));

    /// <summary>
    /// The per-(account, title, request) verdict key, versioned by the prompt generation and the model:
    /// a verdict is the output of one prompt against one model, and serving a six-hour-old verdict
    /// from the previous prompt is how a prompt fix looks like it did nothing.
    /// </summary>
    internal static string FitKey(Guid accountId, Guid titleId, string qnorm, string modelId) => string.Create(
        System.Globalization.CultureInfo.InvariantCulture,
        $"rerank:{AskReelPromptVersion.Current}:{modelId}:{accountId}:{titleId}:{qnorm}");
}
