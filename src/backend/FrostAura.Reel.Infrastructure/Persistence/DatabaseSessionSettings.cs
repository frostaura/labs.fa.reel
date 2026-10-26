using System.Globalization;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace FrostAura.Reel.Infrastructure.Persistence;

/// <summary>
/// Session settings every <see cref="ReelDbContext"/> connection carries (F+1, WS-6 task 9).
///
/// <para><b>Idle in transaction.</b> A SaveChanges transaction holds its row locks until it ends, and
/// no command timeout covers the time BETWEEN its statements: a client thread parked there — the
/// 2026-10 BuildFeed stalls parked on a full console-log queue, inside SaveChanges, between batches —
/// keeps the transaction open, idle, holding the job row and the account row for as long as it is
/// parked, and every heartbeat, sweep and save that touches those rows waits behind it. Postgres's
/// <c>idle_in_transaction_session_timeout</c> ends such a session, which rolls its transaction back and
/// frees the locks; the client's next statement fails and the job retries. Nothing in this codebase
/// legitimately idles inside a transaction: the two explicit transactions (the claim, a session
/// rotation) are database-only, and SaveChanges' own is statements back to back.</para>
///
/// <para><c>Database:IdleInTransactionTimeoutSeconds</c> overrides the default; <c>0</c> leaves the
/// server's setting alone.</para>
/// </summary>
public sealed class DatabaseSessionSettings
{
    public const string IdleInTransactionTimeoutKey = "Database:IdleInTransactionTimeoutSeconds";

    /// <summary>
    /// The default bound. A minute is two orders of magnitude above the longest gap SaveChanges
    /// leaves between batches, and well under the runner's 12-minute stale window, so a parked
    /// transaction is gone long before anything treats its job as dead.
    /// </summary>
    public static readonly TimeSpan DefaultIdleInTransactionTimeout = TimeSpan.FromSeconds(60);

    private DatabaseSessionSettings(string connectionString, TimeSpan? idleInTransactionTimeout)
    {
        ConnectionString = connectionString;
        IdleInTransactionTimeout = idleInTransactionTimeout;
    }

    /// <summary>The connection string the context uses: the configured one plus the session options.</summary>
    public string ConnectionString { get; }

    /// <summary>The bound applied, or null when it is left to the server.</summary>
    public TimeSpan? IdleInTransactionTimeout { get; }

    public static DatabaseSessionSettings From(string connectionString, IConfiguration? configuration)
    {
        var timeout = DefaultIdleInTransactionTimeout;
        var raw = configuration?[IdleInTransactionTimeoutKey];
        if (!string.IsNullOrWhiteSpace(raw)
            && int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds)
            && seconds >= 0)
        {
            timeout = TimeSpan.FromSeconds(seconds);
        }

        return With(connectionString, timeout > TimeSpan.Zero ? timeout : null);
    }

    /// <summary>The settings for an explicit bound (null leaves the server's setting alone). For tests.</summary>
    public static DatabaseSessionSettings With(string connectionString, TimeSpan? idleInTransactionTimeout)
    {
        if (idleInTransactionTimeout is not { } bound)
        {
            return new DatabaseSessionSettings(connectionString, null);
        }

        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        var option = string.Create(
            CultureInfo.InvariantCulture, $"-c idle_in_transaction_session_timeout={(long)bound.TotalMilliseconds}");
        builder.Options = string.IsNullOrWhiteSpace(builder.Options) ? option : $"{builder.Options} {option}";
        return new DatabaseSessionSettings(builder.ConnectionString, bound);
    }
}
