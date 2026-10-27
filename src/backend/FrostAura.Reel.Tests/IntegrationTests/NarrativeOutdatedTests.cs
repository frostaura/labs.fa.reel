using System.Net;
using System.Text.Json;
using FrostAura.Reel.Api.Endpoints;
using FrostAura.Reel.Domain.Lens;
using FrostAura.Reel.Domain.Sync;
using FrostAura.Reel.Infrastructure.Adapters;
using FrostAura.Reel.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FrostAura.Reel.Tests.IntegrationTests;

/// <summary>
/// A narrative the writer no longer stands behind is rewritten without waiting for the weekly pass
/// (IREV3S-07): a v2 narrative said "You rate below the crowd overall" beside a tile reading
/// "with the crowd", and its fingerprint had not moved, so nothing queued a rewrite.
/// </summary>
[Collection("postgres")]
public class NarrativeOutdatedTests(PostgresFixture fixture, ReelApiFactory api) : IClassFixture<ReelApiFactory>
{
    [Fact]
    public async Task Reading_a_narrative_from_an_older_prompt_queues_one_rewrite()
    {
        if (!fixture.Available)
        {
            return;
        }

        var (accountId, client) = await api.CreateSignedInAccountAsync($"narrative-outdated-{Guid.NewGuid():N}");
        using (client)
        {
            await SeedNarrativeAsync(accountId, OpenRouterLensWriter.CurrentNarrativePromptVersion - 1);

            for (var read = 0; read < 3; read++)
            {
                using var response = await client.GetAsync("/api/taste/narrative");
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                Assert.True(body.RootElement.GetProperty("regenerating").GetBoolean());
            }

            var jobs = await JobsAsync(accountId);
            var job = Assert.Single(jobs);
            Assert.Equal(NarrativeEndpoints.OutdatedNarrativeReason, job.TriggerReason);
            // Not forced: the user's edit is carried forward by the rewrite.
            Assert.Null(job.CursorJson);
        }
    }

    [Fact]
    public async Task A_current_narrative_queues_nothing()
    {
        if (!fixture.Available)
        {
            return;
        }

        var (accountId, client) = await api.CreateSignedInAccountAsync($"narrative-current-{Guid.NewGuid():N}");
        using (client)
        {
            await SeedNarrativeAsync(accountId, OpenRouterLensWriter.CurrentNarrativePromptVersion);

            using var response = await client.GetAsync("/api/taste/narrative");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Empty(await JobsAsync(accountId));
        }
    }

    [Fact]
    public async Task The_fact_sheet_behind_a_narrative_is_not_served()
    {
        if (!fixture.Available)
        {
            return;
        }

        // KILL-13 / WS-8 CR-7: GET /api/taste/narrative/facts was a founder-only read of the raw
        // fact sheet with no client. It is gone; the narrative itself still reads.
        var (accountId, client) = await api.CreateSignedInAccountAsync($"narrative-facts-{Guid.NewGuid():N}");
        using (client)
        {
            await SeedNarrativeAsync(accountId, OpenRouterLensWriter.CurrentNarrativePromptVersion);

            using var facts = await client.GetAsync("/api/taste/narrative/facts");
            Assert.Equal(HttpStatusCode.NotFound, facts.StatusCode);
            using var narrative = await client.GetAsync("/api/taste/narrative");
            Assert.Equal(HttpStatusCode.OK, narrative.StatusCode);
        }
    }

    private async Task SeedNarrativeAsync(Guid accountId, int promptVersion)
    {
        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ReelDbContext>();
        var facts = new TasteFacts(
            6.7m, 0, 0.6m, [], [], [], [], [], [], null, [], null, new ContrarianFact(0m, []), [], []);
        db.AccountTasteNarratives.Add(new AccountTasteNarrative
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            Version = 1,
            ProfileHash = "seeded",
            ProfileFingerprint = "{}",
            Text = "You rate much as the crowd does.",
            FactsJson = JsonSerializer.Serialize(facts, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            ModelId = "stub/deterministic-v1",
            PromptVersion = promptVersion,
            GeneratedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private async Task<List<SyncJob>> JobsAsync(Guid accountId)
    {
        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ReelDbContext>();
        return await db.SyncJobs.IgnoreQueryFilters()
            .Where(j => j.AccountId == accountId && j.Kind == JobKind.BuildTasteNarrative)
            .ToListAsync();
    }
}
