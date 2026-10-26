using System.Collections.Concurrent;
using FrostAura.Reel.Application.CrossReference;
using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Domain.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FrostAura.Reel.Application.Llm;

/// <summary>
/// The answer to "may this call be sent?" — and, when it may, the reservation that holds its
/// share of the day's allowance until the call has been recorded on the ledger.
/// </summary>
public sealed class LlmAdmission : IDisposable
{
    private Action? _release;

    private LlmAdmission(bool admitted, string? refusal, Action? release)
    {
        Admitted = admitted;
        Refusal = refusal;
        _release = release;
    }

    /// <summary>Admitted with nothing reserved — the purpose has no per-account ceiling at the gate.</summary>
    public static LlmAdmission Unmetered { get; } = new(true, null, null);

    public bool Admitted { get; }

    /// <summary>Why the call was refused, for the log line. Never shown to a user.</summary>
    public string? Refusal { get; }

    public static LlmAdmission Admit(Action release) => new(true, null, release);

    public static LlmAdmission Refuse(string refusal) => new(false, refusal, null);

    /// <summary>Releases the reservation. Call AFTER the call has been recorded on the usage recorder.</summary>
    public void Dispose()
    {
        Interlocked.Exchange(ref _release, null)?.Invoke();
    }
}

/// <summary>Reserves a call's share of its purpose's daily allowance BEFORE the call is sent.</summary>
public interface ILlmCallGate
{
    /// <summary>
    /// Admits one call for <paramref name="purpose"/> on behalf of <paramref name="accountId"/>,
    /// spending up to <paramref name="estimatedTokens"/> (prompt + completion ceiling), or refuses
    /// it. A null account is catalogue-wide work and has no per-account ceiling here.
    /// </summary>
    Task<LlmAdmission> TryReserveAsync(Guid? accountId, string purpose, long estimatedTokens, CancellationToken ct);
}

/// <summary>
/// The per-account allowance gate every budgeted purpose passes through, inside the shared chat
/// client — so no caller can forget it.
///
/// <para><b>Reserve, then call — never call, then count.</b> The old gates read the ledger and then
/// spent, so N concurrent callers each saw the same headroom and all went through: a ceiling was a
/// forecast, not a limit. Here a call's worst case (one call, its prompt plus its completion
/// ceiling) is reserved under a per-account lock before it is sent, and released only after the
/// recorder holds the real figure. Concurrent callers therefore see each other, and the day's
/// total can never pass the ceiling — the acceptance's "no overshoot".</para>
///
/// <para><b>What is gated.</b> Every Ask Reel call: v2's three purposes against <see cref="AskBudget"/>,
/// and v1's question-reading pair (<c>search-agent</c>, <c>search-interpret</c>) against the plan
/// ceiling they share with <c>ask-plan</c>; the
/// re-rank family against <see cref="RerankBudget"/>, with the Ask members held to its Ask share
/// and the feed to its background fraction; cross-reference against
/// <see cref="CrossReferenceBudget"/>. Everything else — lens (its own budget runs at the caller),
/// extraction (catalogue-wide), probes — is unmetered here.</para>
///
/// <para>A singleton holding the reservations; the ledger is read through a fresh scope per check,
/// because the chat client that calls this is built inside request, job and singleton paths alike.
/// The recorder's un-flushed counters are read BEFORE the ledger, so a flush landing between the
/// two reads counts twice — conservative — rather than not at all.</para>
/// </summary>
public sealed class LlmCallGate(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    ILogger<LlmCallGate> logger) : ILlmCallGate
{
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _locks = new();
    private readonly object _reservationsLock = new();
    private readonly Dictionary<(Guid Account, string Purpose), (long Calls, long Tokens)> _reservations = [];

