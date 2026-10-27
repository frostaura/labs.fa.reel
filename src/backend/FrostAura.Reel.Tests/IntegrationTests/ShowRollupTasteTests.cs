using FrostAura.Reel.Application.Ml;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Library;
using FrostAura.Reel.Domain.Tenancy;

namespace FrostAura.Reel.Tests.IntegrationTests;

/// <summary>
/// WS-5 task 12 on real Postgres: a show rated only episode by episode reaches the taste pools at its
/// rolled-up rating, and is not offered as a "because you loved" anchor.
/// </summary>
[Collection("postgres")]
public class ShowRollupTasteTests(PostgresFixture fixture)
{
    [Fact]
    public async Task An_episode_rated_show_joins_the_genre_pool_but_never_anchors_a_row()
    {
        if (!fixture.Available)
        {
            return;
        }

        var genre = $"rollup-{Guid.NewGuid():N}";
        var now = DateTime.UtcNow;
        await using var db = fixture.CreateContext(new AccountContext());
        var account = new Account
        {
            Id = Guid.NewGuid(),
            TraktUserSlug = $"rollup-{Guid.NewGuid():N}",
            CreatedAt = now,
            LastSeenAt = now,
            PipelineStageChangedAt = now,
        };
        var show = new Title
        {
            Id = Guid.NewGuid(),
            MediaType = MediaType.Show,
            TmdbId = Random.Shared.NextInt64(1, long.MaxValue),
            TraktSlug = string.Empty,
            Name = $"Rollup show {genre}",
            Genres = [genre],
            Year = 2015,
            CreatedAt = now,
        };
        db.AddRange(account, show);
        for (var episode = 1; episode <= 6; episode++)
        {
            db.UserRatings.Add(new UserRating
            {
                Id = Guid.NewGuid(),
                AccountId = account.Id,
                TitleId = show.Id,
                SubjectType = RatingSubjectType.Episode,
                SeasonNumber = 1,
                EpisodeNumber = episode,
                Rating = 10,
                RatedAt = now.AddDays(-episode),
                FirstRatedAt = now.AddDays(-episode),
                SyncedAt = now,
            });
        }

        await db.SaveChangesAsync();

        var taste = await new FeatureVectorBuilder(db).BuildTasteStateAsync(account.Id, now, CancellationToken.None);

        // Six 10s against the neutral 7.0 (no whole-title ratings): (60 + 21) / 9 = 9.
        var pooled = Assert.Single(taste.GenreRatings[genre]);
        Assert.Equal((short)9, pooled.Rating);
        Assert.DoesNotContain(show.Id, taste.LovedAnchorPool);
        Assert.DoesNotContain(show.Id, taste.TopLovedTitleIds);
    }

    /// <summary>
    /// The ablation switch the offline rollup eval reads (R1-23): with it off, a show rated only by
    /// season is absent from every pool, and the same rows with it on reach the pool at the season
    /// mean. Seasons this time, not episodes, so both rollup arms are covered.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_ablation_switch_takes_a_season_rated_show_out_of_the_pools(bool includeRollups)
    {
        if (!fixture.Available)
        {
            return;
        }

        var genre = $"rollup-off-{Guid.NewGuid():N}";
        var now = DateTime.UtcNow;
        await using var db = fixture.CreateContext(new AccountContext());
        var account = new Account
        {
            Id = Guid.NewGuid(),
            TraktUserSlug = $"rollup-off-{Guid.NewGuid():N}",
            CreatedAt = now,
            LastSeenAt = now,
            PipelineStageChangedAt = now,
        };
        var show = new Title
        {
            Id = Guid.NewGuid(),
            MediaType = MediaType.Show,
            TmdbId = Random.Shared.NextInt64(1, long.MaxValue),
            TraktSlug = string.Empty,
            Name = $"Season-rated show {genre}",
            Genres = [genre],
            Year = 2019,
            CreatedAt = now,
        };
        db.AddRange(account, show);
        foreach (var (season, rating) in new[] { (1, (short)9), (2, (short)8) })
        {
            db.UserRatings.Add(new UserRating
            {
                Id = Guid.NewGuid(),
                AccountId = account.Id,
                TitleId = show.Id,
                SubjectType = RatingSubjectType.Season,
                SeasonNumber = season,
                Rating = rating,
                RatedAt = now.AddDays(-season),
                FirstRatedAt = now.AddDays(-season),
                SyncedAt = now,
            });
        }

        await db.SaveChangesAsync();

        var taste = await new FeatureVectorBuilder(db) { IncludeShowRollups = includeRollups }
            .BuildTasteStateAsync(account.Id, now, CancellationToken.None);

        if (includeRollups)
        {
            Assert.Equal((short)9, Assert.Single(taste.GenreRatings[genre]).Rating); // 8.5, the season mean, rounds up
            Assert.Equal(9m, taste.UserMean);
        }
        else
        {
            Assert.False(taste.GenreRatings.ContainsKey(genre));
            Assert.Equal(7m, taste.UserMean); // nothing rated whole and no rollup: the neutral mean
        }
    }
}
