using System.Diagnostics;

namespace FrostAura.Reel.Application.Llm;

/// <summary>Why a budgeted batch loop stopped. Callers log against this rather than re-deriving it.</summary>
public enum LlmBudgetStop
{
    /// <summary>Every batch ran.</summary>
    Completed,

    /// <summary>The soft budget expired between batches. Whatever completed is kept.</summary>
    SoftBudget,

    /// <summary>The hard ceiling fired inside a batch. That batch's work is lost; earlier batches are kept.</summary>
    HardCeiling,

    /// <summary>The provider failed (transport / client-side timeout). Earlier batches are kept.</summary>
    Failed,

    /// <summary>The concurrency gate was still full when the budget expired, so nothing was attempted.</summary>
    GateUnavailable,
}

/// <summary>
/// The knobs of one budgeted loop. <paramref name="Budget"/> is the SOFT deadline checked between
/// batches; <paramref name="Ceiling"/> is the hard backstop against a hung provider.
/// <paramref name="Gate"/> is an optional process-wide concurrency limiter held for the whole loop.
/// </summary>
public sealed record LlmBudgetOptions(TimeSpan Budget, TimeSpan Ceiling, int BatchSize, SemaphoreSlim? Gate = null)
{
    /// <summary>
    /// A loop with no time box — batching and failure handling only. Used where the caller's own
    /// request timeout is the only deadline that makes sense (Ask Reel streams each batch to the
    /// client as it lands, so a partial answer is already the product's behaviour).
    /// </summary>
    public static LlmBudgetOptions Unbounded(int batchSize) =>
        new(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan, batchSize);
}

/// <summary>
/// The one budgeted LLM batch loop, extracted from the three hand-copied versions that had drifted
/// apart: the feed why pass (20s soft / 60s hard, batch 9, shared clock across rails), the
/// paginator's curation pass (4s / 20s, batch 9, <c>SemaphoreSlim(4)</c>) and Ask Reel's rerank
/// stream (batch 6, no box). Lens generation is the fourth caller, and the reason this exists as a
/// type rather than a fourth copy.
///
/// <para><b>The semantics are load-bearing and were identical in all three copies, for the same
/// reason.</b> The soft budget is checked BEFORE starting a batch and never during one: a batch
/// cancelled part-way returns nothing and caches nothing, so the spend is simply lost and the next
/// run repeats it. The FIRST batch always runs — otherwise a cold cache under load never makes
/// forward progress and the cache never warms. The hard ceiling exists only for a provider that
/// has stopped answering at all.</para>
///
/// <para><b>One instance = one clock.</b> The feed's why pass runs a loop per rail against a single
/// shared deadline, and the hero-swap pass afterwards draws on the same clock — a swap is part of
/// that why pass, not a second budget. So the stopwatch, the linked cancellation source and the
/// batch counter live on the instance and <see cref="RunAsync"/> may be called repeatedly.</para>
///
/// <para>Disposal releases the gate (only if it was actually acquired — releasing a semaphore that
/// was never entered permanently inflates its count and uncaps concurrency for the life of the
/// process) and the linked cancellation source.</para>
/// </summary>
public sealed class LlmBudgetLoop : IDisposable
{
    private readonly LlmBudgetOptions _options;
    private readonly CancellationTokenSource _cts;
    private readonly CancellationToken _outer;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private bool _holdsGate;
    private bool _disposed;

    public LlmBudgetLoop(LlmBudgetOptions options, CancellationToken ct)
    {
        _options = options;
        _outer = ct;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (options.Ceiling != Timeout.InfiniteTimeSpan)
        {
            _cts.CancelAfter(options.Ceiling);
        }
    }

    /// <summary>The token every provider call inside this loop must use — outer cancellation OR the ceiling.</summary>
    public CancellationToken Token => _cts.Token;

    /// <summary>Wall clock since construction. Reported in the caller's log line.</summary>
    public TimeSpan Elapsed => _clock.Elapsed;

    /// <summary>How many batches have completed across every <see cref="RunAsync"/> call on this loop.</summary>
    public int BatchesCompleted { get; private set; }

    /// <summary>How many items those batches covered.</summary>
    public int ItemsProcessed { get; private set; }

    /// <summary>
    /// Acquires the shared concurrency gate, waiting no longer than the ceiling. Returns false when
    /// the wait was cut short — the caller then skips generation entirely rather than piling on.
    /// A loop with no gate always succeeds.
    /// </summary>
    public async Task<bool> TryAcquireGateAsync()
    {
        if (_options.Gate is null || _holdsGate)
        {
            return true;
        }

        try
        {
            await _options.Gate.WaitAsync(_cts.Token);
            _holdsGate = true;
            return true;
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested && !_outer.IsCancellationRequested)
        {
            return false;
        }
    }

    /// <summary>
    /// Runs <paramref name="processBatch"/> over <paramref name="items"/> in batches of
    /// <see cref="LlmBudgetOptions.BatchSize"/>, honouring the soft budget between batches and the
    /// hard ceiling inside one. Never throws for a provider failure or a budget expiry — the
    /// outcome says which happened, and the caller decides what to log and what to fall back to.
    /// Outer cancellation (the caller's own token) still propagates: that is the request going away,
    /// not a budget decision.
    /// </summary>
    public async Task<LlmBudgetStop> RunAsync<T>(
        IReadOnlyList<T> items, Func<IReadOnlyList<T>, CancellationToken, Task> processBatch)
    {
        for (var offset = 0; offset < items.Count; offset += _options.BatchSize)
        {
            // Checked BEFORE starting a batch, never during one, and never before the FIRST batch
            // of the whole loop — see the type-level note.
            if (BatchesCompleted > 0 && Expired)
            {
                return LlmBudgetStop.SoftBudget;
            }

            var batch = Slice(items, offset, _options.BatchSize);
            try
            {
                await processBatch(batch, _cts.Token);
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested && !_outer.IsCancellationRequested)
            {
                return LlmBudgetStop.HardCeiling;
            }
            // TaskCanceledException derives from OperationCanceledException, so this clause has to
            // exclude the case where the CALLER went away — that is the request being abandoned,
            // not a provider failure, and swallowing it would let the loop keep spending on work
            // nobody is waiting for.
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
                && !_outer.IsCancellationRequested)
            {
                return LlmBudgetStop.Failed;
            }

            BatchesCompleted++;
            ItemsProcessed += batch.Count;
        }

        return LlmBudgetStop.Completed;
    }

    private bool Expired => _options.Budget != Timeout.InfiniteTimeSpan && _clock.Elapsed > _options.Budget;

    /// <summary>True once the soft budget has passed — for a caller deciding whether a follow-up pass is affordable.</summary>
    public bool BudgetExhausted => Expired;

    private static List<T> Slice<T>(IReadOnlyList<T> items, int offset, int size)
    {
        var count = Math.Min(size, items.Count - offset);
        var slice = new List<T>(count);
        for (var i = 0; i < count; i++)
        {
            slice.Add(items[offset + i]);
        }

        return slice;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_holdsGate)
        {
            _options.Gate!.Release();
            _holdsGate = false;
        }

        _cts.Dispose();
    }
}
