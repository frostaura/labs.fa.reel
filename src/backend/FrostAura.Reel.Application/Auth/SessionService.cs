using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Domain.Auth;
using FrostAura.Reel.Domain.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace FrostAura.Reel.Application.Auth;

/// <summary>
/// One answer from <see cref="SessionService"/>: the session the caller now rides on, its access
/// token and — when a refresh token was minted by THIS call — that token.
///
/// <para><see cref="RefreshToken"/> is null only on a grace answer (<see cref="SessionService.RefreshGrace"/>):
/// a concurrent refresh already rotated the presented token, the successor's raw token exists only
/// in the winner's response (hashes only at rest), and this caller must keep whatever refresh
/// cookie the winner set rather than overwrite it.</para>
/// </summary>
public record SessionTokens(Guid SessionId, string AccessToken, string? RefreshToken, DateTime AccessExpiresAt, DateTime RefreshExpiresAt);

/// <summary>
/// App-session lifecycle: short-lived access JWTs + rotated refresh tokens (hashes only at
/// rest). Trakt OAuth is the identity; these tokens are merely the app's session transport
/// (HttpOnly cookies at the API edge).
/// </summary>
public class SessionService(IReelDbContext db, IOptions<SessionOptions> options)
{
    private static readonly JsonWebTokenHandler TokenHandler = new();

    private SessionOptions Settings => options.Value;
    private string Issuer => Settings.Issuer;
    private string Audience => Settings.Audience;
    private int AccessMinutes => Settings.AccessMinutes;
    private int RefreshDays => Settings.RefreshDays;

    private SymmetricSecurityKey SigningKey
    {
        get
        {
            var secret = Settings.Secret is { Length: > 0 } configured
                ? configured
                : throw new InvalidOperationException($"{SessionOptions.SecretKey} is required.");
            return new SymmetricSecurityKey(SHA256.HashData(Encoding.UTF8.GetBytes("reel.jwt.v1:" + secret)));
        }
    }

    /// <summary>
    /// How long after a refresh token is rotated a second presentation of it is still answered — with
    /// an access token on the successor's session, never a new session. Two tabs, or a page and its
    /// service worker, refresh within milliseconds of each other on wake; without the window the
    /// loser of that race is signed out by the winner (K-11).
    /// </summary>
    public static readonly TimeSpan RefreshGrace = TimeSpan.FromSeconds(10);

    /// <summary>Mints a new refresh session for <paramref name="account"/>; the result carries its id.</summary>
    public async Task<SessionTokens> IssueAsync(Account account, string? userAgent, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var (session, refreshToken) = NewSession(account.Id, Guid.NewGuid(), userAgent, now);
        db.RefreshSessions.Add(session);
        await db.SaveChangesAsync(ct);

        return new SessionTokens(
            session.Id,
            MintAccessToken(account, now),
            refreshToken,
            now.AddMinutes(AccessMinutes),
            session.ExpiresAt);
    }

    /// <summary>
    /// Rotates the refresh token: the presented session is revoked and names its successor in ONE
    /// conditional UPDATE that must affect exactly one row, so concurrent refreshes of the same token
    /// produce exactly one successor (J-10).
    ///
    /// <para><b>The race.</b> The previous version read the row, set <c>RevokedAt</c> in memory and
    /// saved; three concurrent refreshes all read it unrevoked, all minted a session, and the
    /// browser kept whichever cookie arrived last — two live sessions orphaned per wake, and the
    /// successor link pointed at "the newest session for the account", which under a race is a
    /// guess.</para>
    ///
    /// <para>The loser of the race is answered from the successor inside <see cref="RefreshGrace"/>:
    /// an access token for the same account, tagged with the successor's id, and no refresh token
    /// (it keeps the cookie the winner set). After the window, or for a token that was revoked by
    /// logout rather than rotated, the answer is null — sign in again.</para>
    /// </summary>
    public async Task<SessionTokens?> RefreshAsync(string refreshToken, string? userAgent, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var hash = Hash(refreshToken);
        var session = await db.RefreshSessions
            .IgnoreQueryFilters() // pre-auth: no account scope exists yet
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.TokenHash == hash, ct);

        if (session is null || session.ExpiresAt <= now)
        {
            return null;
        }

        if (session.RevokedAt is not null)
        {
            return await GraceAnswerAsync(session, now, ct);
        }

        var account = await db.Accounts.FirstOrDefaultAsync(a => a.Id == session.AccountId, ct);
        if (account is null)
        {
            return null;
        }

