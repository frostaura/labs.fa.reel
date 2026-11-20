using FrostAura.Reel.Application.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace FrostAura.Reel.Infrastructure.Persistence;

/// <summary>
/// Applies pending EF migrations on boot when Database:AutoMigrateOnStartup is true
/// (first boot of a fresh compose stack provisions itself).
/// </summary>
public static class DatabaseInitializer
{
    public static async Task InitializeAsync(IServiceProvider services, CancellationToken ct = default)
    {
        using var scope = services.CreateScope();
        var configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("DatabaseInitializer");

        if (!configuration.GetValue("Database:AutoMigrateOnStartup", false))
        {
            logger.LogInformation("Database auto-migration disabled (Database:AutoMigrateOnStartup=false).");
            return;
        }

        // Boot work belongs to no account (J-11).
        using var system = scope.ServiceProvider.GetRequiredService<IAccountContext>().EnterSystemScope();
        var db = scope.ServiceProvider.GetRequiredService<ReelDbContext>();
        var pending = await WaitForPendingMigrationsAsync(db, logger, ct);
        if (pending.Count == 0)
        {
            logger.LogInformation("Database schema is current — no pending migrations.");
            return;
        }

        logger.LogInformation("Applying {Count} pending migration(s): {Migrations}", pending.Count, string.Join(", ", pending));
        await db.Database.MigrateAsync(ct);
        logger.LogInformation("Database migration complete.");
    }

    /// <summary>
    /// The first read, retried while Postgres is still coming up. Compose's <c>service_healthy</c>
    /// gate only applies to <c>docker compose up</c>: when the Docker daemon restarts, every
    /// <c>restart: always</c> container starts at once and this read lands on a postmaster that is
    /// still replaying WAL (57P03) or not yet accepting connections. Failing on the first attempt
    /// left the API down until someone restarted it by hand (2026-10-14). Only transient errors are
    /// retried; a bad password or a broken migration still fails immediately.
    /// </summary>
    private static async Task<List<string>> WaitForPendingMigrationsAsync(ReelDbContext db, ILogger logger, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(3);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return (await db.Database.GetPendingMigrationsAsync(ct)).ToList();
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
        // 57P03 cannot_connect_now (starting up / shutting down / recovery); class 08 connection errors.
        PostgresException pg => pg.SqlState == "57P03" || pg.SqlState.StartsWith("08", StringComparison.Ordinal),
        NpgsqlException npgsql => npgsql.IsTransient,
        _ => false,
    };
}
