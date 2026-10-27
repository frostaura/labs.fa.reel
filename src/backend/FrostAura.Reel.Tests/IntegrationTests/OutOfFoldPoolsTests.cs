using FrostAura.Reel.Application.Ml;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Library;
using FrostAura.Reel.Domain.Tenancy;

namespace FrostAura.Reel.Tests.IntegrationTests;

/// <summary>
/// HREV2-RET-02 on real Postgres. Leave-one-out described a rated title by its pool minus its own
/// rating, so within one pool the value ordered the rows exactly opposite to their labels and a tree
/// could read the label back out of it: a model trained that way moved its whole 2020s mean from 8.8
/// to 3.1 across ±0.004 of decadeAffinity. Out-of-fold pools take the row's whole fold out, so rows
/// that share a fold and a pool are described by ONE value whatever their own ratings.
/// </summary>
[Collection("postgres")]
public class OutOfFoldPoolsTests(PostgresFixture fixture)
{
    private static readonly string[] Pooled =
    [
        "userGenreAffinity", "genreDrift", "decadeAffinity", "directorAffinity", "castAffinity",
        "writerAffinity", "lovedCastCount", "contrarianAdjustedRating", "knownPeopleFraction",
    ];

    /// <summary>
    /// The fixture varies: one shape pools every title under one actor in one decade, the other under
    /// one director across two genres, with a different spread of ratings — so the property is not an
    /// accident of one pool.
    /// </summary>
    [Theory]
    [InlineData(CreditRole.Actor, 1994, "drama", 0)]
    [InlineData(CreditRole.Director, 2021, "comedy", 3)]
    public async Task Rated_titles_that_share_a_fold_share_every_pooled_value_whatever_their_ratings(
        CreditRole role, int year, string genre, int ratingShift)
    {
        if (!fixture.Available)
        {
            return;
        }

        var (accountId, titles) = await SeedPoolAsync(role, year, genre, ratingShift, rated: 30);
        var vectors = await BuildAsync(accountId, [.. titles.Keys], includeRollups: null);

        var comparedPairs = 0;
        foreach (var fold in titles.Keys.GroupBy(FeatureVectorBuilder.TrainingFold))
        {
            var members = fold.ToList();
            for (var i = 1; i < members.Count; i++)
            {
                if (titles[members[i]] == titles[members[0]])
                {
                    continue;
                }

                comparedPairs++;
                foreach (var scalar in Pooled)
                {
                    var index = Array.IndexOf(FeatureSchema.Scalars, scalar);
                    Assert.True(
                        Math.Abs(vectors[members[0]][index] - vectors[members[i]][index]) < 1e-5f,
                        $"{scalar}: {vectors[members[0]][index]} for a {titles[members[0]]} and {vectors[members[i]][index]} for a {titles[members[i]]} in the same fold — the value still tracks the row's own label.");
                }
            }
        }

        Assert.True(comparedPairs >= 5, $"only {comparedPairs} same-fold pairs with different ratings; the fixture proves nothing");
    }

    [Fact]
    public async Task An_unrated_candidate_still_reads_the_whole_pool()
    {
        if (!fixture.Available)
        {
            return;
        }

        var (accountId, titles) = await SeedPoolAsync(CreditRole.Actor, 1987, "western", 1, rated: 12);
        var candidate = await SeedTitleAsync("Unrated candidate", 1988, "western");
        var vectors = await BuildAsync(accountId, [candidate, .. titles.Keys], includeRollups: null);

        // Nothing is rated outside this pool, so the prior is the pool's own mean and the shrunken
        // decade mean is (S + 3·S/N) / (N + 3) = S/N: the full pool, no fold taken out.
        var mean = titles.Values.Average(r => (decimal)r);
        Assert.Equal((float)mean, vectors[candidate][Array.IndexOf(FeatureSchema.Scalars, "decadeAffinity")], 4);
        Assert.Equal((float)mean, vectors[candidate][Array.IndexOf(FeatureSchema.Scalars, "userGenreAffinity")], 4);
    }

