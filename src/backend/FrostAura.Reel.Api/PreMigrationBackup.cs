using System.Globalization;
using FrostAura.Reel.Application.Configuration;
using FrostAura.Reel.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace FrostAura.Reel.Api;

/// <summary>
/// Settings for the dump the API asks for before it migrates (K-1). The defaults are the posture:
/// when a request directory is configured, no migration runs without a dump.
/// </summary>
public sealed class PreMigrationBackupOptions : ValidatedOptions
{
    public const string RequestDirectoryKey = "BACKUP_REQUEST_DIR";
    public const string TimeoutSecondsKey = "BACKUP_PRE_MIGRATION_TIMEOUT_SECONDS";
    public const string RequiredKey = "BACKUP_PRE_MIGRATION_REQUIRED";

    /// <summary>The directory shared with the backup sidecar; null when this deployment has none.</summary>
    public string? RequestDirectory { get; private set; }

    /// <summary>
    /// How long to wait for the sidecar's answer. Thirty minutes: a request that lands during a nightly
    /// or first-start dump waits for it, then for its own — two dumps of the live database.
    /// </summary>
    public int TimeoutSeconds { get; private set; } = 1800;

    /// <summary>When true, a failed or unanswered request stops the process instead of migrating.</summary>
    public bool Required { get; private set; } = true;

    protected override void Read(SettingReader reader)
    {
        RequestDirectory = reader.Raw(RequestDirectoryKey);
        TimeoutSeconds = reader.Int(TimeoutSecondsKey, 1800, min: 30, max: 7200);
        Required = reader.Bool(RequiredKey, true);
    }
}

/// <summary>
/// "A dump before any pending migration" (K-1, FD-12), as a file handshake with the backup sidecar
/// (<c>deploy/backup/reel-backup.sh</c>) over a shared volume.
///
/// <para><b>Why a handshake rather than a dump at container start.</b> A Portainer redeploy with
/// <c>pullImage</c> recreates only the services whose image changed, so a sidecar that dumped when
/// IT started would not run again when a new backend image arrives carrying a migration — which is
/// exactly the moment a dump is owed. Only the process that is about to migrate knows that it is
/// about to, so it asks: it writes <c>&lt;id&gt;.request</c>, the sidecar writes
/// <c>&lt;id&gt;.done</c> (naming the dump) or <c>&lt;id&gt;.failed</c>, and only then does
/// <c>DatabaseInitializer</c> run. The backend image needs no Postgres client for this.</para>
///
/// <para><b>Failure posture.</b> With <c>BACKUP_PRE_MIGRATION_REQUIRED</c> true (the default) a
/// failed or unanswered request throws, Program's startup guard exits, and the container restarts
/// and asks again — the API stays down rather than migrating without a way back, because rolling a
/// migrated database back means restoring a pre-migration dump (§5.0 rule 8). No request is made at
/// all when nothing is pending, when auto-migration is off, or when no request directory is
/// configured (a host-side <c>dotnet run</c>, the test harness).</para>
/// </summary>
public static class PreMigrationBackup
{
    public enum Outcome
    {
        Done,
        Failed,
        TimedOut,
    }

    public sealed record Answer(Outcome Outcome, string Detail);

