using System.Globalization;
using FrostAura.Reel.Application.CrossReference;
using FrostAura.Reel.Application.Jobs;
using FrostAura.Reel.Application.Lens;

namespace FrostAura.Reel.Api;

/// <summary>
/// The one startup line that says what this process will actually spend.
///
/// <para><b>Why.</b> The ceilings in <c>.env</c> and in Portainer never reached the container
/// (K-5): compose passes an explicit allow-list and the LLM keys were not on it, so the live stack
/// ran the code defaults — 80 cross-reference calls and 1.2M tokens per account per day — while
/// <c>.env</c> said 40. Nothing on screen or in the log could show the difference. This line is
/// read from the SAME budget objects the jobs read, after configuration has been assembled, so it
/// reports the effective number whichever path delivered it (or failed to).</para>
///
/// <para>Model ids are printed because they are not secrets and they decide the bill; no key,
/// token or connection string is ever part of the line.</para>
/// </summary>
public static class EffectiveCeilings
{
    /// <summary>The fixed prefix, so the line can be found with one grep of a container log.</summary>
    public const string Prefix = "Effective LLM ceilings";

    /// <summary>Resolves the budget services from <paramref name="services"/> and describes them.</summary>
    public static string Describe(IServiceProvider services, IConfiguration configuration) =>
        Describe(
            configuration,
            services.GetRequiredService<CrossReferenceBudget>(),
            services.GetRequiredService<RerankBudget>(),
            services.GetRequiredService<LensBudget>(),
            services.GetRequiredService<CatalogEnrichmentBudget>(),
            services.GetRequiredService<CatalogHydrationBudget>());

    /// <summary>The line itself, from already-built budget objects.</summary>
    public static string Describe(
        IConfiguration configuration,
        CrossReferenceBudget crossReference,
        RerankBudget rerank,
        LensBudget lens,
        CatalogEnrichmentBudget enrichment,
        CatalogHydrationBudget hydration)
    {
        var extractionModel = configuration["OPENROUTER_MODEL"] is { Length: > 0 } m ? m : "unset (adapter default)";
        var searchModel = configuration["REEL_SEARCH_MODEL"] is { Length: > 0 } s ? s : "unset (follows OPENROUTER_MODEL)";
        var stubMode = string.Equals(configuration["TestHelpers:ModelStubMode"], "true", StringComparison.OrdinalIgnoreCase);

        var invariant = CultureInfo.InvariantCulture;
        return string.Concat(
            string.Create(invariant, $"{Prefix} — models: extraction={extractionModel}, search={searchModel}, stub={(stubMode ? "on" : "off")}; "),
            string.Create(invariant, $"per account per day: xref calls={crossReference.DailyCalls} tokens={crossReference.DailyTokens}, "),
            string.Create(invariant, $"rerank calls={rerank.DailyCalls} tokens={rerank.DailyTokens} (background stops at {RerankBudget.BackgroundFraction * 100:0}%), "),
            string.Create(invariant, $"lens calls={lens.DailyCalls} tokens={lens.DailyTokens}; "),
            string.Create(invariant, $"system-wide per day: extract tokens={enrichment.ExtractDailyTokens}, embed tokens={enrichment.EmbedDailyTokens}, hydrate titles={hydration.DailyTitles}."));
    }
}
