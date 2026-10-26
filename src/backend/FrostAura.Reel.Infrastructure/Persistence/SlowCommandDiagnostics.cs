using System.Collections.Concurrent;
using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using FrostAura.Reel.Domain.Sync;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace FrostAura.Reel.Infrastructure.Persistence;

/// <summary>
/// The job execution the current async flow belongs to, set by the job runner for the life of one
/// execution. Read by <see cref="SlowCommandDiagnostics"/> to say WHICH job a slow statement was
/// running for; an <see cref="AsyncLocal{T}"/> rather than an <c>Activity</c>, so nothing is added to
/// outgoing HTTP requests.
/// </summary>
public static class JobRunContext
{
    private static readonly AsyncLocal<(Guid Id, JobKind Kind, int Attempt)?> Holder = new();

    public static (Guid Id, JobKind Kind, int Attempt)? Current => Holder.Value;

    /// <summary>Marks the flow as running a job until the handle is disposed.</summary>
    public static IDisposable Enter(Guid id, JobKind kind, int attempt)
    {
        var previous = Holder.Value;
        Holder.Value = (id, kind, attempt);
        return new Restore(previous);
    }

    private sealed class Restore((Guid Id, JobKind Kind, int Attempt)? previous) : IDisposable
    {
        public void Dispose() => Holder.Value = previous;
    }
}

/// <summary>
/// Names a database stall while it is happening (F+1).
///
/// <para><b>Why it exists.</b> BuildFeed jobs stayed Running for 35 minutes to 4 hours after the
/// build itself finished in about a minute, and the logs that would have said why had rotated. The
/// stall sat after the build's stopwatch, where the handler only enqueues follow-ups and saves. A
/// statement that waits on a lock or runs on a bad plan says nothing until it returns, and an hour
/// later the blocker is gone. So this watches every statement while it runs.</para>
///
/// <para><b>What it does.</b> Every statement is registered as it starts and removed as it ends —
/// a dictionary write, nothing else. A sweep every <see cref="SweepInterval"/> looks at what is
/// still running: a statement past <see cref="Threshold"/> is logged once, and then every
/// <see cref="Repeat"/>, with its backend's wait event and the sessions blocking it (their state,
/// transaction age and statement head), read on a separate connection. A statement that finishes
/// past the threshold logs its total. Each line names the job (<see cref="JobRunContext"/>) and the
/// pinned account.</para>
///
/// <para>Statement heads are clipped to <see cref="HeadLength"/> characters. EF and the raw SQL in
/// this codebase are parameterised, so heads carry no values.</para>
/// </summary>
public sealed class SlowCommandDiagnostics : DbCommandInterceptor, IDisposable
{
    public static readonly TimeSpan Threshold = TimeSpan.FromSeconds(30);

    public static readonly TimeSpan Repeat = TimeSpan.FromMinutes(5);

    public static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(10);

    public const int HeadLength = 160;

    /// <summary>A registration older than this is dropped unreported: no statement here runs for a day.</summary>
    private static readonly TimeSpan Forget = TimeSpan.FromHours(24);

    private readonly ConcurrentDictionary<DbCommand, Running> _running = new(ReferenceEqualityComparer.Instance);
    private readonly ILogger<SlowCommandDiagnostics> _logger;
    private readonly string _connectionString;
    private readonly TimeSpan _threshold;
    private readonly Timer _sweep;
    private int _sweeping;

    public SlowCommandDiagnostics(ILogger<SlowCommandDiagnostics> logger, string connectionString, TimeSpan? threshold = null, TimeSpan? sweepInterval = null)
    {
        _logger = logger;
        _connectionString = connectionString;
        _threshold = threshold ?? Threshold;
        var interval = sweepInterval ?? SweepInterval;
        _sweep = new Timer(_ => _ = SweepAsync(DateTime.UtcNow), null, interval, interval);
    }

