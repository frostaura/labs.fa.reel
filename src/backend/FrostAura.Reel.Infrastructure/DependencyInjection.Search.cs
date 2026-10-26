using FrostAura.Reel.Application.Search.AskReel.Engine;
using FrostAura.Reel.Infrastructure.Adapters;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FrostAura.Reel.Infrastructure;

public static partial class DependencyInjection
{
    /// <summary>The Ask Reel engine — v1, and v2 behind <c>ASK_ENGINE=v2</c>.</summary>
    private static IServiceCollection AddReelSearch(this IServiceCollection services)
    {
        services.AddScoped<Application.Search.AskReel.AskReelJudge>();
        services.AddScoped<Application.Search.LiveSearchExpansionService>();

        // ── Ask Reel v2 (§4) ────────────────────────────────────────────────────────────────
        // The three model adapters go through OpenRouterChatClient like every other chat adapter
        // (ask-plan, ask-judge, ask-answer — each billed against its own Ask allowance).
        services.AddScoped<RequestJudge>();
        services.AddScoped<IRequestJudge>(sp => sp.GetRequiredService<RequestJudge>());
        services.AddScoped<TitleFacts>();
        services.AddScoped<ITitleFacts>(sp => sp.GetRequiredService<TitleFacts>());
        services.AddSingleton<ITurnComposer, TurnComposer>();
        services.AddScoped<AskReelEngine>();
        AddAskChatClient<IAskReelPlanner, OpenRouterAskPlanner>(services);
        AddAskChatClient<IRequestJudgeModel, OpenRouterAskJudge>(services);
        AddAskChatClient<IAskReelAnswerer, OpenRouterAskAnswerer>(services);

        return services;
    }

    /// <summary>
    /// The same typed-client shape as the LLM area's chat adapters (no resilience handler — the chat
    /// client owns retries and per-purpose timeouts — and the transport backstop above the longest
    /// purpose), with the base URL read from the container's configuration at client creation.
    /// </summary>
    private static void AddAskChatClient<TClient, TImplementation>(IServiceCollection services)
        where TClient : class
        where TImplementation : class, TClient =>
        services.AddHttpClient<TClient, TImplementation>((sp, client) =>
        {
            var baseUrl = sp.GetRequiredService<IConfiguration>()["OPENROUTER_BASE_URL"] ?? "https://openrouter.ai/api/v1";
            client.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
            client.Timeout = ChatTransportBackstop;
            client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        });
}
