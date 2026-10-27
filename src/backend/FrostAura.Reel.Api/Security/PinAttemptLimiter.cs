namespace FrostAura.Reel.Api.Security;

/// <summary>
/// Wrong settings-PIN attempts, per account: five inside fifteen minutes lock every PIN check on
/// that account until the oldest of the five is fifteen minutes old (K-4). While locked, a PIN is
/// not even compared — a correct guess during the lockout is refused like a wrong one, so the
/// lockout cannot be used as an oracle.
///
/// <para>A PIN is 4–8 digits. Without a limit the 4-digit space falls to a script in minutes; at five
/// per fifteen minutes it takes weeks, and every lockout is visible in the logs.</para>
///
/// <para><b>An attempt is taken before its PIN is compared</b> (HREV1-PLAT-1). The comparison is a
/// 100k-iteration PBKDF2, so a check-then-record limiter let every request already in flight pass the
/// lock check before the first failure was counted: forty wrong PINs sent at once got fourteen
/// comparisons. <see cref="TryBegin"/> counts recorded failures plus attempts still being compared
/// against the allowance, under one lock, so at most the remaining allowance is ever compared. An
/// attempt that ends without a verdict — the request was cancelled mid-hash — gives its place back
/// and counts as nothing.</para>
///
/// <para>A success clears the account's failures. Process memory, like <see cref="AskTurnLimiter"/>:
/// Reel runs one API process, and a restart that clears the count is far rarer than the attempts it
/// would forgive.</para>
/// </summary>
public sealed class PinAttemptLimiter(TimeProvider clock)
{
    public const int MaxFailures = 5;

    public static readonly TimeSpan Window = TimeSpan.FromMinutes(15);

    /// <summary>
    /// What a refusal caused only by attempts still in flight is told to wait. They resolve in well
    /// under a second, after which either the lock is real (and its own wait applies) or a place is free.
    /// </summary>
    private static readonly TimeSpan InFlightRetryAfter = TimeSpan.FromSeconds(1);

    private readonly Dictionary<Guid, AccountLog> logs = [];
    private readonly Lock gate = new();

    /// <summary>
    /// Takes one PIN attempt for <paramref name="accountId"/>, or says how long it is locked. The caller
    /// compares the PIN only with an attempt in hand, then reports <see cref="Attempt.Failed"/> or
    /// <see cref="Attempt.Succeeded"/> and disposes it.
    /// </summary>
    public (Attempt? Attempt, TimeSpan? LockedFor) TryBegin(Guid accountId)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        lock (gate)
        {
            if (!logs.TryGetValue(accountId, out var log))
            {
                log = new AccountLog();
                logs[accountId] = log;
            }

            Prune(log, now);
            if (log.Failures.Count >= MaxFailures)
            {
                var wait = log.Failures.Peek() + Window - now;
                return (null, wait < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : wait);
            }

            if (log.Failures.Count + log.InFlight >= MaxFailures)
            {
                return (null, InFlightRetryAfter);
            }

            log.InFlight++;
            return (new Attempt(this, accountId, log), null);
        }
    }

    private void Finish(Guid accountId, AccountLog log, bool? correct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        lock (gate)
        {
            log.InFlight = Math.Max(0, log.InFlight - 1);
            if (correct == false)
            {
                Prune(log, now);
                log.Failures.Enqueue(now);
                while (log.Failures.Count > MaxFailures)
                {
                    log.Failures.Dequeue();
                }
            }
            else if (correct == true)
            {
                log.Failures.Clear();
            }

            if (log.Failures.Count == 0 && log.InFlight == 0 && logs.TryGetValue(accountId, out var current) && ReferenceEquals(current, log))
            {
                logs.Remove(accountId);
            }
        }
    }

    private static void Prune(AccountLog log, DateTime now)
    {
        while (log.Failures.Count > 0 && now - log.Failures.Peek() >= Window)
        {
            log.Failures.Dequeue();
        }
    }

    internal sealed class AccountLog
    {
        public Queue<DateTime> Failures { get; } = new();

        public int InFlight { get; set; }
    }

    /// <summary>
    /// One PIN comparison's place in the allowance. The first verdict counts; disposing without one
    /// returns the place uncounted.
    /// </summary>
    public sealed class Attempt : IDisposable
    {
        private readonly PinAttemptLimiter owner;
        private readonly Guid accountId;
        private readonly AccountLog log;
        private int finished;

        internal Attempt(PinAttemptLimiter owner, Guid accountId, AccountLog log)
        {
            this.owner = owner;
            this.accountId = accountId;
            this.log = log;
        }

        /// <summary>The PIN was wrong: one failure is recorded.</summary>
        public void Failed() => Finish(false);

        /// <summary>The PIN was right: the account's failures are cleared.</summary>
        public void Succeeded() => Finish(true);

        public void Dispose() => Finish(null);

        private void Finish(bool? correct)
        {
            if (Interlocked.Exchange(ref finished, 1) == 0)
            {
                owner.Finish(accountId, log, correct);
            }
        }
    }
}
