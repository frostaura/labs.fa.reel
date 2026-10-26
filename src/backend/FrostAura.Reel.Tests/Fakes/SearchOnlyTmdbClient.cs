using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Domain.Ports.Tmdb;

namespace FrostAura.Reel.Tests.Fakes;

/// <summary>Search-only fake: canned movie/show pages, or a hard throw to prove degradation.</summary>
internal sealed class SearchOnlyTmdbClient : ITmdbClient
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
