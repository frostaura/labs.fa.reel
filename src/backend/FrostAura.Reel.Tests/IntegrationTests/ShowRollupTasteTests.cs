using FrostAura.Reel.Application.Ml;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Library;
using FrostAura.Reel.Domain.Ml;
using FrostAura.Reel.Domain.Tenancy;

namespace FrostAura.Reel.Tests.IntegrationTests;

/// <summary>
/// WS-5 task 12 on real Postgres: a show rated only episode by episode reaches the taste pools at its
/// rolled-up rating when the pools take rollups, and is not offered as a "because you loved" anchor.
/// Whether they do is the artifact's <see cref="TrainingRecipe"/> (the current one does not).
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

        var taste = await new FeatureVectorBuilder(db) { IncludeShowRollups = true }
            .BuildTasteStateAsync(account.Id, now, CancellationToken.None);

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

    /// <summary>
    /// HREV2-RET-02: a serving taste state describes the user the way the Active artifact was trained
    /// to read one. Rollups reached the pools of an artifact trained without them, shifted the
    /// founder's 2020s decade pool by 0.005, and every 2020s prediction fell from about 6 to about 3.
    /// The training builder always uses the current recipe, whatever the outgoing artifact had.
    /// </summary>
    [Theory]
    [InlineData(null, false, TrainingRecipe.ShowRollups)] // no artifact yet: its first one is the current recipe
    [InlineData("""{"version": 5}""", false, false)] // trained before the recipe stamp: no rollups
    [InlineData("""{"version": 5, "pools": "leave-one-out"}""", false, false)] // an unknown recipe
    [InlineData("""{"version": 5, "pools": "out-of-fold-5"}""", false, false)]
    [InlineData("""{"version": 5, "pools": "out-of-fold-5+show-rollups"}""", false, true)] // a recipe that had them
    [InlineData("""{"version": 5, "pools": "out-of-fold-5+show-rollups"}""", true, TrainingRecipe.ShowRollups)] // training: the current recipe
    public async Task A_serving_taste_state_follows_the_active_artifacts_recipe(
        string? activeSchemaJson, bool forTraining, bool expectRollup)
    {
        if (!fixture.Available)
        {
            return;
        }

        var genre = $"rollup-recipe-{Guid.NewGuid():N}";
        var now = DateTime.UtcNow;
        await using var db = fixture.CreateContext(new AccountContext());
        var account = new Account
        {
            Id = Guid.NewGuid(),
            TraktUserSlug = $"rollup-recipe-{Guid.NewGuid():N}",
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
            Name = $"Recipe show {genre}",
            Genres = [genre],
            Year = 2022,
            CreatedAt = now,
        };
        db.AddRange(account, show);
        db.UserRatings.Add(new UserRating
        {
            Id = Guid.NewGuid(),
            AccountId = account.Id,
            TitleId = show.Id,
            SubjectType = RatingSubjectType.Season,
            SeasonNumber = 1,
            Rating = 8,
            RatedAt = now.AddDays(-2),
            FirstRatedAt = now.AddDays(-2),
            SyncedAt = now,
        });
        if (activeSchemaJson is not null)
        {
            db.ModelArtifacts.Add(new ModelArtifact
            {
                Id = Guid.NewGuid(),
                AccountId = account.Id,
                Version = 1,
                ArtifactBytes = [1],
                FeatureSchemaJson = activeSchemaJson,
                TrainingRunId = Guid.NewGuid(),
                Status = ArtifactStatus.Active,
                TrainedAt = now,
            });
        }

        await db.SaveChangesAsync();

        var serving = new FeatureVectorBuilder(db);
        var taste = await (forTraining ? serving.ForTraining() : serving)
            .BuildTasteStateAsync(account.Id, now, CancellationToken.None);

        Assert.Equal(expectRollup, taste.GenreRatings.ContainsKey(genre));
        Assert.Equal(expectRollup, taste.DecadeRatings.ContainsKey(2020));
    }
}