    private sealed class Running(string head, int? backendPid, (Guid Id, JobKind Kind, int Attempt)? job, Guid? accountId)
    {
        public long StartedTicks { get; } = Stopwatch.GetTimestamp();

        public string Head { get; } = head;

        public int? BackendPid { get; } = backendPid;

        public (Guid Id, JobKind Kind, int Attempt)? Job { get; } = job;

        public Guid? AccountId { get; } = accountId;

        public DateTime? LastReported { get; set; }

        public TimeSpan Elapsed => Stopwatch.GetElapsedTime(StartedTicks);
    }

    // ── interception: register on start, remove on end ───────────────────────────────────────

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        Track(command, eventData);
        return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        Track(command, eventData);
        return base.ReaderExecuting(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Track(command, eventData);
        return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        Track(command, eventData);
        return base.NonQueryExecuting(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
    {
        Track(command, eventData);
        return base.ScalarExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override InterceptionResult<object> ScalarExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
    {
        Track(command, eventData);
        return base.ScalarExecuting(command, eventData, result);
    }

    public override ValueTask<DbDataReader> ReaderExecutedAsync(
        DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
    {
        Untrack(command);
        return base.ReaderExecutedAsync(command, eventData, result, cancellationToken);
    }

    public override DbDataReader ReaderExecuted(DbCommand command, CommandExecutedEventData eventData, DbDataReader result)
    {
        Untrack(command);
        return base.ReaderExecuted(command, eventData, result);
    }

    public override ValueTask<int> NonQueryExecutedAsync(
        DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        Untrack(command);
        return base.NonQueryExecutedAsync(command, eventData, result, cancellationToken);
    }

    public override int NonQueryExecuted(DbCommand command, CommandExecutedEventData eventData, int result)
    {
        Untrack(command);
        return base.NonQueryExecuted(command, eventData, result);
    }

    public override ValueTask<object?> ScalarExecutedAsync(
        DbCommand command, CommandExecutedEventData eventData, object? result, CancellationToken cancellationToken = default)
    {
        Untrack(command);
        return base.ScalarExecutedAsync(command, eventData, result, cancellationToken);
    }

    public override object? ScalarExecuted(DbCommand command, CommandExecutedEventData eventData, object? result)
    {
        Untrack(command);
        return base.ScalarExecuted(command, eventData, result);
    }

    public override void CommandFailed(DbCommand command, CommandErrorEventData eventData)
    {
        Untrack(command);
        base.CommandFailed(command, eventData);
    }

    public override Task CommandFailedAsync(DbCommand command, CommandErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        Untrack(command);
        return base.CommandFailedAsync(command, eventData, cancellationToken);
    }

    public override void CommandCanceled(DbCommand command, CommandEndEventData eventData)
    {
        Untrack(command);
        base.CommandCanceled(command, eventData);
    }

    public override Task CommandCanceledAsync(DbCommand command, CommandEndEventData eventData, CancellationToken cancellationToken = default)
    {
        Untrack(command);
        return base.CommandCanceledAsync(command, eventData, cancellationToken);
    }

    /// <summary>
    /// A reader's statement is still open until the reader is read to the end, but EF materialises
    /// as it reads: "executed" is when the server answered, which is the part a lock wait delays.
    /// </summary>
    private void Track(DbCommand command, CommandEventData eventData)
    {
        var pid = command.Connection is NpgsqlConnection { State: System.Data.ConnectionState.Open } npgsql ? npgsql.ProcessID : (int?)null;
        var account = (eventData.Context as ReelDbContext)?.PinnedAccountId;
        _running[command] = new Running(Head(command.CommandText), pid, JobRunContext.Current, account);
    }

    private void Untrack(DbCommand command)
    {
        if (_running.TryRemove(command, out var running) && running.Elapsed >= _threshold)
        {
            _logger.LogWarning(
                "Slow DB statement finished after {Seconds} s{Job} on backend {Pid}: {Statement}",
                Seconds(running.Elapsed), JobPart(running), running.BackendPid, running.Head);
        }
    }

    // ── the sweep ──────────────────────────────────────────────────────────────────────────

    /// <summary>One sweep. Internal for tests, which call it directly instead of waiting on the timer.</summary>
    internal async Task SweepAsync(DateTime now)
    {
        if (Interlocked.Exchange(ref _sweeping, 1) == 1)
        {
            return;
        }

        try
        {
            foreach (var (command, running) in _running)
            {
                if (running.Elapsed > Forget)
                {
                    // An end callback that never came (a provider path EF does not report): forget it.
                    _running.TryRemove(command, out _);
                    continue;
                }

                if (running.Elapsed < _threshold
                    || (running.LastReported is { } last && now - last < Repeat))
                {
                    continue;
                }

                running.LastReported = now;
                var (wait, blockers) = running.BackendPid is { } pid ? await DescribeAsync(pid) : ("unknown", "unknown");
                _logger.LogWarning(
                    "Slow DB statement still running after {Seconds} s{Job} on backend {Pid}, waiting on {Wait}; blocked by {Blockers}: {Statement}",
                    Seconds(running.Elapsed), JobPart(running), running.BackendPid, wait, blockers, running.Head);
            }
        }
        catch (Exception ex)
        {
            // A diagnostic must never be what breaks the app.
            _logger.LogDebug(ex, "Slow-statement sweep failed.");
        }
        finally
        {
            Volatile.Write(ref _sweeping, 0);
        }
    }

    /// <summary>The backend's wait event, and every session blocking it, on a connection of its own.</summary>
    private async Task<(string Wait, string Blockers)> DescribeAsync(int pid)
    {
        try
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            await using var self = new NpgsqlCommand(
                "SELECT coalesce(wait_event_type || '/' || wait_event, 'nothing (running)') FROM pg_stat_activity WHERE pid = @pid", connection);
            self.Parameters.AddWithValue("pid", pid);
            var wait = (await self.ExecuteScalarAsync()) as string ?? "nothing (the backend has gone)";

            await using var blockers = new NpgsqlCommand(
                """
                SELECT a.pid, a.state,
                       coalesce(extract(epoch FROM now() - a.xact_start)::int, -1),
                       left(regexp_replace(coalesce(a.query, ''), '\s+', ' ', 'g'), @head)
                FROM pg_stat_activity a
                WHERE a.pid = ANY (pg_blocking_pids(@pid))
                ORDER BY a.xact_start
                """,
                connection);
            blockers.Parameters.AddWithValue("pid", pid);
            blockers.Parameters.AddWithValue("head", HeadLength);
            var lines = new List<string>();
            await using (var reader = await blockers.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                    lines.Add(string.Create(
                        CultureInfo.InvariantCulture,
                        $"pid {reader.GetInt32(0)} ({(reader.IsDBNull(1) ? "?" : reader.GetString(1))}, transaction open {reader.GetInt32(2)} s): {reader.GetString(3)}"));
                }
            }

            return (wait, lines.Count == 0 ? "no session" : string.Join(" | ", lines));
        }
        catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException or TimeoutException)
        {
            return ("unknown", $"could not read pg_stat_activity ({ex.GetType().Name})");
        }
    }

    private static string Head(string? sql)
    {
        var flat = string.Join(' ', (sql ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return flat.Length <= HeadLength ? flat : flat[..HeadLength] + "…";
    }

    private static string JobPart(Running running) =>
        running.Job is { } job
            ? string.Create(CultureInfo.InvariantCulture, $" for job {job.Id} ({job.Kind}, attempt {job.Attempt}, account {running.AccountId})")
            : running.AccountId is { } account ? $" for account {account}" : string.Empty;

    private static string Seconds(TimeSpan span) => span.TotalSeconds.ToString("0", CultureInfo.InvariantCulture);

    public void Dispose() => _sweep.Dispose();
}
