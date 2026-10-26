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
/// <para>A success clears the account's failures. Process memory, like <see cref="AskTurnLimiter"/>:
/// Reel runs one API process, and a restart that clears the count is far rarer than the attempts it
/// would forgive.</para>
/// </summary>
public sealed class PinAttemptLimiter(TimeProvider clock)
{
    public const int MaxFailures = 5;

    public static readonly TimeSpan Window = TimeSpan.FromMinutes(15);

    private readonly Dictionary<Guid, Queue<DateTime>> failures = [];
    private readonly Lock gate = new();

    /// <summary>How long <paramref name="accountId"/> stays locked, or null when it may try a PIN.</summary>
    public TimeSpan? LockedFor(Guid accountId)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        lock (gate)
        {
            if (!failures.TryGetValue(accountId, out var log))
            {
                return null;
            }

            Prune(log, now);
            if (log.Count < MaxFailures)
            {
                return null;
            }

            var wait = log.Peek() + Window - now;
            return wait < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : wait;
        }
    }

    public void RecordFailure(Guid accountId)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        lock (gate)
        {
            if (!failures.TryGetValue(accountId, out var log))
            {
                log = new Queue<DateTime>();
                failures[accountId] = log;
            }

            Prune(log, now);
            log.Enqueue(now);
            while (log.Count > MaxFailures)
            {
                log.Dequeue();
            }
        }
    }

    public void RecordSuccess(Guid accountId)
    {
        lock (gate)
        {
            failures.Remove(accountId);
        }
    }

    private static void Prune(Queue<DateTime> log, DateTime now)
    {
        while (log.Count > 0 && now - log.Peek() >= Window)
        {
            log.Dequeue();
        }
    }
}
