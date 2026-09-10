using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FrostAura.Reel.Tests.Search;

/// <summary>
/// The search agent's HttpClient carries an EXPLICIT resilience budget rather than the standard
/// defaults, and the budget is validated at container-build time.
///
/// <para>Two failures live here, and neither is visible from a unit test of the adapter. The
/// standard default is three attempts at 10s inside a 30s total: a slow-but-alive chat completion
/// was killed at 10s and then bought twice more, so a turn that was going to succeed cost triple
/// and still failed. And <c>AddStandardResilienceHandler</c>'s own validator throws at STARTUP
/// when CircuitBreaker sampling drops below 2x the attempt timeout — a mistuned override does not
/// fail a test, it fails the process, which is why the pipeline is actually constructed here.</para>
/// </summary>
public class SearchAgentResilienceTests
{
    [Fact]
    public void The_search_agent_client_builds_its_pipeline_without_tripping_the_validator()
    {
        var services = new ServiceCollection();
        services.AddReelInfrastructure(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Postgres"] = "Host=localhost;Database=none;Username=none;Password=none",
                ["TestHelpers:ModelStubMode"] = "true",
            })
            .Build());
        // The infrastructure registration does not register the configuration it was handed, and
        // the OpenRouter adapters take IConfiguration directly.
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["TestHelpers:ModelStubMode"] = "true" })
            .Build());

        using var provider = services.BuildServiceProvider();

        // Resolving the typed client is what materialises the handler chain, and the handler chain
        // is what runs the options validator. No network, no DB — construction only.
        Assert.NotNull(provider.GetRequiredService<ISearchAgent>());
    }
}

