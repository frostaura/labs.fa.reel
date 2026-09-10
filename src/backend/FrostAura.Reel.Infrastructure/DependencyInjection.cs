using FrostAura.Reel.Application.Auth;
using FrostAura.Reel.Application.Jobs;
using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Application.Pipeline;
using FrostAura.Reel.Application.Sync;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Infrastructure.Adapters;
using FrostAura.Reel.Infrastructure.Background;
using FrostAura.Reel.Infrastructure.Persistence;
using FrostAura.Reel.Infrastructure.RateLimiting;
using FrostAura.Reel.Infrastructure.Security;
using FrostAura.Reel.Infrastructure.Sse;
using FrostAura.Reel.Infrastructure.Telemetry;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FrostAura.Reel.Infrastructure;

public static class DependencyInjection
{
    /// <summary>Wires persistence, tenancy scope, secret protection, external API adapters, and shared rate gates.</summary>
    public static IServiceCollection AddReelInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Postgres")
            ?? throw new InvalidOperationException("ConnectionStrings__Postgres is required.");

        services.AddScoped<IAccountContext, AccountContext>();

        services.AddDbContext<ReelDbContext>(options =>
            options.UseNpgsql(connectionString, npgsql => npgsql.UseVector()));
        services.AddScoped<IReelDbContext>(sp => sp.GetRequiredService<ReelDbContext>());

        services.AddDataProtection()
            .SetApplicationName("FrostAura.Reel")
            .PersistKeysToDbContext<ReelDbContext>();

        services.AddSingleton<ISecretProtector, SecretProtector>();

        // ── Application services ───────────────────────────────────────────────────────────
        services.AddSingleton<OAuthStateCodec>();
        services.AddScoped<SessionService>();
        services.AddScoped<TraktOAuthService>();

        // ── Shared external-API rate gates (global across all tenants) ────────────────────
        var traktBudget = configuration.GetValue("TRAKT_RATE_BUDGET_PER_5MIN", 900);
        services.AddKeyedSingleton<IRateGate>("trakt", (_, _) => new PriorityRateGate(traktBudget, TimeSpan.FromMinutes(5)));
        var tmdbPerSec = configuration.GetValue("TMDB_RATE_PER_SEC", 40);
        services.AddKeyedSingleton<IRateGate>("tmdb", (_, _) => new PriorityRateGate(tmdbPerSec, TimeSpan.FromSeconds(1)));

        // ── Fair-Use usage ledger ──────────────────────────────────────────────────────────
        services.AddSingleton<ApiUsageRecorder>();
        services.AddHostedService(sp => sp.GetRequiredService<ApiUsageRecorder>());
        // The same instance, seen as the port the lens budget gate reads its in-flight counters
        // through — the ledger flushes every 15s and a burst can outrun it.
        services.AddSingleton<Domain.Sync.IPendingApiUsage>(sp => sp.GetRequiredService<ApiUsageRecorder>());

        // ── External adapters ──────────────────────────────────────────────────────────────
        // Trakt/TMDB both front with Cloudflare, which rejects UA-less requests — HttpClient
        // sends no User-Agent by default, so an explicit one is load-bearing, not cosmetic.
        const string userAgent = "fa.reel/0.1 (+https://github.com/frostaura/fa.reel)";

        services.AddHttpClient<ITraktClient, TraktClient>(client =>
        {
            client.BaseAddress = new Uri("https://api.trakt.tv/");
            // The library snapshots (/sync/watched/shows?extended=full for a 3k-title account) are a
            // single multi-MB response that routinely streams for >30s. Let the resilience pipeline's
            // TotalRequestTimeout govern instead — a finite HttpClient cap would pre-empt it and the
            // request would die with Polly's "didn't complete within '00:00:30'" before the body lands.
            client.Timeout = Timeout.InfiniteTimeSpan;
            client.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
        }).AddStandardResilienceHandler(o =>
        {
            // Defaults (10s per attempt / 30s total) are far too tight for the un-paginated watched +
            // ratings snapshots. /sync/watched/shows?extended=full for a heavy account streams for
            // minutes; a 90s attempt still timed out mid-download, failed the sync, and it retried in
            // a spiral. Give one attempt real room to finish, inside a generous total that still
            // allows a retry. CircuitBreaker sampling must stay ≥ 2× the attempt timeout (validator).
            o.AttemptTimeout.Timeout = TimeSpan.FromMinutes(4);
            o.TotalRequestTimeout.Timeout = TimeSpan.FromMinutes(10);
            o.CircuitBreaker.SamplingDuration = TimeSpan.FromMinutes(8);
        });

