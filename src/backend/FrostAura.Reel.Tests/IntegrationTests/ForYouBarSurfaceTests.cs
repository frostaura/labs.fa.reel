using System.Globalization;
using System.Text.Json;
using FrostAura.Reel.Application.Ml;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Feed;
using FrostAura.Reel.Domain.Filters;
using FrostAura.Reel.Domain.Library;
using FrostAura.Reel.Domain.Ml;
using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Domain.Tenancy;
using FrostAura.Reel.Infrastructure.Ml;
using Microsoft.EntityFrameworkCore;

namespace FrostAura.Reel.Tests.IntegrationTests;

/// <summary>
/// R1-04 and R1-22 on the wire. The "for you" bar on <c>GET /api/feed</c> — the snapshot read and
/// its live refill from stored scores — is held to the number the card's badge shows, and the card
/// is sent that number; the title page sends the same number and the same measured band as the feed.
/// </summary>
[Collection("postgres")]
public class ForYouBarSurfaceTests(PostgresFixture fixture, ReelApiFactory api) : IClassFixture<ReelApiFactory>
{
    /// <summary>
    /// A hero card at a midpoint under a bar at each value. The badge decides: a stored 7.35 reads
    /// "7.4" and clears a 7.4 bar (the raw comparison held it back), a 7.44 reads "7.4" and does not
    /// clear 7.5. A served card carries the badge's own one-decimal number.
    /// </summary>
    [Theory]
    [InlineData("7.35", "7.4", "7.4")]
    [InlineData("7.35", "7.35", "7.4")]
    [InlineData("6.05", "6.1", "6.1")]
    [InlineData("7.45", "7.5", "7.5")]
    [InlineData("7.25", "7.3", "7.3")]
    [InlineData("7.34", "7.4", null)]
    [InlineData("6.04", "6.1", null)]
    [InlineData("7.44", "7.5", null)]
    public async Task The_feed_holds_a_card_to_the_bar_as_its_badge_reads(string stored, string bar, string? served)
    {
        if (!fixture.Available)
        {
            return;
        }

        var (accountId, client) = await api.CreateSignedInAccountAsync($"bar-badge-{Guid.NewGuid():N}"[..24]);
        await using (var db = fixture.CreateContext(new AccountContext()))
        {
            var title = NewTitle("Midpoint Drama");
            db.Titles.Add(title);
            var snapshot = Snapshot(accountId, Guid.NewGuid());
            db.FeedSnapshots.Add(snapshot);
            db.FeedItems.Add(Item(snapshot, FeedRowKind.Hero, 0, title, Dec(stored)));
            db.ContentFilters.Add(Bar(accountId, Dec(bar)));
            await db.SaveChangesAsync();
        }

        using var response = await client.GetAsync("/api/feed");
        response.EnsureSuccessStatusCode();
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var hero = body.RootElement.GetProperty("hero").EnumerateArray().ToList();
        if (served is null)
        {
            Assert.Empty(hero);
            Assert.Contains("holds back 1", body.RootElement.GetProperty("degradedReason").GetString(), StringComparison.Ordinal);
            return;
        }

        var card = Assert.Single(hero);
        Assert.Equal(served, card.GetProperty("predictedRating").GetRawText());
    }

