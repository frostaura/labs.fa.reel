using System.Text.Json;
using FrostAura.Reel.Application.Search;
using FrostAura.Reel.Application.Ranking;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Feed;
using FrostAura.Reel.Domain.Filters;
using FrostAura.Reel.Domain.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace FrostAura.Reel.Tests.IntegrationTests;

/// <summary>
/// XM-03 and XM-07 on <c>GET /api/feed</c>: a content rule or a "for you" bar saved AFTER the build
/// applies to the materialised picks and rows on the next read, not on tomorrow's build — and a page
/// the bar holds cards back from says so.
/// </summary>
[Collection("postgres")]
public class FeedLateRulesTests(PostgresFixture fixture, ReelApiFactory api) : IClassFixture<ReelApiFactory>
{
    [Fact]
    public async Task A_rule_and_a_bar_saved_after_the_build_hold_on_the_next_read()
    {
        if (!fixture.Available)
        {
            return;
        }

        var (accountId, client) = await api.CreateSignedInAccountAsync($"feed-late-{Guid.NewGuid():N}"[..24]);
        await using var db = fixture.CreateContext(new AccountContext());
        var account = await db.Accounts.SingleAsync(a => a.Id == accountId);
        account.Tier = AccountTier.Paid;

        var kept = NewTitle("Kept Drama", "drama");
        var horror = NewTitle("Late Horror", "horror");
        var weak = NewTitle("Weak Thriller", "thriller");
        var row = NewTitle("Row Drama", "drama");
        db.Titles.AddRange(kept, horror, weak, row);
        var snapshot = new FeedSnapshot
        {
            Id = Guid.NewGuid(), AccountId = accountId, ModelArtifactId = Guid.NewGuid(),
            GeneratedAt = DateTime.UtcNow.AddMinutes(-5), Status = SnapshotStatus.Active,
        };
        db.FeedSnapshots.Add(snapshot);
        db.FeedItems.AddRange(
            Item(snapshot, FeedRowKind.Hero, 0, kept, 8.4m),
            Item(snapshot, FeedRowKind.Hero, 1, horror, 8.6m),
            Item(snapshot, FeedRowKind.Hero, 2, weak, 7.1m),
            Item(snapshot, FeedRowKind.DeepCuts, 0, row, 7.9m));
        await db.SaveChangesAsync();

        // Saved after the build: no horror, and nothing under 7.5 for you.
        db.ContentFilters.AddRange(
            new ContentFilter { Id = Guid.NewGuid(), AccountId = accountId, Kind = FilterKind.ExcludeGenre, Value = "horror", CreatedAt = DateTime.UtcNow },
            new ContentFilter
            {
                Id = Guid.NewGuid(), AccountId = accountId, Kind = FilterKind.MinPredictedRating,
                Value = ContentFilterRules.FormatRating(7.5m), CreatedAt = DateTime.UtcNow,
            });
        await db.SaveChangesAsync();

        using var response = await client.GetAsync("/api/feed");
        response.EnsureSuccessStatusCode();
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = body.RootElement;
        var hero = root.GetProperty("hero").EnumerateArray().Select(c => c.GetProperty("name").GetString()).ToList();
        var rows = root.GetProperty("rows").EnumerateArray()
            .SelectMany(r => r.GetProperty("items").EnumerateArray())
            .Select(c => c.GetProperty("name").GetString())
            .ToList();

        Assert.Equal(["Kept Drama"], hero);
        Assert.Equal(["Row Drama"], rows);
        var reason = root.GetProperty("degradedReason").GetString();
        Assert.NotNull(reason);
        Assert.Contains("7.5", reason, StringComparison.Ordinal);
        Assert.Contains("holds back 1", reason, StringComparison.Ordinal);

        // The page carries its measured band; this account's closed loop has no pairs, so it is
        // null rather than a "± 0.0" (WS-10A reads it before the Saved queue's).
        Assert.Equal(JsonValueKind.Null, root.GetProperty("expectedAbsoluteError").ValueKind);
    }

    [Fact]
    public void A_short_page_names_the_bar_instead_of_relaxing_it()
    {
        Assert.Null(ForYouBar.ShortReason(0m, 0, 20));
        Assert.Null(ForYouBar.ShortReason(7.5m, 20, 20));
        Assert.Equal(
            "Only 3 titles cleared your ≥9.9 “for you” bar tonight; nothing under it is shown. Lower it in Settings to see more.",
            ForYouBar.ShortReason(9.9m, 3, 20));
        Assert.True(ForYouBar.IsRetiredRelaxation("Your ≥9.9 “for you” floor left only 0 titles tonight; relaxed to 6.2 for this build."));
        Assert.False(ForYouBar.IsRetiredRelaxation(ForYouBar.ShortReason(9.9m, 0, 20)));
    }

    private static Title NewTitle(string name, string genre) => new()
    {
        Id = Guid.NewGuid(),
        MediaType = MediaType.Movie,
        TmdbId = Random.Shared.NextInt64(1, long.MaxValue),
        TraktSlug = $"feed-late-{Guid.NewGuid():N}",
        Name = name,
        Year = 2016,
        Genres = [genre],
        Language = "en",
        PosterPath = "/p.jpg",
        CreatedAt = DateTime.UtcNow,
    };

    private static FeedItem Item(FeedSnapshot snapshot, FeedRowKind row, int rank, Title title, decimal predicted) => new()
    {
        Id = Guid.NewGuid(),
        AccountId = snapshot.AccountId,
        FeedSnapshotId = snapshot.Id,
        Row = row,
        Rank = rank,
        TitleId = title.Id,
        PredictedRating = predicted,
        WhyThisSentence = string.Empty,
        WhySource = "Templater",
    };
}
