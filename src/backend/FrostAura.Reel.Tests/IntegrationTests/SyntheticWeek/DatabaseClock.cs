using Npgsql;

namespace FrostAura.Reel.Tests.IntegrationTests.SyntheticWeek;

/// <summary>
/// Moves an eval database forward in time by moving its data backwards: every timestamp column of every
/// base table is shifted by the same interval, so to code that reads <c>DateTime.UtcNow</c> — most of the
/// app, with no clock the harness could advance — the interval has passed. A watch logged "now" reads
/// as six hours old, a save as a day old, a sync cursor as due, a release as a week further back: exactly
/// what the same interval of wall time would have done, for every rule at once.
///
/// <para><b>What is NOT moved, and why.</b>
/// <list type="bullet">
/// <item><c>__EFMigrationsHistory</c> has no timestamps, and nothing else outside <c>public</c> is the app's.</item>
/// <item><c>TitleEmbeddings.CreatedAt</c>: 609 MB of vector rows rewritten per tick for a column no rule
/// reads; <see cref="Skipped"/> names it so a reader of the run log knows.</item>
/// <item>Timestamps inside JSON (<c>CursorJson</c>, <c>LastActivitiesJson</c>, <c>StateJson</c>): opaque to
/// SQL. The synthetic week's Trakt fake writes its own activity stamps in wall time, so a Trakt cursor
/// still moves forward; nothing the acceptance queries read lives in JSON.</item>
/// <item>Views (<c>ExposureOutcomes</c>, <c>TitleExposureRollups</c>) and generated columns
/// (<c>ExposureEvents.MinuteBucket</c>) are derived, and move with the columns they are computed from.</item>
/// </list></para>
///
/// <para><b>Unique indexes over a timestamp</b> (<c>ExposureEvents</c>'s minute bucket) are shifted in two
/// steps — far into the past, then back by the remainder — because a one-step shift of six hours can move
/// a row onto the bucket another row of the same snapshot has not left yet, and a non-deferrable unique
/// index checks each row as it is written.</para>
/// </summary>
public static class DatabaseClock
{
    /// <summary>Base tables whose timestamps are deliberately left where they are (see the class remarks).</summary>
    public static readonly IReadOnlySet<string> Skipped = new HashSet<string>(StringComparer.Ordinal) { "TitleEmbeddings", "__EFMigrationsHistory" };

    private static readonly TimeSpan FarPast = TimeSpan.FromDays(365 * 200);

    /// <summary>One table's shift: its stored timestamp columns, and whether a unique index makes it two-step.</summary>
    public sealed record TableShift(string Table, IReadOnlyList<string> Columns, bool TwoStep);

    /// <summary>The shifts <see cref="AgeAsync"/> would run on this database, read from its catalogue.</summary>
    public static async Task<IReadOnlyList<TableShift>> PlanAsync(NpgsqlConnection connection, CancellationToken ct = default)
    {
        var columns = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        await using (var command = new NpgsqlCommand(
            """
            select c.table_name, c.column_name
            from information_schema.columns c
            join pg_tables t on t.schemaname = c.table_schema and t.tablename = c.table_name
            where c.table_schema = 'public' and c.data_type in ('timestamp with time zone', 'timestamp without time zone')
              and c.is_generated = 'NEVER'
            order by c.table_name, c.ordinal_position
            """, connection))
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                var table = reader.GetString(0);
                if (Skipped.Contains(table))
                {
                    continue;
                }

                if (!columns.TryGetValue(table, out var list))
                {
                    columns[table] = list = [];
                }

                list.Add(reader.GetString(1));
            }
        }

        var twoStep = new HashSet<string>(StringComparer.Ordinal);
        await using (var command = new NpgsqlCommand(
            """
            select distinct t.relname
            from pg_index i
            join pg_class t on t.oid = i.indrelid
            join pg_namespace n on n.oid = t.relnamespace and n.nspname = 'public'
            join pg_attribute a on a.attrelid = t.oid and a.attnum = any(i.indkey)
            where (i.indisunique or i.indisexclusion)
              and a.atttypid in ('timestamptz'::regtype, 'timestamp'::regtype)
            """, connection))
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                twoStep.Add(reader.GetString(0));
            }
        }

        return [.. columns.Select(kv => new TableShift(kv.Key, kv.Value, twoStep.Contains(kv.Key)))];
    }

    /// <summary>
    /// Ages every planned table by <paramref name="by"/> in one transaction, so a reader never sees half
    /// a tick. Returns the rows written per table, for the run log.
    /// </summary>
    public static async Task<IReadOnlyDictionary<string, int>> AgeAsync(NpgsqlConnection connection, TimeSpan by, CancellationToken ct = default)
    {
        if (by <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(by), by, "Time only moves forward: age by a positive interval.");
        }

        var plan = await PlanAsync(connection, ct);
        var written = new Dictionary<string, int>(StringComparer.Ordinal);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        foreach (var shift in plan)
        {
            var steps = shift.TwoStep ? new[] { -(by + FarPast), FarPast } : new[] { -by };
            foreach (var step in steps)
            {
                var sets = string.Join(", ", shift.Columns.Select(c => $"\"{c}\" = \"{c}\" + @step"));
                await using var command = new NpgsqlCommand($"update \"{shift.Table}\" set {sets}", connection, transaction);
                command.Parameters.AddWithValue("step", step);
                written[shift.Table] = await command.ExecuteNonQueryAsync(ct);
            }
        }

        await transaction.CommitAsync(ct);
        return written;
    }
}
