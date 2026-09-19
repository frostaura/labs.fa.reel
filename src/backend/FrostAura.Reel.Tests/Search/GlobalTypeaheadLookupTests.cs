using FrostAura.Reel.Api.Endpoints;
using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Domain.Ports.Tmdb;

namespace FrostAura.Reel.Tests.Search;

/// <summary>
/// The typeahead's global TMDB leg: movie + show searches unioned, rows the local shortlist
/// already shows deduped away, poster-less rows dropped, popularity-ordered, capped at six, every
/// row flagged source "tmdb" — and a TMDB outage degrading to an empty list rather than failing
/// the whole typeahead. This is a LOOKUP, so no eligibility/footprint filtering applies.
/// </summary>
public class GlobalTypeaheadLookupTests
{
    [Fact]
    public async Task Unions_dedupes_orders_by_popularity_and_flags_the_source()
    {
        var tmdb = new FakeSearchTmdbClient
        {
            MovieResults =
            [
                Item(1, true, "Jeanne Dielman", popularity: 12m, voteAverage: 7.8m),
                Item(2, true, "Already Local", popularity: 90m, voteAverage: 7m),
                Item(3, true, "News from Home", popularity: 6m, voteAverage: 7.4m),
            ],
            ShowResults = [Item(4, false, "Dielman: The Series", popularity: 30m, voteAverage: 0m)],
        };

        var rows = await SearchEndpoints.GlobalLookupAsync(
            tmdb, "dielman", new HashSet<(bool, long)> { (true, 2) }, CancellationToken.None);

        // Local row 2 deduped away; the rest ordered by popularity desc across both media types.
        Assert.Equal([4L, 1L, 3L], rows.Select(r => r.TmdbId));
        Assert.All(rows, r => Assert.Equal("tmdb", r.Source));
        Assert.Equal(["show", "movie", "movie"], rows.Select(r => r.MediaType));

        // voteAverage 0 means "no crowd signal", not "rated 0.0".
        Assert.Null(rows.Single(r => r.TmdbId == 4).CrowdRating);
        Assert.Equal(7.8m, rows.Single(r => r.TmdbId == 1).CrowdRating);

        // A lookup row can never carry a personal prediction — the title isn't scored yet.
        Assert.All(rows, r => Assert.Null(r.PredictedRating));
    }

    [Fact]
    public async Task Drops_posterless_rows_and_caps_at_six()
    {
        var tmdb = new FakeSearchTmdbClient
        {
            MovieResults = Enumerable.Range(1, 9)
                .Select(i => Item(i, true, $"Movie {i}", popularity: 100m - i, voteAverage: 6m))
                .Append(Item(99, true, "No Poster", popularity: 999m, voteAverage: 9m, posterPath: null))
                .ToArray(),
        };

        var rows = await SearchEndpoints.GlobalLookupAsync(
            tmdb, "movie", new HashSet<(bool, long)>(), CancellationToken.None);

        Assert.Equal(6, rows.Count);
        Assert.DoesNotContain(rows, r => r.TmdbId == 99); // poster-less dropped even at top popularity
        Assert.Equal([1L, 2L, 3L, 4L, 5L, 6L], rows.Select(r => r.TmdbId)); // popularity order, top six
    }

    [Fact]
    public async Task A_tmdb_failure_degrades_to_an_empty_list()
    {
        var tmdb = new FakeSearchTmdbClient { Throw = true };

        var rows = await SearchEndpoints.GlobalLookupAsync(
            tmdb, "dielman", new HashSet<(bool, long)>(), CancellationToken.None);

        Assert.Empty(rows); // the local typeahead survives a TMDB outage
    }

    private static TmdbListItem Item(
        long id, bool isMovie, string name, decimal popularity, decimal voteAverage, string? posterPath = "/p.jpg") =>
        new(id, isMovie, name, posterPath, null, "An overview.", popularity, voteAverage, 100,
            new DateTime(1975, 5, 14, 0, 0, 0, DateTimeKind.Utc), [18]);

    /// <summary>Search-only fake: canned movie/show pages, or a hard throw to prove degradation.</summary>
    private sealed class FakeSearchTmdbClient : ITmdbClient
    {
        public IReadOnlyList<TmdbListItem> MovieResults { get; init; } = [];

        public IReadOnlyList<TmdbListItem> ShowResults { get; init; } = [];

        public bool Throw { get; init; }

        public Task<IReadOnlyList<TmdbListItem>> SearchTitlesAsync(bool movies, string query, int page, CancellationToken ct = default) =>
            Throw
                ? throw new HttpRequestException("TMDB is down")
                : Task.FromResult(movies ? MovieResults : ShowResults);

        public Task<TmdbTitleDetails?> GetMovieAsync(long tmdbId, CancellationToken ct = default) =>
            Task.FromResult<TmdbTitleDetails?>(null);

        public Task<TmdbTitleDetails?> GetTvAsync(long tmdbId, CancellationToken ct = default) =>
            Task.FromResult<TmdbTitleDetails?>(null);

        // The bulk-seed walk is not exercised by this fixture; SeedCatalogJobHandlerTests owns its own
        // scripted TMDB client. An empty page reads as "this partition is exhausted", which is the
        // only honest answer a fake that was never given a corpus can give.
        public Task<TmdbDiscoverPage> DiscoverSeedPageAsync(bool movies, int year, int minVoteCount, int page, CancellationToken ct = default) =>
            Task.FromResult(new TmdbDiscoverPage([], page, 0, 0));

        public Task<IReadOnlyList<TmdbListItem>> DiscoverAsync(
            bool movies, int? genreId, string? region, DateTime? releasedAfter, int page, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TmdbListItem>>([]);

        public Task<IReadOnlyList<TmdbListItem>> GetTrendingAsync(bool movies, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TmdbListItem>>([]);

        public Task<IReadOnlyList<TmdbListItem>> GetRecommendationsAsync(bool movies, long tmdbId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TmdbListItem>>([]);

        public Task<IReadOnlyList<TmdbListItem>> GetSimilarAsync(bool movies, long tmdbId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TmdbListItem>>([]);

        public Task<IReadOnlyList<TmdbKeyword>> SearchKeywordsAsync(string query, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TmdbKeyword>>([]);

        public Task<TmdbPerson?> SearchPersonAsync(string name, CancellationToken ct = default) =>
            Task.FromResult<TmdbPerson?>(null);

        public Task<IReadOnlyList<TmdbListItem>> GetPersonCreditsAsync(long personId, PersonCreditRole role, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TmdbListItem>>([]);

        public Task<IReadOnlyList<TmdbListItem>> DiscoverByConceptAsync(
            bool movies, IReadOnlyList<int> genreIds, IReadOnlyList<int> keywordIds,
            string? region, DateTime? releasedAfter, int page,
            IReadOnlyList<int>? excludeGenreIds = null, IReadOnlyList<int>? excludeKeywordIds = null,
            DateTime? releasedBefore = null, int? maxRuntimeMinutes = null, TmdbDiscoverOptions? options = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TmdbListItem>>([]);

        public Task<IReadOnlyList<TmdbWatchProvider>> GetWatchProvidersAsync(long tmdbId, bool movie, string region, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TmdbWatchProvider>>([]);
    }
}
