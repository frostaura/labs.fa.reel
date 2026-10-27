using System.Collections.Concurrent;
using System.Security.Cryptography;
using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Domain.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FrostAura.Reel.Application.Sync;

/// <summary>
/// Resolves a usable Trakt access token for a connection, refreshing proactively only when it is
/// genuinely near expiry (see <see cref="RefreshAhead"/>) and reactively on a 401. Trakt refresh
/// tokens are SINGLE-USE and the refresh endpoint is capped at 2/hour, so every path re-reads the
/// stored token from the DB inside a per-account gate before refreshing — an OAuth re-link or a
/// sibling job may have already rotated it, and adopting that beats burning a scarce refresh.
/// </summary>
public class TraktTokenStore(
    IReelDbContext db,
    ITraktClient traktClient,
    ISecretProtector secretProtector,
    ILogger<TraktTokenStore> logger)
{
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> RefreshLocks = new();

    // Proactive-refresh lead time. Trakt access tokens here live ~7 days, so a 30-day lead meant
    // EVERY sync refreshed an otherwise-valid token — hammering oauth/token until Trakt 429'd the
    // whole account and every sync stalled. A short lead means we ride a healthy token until it is
    // genuinely near expiry; the reactive 401 self-heal (ForceRefreshAsync) covers a token that
    // dies early, so this is purely about not burning refreshes (and rate budget) for nothing.
    private static readonly TimeSpan RefreshAhead = TimeSpan.FromDays(2);

    public async Task<string> GetValidAccessTokenAsync(TraktConnection connection, CancellationToken ct = default)
    {
        if (connection.Status == ConnectionStatus.Revoked)
        {
            throw new InvalidOperationException($"Trakt connection {connection.Id} is revoked — re-link required.");
        }

        if (connection.ExpiresAt - DateTime.UtcNow > RefreshAhead)
        {
            return await RevealAsync(connection, connection.AccessTokenEncrypted, "access", ct);
        }

        var gate = RefreshLocks.GetOrAdd(connection.AccountId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            // Re-read straight from the DB inside the gate: a sibling job or an out-of-band OAuth
            // re-link may have rotated the token while we queued, in which case there is nothing to
            // refresh. Using the caller's tracked entity here would miss that and burn a refresh.
            var (stored, storedEncrypted, storedExpiry) = await ReadStoredAsync(connection, ct);
            connection.AccessTokenEncrypted = storedEncrypted;
            connection.ExpiresAt = storedExpiry;
            if (storedExpiry - DateTime.UtcNow > RefreshAhead)
            {
                return stored;
            }

            try
            {
                return await RefreshAndPersistAsync(connection, ct);
            }
            catch (Exception ex) when (ex is not TraktTokenUnreadableException && storedExpiry > DateTime.UtcNow)
            {
                // A *proactive* refresh is an optimization, not a hard requirement: if Trakt rejects
                // it (e.g. a transient 429) while the current token is still valid, fall back to that
                // token rather than failing the whole sync. The reactive 401 path handles a token
                // that has actually died. Without this, one throttled refresh sinks every sync.
                // Not for a refresh token this host cannot decrypt: that link dies at expiry with no
                // way back, the connection is already marked Revoked, and saying so now beats
                // failing every refresh until then.
                logger.LogWarning(ex,
                    "Proactive Trakt refresh failed for {AccountId}; using the still-valid token (expires {ExpiresAt:o}).",
                    connection.AccountId, storedExpiry);

                // RefreshAndPersistAsync flips Status to RefreshFailed on a transport error before it
                // rethrows; the token is still valid, so keep the connection Active to avoid a
                // spurious "re-link required" prompt.
                if (connection.Status == ConnectionStatus.RefreshFailed)
                {
                    connection.Status = ConnectionStatus.Active;
                    await db.SaveChangesAsync(CancellationToken.None);
                }

                return stored;
            }
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Reactive refresh after Trakt rejects the access token with a 401 — the token died before
    /// its tracked <see cref="TraktConnection.ExpiresAt"/> (revoked server-side, or a fabricated
    /// dev/link expiry). Ignores ExpiresAt and refreshes. Single-use refresh tokens mean we must
    /// not refresh twice: if a concurrent caller already rotated the token while we queued, the
    /// stored token differs from the stale one we tried, so we return the fresh one instead.
    /// </summary>
    public async Task<string> ForceRefreshAsync(TraktConnection connection, string staleToken, CancellationToken ct = default)
    {
        if (connection.Status == ConnectionStatus.Revoked)
        {
            throw new InvalidOperationException($"Trakt connection {connection.Id} is revoked — re-link required.");
        }

        var gate = RefreshLocks.GetOrAdd(connection.AccountId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            // Read the token straight from the DB — NOT the caller's tracked entity, which may be a
            // stale snapshot from before an out-of-band rotation (an OAuth re-link, or a sibling job
            // that already refreshed). If the stored token differs from the one that just 401'd,
            // adopt it rather than spending a refresh against Trakt's 2/hour cap. This is precisely
            // what stops a long-running job from looping on 429 after the account was re-linked
            // underneath it — the reason a stuck sync could previously never recover on its own.
            var (current, currentEncrypted, _) = await ReadStoredAsync(connection, ct);
            if (!string.Equals(current, staleToken, StringComparison.Ordinal))
            {
                connection.AccessTokenEncrypted = currentEncrypted;
                return current;
            }

            return await RefreshAndPersistAsync(connection, ct);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Reads the current stored credentials straight from the database (a projection, so it bypasses
    /// the caller's tracked entity) — the authority on whether an out-of-band rotation has happened.
    /// </summary>
    private async Task<(string Plain, string Encrypted, DateTime ExpiresAt)> ReadStoredAsync(TraktConnection connection, CancellationToken ct)
    {
        var row = await db.TraktConnections
            .Where(c => c.Id == connection.Id)
            .Select(c => new { c.AccessTokenEncrypted, c.ExpiresAt })
            .FirstAsync(ct);
        return (await RevealAsync(connection, row.AccessTokenEncrypted, "access", ct), row.AccessTokenEncrypted, row.ExpiresAt);
    }

    /// <summary>Exchanges the stored refresh token for a fresh pair and persists it. Caller holds the gate.</summary>
    private async Task<string> RefreshAndPersistAsync(TraktConnection connection, CancellationToken ct)
    {
        var refreshToken = await RevealAsync(connection, connection.RefreshTokenEncrypted, "refresh", ct);
        try
        {
            var tokens = await traktClient.RefreshTokenAsync(refreshToken, ct);
            connection.AccessTokenEncrypted = secretProtector.Protect(tokens.AccessToken);
            connection.RefreshTokenEncrypted = secretProtector.Protect(tokens.RefreshToken);
            connection.ExpiresAt = DateTimeOffset.FromUnixTimeSeconds(tokens.CreatedAt + tokens.ExpiresIn).UtcDateTime;
            connection.Status = ConnectionStatus.Active;
            connection.LastRefreshAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Refreshed Trakt token for account {AccountId}.", connection.AccountId);
            return tokens.AccessToken;
        }
        catch (HttpRequestException ex)
        {
            connection.Status = ConnectionStatus.RefreshFailed;
            await db.SaveChangesAsync(CancellationToken.None);
            logger.LogWarning(ex, "Trakt token refresh failed for account {AccountId}.", connection.AccountId);
            throw;
        }
    }

    /// <summary>
    /// Decrypts a stored token. A token this host cannot decrypt (the key ring that sealed it is
    /// gone) is unrecoverable by retry or refresh, so the connection is marked
    /// <see cref="ConnectionStatus.Revoked"/> — written straight to the row, because a job that
    /// throws next never saves its tracked changes — and a <see cref="TraktTokenUnreadableException"/>
    /// is thrown for the caller to fail on once, not retry (R1-27). Before this, DeltaSync spent all
    /// three attempts every minute on a connection the poll kept re-enqueuing.
    ///
    /// <para>The revoke is conditional on the row still holding the ciphertext that failed. A job
    /// that read the old token before the user re-linked must not revoke the fresh link it never
    /// read: that read is stale, not unrecoverable, so it fails as an ordinary retryable miss and
    /// the next attempt reads the re-linked token.</para>
    /// </summary>
    private async Task<string> RevealAsync(TraktConnection connection, string ciphertext, string which, CancellationToken ct)
    {
        try
        {
            return secretProtector.Unprotect(ciphertext);
        }
        catch (CryptographicException ex)
        {
            var stillStored = which == "refresh"
                ? db.TraktConnections.Where(c => c.Id == connection.Id && c.RefreshTokenEncrypted == ciphertext)
                : db.TraktConnections.Where(c => c.Id == connection.Id && c.AccessTokenEncrypted == ciphertext);
            var revoked = await stillStored
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.Status, ConnectionStatus.Revoked), CancellationToken.None);
            if (revoked == 0)
            {
                logger.LogInformation(
                    "The Trakt {Which} token read for account {AccountId} cannot be decrypted, but the link was replaced since it was read; retrying with the stored one.",
                    which, connection.AccountId);
                throw new InvalidOperationException(
                    $"The Trakt {which} token read for account {connection.AccountId} was replaced before it could be used; retry.");
            }

            connection.Status = ConnectionStatus.Revoked;
            logger.LogWarning(
                "The stored Trakt {Which} token for account {AccountId} cannot be decrypted; the connection is marked Revoked until the user re-links.",
                which, connection.AccountId);
            throw new TraktTokenUnreadableException(connection.AccountId, which, ex);
        }
    }
}
