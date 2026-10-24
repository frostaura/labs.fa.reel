using FrostAura.Reel.Application.Auth;
using FrostAura.Reel.Application.Configuration;
using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Infrastructure.Persistence;
using FrostAura.Reel.Infrastructure.RateLimiting;
using FrostAura.Reel.Infrastructure.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FrostAura.Reel.Infrastructure;

public static partial class DependencyInjection
{
    /// <summary>Data protection, secret protection, sign-in, the shared rate gates and web push.</summary>
    private static IServiceCollection AddReelSecurity(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddDataProtection()
            .SetApplicationName("FrostAura.Reel")
            .PersistKeysToDbContext<ReelDbContext>();

        services.AddSingleton<ISecretProtector, SecretProtector>();

        // ── Application services ───────────────────────────────────────────────────────────
        services.AddValidatedOptions<SessionOptions>();
        services.AddSingleton<OAuthStateCodec>();
        services.AddScoped<SessionService>();
        services.AddScoped<TraktOAuthService>();

        // ── Shared external-API rate gates (global across all tenants) ────────────────────
        var traktBudget = configuration.GetValue("TRAKT_RATE_BUDGET_PER_5MIN", 900);
        services.AddKeyedSingleton<IRateGate>("trakt", (_, _) => new PriorityRateGate(traktBudget, TimeSpan.FromMinutes(5)));
        var tmdbPerSec = configuration.GetValue("TMDB_RATE_PER_SEC", 40);
        services.AddKeyedSingleton<IRateGate>("tmdb", (_, _) => new PriorityRateGate(tmdbPerSec, TimeSpan.FromSeconds(1)));

        // ── Web push ───────────────────────────────────────────────────────────────────────
        // Singleton: the VAPID pair must be provisioned once and shared; it scopes its own
        // DbContext for the one-time load.
        services.AddSingleton<Notifications.VapidKeyProvider>();
        services.AddSingleton<Domain.Ports.IWebPushSender, Notifications.WebPushSender>();

        return services;
    }
}
