using System.Text.Json;
using FrostAura.Reel.Application.Ranking;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Feed;
using FrostAura.Reel.Domain.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace FrostAura.Reel.Tests.IntegrationTests;

/// <summary>
/// <c>GET /api/feed</c> over HTTP, for the two things WS-5 changed on the Home read path: an anchored
/// card's chips are the evidence STORED on its row (no catalogue reads, and no drift when the
/// catalogue moves under a kept card), and a persisted line the serve-time guard refuses is served
/// as no line at all.
/// </summary>
[Collection("postgres")]
public class FeedEndpointEvidenceTests(PostgresFixture fixture, ReelApiFactory api) : IClassFixture<ReelApiFactory>
{
    [Fact]
    public async Task Home_serves_the_stored_evidence_and_hides_a_refused_line()
    {
        if (!fixture.Available)
        {
            return;
        }

        var (accountId, client) = await api.CreateSignedInAccountAsync($"feed-evidence-{Guid.NewGuid():N}");
        var (hero, anchor, member) = await SeedAsync(accountId);

        using var response = await client.GetAsync("/api/feed");
        response.EnsureSuccessStatusCode();
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var cards = Cards(body.RootElement).ToDictionary(c => c.GetProperty("titleId").GetGuid());

        // The member shares NOTHING with its anchor in the catalogue (no credits, no keywords): a
        // live read would find no evidence. The chip is the one stored when the rail was dealt.
        var chips = cards[member].GetProperty("evidence");
        Assert.Equal(1, chips.GetArrayLength());
        Assert.Equal("person", chips[0].GetProperty("kind").GetString());
        Assert.Equal("Michael Mann", chips[0].GetProperty("label").GetString());
        Assert.Equal("director", chips[0].GetProperty("role").GetString());

        // A hedged persisted hero line is not shown; the honest anchored line is.
        Assert.Equal(string.Empty, cards[hero].GetProperty("whyThis").GetString());
        Assert.Equal("Same director as Heat: Michael Mann.", cards[member].GetProperty("whyThis").GetString());
        Assert.False(cards.ContainsKey(anchor));
    }

    private static IEnumerable<JsonElement> Cards(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty("titleId", out _) && element.TryGetProperty("whyThis", out _))
            {
                yield return element;
            }

            foreach (var property in element.EnumerateObject())
            {
                foreach (var card in Cards(property.Value))
                {
                    yield return card;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                foreach (var card in Cards(item))
                {
                    yield return card;
                }
            }
        }
    }

    private async Task<(Guid Hero, Guid Anchor, Guid Member)> SeedAsync(Guid accountId)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var account = await db.Accounts.SingleAsync(a => a.Id == accountId);
        account.Tier = AccountTier.Paid; // the free tier is served the hero only

        Title NewTitle(string name) => new()
        {
            Id = Guid.NewGuid(),
            MediaType = MediaType.Movie,
            TmdbId = Random.Shared.NextInt64(1, long.MaxValue),
            TraktSlug = $"feed-evidence-{Guid.NewGuid():N}",
            // The default language rule is English only, and the Home read applies the rules (XM-03).
            Language = "en",
            Name = name,
            Year = 2016,
            PosterPath = "/p.jpg",
            CreatedAt = DateTime.UtcNow,
        };
        var hero = NewTitle("Hero pick");
        var anchor = NewTitle("Heat");
        var member = NewTitle("Collateral");
        db.Titles.AddRange(hero, anchor, member);

        var snapshot = new FeedSnapshot
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            ModelArtifactId = Guid.NewGuid(),
            GeneratedAt = DateTime.UtcNow,
            Status = SnapshotStatus.Active,
        };
        db.FeedSnapshots.Add(snapshot);
        db.FeedItems.Add(new FeedItem
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            FeedSnapshotId = snapshot.Id,
            Row = FeedRowKind.Hero,
            Rank = 0,
            TitleId = hero.Id,
            PredictedRating = 7.6m,
            WhyThisSentence = "Sharp legal drama, though the series format is a weaker fit for you.",
            WhySource = "Llm",
        });
        db.FeedItems.Add(new FeedItem
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            FeedSnapshotId = snapshot.Id,
            Row = FeedRowKind.BecauseYouLoved,
            AnchorTitleId = anchor.Id,
            Rank = 0,
            TitleId = member.Id,
            PredictedRating = 7.2m,
            WhyThisSentence = "Same director as Heat: Michael Mann.",
            WhySource = "Templater",
            EvidenceJson = AnchorRowGate.Serialize([new SharedEvidence("person", "Michael Mann", "director")]),
        });

        await db.SaveChangesAsync();
        return (hero.Id, anchor.Id, member.Id);
    }
}