    public async Task<LlmAdmission> TryReserveAsync(
        Guid? accountId, string purpose, long estimatedTokens, CancellationToken ct)
    {
        if (accountId is not { } account || !IsGated(purpose))
        {
            return LlmAdmission.Unmetered;
        }

        var tokens = Math.Max(0, estimatedTokens);
        var gate = _locks.GetOrAdd(account, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var refusal = await RefusalAsync(account, purpose, tokens, ct);
            if (refusal is not null)
            {
                logger.LogInformation(
                    "LLM call for {Purpose} refused before it was sent for {AccountId}: {Refusal}.",
                    purpose, account, refusal);
                return LlmAdmission.Refuse(refusal);
            }

            Reserve(account, purpose, 1, tokens);
            return LlmAdmission.Admit(() => Reserve(account, purpose, -1, -tokens));
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// True for the purposes this gate meters per account: every Ask Reel call (v2's three purposes
    /// and v1's question-reading pair, see <see cref="AskBudget.PlanStage"/>), the re-rank family
    /// and cross-reference.
    /// </summary>
    public static bool IsGated(string purpose) =>
        AskBudget.Meters(purpose)
        || ApiUsagePurpose.RerankFamily.Contains(purpose)
        || purpose == ApiUsagePurpose.CrossReference;

    private async Task<string?> RefusalAsync(Guid account, string purpose, long tokens, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var services = scope.ServiceProvider;

        if (ApiUsagePurpose.RerankFamily.Contains(purpose))
        {
            var allowance = await services.GetRequiredService<RerankBudget>().AllowanceAsync(account, ct);
            foreach (var member in ApiUsagePurpose.RerankFamily)
            {
                var (c, t) = Reserved(account, member);
                allowance = allowance with
                {
                    CallsUsed = allowance.CallsUsed + c,
                    TokensUsed = allowance.TokensUsed + t,
                    AskCallsUsed = RerankBudget.IsAskMember(member) ? allowance.AskCallsUsed + c : allowance.AskCallsUsed,
                    AskTokensUsed = RerankBudget.IsAskMember(member) ? allowance.AskTokensUsed + t : allowance.AskTokensUsed,
                };
            }

            return allowance.Admits(purpose, tokens)
                ? null
                : $"re-rank family ceiling for {purpose} would be passed ({allowance.Describe()})";
        }

        if (purpose == ApiUsagePurpose.CrossReference)
        {
            var allowance = await services.GetRequiredService<CrossReferenceBudget>().AllowanceAsync(account, ct);
            var (c, t) = Reserved(account, purpose);
            return allowance.CallsUsed + c + 1 <= allowance.CallsLimit
                && allowance.TokensUsed + t + tokens <= allowance.TokensLimit
                ? null
                : $"cross-reference ceiling would be passed ({allowance.Describe()})";
        }

        // An Ask purpose, summed over every string that shares its ceiling: the v1 plan stage's
        // search-agent and search-interpret rows spend ask-plan's allowance, and the reverse.
        var ceiling = AskBudget.For(configuration, purpose)!;
        var members = AskBudget.SharedWith(purpose).ToArray();
        var pendingSource = services.GetService<IPendingApiUsage>();
        long pendingCalls = 0, pendingTokens = 0, reservedCalls = 0, reservedTokens = 0;
        foreach (var member in members)
        {
            var (pc, pt) = pendingSource?.PendingToday(ApiProvider.OpenRouter, account, member) ?? (0L, 0L);
            var (rc, rt) = Reserved(account, member);
            pendingCalls += pc;
            pendingTokens += pt;
            reservedCalls += rc;
            reservedTokens += rt;
        }

        var today = DateTime.UtcNow.Date;
        var rows = await services.GetRequiredService<IReelDbContext>().ExternalApiUsages
            .Where(u => u.Provider == ApiProvider.OpenRouter && u.AccountId == account
                && members.Contains(u.Purpose) && u.Day == today)
            .Select(u => new { u.CallCount, u.TokensUsed })
            .ToListAsync(ct);
        var calls = rows.Sum(r => r.CallCount) + pendingCalls + reservedCalls;
        var used = rows.Sum(r => r.TokensUsed ?? 0) + pendingTokens + reservedTokens;
        var shared = members.Length > 1 ? ", shared by the ask-plan stage" : string.Empty;
        return calls + 1 <= ceiling.Calls && used + tokens <= ceiling.Tokens
            ? null
            : $"{purpose} ceiling would be passed ({calls}/{ceiling.Calls} calls, {used}/{ceiling.Tokens} tokens{shared})";
    }

    private (long Calls, long Tokens) Reserved(Guid account, string purpose)
    {
        lock (_reservationsLock)
        {
            return _reservations.TryGetValue((account, purpose), out var r) ? r : (0, 0);
        }
    }

    private void Reserve(Guid account, string purpose, long calls, long tokens)
    {
        lock (_reservationsLock)
        {
            var key = (account, purpose);
            var (c, t) = _reservations.TryGetValue(key, out var r) ? r : (0, 0);
            c += calls;
            t += tokens;
            if (c <= 0)
            {
                _reservations.Remove(key);
            }
            else
            {
                _reservations[key] = (c, t);
            }
        }
    }
}
