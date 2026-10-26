using System.Text.Json;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Feed;

namespace FrostAura.Reel.Tests.IntegrationTests;

/// <summary>
/// IR1S-03: when BuildDigest relaxes the 7.0 "high for you" floor it writes why onto every item,
/// and <c>GET /api/digest/current</c> used to drop it — so Home titled a rail of 6.5s "New &amp; high
/// for you this week". The endpoint now returns the reason at digest level; these pin that it is
/// present exactly when the build relaxed, and absent otherwise.
/// </summary>
[Collection("postgres")]
public class DigestDegradedReasonEndpointTests(PostgresFixture fixture, ReelApiFactory api) : IClassFixture<ReelApiFactory>
{
    // The sentence the live 2026-10-26 digest carried, verbatim.
    private const string Relaxed =
        "Only 0 of this week's arrivals cleared the 7 “high for you” bar, so it was relaxed to 6 for this digest.";

    private static readonly Guid A = Guid.Parse("00000000-0000-0000-0000-00000000000a");
    private static readonly Guid B = Guid.Parse("00000000-0000-0000-0000-00000000000b");

    /// <summary>
    /// Each row is a two-item digest's per-item reasons and the digest-level reason expected back.
    /// </summary>
    [Theory]
    // An honest digest carries no reason.
    [InlineData(null, null, null)]
    // A relaxed digest carries it on every item.
    [InlineData(Relaxed, Relaxed, Relaxed)]
    // It is a digest-level fact: an item without it does not hide it on the next.
    [InlineData(null, Relaxed, Relaxed)]
    // Blank is no reason — it must not retitle the rail with nothing to say instead.
    [InlineData("  ", "", null)]
    public void The_digest_reason_is_any_items_non_blank_reason(string? first, string? second, string? expected) =>
        Assert.Equal(expected, Api.Endpoints.DigestEndpoints.DigestDegradedReason(
        [
            new DigestItem(A, 6.7m, DigestReasons.NewRelease, first),
            new DigestItem(B, 6.5m, DigestReasons.NewOnProvider, second),
        ]));

    [Fact]
    public void An_empty_digest_carries_no_reason() =>
        Assert.Null(Api.Endpoints.DigestEndpoints.DigestDegradedReason([]));

    [Fact]
    public async Task A_relaxed_digest_says_so_even_when_its_first_title_is_gone()
    {
        if (!fixture.Available)
        {
            return;
        }

        var (accountId, client) = await api.CreateSignedInAccountAsync($"digest-relaxed-{Guid.NewGuid():N}");
        var present = await SeedTitleAsync();
        // The first item names a title no longer in the catalogue, so it renders no card — the
        // reason must still come back, because it describes the build, not that card.
        await SeedDigestAsync(accountId, JsonSerializer.Serialize(new[]
        {
            new DigestItem(Guid.NewGuid(), 6.7m, DigestReasons.NewRelease, Relaxed),
            new DigestItem(present, 6.5m, DigestReasons.NewOnProvider, Relaxed),
        }));

        var body = await GetDigestAsync(client);

        Assert.Equal(Relaxed, body.GetProperty("degradedReason").GetString());
        var card = Assert.Single(body.GetProperty("items").EnumerateArray());
        Assert.Equal(present, card.GetProperty("titleId").GetGuid());
        Assert.Equal(6.5m, card.GetProperty("predictedRating").GetDecimal());
    }

    [Fact]
    public async Task An_honest_digest_and_a_pre_reason_digest_return_a_null_reason()
    {
        if (!fixture.Available)
        {
            return;
        }

        // Honest: written by today's build, every item cleared the bar.
        var (honestAccount, honestClient) = await api.CreateSignedInAccountAsync($"digest-honest-{Guid.NewGuid():N}");
        var honestTitle = await SeedTitleAsync();
        await SeedDigestAsync(honestAccount, JsonSerializer.Serialize(new[]
        {
            new DigestItem(honestTitle, 7.8m, DigestReasons.NewRelease),
        }));

        // Pre-reason: raw jsonb from before the field existed — no "degradedReason" key at all.
        var (legacyAccount, legacyClient) = await api.CreateSignedInAccountAsync($"digest-legacy-{Guid.NewGuid():N}");
        var legacyTitle = await SeedTitleAsync();
        await SeedDigestAsync(legacyAccount,
            $$"""[{"titleId":"{{legacyTitle}}","predictedRating":7.2,"reason":"new-release"}]""");

        foreach (var (client, titleId) in new[] { (honestClient, honestTitle), (legacyClient, legacyTitle) })
        {
            var body = await GetDigestAsync(client);
            Assert.Equal(JsonValueKind.Null, body.GetProperty("degradedReason").ValueKind);
            Assert.Equal(titleId, Assert.Single(body.GetProperty("items").EnumerateArray()).GetProperty("titleId").GetGuid());
        }
    }

    [Fact]
    public async Task No_digest_yet_returns_the_reason_key_as_null()
    {
        if (!fixture.Available)
        {
            return;
        }

        var (_, client) = await api.CreateSignedInAccountAsync($"digest-none-{Guid.NewGuid():N}");

        var body = await GetDigestAsync(client);

        // Same shape on every branch, so the client never has to tell "absent" from "no reason".
        Assert.Equal(JsonValueKind.Null, body.GetProperty("degradedReason").ValueKind);
        Assert.Empty(body.GetProperty("items").EnumerateArray());
    }

    private static async Task<JsonElement> GetDigestAsync(HttpClient client)
    {
        using var response = await client.GetAsync("/api/digest/current");
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.Clone();
    }

    private async Task<Guid> SeedTitleAsync()
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var title = new Title
        {
            Id = Guid.NewGuid(),
            MediaType = MediaType.Movie,
            TmdbId = Random.Shared.NextInt64(1, long.MaxValue),
            TraktId = Random.Shared.NextInt64(1, long.MaxValue),
            TraktSlug = $"digest-reason-{Guid.NewGuid():N}",
            Name = $"Digest Reason {Guid.NewGuid():N}",
            Genres = ["drama"],
            ReleasedAt = DateTime.UtcNow.AddDays(-2),
            CreatedAt = DateTime.UtcNow,
        };
        db.Add(title);
        await db.SaveChangesAsync();
        return title.Id;
    }

    private async Task SeedDigestAsync(Guid accountId, string itemsJson)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        db.AccountDigests.Add(new AccountDigest
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            WeekStart = DigestWeek.MondayOf(DateTime.UtcNow),
            GeneratedAt = DateTime.UtcNow,
            ItemsJson = itemsJson,
        });
        await db.SaveChangesAsync();
    }
}