    [Fact]
    public async Task The_leave_one_out_arm_is_what_the_fold_replaced()
    {
        if (!fixture.Available)
        {
            return;
        }

        // The ablation switch, and the defect it measures: with folds off, two rows in one pool read
        // different decade values that order them opposite to their own ratings.
        var (accountId, titles) = await SeedPoolAsync(CreditRole.Actor, 2003, "mystery", 2, rated: 10);
        var vectors = await BuildAsync(accountId, [.. titles.Keys], includeRollups: null, outOfFold: false);
        var decade = Array.IndexOf(FeatureSchema.Scalars, "decadeAffinity");
        var ordered = titles.OrderBy(t => t.Value).Select(t => vectors[t.Key][decade]).ToList();
        Assert.True(ordered[0] > ordered[^1], "leave-one-out: the lowest-rated row reads the highest pool value");
    }

    private async Task<Dictionary<Guid, float[]>> BuildAsync(
        Guid accountId, IReadOnlyCollection<Guid> titleIds, bool? includeRollups, bool outOfFold = true)
    {
        var scoped = new AccountContext();
        scoped.SetAccount(accountId);
        await using var db = fixture.CreateContext(scoped);
        var builder = new FeatureVectorBuilder(db) { IncludeShowRollups = includeRollups, OutOfFoldPools = outOfFold };
        var now = DateTime.UtcNow;
        var taste = await builder.BuildTasteStateAsync(accountId, now, CancellationToken.None);
        return await builder.BuildAsync(taste, titleIds, now, CancellationToken.None);
    }

    /// <summary>
    /// <paramref name="rated"/> titles in one decade and genre, all crediting one person in
    /// <paramref name="role"/>, rated 2–10 in a spread set by <paramref name="ratingShift"/>.
    /// </summary>
    private async Task<(Guid AccountId, Dictionary<Guid, short> Ratings)> SeedPoolAsync(
        CreditRole role, int year, string genre, int ratingShift, int rated)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var now = DateTime.UtcNow;
        var account = new Account
        {
            Id = Guid.NewGuid(),
            TraktUserSlug = $"oof-{Guid.NewGuid():N}",
            CreatedAt = now,
            LastSeenAt = now,
            PipelineStageChangedAt = now,
        };
        var person = new Person { Id = Guid.NewGuid(), TmdbId = Random.Shared.NextInt64(1, long.MaxValue), Name = "Pooled person" };
        db.AddRange(account, person);

        var ratings = new Dictionary<Guid, short>();
        for (var i = 0; i < rated; i++)
        {
            var title = NewTitle($"Pooled {i}", year + (i % 3), genre);
            var rating = (short)(2 + ((i * 7 + ratingShift) % 9));
            db.Titles.Add(title);
            db.TitleCredits.Add(new TitleCredit
            {
                Id = Guid.NewGuid(),
                TitleId = title.Id,
                PersonId = person.Id,
                Role = role,
                CastOrder = role == CreditRole.Actor ? 0 : null,
            });
            db.UserRatings.Add(new UserRating
            {
                Id = Guid.NewGuid(),
                AccountId = account.Id,
                TitleId = title.Id,
                SubjectType = RatingSubjectType.Movie,
                Rating = rating,
                RatedAt = now.AddDays(-1 - i),
                FirstRatedAt = now.AddDays(-1 - i),
                SyncedAt = now,
            });
            ratings[title.Id] = rating;
        }

        await db.SaveChangesAsync();
        return (account.Id, ratings);
    }

    private async Task<Guid> SeedTitleAsync(string name, int year, string genre)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var title = NewTitle(name, year, genre);
        db.Titles.Add(title);
        await db.SaveChangesAsync();
        return title.Id;
    }

    private static Title NewTitle(string name, int year, string genre) => new()
    {
        Id = Guid.NewGuid(),
        MediaType = MediaType.Movie,
        TmdbId = Random.Shared.NextInt64(1, long.MaxValue),
        TraktSlug = string.Empty,
        Name = name,
        Language = "en",
        // No poster: discovery lanes filter on PosterPath, so these rows stay out of other tests' pools.
        PosterPath = null,
        Genres = [genre, "oof-shared"],
        Year = year,
        RuntimeMinutes = 100,
        TraktRating = 7m,
        TraktVotes = 500,
        TmdbPopularity = 10m,
        ReleasedAt = new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        CreatedAt = DateTime.UtcNow,
    };
}