    /// <summary>Asks for and waits on the pre-migration dump when one is owed. Throws when one is owed and required but did not happen.</summary>
    public static async Task EnsureAsync(IServiceProvider services, CancellationToken ct = default)
    {
        using var scope = services.CreateScope();
        var configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("PreMigrationBackup");
        var options = scope.ServiceProvider.GetRequiredService<IOptions<PreMigrationBackupOptions>>().Value;

        if (!configuration.GetValue("Database:AutoMigrateOnStartup", false))
        {
            return;
        }

        var db = scope.ServiceProvider.GetRequiredService<ReelDbContext>();
        var pending = await PendingMigrationsAsync(db, logger, ct);
        if (pending.Count == 0)
        {
            return;
        }

        if (options.RequestDirectory is null)
        {
            logger.LogWarning(
                "{Count} pending migration(s) will apply WITHOUT a pre-migration dump: {Key} is unset (no backup service on this host).",
                pending.Count,
                PreMigrationBackupOptions.RequestDirectoryKey);
            return;
        }

        logger.LogInformation("{Count} pending migration(s); requesting a pre-migration dump before applying them.", pending.Count);
        var answer = await RequestAsync(
            options.RequestDirectory,
            pending.Count,
            pending[^1],
            TimeSpan.FromSeconds(options.TimeoutSeconds),
            TimeSpan.FromSeconds(2),
            ct);

        if (answer.Outcome == Outcome.Done)
        {
            logger.LogInformation("Pre-migration dump written: {Dump}", answer.Detail);
            return;
        }

        if (options.Required)
        {
            throw new InvalidOperationException(
                $"Refusing to apply {pending.Count} migration(s) without a pre-migration dump: {answer.Outcome} — {answer.Detail}. " +
                $"Fix the backup sidecar, or set {PreMigrationBackupOptions.RequiredKey}=false to migrate without one.");
        }

        logger.LogWarning("Pre-migration dump {Outcome} ({Detail}); migrating anyway because {Key}=false.", answer.Outcome, answer.Detail, PreMigrationBackupOptions.RequiredKey);
    }

    /// <summary>
    /// The handshake itself: writes the request atomically, then polls for the sidecar's answer
    /// until <paramref name="timeout"/>. The request is withdrawn on timeout so a late sidecar does
    /// not dump for a process that has already given up.
    /// </summary>
    public static async Task<Answer> RequestAsync(
        string directory, int pendingCount, string latestMigration, TimeSpan timeout, TimeSpan poll, CancellationToken ct)
    {
        if (!Directory.Exists(directory))
        {
            return new Answer(Outcome.Failed, $"request directory {directory} does not exist (is the backup volume mounted?)");
        }

        var id = string.Create(CultureInfo.InvariantCulture, $"pre-migration-{DateTime.UtcNow:yyyyMMdd'T'HHmmss'Z'}-{Guid.NewGuid().ToString("N")[..8]}");
        var request = Path.Combine(directory, id + ".request");
        var done = Path.Combine(directory, id + ".done");
        var failed = Path.Combine(directory, id + ".failed");

        var partial = request + ".partial";
        await File.WriteAllTextAsync(
            partial,
            string.Create(CultureInfo.InvariantCulture, $"pending={pendingCount}\nlatest={latestMigration}\n"),
            ct);
        File.Move(partial, request);

        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(done))
            {
                var dump = (await File.ReadAllTextAsync(done, ct)).Trim();
                File.Delete(done);
                return new Answer(Outcome.Done, dump);
            }

            if (File.Exists(failed))
            {
                var reason = (await File.ReadAllTextAsync(failed, ct)).Trim();
                File.Delete(failed);
                return new Answer(Outcome.Failed, reason);
            }

            await Task.Delay(poll, ct);
        }

        File.Delete(request);
        return new Answer(Outcome.TimedOut, string.Create(CultureInfo.InvariantCulture, $"no answer from the backup sidecar within {timeout.TotalSeconds:0}s"));
    }

    /// <summary>
    /// Pending migrations, retried while Postgres is still starting — the same transient set
    /// <c>DatabaseInitializer</c> waits out, because this read now comes first.
    /// </summary>
    private static async Task<List<string>> PendingMigrationsAsync(ReelDbContext db, ILogger logger, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(3);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return [.. await db.Database.GetPendingMigrationsAsync(ct)];
            }
            catch (Exception ex) when (IsDatabaseNotReady(ex) && DateTime.UtcNow < deadline)
            {
                logger.LogWarning("Database not ready yet (attempt {Attempt}): {Reason} — retrying in 2s.", attempt, ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
            }
        }
    }

    private static bool IsDatabaseNotReady(Exception ex) => ex switch
    {
        PostgresException pg => pg.SqlState == "57P03" || pg.SqlState.StartsWith("08", StringComparison.Ordinal),
        NpgsqlException npgsql => npgsql.IsTransient,
        _ => false,
    };
}
