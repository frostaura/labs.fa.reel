using FrostAura.Reel.Api;
using FrostAura.Reel.Application.CrossReference;
using FrostAura.Reel.Application.Jobs;
using FrostAura.Reel.Application.Lens;
using Microsoft.Extensions.Configuration;

namespace FrostAura.Reel.Tests.Harness;

/// <summary>
/// The startup line that reports what the process will actually spend (K-5). It is built from the
/// same budget objects the jobs read, so these tests vary the configuration and read the line back.
/// The budgets never touch their database for these properties, which is why <c>null!</c> is honest.
/// </summary>
public class EffectiveCeilingsTests
{
    private static string Line(params (string Key, string? Value)[] settings)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build();

        return EffectiveCeilings.Describe(
            configuration,
            new CrossReferenceBudget(null!, configuration),
            new RerankBudget(null!, configuration),
            new LensBudget(null!, configuration),
            new CatalogEnrichmentBudget(null!, configuration),
            new CatalogHydrationBudget(null!, configuration));
    }

    [Fact]
    public void With_nothing_configured_the_line_reports_the_code_defaults()
    {
        var line = Line();

        Assert.StartsWith(EffectiveCeilings.Prefix, line, StringComparison.Ordinal);
        Assert.Contains("xref calls=80 tokens=1200000", line, StringComparison.Ordinal);
        Assert.Contains("rerank calls=240 tokens=600000 (background stops at 70%)", line, StringComparison.Ordinal);
        Assert.Contains("lens calls=60 tokens=120000", line, StringComparison.Ordinal);
        Assert.Contains("extract tokens=1500000, embed tokens=3000000, hydrate titles=1500", line, StringComparison.Ordinal);
        Assert.Contains("extraction=unset (adapter default)", line, StringComparison.Ordinal);
        Assert.Contains("search=unset (follows OPENROUTER_MODEL)", line, StringComparison.Ordinal);
        Assert.Contains("stub=off", line, StringComparison.Ordinal);
    }

    [Fact]
    public void Configured_ceilings_are_what_the_line_reports()
    {
        // The founder's day-0 containment: the cross-reference ceiling at zero is the off switch.
        var line = Line(
            ("LLM_XREF_DAILY_CALLS", "0"),
            ("LLM_XREF_DAILY_TOKENS", "5"),
            ("LLM_RERANK_DAILY_CALLS", "7"),
            ("LLM_RERANK_DAILY_TOKENS", "8"),
            ("LLM_LENS_DAILY_CALLS", "9"),
            ("LLM_LENS_DAILY_TOKENS", "10"),
            ("EXTRACT_DAILY_TOKENS", "11"),
            ("EMBED_DAILY_TOKENS", "12"),
            ("HYDRATE_MAX_TITLES_PER_DAY", "13"),
            ("OPENROUTER_MODEL", "openai/gpt-5.4-mini"),
            ("REEL_SEARCH_MODEL", "openai/gpt-6-luna"),
            ("TestHelpers:ModelStubMode", "True"));

        Assert.Contains("xref calls=0 tokens=5", line, StringComparison.Ordinal);
        Assert.Contains("rerank calls=7 tokens=8", line, StringComparison.Ordinal);
        Assert.Contains("lens calls=9 tokens=10", line, StringComparison.Ordinal);
        Assert.Contains("extract tokens=11, embed tokens=12, hydrate titles=13", line, StringComparison.Ordinal);
        Assert.Contains("extraction=openai/gpt-5.4-mini, search=openai/gpt-6-luna, stub=on", line, StringComparison.Ordinal);
    }

    [Fact]
    public void A_negative_ceiling_is_reported_as_the_zero_the_budget_enforces()
    {
        Assert.Contains("lens calls=0", Line(("LLM_LENS_DAILY_CALLS", "-4")), StringComparison.Ordinal);
    }

    [Fact]
    public void The_line_never_carries_a_credential()
    {
        var line = Line(
            ("OPENROUTER_API_KEY", "sk-or-v1-secret-value"),
            ("EMBEDDINGS_API_KEY", "sk-embed-secret-value"),
            ("JWT_SECRET", "jwt-secret-value"),
            ("ConnectionStrings:Postgres", "Host=x;Password=pg-secret-value"));

        Assert.DoesNotContain("secret-value", line, StringComparison.Ordinal);
    }
}