        var successorId = Guid.NewGuid();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var claimed = await db.RefreshSessions
            .IgnoreQueryFilters()
            .Where(s => s.Id == session.Id && s.RevokedAt == null)
            .ExecuteUpdateAsync(set => set
                .SetProperty(s => s.RevokedAt, now)
                .SetProperty(s => s.ReplacedBySessionId, successorId), ct);
        if (claimed != 1)
        {
            // Another refresh of this token won between the read and the update.
            await tx.RollbackAsync(ct);
            var current = await db.RefreshSessions.IgnoreQueryFilters().AsNoTracking()
                .FirstOrDefaultAsync(s => s.Id == session.Id, ct);
            return current is null ? null : await GraceAnswerAsync(current, now, ct);
        }

        var (successor, successorToken) = NewSession(account.Id, successorId, userAgent, now);
        db.RefreshSessions.Add(successor);
        account.LastSeenAt = now;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return new SessionTokens(
            successor.Id,
            MintAccessToken(account, now),
            successorToken,
            now.AddMinutes(AccessMinutes),
            successor.ExpiresAt);
    }

    /// <summary>
    /// The answer for a token that is already revoked: an access token on its successor when the
    /// rotation happened within <see cref="RefreshGrace"/> and the successor is still live; null
    /// otherwise. Mints nothing in the database.
    /// </summary>
    private async Task<SessionTokens?> GraceAnswerAsync(RefreshSession revoked, DateTime now, CancellationToken ct)
    {
        if (revoked.ReplacedBySessionId is not { } successorId
            || revoked.RevokedAt is not { } revokedAt
            || now - revokedAt > RefreshGrace)
        {
            return null;
        }

        var successor = await db.RefreshSessions.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == successorId, ct);
        if (successor is null || successor.RevokedAt is not null || successor.ExpiresAt <= now)
        {
            return null;
        }

        var account = await db.Accounts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == successor.AccountId, ct);
        return account is null
            ? null
            : new SessionTokens(successor.Id, MintAccessToken(account, now), null, now.AddMinutes(AccessMinutes), successor.ExpiresAt);
    }

    private (RefreshSession Session, string RefreshToken) NewSession(Guid accountId, Guid id, string? userAgent, DateTime now)
    {
        var refreshToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        return (new RefreshSession
        {
            Id = id,
            AccountId = accountId,
            TokenHash = Hash(refreshToken),
            ExpiresAt = now.AddDays(RefreshDays),
            CreatedAt = now,
            UserAgent = userAgent is { Length: > 0 } ua ? ua[..Math.Min(ua.Length, 256)] : null,
        }, refreshToken);
    }

    public async Task RevokeAsync(string refreshToken, CancellationToken ct = default)
    {
        var hash = Hash(refreshToken);
        var session = await db.RefreshSessions
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.TokenHash == hash, ct);
        if (session is not null && session.RevokedAt is null)
        {
            session.RevokedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
        }
    }

    public string MintAccessToken(Account account, DateTime now) =>
        TokenHandler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = Audience,
            IssuedAt = now,
            NotBefore = now,
            Expires = now.AddMinutes(AccessMinutes),
            Claims = new Dictionary<string, object>
            {
                [JwtRegisteredClaimNames.Sub] = account.Id.ToString(),
                [JwtRegisteredClaimNames.Jti] = Guid.NewGuid().ToString("N"),
                ["tier"] = account.Tier.ToString(),
            },
            SigningCredentials = new SigningCredentials(SigningKey, SecurityAlgorithms.HmacSha256),
        });

    /// <summary>Validates an access JWT and returns the account id, or null when invalid/expired.</summary>
    public async Task<Guid?> ValidateAccessTokenAsync(string accessToken)
    {
        var result = await TokenHandler.ValidateTokenAsync(accessToken, new TokenValidationParameters
        {
            ValidIssuer = Issuer,
            ValidAudience = Audience,
            IssuerSigningKey = SigningKey,
            ClockSkew = TimeSpan.FromSeconds(30),
        });

        if (!result.IsValid)
        {
            return null;
        }

        var sub = result.Claims.TryGetValue(JwtRegisteredClaimNames.Sub, out var value) ? value?.ToString()
            : result.Claims.TryGetValue(ClaimTypes.NameIdentifier, out var mapped) ? mapped?.ToString()
            : null;
        return Guid.TryParse(sub, out var accountId) ? accountId : null;
    }

    private static string Hash(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
