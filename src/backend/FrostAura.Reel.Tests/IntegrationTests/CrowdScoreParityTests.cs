using FrostAura.Reel.Application.Search.Retrieval;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Catalog;
using Microsoft.EntityFrameworkCore;

namespace FrostAura.Reel.Tests.IntegrationTests;

/// <summary>
/// The crowd score exists twice — <see cref="CrowdRating"/> in memory (every badge) and
/// <see cref="CrowdVotes"/> in SQL (supply counts, ordering, the shrunk-floor measurement). This pins
/// the two to each other on real Postgres across every source-selection branch, so a ranking or a
/// count can never disagree with the badge it sits next to.
/// </summary>
[Collection("postgres")]
public class CrowdScoreParityTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Sql_and_memory_pick_the_same_source_votes_and_shrunk_score()
    {
        if (!fixture.Available)
        {
            return;
        }

        (decimal? TmdbAverage, int TmdbVotes, decimal? TraktRating, int TraktVotes)[] shapes =
        [
            (7.44m, 1200, 8.1m, 4000),   // Trakt leads
            (7.44m, 1200, 9.1m, 40),     // thin Trakt yields to TMDB
            (null, 0, 8.25m, 77),        // Trakt alone
            (0m, 0, 6.5m, 12),           // TMDB zero falls through
            (10.0m, 2, null, 0),         // thin TMDB, shrunk hard
            (9.6m, 0, null, 0),          // rating without votes: no badge
            (null, 0, null, 0),          // nothing
            (5.0m, 2, 0m, 0),
            (8.44m, 20000, 7.9m, 50),    // Trakt exactly at its lead threshold
        ];

        var ids = new List<Guid>();
        await using (var db = fixture.CreateContext(new AccountContext()))
        {
            foreach (var (tmdbAverage, tmdbVotes, traktRating, traktVotes) in shapes)
            {
                var title = new Title
                {
                    Id = Guid.NewGuid(),
                    MediaType = MediaType.Movie,
                    TmdbId = Random.Shared.NextInt64(1, long.MaxValue),
                    TraktSlug = string.Empty,
                    Name = $"crowd-parity {Guid.NewGuid():N}",
                    Language = "en",
                    Genres = ["drama"],
                    TmdbPopularity = 0m,
                    TmdbVoteAverage = tmdbAverage,
                    TmdbVoteCount = tmdbVotes,
                    TraktRating = traktRating,
                    TraktVotes = traktVotes,
                    CreatedAt = DateTime.UtcNow,
                };
                db.Add(title);
                ids.Add(title.Id);
            }

            await db.SaveChangesAsync();
        }

        await using var read = fixture.CreateContext(new AccountContext());
        var titles = await read.Titles.AsNoTracking().Where(t => ids.Contains(t.Id)).ToListAsync();
        Assert.Equal(ids.Count, titles.Count);

        foreach (var title in titles)
        {
            var one = read.Titles.Where(t => t.Id == title.Id);
            var sqlVoteCount = await one.Select(CrowdVotes.Votes).SingleAsync();
            var sqlScore = await one.Select(CrowdVotes.ShrunkScore).SingleAsync();

            Assert.Equal(CrowdRating.VotesFor(title) ?? 0, sqlVoteCount);
            Assert.Equal(CrowdVotes.Of(title), sqlVoteCount);
            Assert.Equal(CrowdRating.For(title), sqlScore is { } s ? Math.Round(s, 1, MidpointRounding.AwayFromZero) : null);
        }

        // Ordering in SQL agrees with the badge: best shrunk first, unrated last.
        var ordered = await CrowdVotes.BestShrunkFirst(read.Titles.Where(t => ids.Contains(t.Id))).Select(t => t.Id).ToListAsync();
        var badges = ordered.Select(id => CrowdRating.For(titles.Single(t => t.Id == id))).ToList();
        var rated = badges.TakeWhile(b => b is not null).ToList();
        Assert.Equal(rated.OrderByDescending(b => b), rated);
        Assert.All(badges.Skip(rated.Count), b => Assert.Null(b));
    }
}
