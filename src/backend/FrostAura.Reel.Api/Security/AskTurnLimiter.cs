using FrostAura.Reel.Application.Auth;
using FrostAura.Reel.Domain.Tenancy;
using Microsoft.Extensions.Options;

namespace FrostAura.Reel.Api.Security;

/// <summary>
/// The inbound Ask Reel turn limits, partitioned by account (FD-8, J-8): turns in flight, and turns
/// STARTED in the last minute, hour and 24 hours. A refused request records nothing, so a client
/// that keeps retrying into a limit does not push its own window further out.
///
/// <para><b>Hand-rolled, not <c>System.Threading.RateLimiting</c>.</b> A chain of the framework's
/// window limiters consumes a permit from every window it passes before a later one refuses, so a
/// turn refused by the daily limit would still have spent minute and hour permits — and its
/// <c>Retry-After</c> would be the wrong window's. One timestamp log per account answers all three
/// windows exactly and says precisely when the first slot frees.</para>
///
/// <para><b>Process memory, deliberately.</b> A restart forgets the windows. The durable backstop is
/// the per-purpose LLM ceilings in the ledger (<c>AskBudget</c>), which a restart does not reset;
/// this layer exists to refuse the request before any of that is spent, and Reel runs one API
/// process.</para>
/// </summary>
public sealed class AskTurnLimiter(IOptions<AskTurnLimitOptions> options, TimeProvider clock)
{
    private static readonly TimeSpan Minute = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan Hour = TimeSpan.FromHours(1);
    private static readonly TimeSpan Day = TimeSpan.FromDays(1);

    /// <summary>What a turn in flight is told to wait: long enough for most turns to finish.</summary>
    internal static readonly TimeSpan InFlightRetryAfter = TimeSpan.FromSeconds(15);

    private readonly Dictionary<Guid, AccountLog> logs = [];
    private readonly Lock gate = new();

    /// <summary>A refusal: the stable code the client switches on, and how long until a slot frees.</summary>
    public sealed record Refusal(string Code, TimeSpan RetryAfter, string Message);

    /// <summary>
    /// Admits one turn for <paramref name="accountId"/> or says why not. An admitted turn holds an
    /// in-flight slot until the returned lease is disposed — at the end of the response, streaming
    /// included.
    /// </summary>
    public (IDisposable? Lease, Refusal? Refusal) TryStart(Guid accountId, AccountTier tier)
    {
        var limits = options.Value;
        var now = clock.GetUtcNow().UtcDateTime;
        lock (gate)
        {
            if (!logs.TryGetValue(accountId, out var log))
            {
                log = new AccountLog();
                logs[accountId] = log;
            }

            while (log.Started.Count > 0 && now - log.Started.Peek() >= Day)
            {
                log.Started.Dequeue();
            }

            if (log.InFlight >= limits.Concurrent)
            {
                return (null, new Refusal(
                    "ask_turn_in_flight",
                    InFlightRetryAfter,
                    "Another question is still being answered on this account — wait for it to finish, then ask again."));
            }

            var refusal = Check(log, now, Minute, limits.PerMinute, "ask_rate_minute", "a minute")
                ?? Check(log, now, Hour, limits.PerHour, "ask_rate_hour", "an hour")
                ?? Check(log, now, Day, limits.PerDayFor(tier), "ask_rate_day", "a day");
            if (refusal is not null)
            {
                return (null, refusal);
            }

            log.Started.Enqueue(now);
            log.InFlight++;
            return (new Lease(this, log), null);
        }
    }

    private static Refusal? Check(AccountLog log, DateTime now, TimeSpan window, int limit, string code, string per)
    {
        var inWindow = log.Started.Where(t => now - t < window).ToList();
        if (inWindow.Count < limit)
        {
            return null;
        }

        // The slot that frees first is the one taken `limit` turns ago, counted back from the newest.
        var frees = inWindow[inWindow.Count - limit] + window;
        var wait = frees - now;
        return new Refusal(
            code,
            wait < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : wait,
            $"That's the most questions Reel answers in {per} ({limit.ToString(System.Globalization.CultureInfo.InvariantCulture)}).");
    }

    private void Release(AccountLog log)
    {
        lock (gate)
        {
            log.InFlight = Math.Max(0, log.InFlight - 1);
        }
    }

    private sealed class AccountLog
    {
        public Queue<DateTime> Started { get; } = new();

        public int InFlight { get; set; }
    }

    private sealed class Lease(AskTurnLimiter owner, AccountLog log) : IDisposable
    {
        private int disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
            {
                owner.Release(log);
            }
        }
    }
}
