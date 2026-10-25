using System.Net;
using System.Text.Json;
using FrostAura.Reel.Domain.Sync;
using FrostAura.Reel.Infrastructure.Llm;
using FrostAura.Reel.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FrostAura.Reel.Tests.IntegrationTests;

/// <summary>
/// "Regenerate my taste profile" while the provider breaker is open (WS-1 task 2's "LLM job kinds
/// paused"). The request used to be queued and then quietly consumed by a job that paused; it is
/// now refused up front, with the breaker's code and retry time, and nothing is queued. Its own
/// class — and so its own host — because it opens the host's process-wide breaker.
/// </summary>
[Collection("postgres")]
public class NarrativeRegenerateBreakerTests(PostgresFixture fixture, ReelApiFactory api)
    : IClassFixture<ReelApiFactory>
{
    [Fact]
    public async Task Regenerate_behind_an_open_breaker_is_a_coded_503_and_queues_nothing()
    {
        if (!fixture.Available)
        {
            return;
        }

        var health = api.Services.GetRequiredService<ProviderHealthService>();
        var (accountId, client) = await api.CreateSignedInAccountAsync($"narrative-breaker-{Guid.NewGuid():N}");
        using (client)
        {
            health.ReportRefused(ApiProvider.OpenRouter, 402);
            try
            {
                using var refused = await client.PostAsync("/api/taste/narrative/regenerate", null);
                Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.StatusCode);
                using var body = JsonDocument.Parse(await refused.Content.ReadAsStringAsync());
                Assert.Equal("payment_required", body.RootElement.GetProperty("reason").GetString());
                Assert.Contains("out of credit", body.RootElement.GetProperty("error").GetString(), StringComparison.Ordinal);
                Assert.Equal(0, await NarrativeJobsAsync(accountId));
            }
            finally
            {
                health.ReportAnswered(ApiProvider.OpenRouter);
            }

            // The control: with the breaker closed the same request is accepted and queued.
            using var accepted = await client.PostAsync("/api/taste/narrative/regenerate", null);
            Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
            Assert.Equal(1, await NarrativeJobsAsync(accountId));
        }
    }

    private async Task<int> NarrativeJobsAsync(Guid accountId)
    {
        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ReelDbContext>();
        return await db.SyncJobs.IgnoreQueryFilters()
            .CountAsync(j => j.AccountId == accountId && j.Kind == JobKind.BuildTasteNarrative);
    }
}
