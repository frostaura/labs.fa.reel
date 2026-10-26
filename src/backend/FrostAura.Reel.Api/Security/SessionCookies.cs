using FrostAura.Reel.Api.Middleware;
using FrostAura.Reel.Application.Auth;

namespace FrostAura.Reel.Api.Security;

/// <summary>
/// The one writer of the session cookies (<c>reel_at</c>, <c>reel_rt</c>), for every route that
/// signs someone in: the OAuth callback, refresh and dev/link.
///
/// <para><b>Secure outside Development, always (J-7).</b> The previous rule set <c>Secure</c> only
/// when the request itself looked like HTTPS — <c>Request.IsHttps</c> or an
/// <c>X-Forwarded-Proto: https</c> header. Behind cloudflared → nginx → API the API sees plain HTTP,
/// and the gateway forwarded its own <c>$scheme</c> (http), so production cookies went out without
/// <c>Secure</c> and would ride any plain-HTTP request to the host. Outside Development the flag no
/// longer depends on what the request claims. Development keeps the old behaviour, because a local
/// Vite or slot API is plain HTTP and Safari drops a <c>Secure</c> cookie set over HTTP.</para>
/// </summary>
public static class SessionCookies
{
    public const string RefreshTokenCookie = "reel_rt";

    /// <summary>The refresh token only ever travels to the auth endpoints.</summary>
    public const string RefreshCookiePath = "/api/auth";

    /// <summary>Whether a session cookie written on this request carries <c>Secure</c>.</summary>
    public static bool IsSecure(HttpContext http) =>
        http.Request.IsHttps || !http.RequestServices.GetRequiredService<IWebHostEnvironment>().IsDevelopment();

    /// <summary>
    /// Writes the access cookie, and the refresh cookie when <paramref name="tokens"/> carries one.
    /// A grace answer from a concurrent refresh carries none and must leave the winner's cookie alone.
    /// </summary>
    public static void Write(HttpContext http, SessionTokens tokens)
    {
        var secure = IsSecure(http);

        http.Response.Cookies.Append(AccountResolutionMiddleware.AccessTokenCookie, tokens.AccessToken, new CookieOptions
        {
            HttpOnly = true,
            Secure = secure,
            SameSite = SameSiteMode.Lax,
            Path = "/",
            Expires = tokens.AccessExpiresAt,
        });

        if (tokens.RefreshToken is { } refreshToken)
        {
            http.Response.Cookies.Append(RefreshTokenCookie, refreshToken, new CookieOptions
            {
                HttpOnly = true,
                Secure = secure,
                SameSite = SameSiteMode.Lax,
                Path = RefreshCookiePath,
                Expires = tokens.RefreshExpiresAt,
            });
        }
    }

    public static void Clear(HttpContext http)
    {
        var secure = IsSecure(http);
        http.Response.Cookies.Delete(AccountResolutionMiddleware.AccessTokenCookie, new CookieOptions { Path = "/", Secure = secure });
        http.Response.Cookies.Delete(RefreshTokenCookie, new CookieOptions { Path = RefreshCookiePath, Secure = secure });
    }
}
