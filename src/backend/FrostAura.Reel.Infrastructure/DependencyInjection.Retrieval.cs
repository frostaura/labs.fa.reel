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
        // Keyless search on the full-text arm and the shared concept resolver (task 12).
        services.AddScoped<Application.Search.Retrieval.KeylessTextSearch>();
        // The search box's lookup: tiered local leg, condition-judged global leg (FD-5).
        services.AddScoped<Application.Search.Typeahead.TypeaheadSearch>();
        // Feed-starvation diagnostic: injected by BuildFeedJobHandler on the serving path, and read by /api/dev.
        services.AddScoped<Application.Ranking.FeedStarvationDiagnostic>();

        // ── Ask Reel retrieval contract ─────────────────────────────────────────────────
        // SupplyReport v0.
        services.AddScoped<Application.Search.Retrieval.SupplyReporter>();
        services.AddScoped<Application.Search.Retrieval.ISupplyReporter>(sp =>
            sp.GetRequiredService<Application.Search.Retrieval.SupplyReporter>());
        // The concept resolver and its vocabulary (M2): containment, synonyms, then nearest labels.
        services.AddScoped<Application.Search.Retrieval.Concepts.KeywordVocabulary>();
        // Scoped, not singleton: its provider is a typed HttpClient that takes the scoped IAccountContext
        // (ledger attribution), and a singleton resolved it from the root — a 500 on every Ask in
        // Development (scope validation) and an unpinned account context everywhere else. Its state is
        // static (the in-flight calls) or the shared IMemoryCache, so scoping it loses nothing.
        services.AddScoped<Application.Search.Retrieval.Concepts.ConceptEmbeddings>();
        services.AddScoped<Application.Search.Retrieval.Concepts.IConceptResolver, Application.Search.Retrieval.Concepts.ConceptResolver>();
        services.AddScoped<Application.Search.Retrieval.Concepts.KeywordLabelRefresher>();
        services.AddHostedService<Background.KeywordLabelRefreshService>();
        // The hybrid retriever: facet, full-text, semantic and TMDB arms fused by RRF (k = 60). The
        // facet arm alone stays registered for callers that want only it.
        services.AddScoped<Application.Search.Retrieval.FullTextArm>();
        services.AddScoped<Application.Search.Retrieval.FacetRetriever>();
        services.AddScoped<Application.Search.Retrieval.HybridRetriever>();
        // Keyword / theme seeding for WS-5's Similar rail: eligible titles sharing specific evidence.
        services.AddScoped<Application.Search.Retrieval.KeywordNeighbours>();
        services.AddScoped<Application.Search.Retrieval.IAskReelRetriever>(sp =>
            sp.GetRequiredService<Application.Search.Retrieval.HybridRetriever>());
        // The TMDB arm: standing conditions sent WITH the fetch, up to three pages, title search only
        // for title-shaped messages. Consumed by Ask Reel's TMDB fetch and, later, the hybrid retriever.
        services.AddScoped<Application.Search.Retrieval.TmdbDiscoveryArm>();

        return services;
    }
}
