using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Infrastructure.Adapters;
using Microsoft.Extensions.DependencyInjection;

namespace FrostAura.Reel.Infrastructure;

public static partial class DependencyInjection
{
    /// <summary>The TMDB adapter, eligibility, the filter cost meter, lexical search, the starvation diagnostic, and Ask Reel retrieval and supply.</summary>
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
        // Catalogue counts behind the conditions editor's genre chips (H-7), cached catalogue-wide.
        services.AddScoped<Application.Search.Retrieval.FilterChipCounts>();
        services.AddScoped<Application.Search.LexicalSearchService>();
        // Feed-starvation diagnostic: injected by BuildFeedJobHandler on the serving path, and read by /api/dev.
        services.AddScoped<Application.Ranking.FeedStarvationDiagnostic>();

        // ── Ask Reel retrieval contract ─────────────────────────────────────────────────
        // SupplyReport v0 and the facet arm standing alone as the retriever until the hybrid lands.
        services.AddScoped<Application.Search.Retrieval.SupplyReporter>();
        services.AddScoped<Application.Search.Retrieval.ISupplyReporter>(sp =>
            sp.GetRequiredService<Application.Search.Retrieval.SupplyReporter>());
        services.AddScoped<Application.Search.Retrieval.IAskReelRetriever, Application.Search.Retrieval.FacetRetriever>();
        // The TMDB arm: standing conditions sent WITH the fetch, up to three pages, title search only
        // for title-shaped messages. Consumed by Ask Reel's TMDB fetch and, later, the hybrid retriever.
        services.AddScoped<Application.Search.Retrieval.TmdbDiscoveryArm>();

        return services;
    }
}