        services.AddHttpClient<ITmdbClient, TmdbClient>(client =>
        {
            client.BaseAddress = new Uri("https://api.themoviedb.org/3/");
            client.Timeout = TimeSpan.FromSeconds(30);
            client.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
        }).AddStandardResilienceHandler();

        services.AddHttpClient<IEmbeddingProvider, OpenAiEmbeddingProvider>(client =>
        {
            var baseUrl = configuration["EMBEDDINGS_BASE_URL"] ?? "https://api.openai.com/v1";
            client.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
            client.Timeout = TimeSpan.FromSeconds(30);
            client.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
        }).AddStandardResilienceHandler();

        services.AddHttpClient<ITitleAttributeExtractor, OpenRouterAttributeExtractor>(client =>
        {
            var baseUrl = configuration["OPENROUTER_BASE_URL"] ?? "https://openrouter.ai/api/v1";
            client.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
            client.Timeout = TimeSpan.FromSeconds(60); // chat completions run longer than embeddings
            client.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
        }).AddStandardResilienceHandler();

        services.AddHttpClient<ISearchQueryInterpreter, OpenRouterSearchInterpreter>(client =>
        {
            var baseUrl = configuration["OPENROUTER_BASE_URL"] ?? "https://openrouter.ai/api/v1";
            client.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
            client.Timeout = TimeSpan.FromSeconds(60);
            client.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
        }).AddStandardResilienceHandler();

        services.AddHttpClient<ISearchAgent, OpenRouterSearchAgent>(client =>
        {
            var baseUrl = configuration["OPENROUTER_BASE_URL"] ?? "https://openrouter.ai/api/v1";
            client.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
            client.Timeout = TimeSpan.FromSeconds(60);
            client.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
        }).AddStandardResilienceHandler();

        // The lens writer runs longer than the other chat calls: a narrative is ~10 sentences and a
        // lens is five to ten times a why-line's output tokens, so the transport cap has to sit
        // above the callers' own budgets rather than pre-empting them.
        services.AddHttpClient<Domain.Lens.ILensWriter, OpenRouterLensWriter>(client =>
        {
            var baseUrl = configuration["OPENROUTER_BASE_URL"] ?? "https://openrouter.ai/api/v1";
            client.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
            client.Timeout = TimeSpan.FromSeconds(90);
            client.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
        }).AddStandardResilienceHandler();

        // ── Shared-work coordination + request caching ───────────────────────────────────
        services.AddMemoryCache();
        services.AddSingleton<Application.Abstractions.ICatalogWorkCoordinator, Concurrency.CatalogWorkCoordinator>();

        // ── ML engine ──────────────────────────────────────────────────────────────────────
        services.AddSingleton<IModelEngine, Ml.FastTreeModelEngine>();
        services.AddScoped<Application.Ml.FeatureVectorBuilder>();
        services.AddScoped<Application.Ml.OnlineResidualModel>();
        services.AddScoped<Application.Ml.RatingCalibrationService>();
        services.AddScoped<Application.Ml.RetrainGate>();
        services.AddScoped<Application.Ml.ModelLearnedQuery>();
        services.AddScoped<Application.Ml.ResidualReplay>();
        services.AddScoped<Application.Ranking.DislikeSignal>();
        services.AddScoped<Application.Ml.OnDemandScorer>();
        services.AddScoped<Application.Ml.EvalHarness>();
        services.AddScoped<Application.Ml.PersonAffinityWriter>();
        services.AddScoped<Application.Ml.ClosedLoop.RetroLoopQuery>();
        services.AddScoped<Application.Ml.ClosedLoop.ExposureLog>();
        services.AddScoped<Application.Experiments.InterleaveSummaryQuery>();
        services.AddScoped<Application.Ml.ClosedLoop.AccuracyQuery>();
        services.AddScoped<Application.Ml.ClosedLoop.RatingVerdictQuery>();
        services.AddScoped<Application.Ml.TasteFactsBuilder>();
        services.AddScoped<Application.Ml.PersonalisationContext>();

        // ── "Through your lens" ────────────────────────────────────────────────────────────
        services.AddScoped<Application.Lens.LensBudget>();
        services.AddScoped<Application.Lens.LensInputBuilder>();
        services.AddScoped<Application.Lens.LensVerifier>();
        services.AddScoped<Application.Lens.LensService>();
        services.AddScoped<Application.Lens.TasteNarrativeService>();
        services.AddScoped<Application.Lens.LensAudit>();

