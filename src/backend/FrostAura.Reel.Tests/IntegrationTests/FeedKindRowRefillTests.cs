using System.Text.Json;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Feed;
using FrostAura.Reel.Domain.Library;
using FrostAura.Reel.Domain.Ml;
using FrostAura.Reel.Domain.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace FrostAura.Reel.Tests.IntegrationTests;

/// <summary>
/// The Home refill (<c>GET /api/feed</c> backfilling a seat a since-seen title left) keeps each
/// labelled row's label true (HREV6-RET-01): a "New &amp; for you" seat goes only to a title released
/// inside the build's window, a "Deep cuts — under the radar" seat only to a title no more popular
/// than the deep cuts the build dealt and at the deep-cuts prediction floor — or the seat stays
/// empty. Checked over real Postgres and the real endpoint, with the refill pool set by hand so the
/// title the pool ranks FIRST is always the one the row's rule must refuse.
/// </summary>
[Collection("postgres")]
public class FeedKindRowRefillTests(PostgresFixture fixture, ReelApiFactory api) : IClassFixture<ReelApiFactory>
{
    private static readonly DateTime Now = DateTime.UtcNow;

    public static TheoryData<bool> WithAndWithoutAQualifyingTitle => [true, false];

    [Theory]
    [MemberData(nameof(WithAndWithoutAQualifyingTitle))]
    public async Task A_since_seen_new_for_you_card_is_refilled_only_by_a_recent_release_or_not_at_all(bool poolHasRecent)
    {
        if (!fixture.Available)
        {
            return;
        }

        // The pool's leader is a 2005 title (the old-show case on the founder's dump); the recent one
        // sits under it on prediction, so a refill that ignores the row's rule takes the old one.
        var oldLeader = NewTitle("Old leader", released: new DateTime(2005, 3, 1, 0, 0, 0, DateTimeKind.Utc), popularity: 2m);
        var justOutside = NewTitle("Just outside the window", released: Now.AddDays(-905), popularity: 2m);
        var recent = NewTitle("Recent", released: Now.AddDays(-200), popularity: 2m);
        var pool = poolHasRecent
            ? new[] { (oldLeader, 8.90m), (justOutside, 8.80m), (recent, 7.20m) }
            : new[] { (oldLeader, 8.90m), (justOutside, 8.80m) };

        var (client, kept) = await SeedAsync(
            FeedRowKind.NewForYou,
            dealt: [.. Enumerable.Range(0, 3).Select(i => NewTitle($"Dealt new {i}", released: Now.AddDays(-100 - i), popularity: 2m))],
            pool);

        var row = await RowAsync(client, "new-for-you");

        Assert.DoesNotContain(oldLeader.Id, row);
        Assert.DoesNotContain(justOutside.Id, row);
        Assert.Equal(poolHasRecent ? [.. kept, recent.Id] : kept, row);
    }

    [Theory]
    [MemberData(nameof(WithAndWithoutAQualifyingTitle))]
    public async Task A_since_seen_deep_cut_is_refilled_only_by_a_title_under_the_radar_or_not_at_all(bool poolHasObscure)
    {
        if (!fixture.Available)
        {
            return;
        }

        // The dealt deep cuts top out at 31.25 (the founder's dump). The pool's leader is a mainstream
        // show (popularity 104); next is an obscure title predicted under the deep-cuts floor of 6;
        // then one exactly at the dealt ceiling and one obscure enough, both clearing the floor.
        var mainstream = NewTitle("Mainstream", released: new DateTime(2012, 1, 1, 0, 0, 0, DateTimeKind.Utc), popularity: 104m);
        var underFloor = NewTitle("Obscure but under six", released: new DateTime(2012, 1, 1, 0, 0, 0, DateTimeKind.Utc), popularity: 1m);
        var aboveCeiling = NewTitle("Just above the dealt ceiling", released: new DateTime(2012, 1, 1, 0, 0, 0, DateTimeKind.Utc), popularity: 31.26m);
        var obscure = NewTitle("Obscure", released: new DateTime(2012, 1, 1, 0, 0, 0, DateTimeKind.Utc), popularity: 4m);
        var pool = poolHasObscure
            ? new[] { (mainstream, 8.90m), (aboveCeiling, 8.80m), (underFloor, 5.90m), (obscure, 6.50m) }
            : new[] { (mainstream, 8.90m), (aboveCeiling, 8.80m), (underFloor, 5.90m) };

        decimal[] dealtPopularity = [12m, 31.25m, 7m];
        var (client, kept) = await SeedAsync(
            FeedRowKind.DeepCuts,
            dealt: [.. dealtPopularity.Select((p, i) => NewTitle($"Dealt deep cut {i}", released: new DateTime(2010, 1, 1, 0, 0, 0, DateTimeKind.Utc), popularity: p))],
            pool);

        var row = await RowAsync(client, "deep-cuts");

        Assert.DoesNotContain(mainstream.Id, row);
        Assert.DoesNotContain(aboveCeiling.Id, row);
        Assert.DoesNotContain(underFloor.Id, row);
        Assert.Equal(poolHasObscure ? [.. kept, obscure.Id] : kept, row);
    }

