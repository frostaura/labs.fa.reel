using FrostAura.Reel.Application.Auth;
using FrostAura.Reel.Domain.Tenancy;
using Microsoft.Extensions.Options;

namespace FrostAura.Reel.Api.Security;

/// <summary>
/// The inbound Ask Reel turn limits, partitioned by account (FD-8, J-8): turns in flight, turns
/// STARTED in the last minute and hour (rolling), and turns started since the account's own local
/// midnight. A refused request records nothing, so a client that keeps retrying into a limit does
/// not push its own window further out.
///
/// <para><b>The day is a calendar day in the account's time zone</b> (R1-15, ADD-22), counted from
/// <see cref="AccountTimeZone.DayStartUtc"/>, not a rolling 24 hours. The ledger's per-account Ask
/// allowance (<c>IBudgetCalendar</c>) resets at that same local midnight, and the 429 says when the
/// day's turns come back; a rolling window here would refuse with a wait that ends at a different
/// moment from the plan allowance it fronts. An account with no zone counts the UTC day.</para>
///
/// <para><b>A refused request is not a turn</b> (HREV1-PLAT-2). The limit is taken at the edge, before
/// the handler has read the body, so a request the handler then refuses — a malformed body, a missing
/// turn id, an unknown conversation — hands its start back with <see cref="Lease.Refund"/>. Without
/// that, six malformed asks spent the minute's six turns and six of the hour's twenty although nothing
/// had been answered. The in-flight slot still applies while the handler decides.</para>
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

    /// <summary>
    /// How long a start is remembered: longer than any local day (at most 25 hours), whatever zone
    /// the account switches to, and longer than the hour window.
    /// </summary>
    private static readonly TimeSpan Retention = TimeSpan.FromHours(26);

    /// <summary>What a turn in flight is told to wait: long enough for most turns to finish.</summary>
    internal static readonly TimeSpan InFlightRetryAfter = TimeSpan.FromSeconds(15);

    private readonly Dictionary<Guid, AccountLog> logs = [];
    private readonly Lock gate = new();

    /// <summary>
    /// A refusal: the HTTP status, the stable code the client switches on, and how long until a slot
    /// frees. A turn already in flight is <c>409 turn_in_progress</c> — the same answer the Ask
    /// handler gives a second question on a busy thread, which the client already turns into "one
    /// moment" with a check-again — and every window limit is <c>429</c>.
    /// </summary>
    public sealed record Refusal(int Status, string Code, TimeSpan RetryAfter, string Message);

    /// <summary>
    /// Admits one turn for <paramref name="accountId"/> or says why not. An admitted turn holds an
    /// in-flight slot until the returned lease is disposed — at the end of the response, streaming
    /// included. <paramref name="timeZoneId"/> is the account's stored zone
    /// (<c>AccountSettings.TimeZone</c>); null or unresolvable counts the UTC day.
    /// </summary>
    public (Lease? Lease, Refusal? Refusal) TryStart(Guid accountId, AccountTier tier, string? timeZoneId = null)
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

            log.Started.RemoveAll(t => now - t >= Retention);

            if (log.InFlight >= limits.Concurrent)
            {
                return (null, new Refusal(
                    StatusCodes.Status409Conflict,
                    "turn_in_progress",
                    InFlightRetryAfter,
                    "Another question is still being answered — one moment."));
            }

            var refusal = Check(log, now, Minute, limits.PerMinute, "ask_rate_minute", "a minute")
                ?? Check(log, now, Hour, limits.PerHour, "ask_rate_hour", "an hour")
                ?? CheckDay(log, now, timeZoneId, limits.PerDayFor(tier));
            if (refusal is not null)
            {
                return (null, refusal);
            }

            log.Started.Add(now);
            log.InFlight++;
            return (new Lease(this, log, now), null);
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
            StatusCodes.Status429TooManyRequests,
            code,
            wait < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : wait,
            $"That's the most questions Reel answers in {per} ({limit.ToString(System.Globalization.CultureInfo.InvariantCulture)}).");
    }

    /// <summary>The account's calendar day: every turn started since local midnight counts, and the day's turns come back at the next one.</summary>
    private static Refusal? CheckDay(AccountLog log, DateTime now, string? timeZoneId, int limit)
    {
        var dayStart = AccountTimeZone.DayStartUtc(timeZoneId, now);
        if (log.Started.Count(t => t >= dayStart) < limit)
        {
            return null;
        }

        var wait = AccountTimeZone.NextDayStartUtc(timeZoneId, now) - now;
        return new Refusal(
            StatusCodes.Status429TooManyRequests,
            "ask_rate_day",
            wait < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : wait,
            $"That's the most questions Reel answers in a day ({limit.ToString(System.Globalization.CultureInfo.InvariantCulture)}).");
    }

    private void Release(AccountLog log)
    {
        lock (gate)
        {
            log.InFlight = Math.Max(0, log.InFlight - 1);
        }
    }

    private void Unstart(AccountLog log, DateTime started)
    {
        lock (gate)
        {
            // Equal timestamps are interchangeable, so removing any one of them returns exactly one slot.
            log.Started.Remove(started);
        }
    }

    /// <summary>Started times, oldest first (appended in clock order under the lock).</summary>
    internal sealed class AccountLog
    {
        public List<DateTime> Started { get; } = [];

        public int InFlight { get; set; }
    }

    /// <summary>
    /// An admitted turn: holds its in-flight slot until disposed. <see cref="Refund"/> also hands back
    /// the window slot it took, for a request the handler refused before answering anything.
    /// </summary>
    public sealed class Lease : IDisposable
    {
        private readonly AskTurnLimiter owner;
        private readonly AccountLog log;
        private readonly DateTime started;
        private int disposed;
        private int refunded;

        internal Lease(AskTurnLimiter owner, AccountLog log, DateTime started)
        {
            this.owner = owner;
            this.log = log;
            this.started = started;
        }

        /// <summary>Uncounts this turn from every window. Once only; the in-flight slot is still released by Dispose.</summary>
        public void Refund()
        {
            if (Interlocked.Exchange(ref refunded, 1) == 0)
            {
                owner.Unstart(log, started);
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
            {
                owner.Release(log);
            }
        }
    }
}
