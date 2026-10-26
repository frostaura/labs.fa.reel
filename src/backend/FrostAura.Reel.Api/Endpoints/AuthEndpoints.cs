using FrostAura.Reel.Api.Security;
using FrostAura.Reel.Application.Auth;
using FrostAura.Reel.Application.Persistence;

namespace FrostAura.Reel.Api.Endpoints;

public static class AuthEndpoints
{
    public record CallbackRequest(string Code, string State);

    public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/auth");

        group.MapPost("/trakt/start", (TraktOAuthService oauth) =>
        {
            var result = oauth.Start();
            return Results.Ok(new { authorizeUrl = result.AuthorizeUrl, state = result.State });
        });

        group.MapPost("/trakt/callback", async (
            CallbackRequest request,
            TraktOAuthService oauth,
            SessionService sessions,
            HttpContext http,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Code) || string.IsNullOrWhiteSpace(request.State))
            {
                return Results.BadRequest(new { error = "code and state are required" });
            }

            // Cloudflare provides the visitor country in prod; nginx forwards it.
            var countryHint = http.Request.Headers["CF-IPCountry"].FirstOrDefault();
            var result = await oauth.HandleCallbackAsync(request.Code, request.State, countryHint, ct);
            if (result is null)
            {
                return Results.BadRequest(new { error = "invalid or expired sign-in state" });
            }

            var tokens = await sessions.IssueAsync(result.Account, http.Request.Headers.UserAgent.FirstOrDefault(), ct);
            SessionCookies.Write(http, tokens);
            return Results.Ok(await result.Account.ToSessionPayloadAsync(http, ct));
        });

        group.MapPost("/refresh", async (SessionService sessions, IReelDbContext db, HttpContext http, CancellationToken ct) =>
        {
            if (!http.Request.Cookies.TryGetValue(SessionCookies.RefreshTokenCookie, out var refreshToken) || string.IsNullOrEmpty(refreshToken))
            {
                return Results.Unauthorized();
            }

            var tokens = await sessions.RefreshAsync(refreshToken, http.Request.Headers.UserAgent.FirstOrDefault(), ct);
            if (tokens is null)
            {
                SessionCookies.Clear(http);
                return Results.Unauthorized();
            }

            // A grace answer (a concurrent refresh already rotated this token) carries no refresh
            // token, and Write leaves the refresh cookie the winner set alone.
            SessionCookies.Write(http, tokens);
            return Results.Ok(new { refreshed = true });
        });

        group.MapPost("/logout", async (SessionService sessions, HttpContext http, CancellationToken ct) =>
        {
            if (http.Request.Cookies.TryGetValue(SessionCookies.RefreshTokenCookie, out var refreshToken) && !string.IsNullOrEmpty(refreshToken))
            {
                await sessions.RevokeAsync(refreshToken, ct);
            }

            SessionCookies.Clear(http);
            return Results.Ok(new { signedOut = true });
        });

        group.MapGet("/me", async (IReelDbContext db, HttpContext http, CancellationToken ct) =>
        {
            var account = await http.GetCurrentAccountAsync(db, ct);
            return account is null ? Results.Unauthorized() : Results.Ok(await account.ToSessionPayloadAsync(http, ct));
        });
    }
}
