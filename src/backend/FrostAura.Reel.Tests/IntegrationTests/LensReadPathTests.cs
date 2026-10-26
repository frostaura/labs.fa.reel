using System.Diagnostics;
using System.Net;
using System.Text.Json;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Sync;
using FrostAura.Reel.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FrostAura.Reel.Tests.IntegrationTests;

/// <summary>
/// <c>GET …/lens</c> is off the read path (WS-1 task 7, F-6): it answers from what is stored and
/// queues the lens pass instead of generating while the request waits. Its own class — and host —
/// so the timing is not shared with a heavier suite.
/// </summary>
[Collection("postgres")]
public class LensReadPathTests(PostgresFixture fixture, ReelApiFactory api) : IClassFixture<ReelApiFactory>
{
    [Fact]
    public async Task Opening_an_uncached_title_answers_without_generating_and_queues_one_pass()
    {
        if (!fixture.Available)
        {
            return;
        }

        var (accountId, client) = await api.CreateSignedInAccountAsync($"lens-read-{Guid.NewGuid():N}");
        using (client)
        {
            var tmdbId = await SeedTitleAsync();
            var url = $"/api/titles/movie/{tmdbId}/lens";

            // Warm-up: the first request pays the host's own first-hit costs and the fingerprint.
            using (var warm = await client.GetAsync(url))
            {
                Assert.Equal(HttpStatusCode.OK, warm.StatusCode);
            }

            var timings = new List<double>();
            for (var open = 0; open < 3; open++)
            {
                var clock = Stopwatch.StartNew();
                using var response = await client.GetAsync(url);
                timings.Add(clock.Elapsed.TotalMilliseconds);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

                // Nothing was written on the request: the ladder's bottom rung, and a promise.
                Assert.Equal("none", body.RootElement.GetProperty("source").GetString());
                Assert.True(body.RootElement.GetProperty("pending").GetBoolean());
            }

            // Four opens, one queued pass, and no note written inline.
            Assert.Equal(1, await CountAsync(db => db.SyncJobs.IgnoreQueryFilters()
                .CountAsync(j => j.AccountId == accountId && j.Kind == JobKind.PersonaliseTitles)));
            Assert.Equal(0, await CountAsync(db => db.TitleLensCopies.IgnoreQueryFilters()
                .CountAsync(c => c.AccountId == accountId)));
            Assert.All(timings, ms => Assert.True(ms < 150, $"lens GET took {ms:0} ms (≥ 150)"));
        }
    }

    private async Task<long> SeedTitleAsync()
    {
        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ReelDbContext>();
        var tmdbId = Random.Shared.Next(1, int.MaxValue);
        db.Titles.Add(new Title
        {
            Id = Guid.NewGuid(),
            MediaType = MediaType.Movie,
            TraktSlug = $"lens-read-{Guid.NewGuid():N}",
            TmdbId = tmdbId,
            Name = "Soldado",
            Year = 2018,
            Genres = ["crime", "thriller"],
            Overview = "A task force escalates the war on the cartels.",
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        return tmdbId;
    }

    private async Task<int> CountAsync(Func<ReelDbContext, Task<int>> count)
    {
        using var scope = api.Services.CreateScope();
        return await count(scope.ServiceProvider.GetRequiredService<ReelDbContext>());
    }
}
