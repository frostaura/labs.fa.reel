using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FrostAura.Reel.Infrastructure;

/// <summary>
/// The infrastructure composition root. Each area's registrations live in their own
/// <c>DependencyInjection.&lt;Area&gt;.cs</c> partial, so a change to one area never edits another's file.
/// </summary>
public static partial class DependencyInjection
{
    // Trakt/TMDB both front with Cloudflare, which rejects UA-less requests — HttpClient
    // sends no User-Agent by default, so an explicit one is load-bearing, not cosmetic.
    private const string UserAgent = "fa.reel/0.1 (+https://github.com/frostaura/fa.reel)";

    /// <summary>Wires persistence, tenancy scope, secret protection, external API adapters, and shared rate gates.</summary>
    public static IServiceCollection AddReelInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Postgres")
            ?? throw new InvalidOperationException("ConnectionStrings__Postgres is required.");

        // Hosted services start in registration order, so this call order also fixes their start order.
        services.AddReelSecurity(configuration);
        services.AddReelLlm(configuration);
        services.AddReelJobs(connectionString);
        services.AddReelData();
        services.AddReelRetrieval();
        services.AddReelSearch();
        services.AddReelFeed(configuration);

        // Last area call: on the eval stack it replaces the Trakt adapter registered above.
        services.AddReelEvalIsolation(configuration);

        // ── Shared-work coordination + request caching ───────────────────────────────────
        services.AddMemoryCache();

        return services;
    }
}