        // ── Serving pipeline ─────────────────────────────────────────────────────────────
        services.AddScoped<Application.Search.EligibilityQueryBuilder>();
        services.AddScoped<Application.Search.FilterCostMeter>();
        services.AddScoped<Application.Search.LexicalSearchService>();
        services.AddScoped<Application.Search.LiveSearchExpansionService>();
        services.AddScoped<Application.Ranking.CandidateGenerator>();
        // The day's taste partition. Scoped so it shares the request/job DbContext; the partition
        // itself is cached day-granular in IMemoryCache, which is a singleton, so a build and the
        // rails it triggers all split their ANN budget across the same pockets.
        services.AddScoped<Application.Ranking.TasteClusterProvider>();
        services.AddScoped<Application.Ranking.CandidateRetriever>();
        // Offline diagnostic, reached only through the founder-only /api/dev surface. Scoped like
        // everything else it composes; it writes nothing.
        services.AddScoped<Application.Ranking.RetrievalRecallProbe>();
        services.AddScoped<Application.Ranking.FeedStarvationDiagnostic>();
        services.AddScoped<Application.Ranking.FeedPaginator>();
        services.AddScoped<Application.Ranking.FeedRefreshCoordinator>();
        services.AddScoped<Application.Ingestion.TitleHydrator>();

        // ── Pipeline: event hub, token store, ingestor, job handlers, schedulers ──────────
        services.AddSingleton<IPipelineEventHub, PipelineEventHub>();
        services.AddScoped<TraktTokenStore>();
        services.AddScoped<Application.Sync.OutboxDispatcher>();
        services.AddScoped<Application.Sync.ManagedListReconciler>();
        services.AddScoped<Application.Ingestion.TraktLibraryIngestor>();
        services.AddScoped<IJobHandler, FullIngestJobHandler>();
        services.AddScoped<IJobHandler, DeltaSyncJobHandler>();
        services.AddScoped<IJobHandler, HydrateCatalogJobHandler>();
        services.AddScoped<IJobHandler, EnrichCatalogJobHandler>();
        services.AddScoped<IJobHandler, TrainJobHandler>();
        services.AddScoped<IJobHandler, EvaluateJobHandler>();
        // The interleaving posture, resolved from the environment once per process. Singleton so
        // every build in a process agrees on what the experiment is; off unless INTERLEAVE_ENABLED
        // says otherwise, in which case nothing downstream behaves differently at all.
        services.AddSingleton(Application.Ranking.Interleaving.InterleaveOptions.From(configuration));
        services.AddScoped<IJobHandler, BuildFeedJobHandler>();
        services.AddScoped<IJobHandler, RefreshAvailabilityJobHandler>();
        services.AddScoped<IJobHandler, BuildDigestJobHandler>();
        services.AddScoped<IJobHandler, BuildTasteNarrativeJobHandler>();
        services.AddScoped<IJobHandler, PersonaliseTitlesJobHandler>();
        services.AddScoped<IJobHandler, SeedCatalogJobHandler>();

        // The catalogue's two daily ceilings. Scoped because both read the ledger through the
        // request/job DbContext; the enrichment one also reads the recorder's un-flushed counters,
        // which is why IPendingApiUsage is a singleton resolved into it rather than the reverse.
        services.AddScoped<Application.Jobs.CatalogHydrationBudget>();
        services.AddScoped<Application.Jobs.CatalogEnrichmentBudget>();
        services.AddScoped<Application.Jobs.CatalogSeedProgressQuery>();
        services.AddHostedService<JobRunnerService>();
        services.AddHostedService<TraktDeltaPollService>();
        services.AddHostedService<NightlyReconcileService>();
        services.AddHostedService<CatalogGrowthService>();
        services.AddHostedService<CatalogFollowThroughService>();
        services.AddHostedService<TitleLanguageBackfillService>();
        services.AddHostedService<TokenRefreshService>();
        services.AddHostedService<TraktOutboxDispatcherService>();
        services.AddHostedService<WeeklyDigestService>();

        // ── Web push + blend ─────────────────────────────────────────────────────────────
        // Singleton: the VAPID pair must be provisioned once and shared; it scopes its own
        // DbContext for the one-time load.
        services.AddSingleton<Notifications.VapidKeyProvider>();
        services.AddSingleton<Domain.Ports.IWebPushSender, Notifications.WebPushSender>();
        services.AddScoped<Application.Blend.BlendService>();

        return services;
    }
}
