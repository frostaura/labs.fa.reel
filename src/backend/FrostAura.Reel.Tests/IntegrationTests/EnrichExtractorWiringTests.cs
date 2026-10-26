using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Ports;
using Microsoft.Extensions.DependencyInjection;

namespace FrostAura.Reel.Tests.IntegrationTests;

/// <summary>
/// The enrich handler classifies a failure only when the extractor the CONTAINER serves implements
/// <see cref="IClassifyingAttributeExtractor"/>. A decorator or a re-registration that hides it
/// compiles, passes every adapter test, and silently puts every network drop back on the attempt
/// budget (E-6, R1-47) — so the wiring itself is pinned here.
/// </summary>
[Collection("postgres")]
public class EnrichExtractorWiringTests(PostgresFixture fixture, ReelApiFactory api) : IClassFixture<ReelApiFactory>
{
    [Fact]
    public void The_container_serves_an_extractor_that_classifies_its_failures()
    {
        if (!fixture.Available)
        {
            return;
        }

        using var scope = api.Services.CreateScope();
        var extractor = scope.ServiceProvider.GetRequiredService<ITitleAttributeExtractor>();
        Assert.IsAssignableFrom<IClassifyingAttributeExtractor>(extractor);
    }
}
