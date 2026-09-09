using FrostAura.Reel.Application.Ml;
using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Application.Ranking;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Filters;
using FrostAura.Reel.Domain.Library;
using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Domain.Ports.Tmdb;
using FrostAura.Reel.Domain.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrostAura.Reel.Tests.IntegrationTests;

/// <summary>
/// What the build actually asks TMDB for, on real Postgres.
///
/// <para>
/// Two defects are pinned. <b>Banned genres:</b> top genres were derived from every rated genre
/// with no reference to the account's ExcludeGenre rules, so a genre the user has explicitly
/// banned — Dean bans 8 of 25, including two of his highest-count ones — consumed a discover arm
/// on every build, for ever, fetching titles the eligibility layer is guaranteed to reject.
/// <b>Unordered seeds:</b> the "people who liked your favourites" seed query took five loved
/// titles with no ORDER BY, so Postgres could return any five rows — a different five between
/// builds, and rarely the actual top five that TopLovedTitleIds already ranks.
/// </para>
/// </summary>
[Collection("postgres")]
public class CandidateGeneratorPoolTests(PostgresFixture fixture)
{
    [Fact]
    public async Task A_banned_genre_never_costs_a_discover_arm_however_much_the_user_rated_it()
    {
        if (!fixture.Available)
        {
            return; // no Postgres available — covered in CI's backend-integration job
        }

        var accountId = await SeedAccountAsync();
        // Comedy is rated the most AND banned — exactly the shape that made this expensive.
        await RateGenreAsync(accountId, "comedy", 9, count: 12);
        await RateGenreAsync(accountId, "thriller", 8, count: 4);
        await RateGenreAsync(accountId, "drama", 8, count: 3);
        await ExcludeGenreAsync(accountId, "comedy");

        var tmdb = await RefreshAsync(accountId);

        var comedy = TmdbGenres.SlugToIds["comedy"];
        Assert.DoesNotContain(tmdb.DiscoveredGenreIds, id => id == comedy.Movie);
        Assert.DoesNotContain(tmdb.DiscoveredGenreIds, id => id == comedy.Tv);
        // The budget it used to burn goes to a genre the user can actually be shown.
        var thriller = TmdbGenres.SlugToIds["thriller"];
        Assert.Contains(tmdb.DiscoveredGenreIds, id => id == thriller.Movie);
    }

    [Fact]
    public async Task With_no_exclusions_the_top_genre_is_still_discovered()
    {
        if (!fixture.Available)
        {
            return;
        }

        // The mirror of the test above: the exclusion is what removes the arm, not the new query.
        var accountId = await SeedAccountAsync();
        await RateGenreAsync(accountId, "comedy", 9, count: 12);

        var tmdb = await RefreshAsync(accountId);

        var comedy = TmdbGenres.SlugToIds["comedy"];
        Assert.Contains(tmdb.DiscoveredGenreIds, id => id == comedy.Movie);
    }

    [Fact]
    public async Task The_recommendation_seeds_are_the_actual_top_five_loved_titles_in_order()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        // Eight loved titles, inserted in an order deliberately unrelated to their ratings, so a
        // seed list that reflects insertion order (or none at all) cannot pass by luck.
        var loved = new List<(Guid Id, long TmdbId, short Rating)>();
        short[] ratings = [8, 10, 9, 8, 10, 9, 10, 8];
        foreach (var rating in ratings)
        {
            var (id, tmdbId) = await SeedTitleAsync();
            await RateTitleAsync(accountId, id, rating);
            loved.Add((id, tmdbId, rating));
        }

        var taste = await TasteAsync(accountId);
        var expected = taste.TopLovedTitleIds
            .Take(5)
            .Select(id => loved.Single(l => l.Id == id).TmdbId)
            .ToList();

        var tmdb = await RefreshAsync(accountId, taste);