    /// <summary>
    /// The live refill's SQL bound and the read's in-memory check are one rule: a stored 7.45 refills
    /// a seat under a 7.5 bar (it reads "7.5"), a stored 7.44 never does. The raw SQL bound refilled
    /// neither and left the seat empty.
    /// </summary>
    [Fact]
    public async Task The_refill_admits_exactly_what_the_badge_clears()
    {
        if (!fixture.Available)
        {
            return;
        }

        var (accountId, client) = await api.CreateSignedInAccountAsync($"bar-refill-{Guid.NewGuid():N}"[..24]);
        await using (var db = fixture.CreateContext(new AccountContext()))
        {
            // Paid: a free account's page is the hero alone, and this seat is on a row.
            (await db.Accounts.SingleAsync(a => a.Id == accountId)).Tier = AccountTier.Paid;
            var artifactId = Guid.NewGuid();
            var dealt = NewTitle("Dealt Then Disliked");
            var clears = NewTitle("Refill At 7.45");
            var under = NewTitle("Refill At 7.44");
            db.Titles.AddRange(dealt, clears, under);
            var snapshot = Snapshot(accountId, artifactId);
            db.FeedSnapshots.Add(snapshot);
            db.FeedItems.Add(Item(snapshot, FeedRowKind.DeepCuts, 0, dealt, 8.2m));
            db.TitleScores.AddRange(Score(accountId, artifactId, clears, 7.45m), Score(accountId, artifactId, under, 7.44m));
            db.ContentFilters.Add(Bar(accountId, 7.5m));

            // Since the build, the dealt card picked up a footprint, so its seat is refilled live.
            db.UserTitleReactions.Add(new UserTitleReaction
            {
                Id = Guid.NewGuid(),
                AccountId = accountId,
                TitleId = dealt.Id,
                Kind = ReactionKind.NotInterested,
                CreatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        using var response = await client.GetAsync("/api/feed");
        response.EnsureSuccessStatusCode();
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var cards = body.RootElement.GetProperty("rows").EnumerateArray()
            .SelectMany(r => r.GetProperty("items").EnumerateArray())
            .ToDictionary(c => c.GetProperty("name").GetString()!, c => c.GetProperty("predictedRating").GetRawText());

        Assert.Equal("7.5", Assert.Contains("Refill At 7.45", cards));
        Assert.DoesNotContain("Refill At 7.44", cards.Keys);
        Assert.DoesNotContain("Dealt Then Disliked", cards.Keys);
    }

    /// <summary>
    /// A deep-linked title page reads the number its card reads (a stored 7.25 is "7.3", not the
    /// half-to-even "7.2") and carries the same measured band as <c>/api/feed</c>, from the same
    /// calibrator — and an unmeasured band is null on both rather than a "± 0.0".
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_title_page_carries_the_cards_number_and_the_feeds_band(bool measured)
    {
        if (!fixture.Available)
        {
            return;
        }

        var (accountId, client) = await api.CreateSignedInAccountAsync($"title-band-{Guid.NewGuid():N}"[..24]);
        var viewed = NewTitle("Deep Linked Title");
        await using (var db = fixture.CreateContext(new AccountContext()))
        {
            var artifactId = Guid.NewGuid();
            db.ModelArtifacts.Add(TrainedArtifact(accountId, artifactId));
            db.Titles.Add(viewed);
            var viewedScore = Score(accountId, artifactId, viewed, 7.25m);
            // With its contributions, as a build writes it: a bare number is rescored on open.
            viewedScore.ContributionsJson = """[{"feature":"traktRating","value":0.4}]""";
            db.TitleScores.Add(viewedScore);
            if (measured)
            {
                // Predictions that met a later rating: the closed loop's pairs, off by 1.0–2.0.
                var ratedAt = DateTime.UtcNow.AddDays(-2);
                foreach (var (predicted, rating) in new[] { (8.0m, (short)6), (6.0m, (short)7), (7.5m, (short)9), (5.0m, (short)6) })
                {
                    var rated = NewTitle($"Rated {predicted.ToString(CultureInfo.InvariantCulture)}");
                    db.Titles.Add(rated);
                    var score = Score(accountId, artifactId, rated, predicted);
                    score.ScoredAt = ratedAt.AddDays(-1);
                    db.TitleScores.Add(score);
                    db.UserRatings.Add(new UserRating
                    {
                        Id = Guid.NewGuid(),
                        AccountId = accountId,
                        TitleId = rated.Id,
                        SubjectType = RatingSubjectType.Movie,
                        Rating = rating,
                        RatedAt = ratedAt,
                        SyncedAt = ratedAt,
                    });
                }
            }

            await db.SaveChangesAsync();
        }

        using var titleResponse = await client.GetAsync($"/api/titles/movie/{viewed.TmdbId}");
        titleResponse.EnsureSuccessStatusCode();
        using var title = JsonDocument.Parse(await titleResponse.Content.ReadAsStringAsync());
        var prediction = title.RootElement.GetProperty("prediction");
        Assert.Equal("7.3", prediction.GetProperty("predictedRating").GetRawText());

        using var feedResponse = await client.GetAsync("/api/feed");
        feedResponse.EnsureSuccessStatusCode();
        using var feed = JsonDocument.Parse(await feedResponse.Content.ReadAsStringAsync());
        var feedBand = feed.RootElement.GetProperty("expectedAbsoluteError");
        var titleBand = prediction.GetProperty("expectedAbsoluteError");

        Assert.Equal(feedBand.GetRawText(), titleBand.GetRawText());
        Assert.Equal(measured ? JsonValueKind.Number : JsonValueKind.Null, titleBand.ValueKind);
        if (measured)
        {
            Assert.True(titleBand.GetDouble() > 0d);
        }
    }

    private static Title NewTitle(string name) => new()
    {
        Id = Guid.NewGuid(),
        MediaType = MediaType.Movie,
        TmdbId = Random.Shared.NextInt64(1, long.MaxValue),
        TraktSlug = $"bar-badge-{Guid.NewGuid():N}",
        Name = name,
        Year = 2016,
        Genres = ["drama"],
        Language = "en",
        PosterPath = "/p.jpg",
        CreatedAt = DateTime.UtcNow,
    };

    private static FeedSnapshot Snapshot(Guid accountId, Guid artifactId) => new()
    {
        Id = Guid.NewGuid(),
        AccountId = accountId,
        ModelArtifactId = artifactId,
        GeneratedAt = DateTime.UtcNow.AddMinutes(-5),
        Status = SnapshotStatus.Active,
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

    private static TitleScore Score(Guid accountId, Guid artifactId, Title title, decimal predicted) => new()
    {
        Id = Guid.NewGuid(),
        AccountId = accountId,
        TitleId = title.Id,
        ModelArtifactId = artifactId,
        PredictedRating = predicted,
        ScoredAt = DateTime.UtcNow,
    };

    private static ContentFilter Bar(Guid accountId, decimal bar) => new()
    {
        Id = Guid.NewGuid(),
        AccountId = accountId,
        Kind = FilterKind.MinPredictedRating,
        // As typed, not through FormatRating's one decimal: the theory holds a bar at each midpoint.
        Value = bar.ToString(CultureInfo.InvariantCulture),
        CreatedAt = DateTime.UtcNow,
    };

    /// <summary>A real (tiny) FastTree artifact, so the title page's walk can load it.</summary>
    private static ModelArtifact TrainedArtifact(Guid accountId, Guid artifactId)
    {
        var rng = new Random(31);
        var names = FeatureSchema.AllNames();
        var rows = new float[120][];
        var labels = new float[120];
        for (var i = 0; i < rows.Length; i++)
        {
            var v = new float[names.Length];
            for (var j = 0; j < v.Length; j++)
            {
                v[j] = (float)rng.NextDouble();
            }

            rows[i] = v;
            labels[i] = Math.Clamp(3f + (5f * v[0]), 1f, 10f);
        }

        var trained = new FastTreeModelEngine().Train(
            new TrainingMatrix(rows, labels, names),
            """{"Trees":20,"Leaves":8,"MinExamplesPerLeaf":5,"LearningRate":0.2}""");
        return new ModelArtifact
        {
            Id = artifactId,
            AccountId = accountId,
            Version = 1,
            Algo = "FastTree",
            ArtifactBytes = trained.ArtifactBytes,
            FeatureSchemaJson = JsonSerializer.Serialize(new { version = FeatureSchema.Version, names = FeatureSchema.AllNames() }),
            TrainingRunId = Guid.NewGuid(),
            Status = ArtifactStatus.Active,
            TrainedAt = DateTime.UtcNow,
        };
    }

    private static decimal Dec(string value) => decimal.Parse(value, CultureInfo.InvariantCulture);
}
