using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Sync;
using FrostAura.Reel.Domain.Tenancy;
using FrostAura.Reel.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FrostAura.Reel.Tests.IntegrationTests;

/// <summary>
/// <c>POST /api/sync/history-cleanup</c> is founder-only through the shared route gate
/// (<c>.RequireFounder()</c>), never through a tier comparison inside its handler (J-11, R1-25).
///
/// <para>The deny answer is asserted by its body, not only its status: the shared gate answers
/// <c>tier not permitted</c>, and an inline check answers whatever its author typed. That body is
/// the one observable difference between "the route carries the gate" and "the handler happens to
/// check", so it is what keeps a future inline copy from passing as the gate.</para>
///
/// <para>The admitted cases prove the handler reads the account the gate admitted
/// (<c>http.GatedAccount()</c>) and not some other row: two founders exist, only the OTHER one has a
/// Trakt connection, and the caller must still be told its own connection is unavailable.</para>
///
/// <para>No case reaches Trakt: every admitted request stops at a local check (unknown title, no
/// connection, malformed body) before a token is read.</para>
/// </summary>
[Collection("postgres")]
public class HistoryCleanupGateTests(PostgresFixture fixture, ReelApiFactory api) : IClassFixture<ReelApiFactory>
{
    private const string Route = "/api/sync/history-cleanup";

    [Theory]
    [InlineData(AccountTier.Free)]
    [InlineData(AccountTier.Paid)]
    public async Task A_non_founder_is_refused_by_the_shared_founder_gate(AccountTier tier)
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync(tier);
        var tmdbId = await SeedTitleAsync();
        using var client = await api.CreateSignedInClientAsync(accountId);

        using var response = await client.PostAsJsonAsync(Route, Body(tmdbId));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("tier not permitted", await ErrorAsync(response));
    }

    [Fact]
    public async Task A_founder_is_admitted_and_an_unknown_title_answers_404()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync(AccountTier.Founder);
        using var client = await api.CreateSignedInClientAsync(accountId);

        using var response = await client.PostAsJsonAsync(Route, Body(tmdbId: -Random.Shared.NextInt64(1, long.MaxValue)));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_founder_is_judged_on_its_own_trakt_connection_not_another_founders()
    {
        if (!fixture.Available)
        {
            return;
        }

        var caller = await SeedAccountAsync(AccountTier.Founder);
        var other = await SeedAccountAsync(AccountTier.Founder);
        await SeedConnectionAsync(other, ConnectionStatus.Active);
        var tmdbId = await SeedTitleAsync();
        using var client = await api.CreateSignedInClientAsync(caller);

        using var response = await client.PostAsJsonAsync(Route, Body(tmdbId));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("trakt connection unavailable", await ErrorAsync(response));
    }

    [Fact]
    public async Task A_founder_with_a_revoked_connection_is_told_the_connection_is_unavailable()
    {
        if (!fixture.Available)
        {
            return;
        }

        var caller = await SeedAccountAsync(AccountTier.Founder);
        await SeedConnectionAsync(caller, ConnectionStatus.Revoked);
        var tmdbId = await SeedTitleAsync();
        using var client = await api.CreateSignedInClientAsync(caller);

        using var response = await client.PostAsJsonAsync(Route, Body(tmdbId));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("trakt connection unavailable", await ErrorAsync(response));
    }

    [Fact]
    public async Task A_founder_request_without_a_media_type_is_a_400_not_a_server_error()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync(AccountTier.Founder);
        using var client = await api.CreateSignedInClientAsync(accountId);

        using var content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
        using var response = await client.PostAsync(Route, content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("mediaType required", await ErrorAsync(response));
    }

    private static object Body(long tmdbId) => new
    {
        mediaType = "movie",
        tmdbId,
        startAt = DateTime.UtcNow.AddDays(-2),
        endAt = DateTime.UtcNow.AddDays(-1),
        dryRun = true,
    };

    private static async Task<string?> ErrorAsync(HttpResponseMessage response)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.TryGetProperty("error", out var error) ? error.GetString() : null;
    }

    private async Task<Guid> SeedAccountAsync(AccountTier tier)
    {
        var accountId = await api.SeedAccountAsync($"cleanup-{tier}-{Guid.NewGuid():N}");
        await WithDbAsync(async db =>
        {
            var account = await db.Accounts.SingleAsync(a => a.Id == accountId);
            account.Tier = tier;
        });
        return accountId;
    }

    private async Task<long> SeedTitleAsync()
    {
        var tmdbId = Random.Shared.NextInt64(1_000_000_000, long.MaxValue);
        await WithDbAsync(db =>
        {
            db.Titles.Add(new Title
            {
                Id = Guid.NewGuid(),
                MediaType = MediaType.Movie,
                TmdbId = tmdbId,
                TraktId = Random.Shared.NextInt64(1, long.MaxValue),
                TraktSlug = "cleanup",
                Name = "Cleanup",
                CreatedAt = DateTime.UtcNow,
            });
            return Task.CompletedTask;
        });
        return tmdbId;
    }

    private Task SeedConnectionAsync(Guid accountId, ConnectionStatus status) => WithDbAsync(db =>
    {
        db.TraktConnections.Add(new TraktConnection
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            Status = status,
            ExpiresAt = DateTime.UtcNow.AddDays(30),
        });
        return Task.CompletedTask;
    });

    private async Task WithDbAsync(Func<ReelDbContext, Task> work)
    {
        using var scope = api.Services.CreateScope();
        // Fixture work reads and writes across accounts on purpose (J-11).
        using var system = scope.ServiceProvider.GetRequiredService<IAccountContext>().EnterSystemScope();
        var db = scope.ServiceProvider.GetRequiredService<ReelDbContext>();
        await work(db);
        await db.SaveChangesAsync();
    }
}