    /// <summary>
    /// A paid account, an active snapshot whose <paramref name="kind"/> row holds
    /// <paramref name="dealt"/> (its FIRST card since rated), and <paramref name="pool"/> persisted as
    /// the snapshot artifact's scores. Returns the client and the row's kept cards in rank order.
    /// </summary>
    private async Task<(HttpClient Client, List<Guid> Kept)> SeedAsync(
        FeedRowKind kind, IReadOnlyList<Title> dealt, IReadOnlyList<(Title Title, decimal Predicted)> pool)
    {
        var (accountId, client) = await api.CreateSignedInAccountAsync($"kind-refill-{Guid.NewGuid():N}");
        await using var db = fixture.CreateContext(new AccountContext());
        var account = await db.Accounts.SingleAsync(a => a.Id == accountId);
        account.Tier = AccountTier.Paid;

        var snapshot = new FeedSnapshot
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            ModelArtifactId = Guid.NewGuid(),
            GeneratedAt = Now,
            Status = SnapshotStatus.Active,
        };
        db.FeedSnapshots.Add(snapshot);
        db.Titles.AddRange(dealt);
        for (var rank = 0; rank < dealt.Count; rank++)
        {
            db.FeedItems.Add(new FeedItem
            {
                Id = Guid.NewGuid(),
                AccountId = accountId,
                FeedSnapshotId = snapshot.Id,
                Row = kind,
                Rank = rank,
                TitleId = dealt[rank].Id,
                PredictedRating = 7.5m,
                WhyThisSentence = string.Empty,
                WhySource = "Templater",
            });
        }

        db.UserRatings.Add(new UserRating
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            TitleId = dealt[0].Id,
            SubjectType = RatingSubjectType.Movie,
            Rating = 8,
            RatedAt = Now,
            FirstRatedAt = Now,
            SyncedAt = Now,
        });

        foreach (var (title, predicted) in pool)
        {
            db.Titles.Add(title);
            db.TitleScores.Add(new TitleScore
            {
                Id = Guid.NewGuid(),
                AccountId = accountId,
                TitleId = title.Id,
                ModelArtifactId = snapshot.ModelArtifactId,
                PredictedRating = predicted,
                ScoredAt = Now,
            });
        }

        await db.SaveChangesAsync();
        return (client, [.. dealt.Skip(1).Select(t => t.Id)]);
    }

    private static async Task<List<Guid>> RowAsync(HttpClient client, string kind)
    {
        using var response = await client.GetAsync("/api/feed");
        response.EnsureSuccessStatusCode();
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return [.. body.RootElement.GetProperty("rows").EnumerateArray()
            .Where(r => r.GetProperty("kind").GetString() == kind)
            .SelectMany(r => r.GetProperty("items").EnumerateArray())
            .Select(c => c.GetProperty("titleId").GetGuid())];
    }

    private static Title NewTitle(string name, DateTime released, decimal popularity) => new()
    {
        Id = Guid.NewGuid(),
        MediaType = MediaType.Movie,
        TmdbId = Random.Shared.NextInt64(1, long.MaxValue),
        TraktSlug = $"kind-refill-{Guid.NewGuid():N}",
        Name = $"{name} {Guid.NewGuid():N}",
        Language = "en", // no language rows reads as English-only
        Year = released.Year,
        ReleasedAt = released,
        PosterPath = "/p.jpg",
        TmdbPopularity = popularity,
        CreatedAt = Now,
    };
}
