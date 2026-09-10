using System.Collections.Concurrent;
using FrostAura.Reel.Domain.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using FrostAura.Reel.Infrastructure.Persistence;
using Npgsql;
using NpgsqlTypes;

namespace FrostAura.Reel.Infrastructure.Telemetry;

/// <summary>
/// Lock-free in-memory counters flushed to the ExternalApiUsages day ledger every 15s.
/// Fair-Use monitoring runs from day 1 — the M4 exit criterion reads this table.
///
/// The ledger was one bucket per (provider, day) with TokensUsed written as a literal NULL, which
/// made "what does a feed build cost" and "what has this account spent" unanswerable, and put
/// embeddings in the same bucket as chat. The key is now
/// (Provider, Day, AccountId, Model, Purpose); the flush shape is unchanged — a concurrent
/// dictionary drained on a timer, never a write on the request path.
///
/// Callers must record AFTER the response, so the token counts are in hand. A call that fails
/// before a response body is still worth a row: pass no tokens and the columns stay NULL, which
/// reads as "never reported" rather than "cost nothing".
/// </summary>
public sealed class ApiUsageRecorder(IServiceScopeFactory scopeFactory, ILogger<ApiUsageRecorder> logger)
    : BackgroundService, IPendingApiUsage
{
    private readonly ConcurrentDictionary<UsageKey, Counters> _pending = new();

    /// <summary>
    /// Records one external API call. <paramref name="purpose"/> is one of
    /// <see cref="ApiUsagePurpose"/> for LLM work and null elsewhere; <paramref name="model"/> is
    /// the billed model id; <paramref name="accountId"/> attributes the spend to a tenant where the
    /// call was made on their behalf. Token counts come from the provider's own
    /// <c>usage</c> block — never estimated here.
    /// </summary>
    public void Record(
        ApiProvider provider,
        string? purpose = null,
        string? model = null,
        Guid? accountId = null,
        long? promptTokens = null,
        long? completionTokens = null,
        long calls = 1)
    {
        var key = new UsageKey(provider, DateOnly.FromDateTime(DateTime.UtcNow), accountId, model, purpose);
        var counters = _pending.GetOrAdd(key, static _ => new Counters());

        Interlocked.Add(ref counters.Calls, calls);
        // Each side is tracked independently: the embeddings API bills prompt tokens and has no
        // completion side at all, so a reported prompt must not make the completion column read 0.
        if (promptTokens is { } prompt)
        {
            Interlocked.Add(ref counters.PromptTokens, prompt);
            Interlocked.Increment(ref counters.PromptReports);
        }

        if (completionTokens is { } completion)
        {
            Interlocked.Add(ref counters.CompletionTokens, completion);
            Interlocked.Increment(ref counters.CompletionReports);
        }
    }

    /// <inheritdoc />
    public (long Calls, long Tokens) PendingToday(ApiProvider provider, Guid? accountId, string purpose)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        long calls = 0, tokens = 0;
        // Iterating the whole dictionary rather than probing a key: the ledger's grain includes the
        // MODEL, which the caller has no business knowing, so a budget for a purpose has to sum
        // across whichever models served it. The dictionary holds at most one entry per
        // (provider, day, account, model, purpose) within a 15s window — this is a handful of rows.
        foreach (var (key, counters) in _pending)
        {
            if (key.Provider != provider || key.Day != today || key.AccountId != accountId
                || !string.Equals(key.Purpose, purpose, StringComparison.Ordinal))
            {
                continue;
            }

            calls += Interlocked.Read(ref counters.Calls);
            tokens += Interlocked.Read(ref counters.PromptTokens) + Interlocked.Read(ref counters.CompletionTokens);
        }

        return (calls, tokens);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        while (await WaitAsync(timer, stoppingToken))
        {
            await FlushAsync(stoppingToken);
        }

        await FlushAsync(CancellationToken.None); // final drain on shutdown
    }

    internal async Task FlushAsync(CancellationToken ct)
    {
        if (_pending.IsEmpty)
        {
            return;
        }

        var snapshot = _pending.Keys.ToList();
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ReelDbContext>();

            foreach (var key in snapshot)
            {
                if (!_pending.TryRemove(key, out var counters))
                {
                    continue;
                }

                var day = key.Day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
                var promptReported = Interlocked.Read(ref counters.PromptReports) > 0;
                var completionReported = Interlocked.Read(ref counters.CompletionReports) > 0;
                var prompt = Interlocked.Read(ref counters.PromptTokens);
                var completion = Interlocked.Read(ref counters.CompletionTokens);

                // Raw SQL with explicitly typed parameters, not interpolation: three of the five key
                // columns are legitimately null (a Trakt call has no model), and Npgsql cannot infer a
                // parameter's type from a bare null. The unique index is NULLS NOT DISTINCT, so those
                // nulls collide and this upserts instead of piling up a row per 15s tick.
                await db.Database.ExecuteSqlRawAsync(
                    """
                    INSERT INTO "ExternalApiUsages"
                        ("Id", "Provider", "Day", "AccountId", "Model", "Purpose",
                         "CallCount", "PromptTokens", "CompletionTokens", "TokensUsed")
                    VALUES (gen_random_uuid(), @provider, @day, @accountId, @model, @purpose,
                            @calls, @prompt, @completion, @tokens)
                    ON CONFLICT ("Provider", "Day", "AccountId", "Model", "Purpose")
                    DO UPDATE SET
                        "CallCount" = "ExternalApiUsages"."CallCount" + EXCLUDED."CallCount",
                        "PromptTokens" = CASE WHEN EXCLUDED."PromptTokens" IS NULL
                            THEN "ExternalApiUsages"."PromptTokens"
                            ELSE COALESCE("ExternalApiUsages"."PromptTokens", 0) + EXCLUDED."PromptTokens" END,
                        "CompletionTokens" = CASE WHEN EXCLUDED."CompletionTokens" IS NULL
                            THEN "ExternalApiUsages"."CompletionTokens"
                            ELSE COALESCE("ExternalApiUsages"."CompletionTokens", 0) + EXCLUDED."CompletionTokens" END,
                        "TokensUsed" = CASE WHEN EXCLUDED."TokensUsed" IS NULL
                            THEN "ExternalApiUsages"."TokensUsed"
                            ELSE COALESCE("ExternalApiUsages"."TokensUsed", 0) + EXCLUDED."TokensUsed" END
                    """,
                    [
                        Text("provider", key.Provider.ToString()),
                        new NpgsqlParameter("day", NpgsqlDbType.Date) { Value = day },
                        new NpgsqlParameter("accountId", NpgsqlDbType.Uuid) { Value = (object?)key.AccountId ?? DBNull.Value },
                        Text("model", key.Model),
                        Text("purpose", key.Purpose),
                        Bigint("calls", Interlocked.Read(ref counters.Calls)),
                        Bigint("prompt", promptReported ? prompt : null),
                        Bigint("completion", completionReported ? completion : null),
                        // TokensUsed stays prompt + completion for the readers that predate this change.
                        Bigint("tokens", promptReported || completionReported ? prompt + completion : null),
                    ],
                    ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "API usage flush failed; counters retry next tick.");
        }
    }

    private static NpgsqlParameter Text(string name, string? value) =>
        new(name, NpgsqlDbType.Text) { Value = (object?)value ?? DBNull.Value };

    private static NpgsqlParameter Bigint(string name, long? value) =>
        new(name, NpgsqlDbType.Bigint) { Value = (object?)value ?? DBNull.Value };

    private static async ValueTask<bool> WaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try
        {
            return await timer.WaitForNextTickAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>The ledger's grain. Null model/purpose/account are real values here, not placeholders.</summary>
    private readonly record struct UsageKey(
        ApiProvider Provider, DateOnly Day, Guid? AccountId, string? Model, string? Purpose);

    /// <summary>
    /// Mutable counters updated with Interlocked — fields, not properties, because
    /// <see cref="Interlocked.Add(ref long, long)"/> needs a ref to storage.
    /// </summary>
    private sealed class Counters
    {
        public long Calls;
        public long PromptTokens;
        public long CompletionTokens;

        /// <summary>How many prompt-token figures the provider reported; zero means "unknown".</summary>
        public long PromptReports;

        /// <summary>How many completion-token figures the provider reported; zero means "unknown".</summary>
        public long CompletionReports;
    }
}
