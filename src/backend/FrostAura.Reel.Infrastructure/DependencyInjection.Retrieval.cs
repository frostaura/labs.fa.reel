using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Infrastructure.Adapters;
using Microsoft.Extensions.DependencyInjection;

namespace FrostAura.Reel.Infrastructure;

public static partial class DependencyInjection
{
    /// <summary>The TMDB adapter, eligibility, the filter cost meter, lexical search and the starvation diagnostic.</summary>
    private static IServiceCollection AddReelRetrieval(this IServiceCollection services)
    {
        // ── External adapters ──────────────────────────────────────────────────────────────
        services.AddHttpClient<ITmdbClient, TmdbClient>(client =>
        {
            client.BaseAddress = new Uri("https://api.themoviedb.org/3/");
            client.Timeout = TimeSpan.FromSeconds(30);
            client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        }).AddStandardResilienceHandler();

        // ── Serving pipeline ─────────────────────────────────────────────────────────────
        services.AddScoped<Application.Search.EligibilityQueryBuilder>();
        services.AddScoped<Application.Search.FilterCostMeter>();
        services.AddScoped<Application.Search.LexicalSearchService>();
        // Feed-starvation diagnostic: injected by BuildFeedJobHandler on the serving path, and read by /api/dev.
        services.AddScoped<Application.Ranking.FeedStarvationDiagnostic>();

        return services;
    }
}
