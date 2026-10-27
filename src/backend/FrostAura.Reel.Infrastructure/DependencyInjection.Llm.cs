using FrostAura.Reel.Application.Jobs;
using FrostAura.Reel.Application.Llm;
using FrostAura.Reel.Application.Pipeline;
using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Infrastructure.Adapters;
using FrostAura.Reel.Infrastructure.Llm;
using FrostAura.Reel.Infrastructure.Telemetry;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FrostAura.Reel.Infrastructure;

public static partial class DependencyInjection
{
    /// <summary>The usage ledger, the embedding and OpenRouter adapters, the lens and cross-reference services and their jobs.</summary>
    private static IServiceCollection AddReelLlm(this IServiceCollection services, IConfiguration configuration)
    {
        // ── Fair-Use usage ledger ──────────────────────────────────────────────────────────
        // The day a row counts against: the account's own for per-account ceilings (task 12,
        // ADD-22), UTC for the rest. One singleton, so the recorder and every budget read agree.
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<IAccountZoneSource>(sp => new AccountZoneSource(sp.GetRequiredService<IServiceScopeFactory>()));
        services.AddSingleton<IBudgetCalendar>(sp => new BudgetCalendar(
            sp.GetRequiredService<IAccountZoneSource>(), sp.GetRequiredService<TimeProvider>(),
            sp.GetService<Microsoft.Extensions.Logging.ILogger<BudgetCalendar>>()));
        services.AddSingleton<ApiUsageRecorder>();
        services.AddHostedService(sp => sp.GetRequiredService<ApiUsageRecorder>());
        // The same instance, seen as the port the lens budget gate reads its in-flight counters
        // through — the ledger flushes every 15s and a burst can outrun it.
        services.AddSingleton<Domain.Sync.IPendingApiUsage>(sp => sp.GetRequiredService<ApiUsageRecorder>());

        // ── External adapters ──────────────────────────────────────────────────────────────
        services.AddHttpClient<IEmbeddingProvider, OpenAiEmbeddingProvider>(client =>
        {
            var baseUrl = configuration["EMBEDDINGS_BASE_URL"] ?? "https://api.openai.com/v1";
            client.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
            client.Timeout = TimeSpan.FromSeconds(30);
            client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        }).AddStandardResilienceHandler();

        // ── The LLM platform (WS-1) ────────────────────────────────────────────────────────
        // Per-purpose bounds, the per-model capability table, the provider breakers (which are
        // also the status API's provider-health reading — registered here, ahead of the jobs
        // partial's TryAdd fallback, so it wins), and the reserve-before-call budget gate. All
        // singletons: each holds process-wide state that every adapter instance must share.
        services.AddSingleton<ILlmPurposeCatalog, LlmPurposeCatalog>();
        services.AddSingleton(sp => new ModelCapabilities(sp.GetRequiredService<IConfiguration>()));
        services.AddSingleton<ProviderHealthService>();
        services.AddSingleton<IProviderHealth>(sp => sp.GetRequiredService<ProviderHealthService>());
        services.AddSingleton<IProviderHealthReader>(sp => sp.GetRequiredService<ProviderHealthService>());
        services.AddSingleton<ILlmCallGate, LlmCallGate>();
        // Per-purpose length / fallback / retry counters and their 2%-over-50 alarm (task 8), on the
        // FrostAura.Reel meter; the reader is what the metrics report and status API consume.
        services.AddSingleton<LlmCallMetrics>();
        services.AddSingleton<ILlmCallHealthReader>(sp => sp.GetRequiredService<LlmCallMetrics>());

        // Every chat adapter goes through OpenRouterChatClient, which owns retries and the per-purpose
        // attempt/total timeouts. So these typed clients deliberately carry NO standard resilience
        // handler: its 10-second attempt timeout killed slow-but-alive completions and its retries
        // bought them again (K-2), and two retry layers would multiply. The HttpClient timeout is
        // only a backstop above the longest purpose's total budget.
        AddOpenRouterChatClient<ITitleAttributeExtractor, OpenRouterAttributeExtractor>(services, configuration);
        AddOpenRouterChatClient<ISearchQueryInterpreter, OpenRouterSearchInterpreter>(services, configuration);
        AddOpenRouterChatClient<ISearchAgent, OpenRouterSearchAgent>(services, configuration);
        AddOpenRouterChatClient<Domain.Ports.ICrossReferenceJudge, OpenRouterCrossReferenceJudge>(services, configuration);
        AddOpenRouterChatClient<Domain.Lens.ILensWriter, OpenRouterLensWriter>(services, configuration);

        // ── "Through your lens" ────────────────────────────────────────────────────────────
        services.AddScoped<Application.Lens.LensBudget>();
        services.AddScoped<Application.Lens.LensInputBuilder>();
        services.AddScoped<Application.Lens.LensVerifier>();
        services.AddScoped<Application.Lens.LensService>();
        services.AddScoped<Application.Lens.TasteNarrativeService>();
        services.AddScoped<Application.Lens.LensAudit>();

        // ── Cross-reference rounds ─────────────────────────────────────────────────────────
        // Scoped like the lens set, and for the same reason: every one of these reads the ledger or
        // the catalogue through the request/job DbContext. RerankBudget lives here rather than under
        // the serving pipeline because it is the same kind of object as the other two — a ceiling
        // read off the token ledger — and keeping the three together is what stops a fourth being
        // written somewhere else.
        services.AddScoped<Application.CrossReference.CrossReferenceBudget>();
        services.AddScoped<Application.CrossReference.RerankBudget>();
        services.AddScoped<Application.CrossReference.NeighbourFinder>();
        services.AddScoped<Application.CrossReference.CrossReferenceCandidateBuilder>();
        services.AddScoped<Application.CrossReference.CrossReferenceService>();
        // The enqueue gate the feed build asks before queuing a pass. Scoped so the job it adds
        // lands on the BUILD's tracked context and commits with the build's own writes.
        services.AddScoped<Application.CrossReference.CrossReferenceScheduler>();
        // The cross-run breaker: process-wide, because the runs it counts are separate scopes.
        services.AddSingleton<Application.CrossReference.CrossReferenceRunBreaker>();
        // The read side of the same table, shared by the feed build and both read-path serves so
        // the three cannot disagree about which prompt generation or which fingerprint is current.
        services.AddScoped<Application.CrossReference.CrossReferenceFactors>();
        // What the home band is allowed to say, in counted numbers only.
        services.AddScoped<Application.CrossReference.CrossReferenceStatusQuery>();
        // Offline only, reached through the founder-only /api/dev surface.
        services.AddScoped<Application.CrossReference.CrossReferenceBacktest>();

        // ── Job handlers ───────────────────────────────────────────────────────────────────
        services.AddScoped<IJobHandler, BuildTasteNarrativeJobHandler>();
        services.AddScoped<IJobHandler, PersonaliseTitlesJobHandler>();
        services.AddScoped<IJobHandler, CrossReferenceJobHandler>();

        return services;
    }

    /// <summary>The transport backstop for every chat adapter: above the longest purpose's total budget.</summary>
    private static readonly TimeSpan ChatTransportBackstop = TimeSpan.FromMinutes(5);

    private static void AddOpenRouterChatClient<TClient, TImplementation>(IServiceCollection services, IConfiguration configuration)
        where TClient : class
        where TImplementation : class, TClient =>
        services.AddHttpClient<TClient, TImplementation>(client =>
        {
            var baseUrl = configuration["OPENROUTER_BASE_URL"] ?? "https://openrouter.ai/api/v1";
            client.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
            client.Timeout = ChatTransportBackstop;
            client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        });
}
