using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Domain.Auth;
using FrostAura.Reel.Domain.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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
public class SessionService(IReelDbContext db, IOptions<SessionOptions> options, ILogger<SessionService>? logger = null)
{
    private static readonly JsonWebTokenHandler TokenHandler = new();

    private readonly ILogger log = (ILogger?)logger ?? NullLogger.Instance;

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

    /// <summary>
    /// The prefix on the stored <see cref="RefreshSession.UserAgent"/> of a QA session — one issued by
    /// the dev/link sign-in, and every successor rotated from it — so QA traffic is excluded from the
    /// founder's data by identity, not by clock (FD-18). The retention purge keeps rows that start
    /// with it.
    /// </summary>
    public const string QaSessionMarker = "reel-dev-link";

    /// <summary>The stored-agent column's width.</summary>
    private const int UserAgentLength = 256;

    /// <summary>Mints a new refresh session for <paramref name="account"/>; the result carries its id.</summary>
    public Task<SessionTokens> IssueAsync(Account account, string? userAgent, CancellationToken ct = default) =>
        IssueAsync(account, userAgent, qaSession: false, ct);

    /// <summary>
    /// Mints a QA session (dev/link only): its stored agent carries <see cref="QaSessionMarker"/>, and
    /// <see cref="RefreshAsync"/> passes the marker to every successor.
    /// </summary>
    public Task<SessionTokens> IssueQaAsync(Account account, string? userAgent, CancellationToken ct = default) =>
        IssueAsync(account, userAgent, qaSession: true, ct);

    /// <summary>
    /// True when a stored agent marks a QA session — the same rule as the retention purge's
    /// <c>LIKE 'reel-dev-link%'</c>, so what rotation keeps marked is exactly what the purge keeps.
    /// </summary>
    public static bool IsQaSession(string? storedUserAgent) =>
        storedUserAgent is not null && storedUserAgent.StartsWith(QaSessionMarker, StringComparison.Ordinal);

    /// <summary>
    /// The stored agent of a QA session: <see cref="QaSessionMarker"/>, then the caller's own agent
    /// (minus any marker it already carries) when it sent one, cut to the column's width.
    /// </summary>
    public static string MarkQaUserAgent(string? userAgent)
    {
        var own = WithoutMarker(userAgent);
        var marked = own is null ? QaSessionMarker : QaSessionMarker + " " + own;
        return marked.Length <= UserAgentLength ? marked : marked[..UserAgentLength];
    }

    /// <summary>
    /// The stored agent of an ordinary session. A request cannot claim the marker: an agent spelled
    /// like it is stored without it, so only dev/link — and rotation from a dev/link session — ever
    /// enters the QA chain.
    /// </summary>
    private static string? OrdinaryUserAgent(string? userAgent)
    {
        var own = WithoutMarker(userAgent);
        return own is null ? null : own[..Math.Min(own.Length, UserAgentLength)];
    }

    private static string? WithoutMarker(string? userAgent)
    {
        var own = userAgent?.Trim();
        while (own is not null && own.StartsWith(QaSessionMarker, StringComparison.Ordinal))
        {
            own = own[QaSessionMarker.Length..].Trim();
        }

        return string.IsNullOrEmpty(own) ? null : own;
    }

    private async Task<SessionTokens> IssueAsync(Account account, string? userAgent, bool qaSession, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var (session, refreshToken) = NewSession(account.Id, Guid.NewGuid(), userAgent, qaSession, now);
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
    /// (it keeps the cookie the winner set). A token revoked by logout answers null. A ROTATED token
    /// presented after the window is reuse: the answer is null and its whole family is revoked
    /// (<see cref="RevokeFamilyAfterReuseAsync"/>).</para>
    ///
    /// <para>A successor of a QA session is a QA session (<see cref="QaSessionMarker"/>): the
    /// frontend refreshes on every access-token expiry, so without that a dev/link session lost its
    /// identity at the first rotation and was purged once it expired (HREV4-PLAT-01).</para>
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
            if (session.ReplacedBySessionId is not null && now - session.RevokedAt.Value > RefreshGrace)
            {
                await RevokeFamilyAfterReuseAsync(session, now, ct);
                return null;
            }

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

        var (successor, successorToken) = NewSession(account.Id, successorId, userAgent, IsQaSession(session.UserAgent), now);
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

    /// <summary>
    /// Reuse of a rotated refresh token (J*4): a token that was replaced more than
    /// <see cref="RefreshGrace"/> ago is presented again. Only two parties can hold it — the browser
    /// it was issued to, which threw it away at rotation, and someone who copied it — so its
    /// presentation means the family is compromised, and every live session descended from it is
    /// revoked: the thief's and the owner's alike. The owner signs in again; the thief, who cannot,
    /// is out.
    ///
    /// <para>The family is the successor chain from the reused session forward
    /// (<see cref="RefreshSession.ReplacedBySessionId"/>), walked in ONE recursive statement so a
    /// month of rotations is one round trip and no schema change. Sessions the account holds on
    /// other devices are separate families and are untouched. A token revoked by logout names no
    /// successor and never reaches here. The chain is never broken by retention: it deletes only
    /// EXPIRED sessions, and every descendant of an unexpired session was created after it and
    /// expires after it.</para>
    /// </summary>
    private async Task RevokeFamilyAfterReuseAsync(RefreshSession reused, DateTime now, CancellationToken ct)
    {
        var revoked = await db.Database.ExecuteSqlAsync(
            $"""
             WITH RECURSIVE family AS (
                 SELECT s."Id", s."ReplacedBySessionId", 0 AS depth
                 FROM "RefreshSessions" s WHERE s."Id" = {reused.Id}
                 UNION ALL
                 SELECT r."Id", r."ReplacedBySessionId", f.depth + 1
                 FROM "RefreshSessions" r JOIN family f ON r."Id" = f."ReplacedBySessionId"
                 WHERE f.depth < {MaxFamilyDepth})
             UPDATE "RefreshSessions" SET "RevokedAt" = {now}
             WHERE "Id" IN (SELECT "Id" FROM family) AND "RevokedAt" IS NULL
             """,
            ct);

        log.LogWarning(
            "Refresh token reuse for account {AccountId}: session {SessionId}, rotated {Age} ago, was presented again; "
            + "revoked {Revoked} live session(s) in its family. The owner must sign in again.",
            reused.AccountId, reused.Id, now - reused.RevokedAt!.Value, revoked);
    }

    /// <summary>
    /// A ceiling on the successor walk, far above any real chain (a 365-day refresh lifetime at one
    /// rotation per 1-minute access token is about 525k links; at the default 15 minutes, 35k).
    /// </summary>
    private const int MaxFamilyDepth = 1_000_000;

    private (RefreshSession Session, string RefreshToken) NewSession(Guid accountId, Guid id, string? userAgent, bool qaSession, DateTime now)
    {
        var refreshToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        return (new RefreshSession
        {
            Id = id,
            AccountId = accountId,
            TokenHash = Hash(refreshToken),
            ExpiresAt = now.AddDays(RefreshDays),
            CreatedAt = now,
            UserAgent = qaSession ? MarkQaUserAgent(userAgent) : OrdinaryUserAgent(userAgent),
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