        Assert.Equal(5, tmdb.RecommendationSeeds.Count);
        Assert.Equal(expected, tmdb.RecommendationSeeds);
        // And every seed really is one of the top-rated titles, not an arbitrary loved row.
        Assert.All(tmdb.RecommendationSeeds, seed => Assert.True(loved.Single(l => l.TmdbId == seed).Rating >= 9));
    }

    // ── harness ─────────────────────────────────────────────────────────────────────────────

    private async Task<RecordingTmdbClient> RefreshAsync(Guid accountId, FeatureVectorBuilder.TasteState? taste = null)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        taste ??= await new FeatureVectorBuilder(db).BuildTasteStateAsync(accountId, DateTime.UtcNow, CancellationToken.None);
        var tmdb = new RecordingTmdbClient();
        var generator = new CandidateGenerator(db, tmdb, NullLogger<CandidateGenerator>.Instance);

        await generator.RefreshPoolAsync(accountId, taste, "ZA", CancellationToken.None);

        return tmdb;
    }

    private async Task<FeatureVectorBuilder.TasteState> TasteAsync(Guid accountId)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        return await new FeatureVectorBuilder(db).BuildTasteStateAsync(accountId, DateTime.UtcNow, CancellationToken.None);
    }

    private async Task<Guid> SeedAccountAsync()
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var account = new Account
        {
            Id = Guid.NewGuid(),
            TraktUserSlug = $"pool-{Guid.NewGuid():N}",
            CreatedAt = DateTime.UtcNow,
            LastSeenAt = DateTime.UtcNow,
            PipelineStageChangedAt = DateTime.UtcNow,
        };
        db.Add(account);
        await db.SaveChangesAsync();
        return account.Id;
    }

    private async Task<(Guid Id, long TmdbId)> SeedTitleAsync(params string[] genres)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var title = new Title
        {
            Id = Guid.NewGuid(),
            MediaType = MediaType.Movie,
            TmdbId = Random.Shared.NextInt64(1, long.MaxValue),
            TraktId = Random.Shared.NextInt64(1, long.MaxValue),
            TraktSlug = string.Empty,
            Name = $"title {Guid.NewGuid():N}",
            Genres = genres,
            Year = 2015,
            CreatedAt = DateTime.UtcNow,
        };
        db.Add(title);
        await db.SaveChangesAsync();
        return (title.Id, title.TmdbId!.Value);
    }

    private async Task RateGenreAsync(Guid accountId, string genre, short rating, int count)
    {
        for (var i = 0; i < count; i++)
        {
            var (id, _) = await SeedTitleAsync(genre);
            await RateTitleAsync(accountId, id, rating);
        }
    }

    private async Task RateTitleAsync(Guid accountId, Guid titleId, short rating)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        db.UserRatings.Add(new UserRating
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            TitleId = titleId,
            SubjectType = RatingSubjectType.Movie,
            Rating = rating,
            RatedAt = DateTime.UtcNow.AddDays(-Random.Shared.Next(1, 400)),
            SyncedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private async Task ExcludeGenreAsync(Guid accountId, string genre)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        db.ContentFilters.Add(new ContentFilter
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            Kind = FilterKind.ExcludeGenre,
            Value = genre,
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    /// <summary>Answers nothing, records everything: the assertions are about the CALLS, not the pool.</summary>
    private sealed class RecordingTmdbClient : ITmdbClient
    {
        public List<int> DiscoveredGenreIds { get; } = [];

        public List<long> RecommendationSeeds { get; } = [];

        public Task<IReadOnlyList<TmdbListItem>> DiscoverAsync(bool movies, int? genreId, string? region, DateTime? releasedAfter, int page, CancellationToken ct = default)
        {
            if (genreId is { } id)
            {
                DiscoveredGenreIds.Add(id);
            }

            return Task.FromResult<IReadOnlyList<TmdbListItem>>([]);
        }

        public Task<IReadOnlyList<TmdbListItem>> GetRecommendationsAsync(bool movies, long tmdbId, CancellationToken ct = default)
        {
            RecommendationSeeds.Add(tmdbId);
            return Task.FromResult<IReadOnlyList<TmdbListItem>>([]);
        }

        public Task<TmdbTitleDetails?> GetMovieAsync(long tmdbId, CancellationToken ct = default) => Task.FromResult<TmdbTitleDetails?>(null);

        public Task<TmdbTitleDetails?> GetTvAsync(long tmdbId, CancellationToken ct = default) => Task.FromResult<TmdbTitleDetails?>(null);

        public Task<IReadOnlyList<TmdbListItem>> GetTrendingAsync(bool movies, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TmdbListItem>>([]);

        public Task<IReadOnlyList<TmdbKeyword>> SearchKeywordsAsync(string query, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TmdbKeyword>>([]);

        public Task<IReadOnlyList<TmdbListItem>> SearchTitlesAsync(bool movies, string query, int page, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TmdbListItem>>([]);

        public Task<TmdbPerson?> SearchPersonAsync(string name, CancellationToken ct = default) => Task.FromResult<TmdbPerson?>(null);

        public Task<IReadOnlyList<TmdbListItem>> GetPersonCreditsAsync(long personId, PersonCreditRole role, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TmdbListItem>>([]);

        public Task<IReadOnlyList<TmdbListItem>> DiscoverByConceptAsync(bool movies, IReadOnlyList<int> genreIds, IReadOnlyList<int> keywordIds, string? region, DateTime? releasedAfter, int page, IReadOnlyList<int>? excludeGenreIds = null, IReadOnlyList<int>? excludeKeywordIds = null, DateTime? releasedBefore = null, int? maxRuntimeMinutes = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TmdbListItem>>([]);

        public Task<IReadOnlyList<TmdbWatchProvider>> GetWatchProvidersAsync(long tmdbId, bool movie, string region, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TmdbWatchProvider>>([]);
    }
}
