using FrostAura.Reel.Application.Jobs;
using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Infrastructure.Adapters;
using FrostAura.Reel.Infrastructure.Telemetry;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FrostAura.Reel.Infrastructure;

public static partial class DependencyInjection
{
    /// <summary>The usage ledger, the embedding and OpenRouter adapters, the lens and cross-reference services and their jobs.</summary>
    private static IServiceCollection AddReelLlm(this IServiceCollection services, IConfiguration configuration)
    {
        // ── Fair-Use usage ledger ──────────────────────────────────────────────────────────
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

        services.AddHttpClient<ITitleAttributeExtractor, OpenRouterAttributeExtractor>(client =>
        {
            var baseUrl = configuration["OPENROUTER_BASE_URL"] ?? "https://openrouter.ai/api/v1";
            client.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
            client.Timeout = TimeSpan.FromSeconds(60); // chat completions run longer than embeddings
            client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        }).AddStandardResilienceHandler();

        services.AddHttpClient<ISearchQueryInterpreter, OpenRouterSearchInterpreter>(client =>
        {
            var baseUrl = configuration["OPENROUTER_BASE_URL"] ?? "https://openrouter.ai/api/v1";
            client.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
            client.Timeout = TimeSpan.FromSeconds(60);
            client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        }).AddStandardResilienceHandler();

        services.AddHttpClient<ISearchAgent, OpenRouterSearchAgent>(client =>
        {
            var baseUrl = configuration["OPENROUTER_BASE_URL"] ?? "https://openrouter.ai/api/v1";
            client.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
            client.Timeout = TimeSpan.FromSeconds(60);
            client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        }).AddStandardResilienceHandler(o =>
        {
            // The ONE client a user sits and waits on, so the budget is stated rather than
            // defaulted. The default is 3 attempts at 10s inside 30s total; here a slow-but-alive
            // completion was being killed at 10s and then bought twice more, tripling the spend on
            // a turn that was going to succeed. ONE retry, because a chat completion that failed
            // has already been paid for and an identical re-ask is a second bill, not a cheaper
            // one. 25s per attempt is above the observed p99 for these two calls; 55s total leaves
            // room for exactly one retry and still lands inside the client's own 60s cap.
            // CircuitBreaker sampling must stay >= 2x the attempt timeout or the validator throws
            // at startup.
            o.Retry.MaxRetryAttempts = 1;
            o.AttemptTimeout.Timeout = TimeSpan.FromSeconds(25);
            o.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(55);
            o.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(50);
        });

        // The cross-reference judge runs a 60-title batch against a strict schema — the longest
        // single chat call in the product at 20-35s observed, so it gets the lens writer's ceiling
        // rather than the 60s the smaller calls use.
        services.AddHttpClient<Domain.Ports.ICrossReferenceJudge, OpenRouterCrossReferenceJudge>(client =>
        {
            var baseUrl = configuration["OPENROUTER_BASE_URL"] ?? "https://openrouter.ai/api/v1";
            client.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
            client.Timeout = TimeSpan.FromSeconds(90);
            client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        }).AddStandardResilienceHandler();

        // The lens writer runs longer than the other chat calls: a narrative is ~10 sentences and a
        // lens is five to ten times a why-line's output tokens, so the transport cap has to sit
        // above the callers' own budgets rather than pre-empting them.
        services.AddHttpClient<Domain.Lens.ILensWriter, OpenRouterLensWriter>(client =>
        {
            var baseUrl = configuration["OPENROUTER_BASE_URL"] ?? "https://openrouter.ai/api/v1";
            client.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
            client.Timeout = TimeSpan.FromSeconds(90);
            client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        }).AddStandardResilienceHandler();

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
}
