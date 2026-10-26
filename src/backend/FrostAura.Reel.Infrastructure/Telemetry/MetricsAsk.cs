using System.Text.Json;
using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Domain.Search;
using Microsoft.EntityFrameworkCore;

namespace FrostAura.Reel.Infrastructure.Telemetry;

/// <summary>A latency distribution in milliseconds; null figures when there is no sample.</summary>
public sealed record LatencyStats(int Samples, long? P50, long? P95, long? Max);

/// <summary>One Ask stage's latency (a key of <c>AskStageTimings</c>).</summary>
public sealed record AskStageLatency(string Stage, LatencyStats Latency);

/// <summary>The caller's Ask Reel turns over the report window.</summary>
/// <param name="Turns">Assistant turns that settled in the window (completed or failed).</param>
/// <param name="Failed">Of those, turns that failed.</param>
/// <param name="PlanFallbacks">Turns whose plan fell back to the heuristic reading (no model, or the plan call failed).</param>
/// <param name="JudgeFallbacks">Turns whose request judge failed or left titles unjudged.</param>
/// <param name="Latency">Whole-turn wall time.</param>
/// <param name="Stages">Per stage, in pipeline order, from the stored stage timings.</param>
public sealed record AskMetrics(
    int Turns,
    int Failed,
    int PlanFallbacks,
    int JudgeFallbacks,
    LatencyStats Latency,
    IReadOnlyList<AskStageLatency> Stages);

/// <summary>
/// Ask Reel stage latency and fallbacks (K-7), read back from the turn record WS-2 writes
/// (<c>LatencyMs</c>, <c>StageTimingsJson</c>, <c>DegradedFlags</c>), so the figures survive a restart
/// and agree with what each turn stored. Caller's turns only.
/// </summary>
public sealed class AskMetricsQuery(IReelDbContext db)
{
    /// <summary>Most turns read per report; a busier window is reported on its newest turns.</summary>
    private const int TurnCap = 5_000;

    public async Task<AskMetrics> GetAsync(Guid accountId, DateTime since, CancellationToken ct)
    {
        var turns = await db.SearchTurns.AsNoTracking()
            .Where(t => t.AccountId == accountId
                && t.Role == "assistant"
                && t.Status != SearchTurnStatus.Pending
                && (t.CompletedAt ?? t.CreatedAt) >= since)
            .OrderByDescending(t => t.CompletedAt ?? t.CreatedAt)
            .Take(TurnCap)
            .Select(t => new { t.Status, t.LatencyMs, t.StageTimingsJson, t.DegradedFlags })
            .ToListAsync(ct);

        var stages = new Dictionary<string, List<long>>(StringComparer.Ordinal);
        foreach (var turn in turns)
        {
            foreach (var (stage, ms) in ReadTimings(turn.StageTimingsJson))
            {
                if (!stages.TryGetValue(stage, out var samples))
                {
                    stages[stage] = samples = [];
                }

                samples.Add(ms);
            }
        }

        return new AskMetrics(
            turns.Count,
            turns.Count(t => t.Status == SearchTurnStatus.Failed),
            turns.Count(t => SearchTurnDegraded.PlanFailed(t.DegradedFlags)),
            turns.Count(t => SearchTurnDegraded.JudgeFailed(t.DegradedFlags)),
            Stats(turns.Where(t => t.LatencyMs is not null).Select(t => (long)t.LatencyMs!.Value)),
            stages
                .OrderBy(s => StageOrder(s.Key))
                .ThenBy(s => s.Key, StringComparer.Ordinal)
                .Select(s => new AskStageLatency(s.Key, Stats(s.Value)))
                .ToList());
    }

    /// <summary>
    /// The stored timings: a JSON object of stage → milliseconds. A row this build cannot read (an
    /// older or newer shape) contributes nothing rather than failing the report.
    /// </summary>
    internal static IEnumerable<(string Stage, long Ms)> ReadTimings(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return [];
            }

            return document.RootElement.EnumerateObject()
                .Where(p => p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetInt64(out var v) && v >= 0)
                .Select(p => (p.Name, p.Value.GetInt64()))
                .ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>Nearest-rank percentiles.</summary>
    internal static LatencyStats Stats(IEnumerable<long> values)
    {
        var sorted = values.Order().ToArray();
        if (sorted.Length == 0)
        {
            return new LatencyStats(0, null, null, null);
        }

        return new LatencyStats(sorted.Length, Rank(sorted, 0.50), Rank(sorted, 0.95), sorted[^1]);
    }

    private static long Rank(long[] sorted, double quantile) =>
        sorted[Math.Clamp((int)Math.Ceiling(quantile * sorted.Length) - 1, 0, sorted.Length - 1)];

    private static int StageOrder(string stage)
    {
        var index = -1;
        for (var i = 0; i < Application.Search.AskStageTimings.Order.Count; i++)
        {
            if (string.Equals(Application.Search.AskStageTimings.Order[i], stage, StringComparison.Ordinal))
            {
                index = i;
                break;
            }
        }

        return index < 0 ? int.MaxValue : index;
    }
}
