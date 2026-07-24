using FrostAura.Reel.Application.Auth;
using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Domain.Ports.Trakt;
using FrostAura.Reel.Domain.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace FrostAura.Reel.Api.Endpoints;

/// <summary>
/// QA helpers — Development environment + QA_HELPERS_ENABLED=true only (fa.startup precedent).
/// Never mapped in production builds of the compose stack.
/// </summary>
public static class DevEndpoints
{
    public static void MapDevEndpoints(this WebApplication app)
    {
        if (!app.Environment.IsDevelopment() || !app.Configuration.GetValue("QA_HELPERS_ENABLED", false))
        {
            return;
        }

        // Links the configured dev profile using pre-provisioned tokens (TRAKT_ACCESS_TOKEN /
        // TRAKT_REFRESH_TOKEN from .env) — full pipeline + session without the browser consent
        // hop. The production path of record stays the OAuth callback.
        app.MapPost("/api/auth/dev/link", async (
            TraktOAuthService oauth,
            SessionService sessions,
            ITraktClient trakt,
            IReelDbContext db,
            HttpContext http,
            IConfiguration configuration,
            CancellationToken ct) =>
        {
            // Preferred QA path: the dev profile is normally already linked — its Account and
            // TraktConnection live in the DB and self-heal their Trakt tokens on 401 during sync. A
            // session only needs the Account, so we don't re-link through Trakt (which would depend on
            // the pre-provisioned .env tokens, and single-use refresh tokens rotate and go stale).
            // Issue a session for the existing account directly — robust regardless of .env freshness.
            var devProfile = configuration["TRAKT_DEV_PROFILE"];
            Account? account = null;
            if (!string.IsNullOrWhiteSpace(devProfile))
            {
                account = await db.Accounts
                    .IgnoreQueryFilters()
                    .FirstOrDefaultAsync(a => a.TraktUserSlug == devProfile || a.TraktUsername == devProfile, ct);
            }

            // Fresh QA database — nothing linked yet. Fall back to linking via the pre-provisioned
            // tokens, and surface a clean 400 (not a 500) if those tokens are stale/revoked.
            if (account is null)
            {
                var accessToken = configuration["TRAKT_ACCESS_TOKEN"];
                var refreshToken = configuration["TRAKT_REFRESH_TOKEN"];
                if (string.IsNullOrWhiteSpace(accessToken) || string.IsNullOrWhiteSpace(refreshToken))
                {
                    return Results.BadRequest(new { error = "No linked dev account, and TRAKT_ACCESS_TOKEN / TRAKT_REFRESH_TOKEN not configured." });
                }

                // Unknown provenance: try the access token; if stale, rotate via refresh first.
                var tokens = new TraktTokenResponse(
                    accessToken, refreshToken, "public write",
                    DateTimeOffset.UtcNow.ToUnixTimeSeconds(), (long)TimeSpan.FromDays(60).TotalSeconds);
                try
                {
                    await trakt.GetUserSettingsAsync(accessToken, ct);
                }
                catch (Exception)
                {
                    try
                    {
                        tokens = await trakt.RefreshTokenAsync(refreshToken, ct);
                    }
                    catch (Exception ex)
                    {
                        return Results.BadRequest(new { error = "Pre-provisioned Trakt tokens are stale/revoked — sign in through the OAuth flow once to re-link the dev profile.", detail = ex.Message });
                    }
                }

                var result = await oauth.LinkAccountAsync(tokens, countryHint: null, ct);
                account = await db.Accounts.IgnoreQueryFilters().FirstAsync(a => a.Id == result.Account.Id, ct);
            }

            // The dev profile is the founder account — everything unlocked during the beta.
            account.Tier = AccountTier.Founder;
            await db.SaveChangesAsync(ct);

            var session = await sessions.IssueAsync(account, http.Request.Headers.UserAgent.FirstOrDefault(), ct);
            http.Response.Cookies.Append("reel_at", session.AccessToken, new CookieOptions
            {
                HttpOnly = true,
                SameSite = SameSiteMode.Lax,
                Path = "/",
                Expires = session.AccessExpiresAt,
            });
            http.Response.Cookies.Append("reel_rt", session.RefreshToken, new CookieOptions
            {
                HttpOnly = true,
                SameSite = SameSiteMode.Lax,
                Path = "/api/auth",
                Expires = session.RefreshExpiresAt,
            });

            return Results.Ok(account.ToSessionPayload());
        });
    }
}
