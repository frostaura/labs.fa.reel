using FrostAura.Reel.Application.Llm;
using FrostAura.Reel.Application.Pipeline;
using FrostAura.Reel.Domain.Sync;
using Microsoft.Extensions.Logging;

namespace FrostAura.Reel.Application.CrossReference;

/// <summary>
/// The cross-reference breaker ACROSS runs: after <see cref="ConsecutiveFailedRuns"/> runs in a row in
/// which every batch came back truncated or unanswered on the same model, effort and prompt, the
/// purpose is off for the rest of that UTC day — and the reason is on the status API.
///
/// <para><b>Why runs, not batches.</b> The per-run rule already stops a run after its first
/// unanswered batch. What it could not stop was the NEXT run: from 2026-10-14 every feed build
/// queued a pass, every pass paid for a batch that truncated at exactly 4,096 tokens, and the
/// account billed ~230k tokens a day for zero verdicts for nineteen days. A truncation on one model
/// and prompt is deterministic — the second identical run is evidence, the third is waste.</para>
///
/// <para><b>Keyed by what makes the failure deterministic</b> — the judge's run key (model and
/// effort) and its prompt version. A run on a different key is a different instrument and starts
/// the count again; a run with even one answered batch resets it. Only DETERMINISTIC failures count
/// — truncated, empty or unreadable answers. A refusal, an open breaker or a spent budget sends
/// nothing (the provider breaker and the budget gate own those), and a 5xx or a timeout says
/// nothing about whether the next identical run truncates: the runner's own retry of a transient
/// failure used to be the "second run" that turned the purpose off for a whole UTC day.</para>
///
/// <para>Process-local, like the provider breaker it opens: a restart forgets the count and, at
/// worst, buys two more failed runs before tripping again. Persisting it is the M5 purpose-health
/// columns (a WS-6 schema request).</para>
/// </summary>
public sealed class CrossReferenceRunBreaker(
    IProviderHealth health, ILogger<CrossReferenceRunBreaker>? logger = null, TimeProvider? clock = null)
{
    /// <summary>Consecutive fully-failed runs on one key that turn the purpose off for the day.</summary>
    public const int ConsecutiveFailedRuns = 2;

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly object _lock = new();
    private readonly Dictionary<(string RunKey, int PromptVersion), int> _failedRuns = [];

    /// <summary>The open breaker that stops cross-reference now (its own or the provider's), or null.</summary>
    public ProviderBreaker? OpenBreaker => health.OpenBreakerFor(ApiProvider.OpenRouter, ApiUsagePurpose.CrossReference);

    /// <summary>
    /// Records how a run ended. Returns true when THIS run tripped the breaker. A run with any
    /// answered batch resets its key; <paramref name="unansweredBatches"/> is the run's
    /// DETERMINISTIC failures only, and a run with none is not counted.
    /// </summary>
    public bool RecordRun(string runKey, int promptVersion, int answeredBatches, int unansweredBatches)
    {
        lock (_lock)
        {
            var key = (runKey, promptVersion);
            if (answeredBatches > 0)
            {
                _failedRuns.Remove(key);
                return false;
            }

            if (unansweredBatches == 0)
            {
                return false;
            }

            var count = _failedRuns.GetValueOrDefault(key) + 1;
            if (count < ConsecutiveFailedRuns)
            {
                _failedRuns[key] = count;
                return false;
            }

            _failedRuns.Remove(key);
        }

        var until = _clock.GetUtcNow().UtcDateTime.Date.AddDays(1);
        health.OpenPurpose(ApiProvider.OpenRouter, ApiUsagePurpose.CrossReference, ProviderBreakerReasons.TruncationBreaker, until);
        logger?.LogWarning(
            "Cross-reference is off until {Until:O}: {Runs} consecutive runs on {RunKey} (prompt v{Version}) answered no batch.",
            until, ConsecutiveFailedRuns, runKey, promptVersion);
        return true;
    }
}
