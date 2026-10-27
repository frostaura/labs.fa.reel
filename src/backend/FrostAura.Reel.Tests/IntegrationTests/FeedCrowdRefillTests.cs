using System.Text.Json;
using FrostAura.Reel.Application.Ranking;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Feed;
using FrostAura.Reel.Domain.Library;
using FrostAura.Reel.Domain.Ml;
using FrostAura.Reel.Domain.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace FrostAura.Reel.Tests.IntegrationTests;

/// <summary>
/// The Home refill (<c>GET /api/feed</c> backfilling seats a since-rated title left) is the one path
/// that seats a card without a build choosing it, so it must rank the way the build ranks — including
/// the crowd term (R1-49, WS-3 CR-1). Checked over real Postgres and the real endpoint, with the
/// persisted predictions set by hand so the fixture, not a trained artifact, decides what the model
/// alone would pick.
/// </summary>
[Collection("postgres")]
public class FeedCrowdRefillTests(PostgresFixture fixture, ReelApiFactory api) : IClassFixture<ReelApiFactory>
{
    [Fact]
    public async Task A_refilled_seat_goes_to_the_title_the_crowd_term_ranks_first_not_the_highest_prediction()
    {
        if (!fixture.Available)
        {
            return;
        }

        var (accountId, client) = await api.CreateSignedInAccountAsync($"crowd-refill-{Guid.NewGuid():N}");
        Guid kept, wellVoted, fewVotes, noCrowd, disliked;
        await using (var db = fixture.CreateContext(new AccountContext()))
        {
            var account = await db.Accounts.SingleAsync(a => a.Id == accountId);
            account.Tier = AccountTier.Paid;

            var snapshot = new FeedSnapshot
            {
                Id = Guid.NewGuid(),
                AccountId = accountId,
                ModelArtifactId = Guid.NewGuid(),
                GeneratedAt = DateTime.UtcNow,
                Status = SnapshotStatus.Active,
            };
            db.FeedSnapshots.Add(snapshot);

            // A three-card hero whose first two cards the reader has since rated: two seats to refill.
            var heroTitles = Enumerable.Range(0, 3).Select(i => NewTitle($"Built hero {i}", null, 0)).ToList();
            db.Titles.AddRange(heroTitles);
            for (var rank = 0; rank < heroTitles.Count; rank++)
            {
                db.FeedItems.Add(new FeedItem
                {
                    Id = Guid.NewGuid(),
                    AccountId = accountId,
                    FeedSnapshotId = snapshot.Id,
                    Row = FeedRowKind.Hero,
                    Rank = rank,
                    TitleId = heroTitles[rank].Id,
                    PredictedRating = 7.5m,
                    WhyThisSentence = string.Empty,
                    WhySource = "Templater",
                });
            }

            foreach (var rated in heroTitles.Take(2))
            {
                db.UserRatings.Add(new UserRating
                {
                    Id = Guid.NewGuid(),
                    AccountId = accountId,
                    TitleId = rated.Id,
                    SubjectType = RatingSubjectType.Movie,
                    Rating = 8,
                    RatedAt = DateTime.UtcNow,
                    FirstRatedAt = DateTime.UtcNow,
                    SyncedAt = DateTime.UtcNow,
                });
            }

            // The pool the refill reads, every prediction set against its crowd: by prediction alone
            // the disliked title leads and the well-voted, liked one is last; by the build's key
            // (prediction × the crowd's score and volume terms) the liked one leads, the disliked one
            // follows it on its 5,000 votes, and the two-vote 10.0 and the no-crowd title — a tie on
            // prediction — fall behind both on the little that stands behind them, the shrunk score
            // alone separating the two.
            (Title Title, decimal Predicted) Candidate(string name, decimal? rating, int votes, decimal predicted) =>
                (NewTitle(name, rating, votes), predicted);
            var pool = new[]
            {
                Candidate("Well voted, liked", 8.0m, 5_000, 7.00m),
                Candidate("Perfect on two votes", 10.0m, 2, 7.10m),
                Candidate("No crowd", null, 0, 7.10m),
                Candidate("Well voted, disliked", 5.0m, 5_000, 7.30m),
            };
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
                    ScoredAt = DateTime.UtcNow,
                });
            }

            // The fixture holds, checked rather than assumed: well-voted-liked, then well-voted-disliked,
            // then the two-vote 10.0, then no crowd.
            var keys = pool.Select(c => (double)c.Predicted * CrowdPrior.FactorFor(c.Title)).ToList();
            Assert.True(keys[0] > keys[3] && keys[3] > keys[1] && keys[1] > keys[2], string.Join(", ", keys));

            await db.SaveChangesAsync();
            kept = heroTitles[2].Id;
            (wellVoted, fewVotes, noCrowd, disliked) = (pool[0].Title.Id, pool[1].Title.Id, pool[2].Title.Id, pool[3].Title.Id);
        }

        using var response = await client.GetAsync("/api/feed");
        response.EnsureSuccessStatusCode();
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var hero = body.RootElement.GetProperty("hero").EnumerateArray().ToList();
        var served = hero.Select(c => c.GetProperty("titleId").GetGuid()).ToList();

        // The kept card holds its seat, and the two refilled seats go to the crowd term's top two in
        // its order — not to the two highest predictions in theirs (the disliked title, then a tie).
        Assert.Equal([kept, wellVoted, disliked], served);
        Assert.DoesNotContain(fewVotes, served);
        Assert.DoesNotContain(noCrowd, served);

        // The refill reorders and never rewrites: each card shows its persisted prediction.
        var shown = hero.ToDictionary(c => c.GetProperty("titleId").GetGuid(), c => c.GetProperty("predictedRating").GetDecimal());
        Assert.Equal(7.00m, shown[wellVoted]);
        Assert.Equal(7.30m, shown[disliked]);
    }

    private static Title NewTitle(string name, decimal? tmdbRating, int tmdbVotes) => new()
    {
        Id = Guid.NewGuid(),
        MediaType = MediaType.Movie,
        TmdbId = Random.Shared.NextInt64(1, long.MaxValue),
        TraktSlug = $"crowd-refill-{Guid.NewGuid():N}",
        Name = $"{name} {Guid.NewGuid():N}",
        Language = "en", // no language rows reads as English-only
        Year = 2015,
        ReleasedAt = new DateTime(2015, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        PosterPath = "/p.jpg",
        TmdbVoteAverage = tmdbRating,
        TmdbVoteCount = tmdbVotes,
        CreatedAt = DateTime.UtcNow,
    };
}
