using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Domain.Ports.Tmdb;

namespace FrostAura.Reel.Tests.Fakes;

/// <summary>
/// A catalogue that answers EVERYTHING empty. Wired where a test needs a turn to run end-to-end
/// through the real endpoint while producing no cards at all.
///
/// <para>That sounds like a degenerate fixture and is in fact the load-bearing one for the
/// interpretation-cache tests: an answer with cards puts those cards into the conversation's
/// no-repeat set, so two conversations compared against each other would differ in their SHOWN set
/// as well as in their history, and a test meant to pin the HISTORY half of the cache key would
/// pass whichever half was fixed. With no cards anywhere, both conversations carry an empty shown
/// set and the only thing separating them is the transcript.</para>
///
/// <para>A turn with no candidates is a completed turn with no cards — <c>TurnOutcome.Answered</c>
/// with <c>AskReelReason.Empty</c>, not a failure — so the transcript still records the question and
/// the reply, which is exactly what the next turn's history is built from.</para>
/// </summary>
internal sealed class SilentTmdbClient : ITmdbClient
{
    public Task<TmdbTitleDetails?> GetMovieAsync(long tmdbId, CancellationToken ct = default) =>
        Task.FromResult<TmdbTitleDetails?>(null);

    public Task<TmdbTitleDetails?> GetTvAsync(long tmdbId, CancellationToken ct = default) =>
        Task.FromResult<TmdbTitleDetails?>(null);

    public Task<IReadOnlyList<TmdbListItem>> DiscoverAsync(
        bool movies, int? genreId, string? region, DateTime? releasedAfter, int page, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<TmdbListItem>>([]);

    public Task<TmdbDiscoverPage> DiscoverSeedPageAsync(
        bool movies, int year, int minVoteCount, int page, CancellationToken ct = default) =>
        Task.FromResult(new TmdbDiscoverPage([], page, 0, 0));

    public Task<IReadOnlyList<TmdbListItem>> GetTrendingAsync(bool movies, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<TmdbListItem>>([]);

    public Task<IReadOnlyList<TmdbListItem>> GetRecommendationsAsync(
        bool movies, long tmdbId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<TmdbListItem>>([]);

    public Task<IReadOnlyList<TmdbListItem>> GetSimilarAsync(
        bool movies, long tmdbId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<TmdbListItem>>([]);

    public Task<IReadOnlyList<TmdbKeyword>> SearchKeywordsAsync(string query, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<TmdbKeyword>>([]);

    public Task<IReadOnlyList<TmdbListItem>> SearchTitlesAsync(
        bool movies, string query, int page, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<TmdbListItem>>([]);

    public Task<TmdbPerson?> SearchPersonAsync(string name, CancellationToken ct = default) =>
        Task.FromResult<TmdbPerson?>(null);

    public Task<IReadOnlyList<TmdbListItem>> GetPersonCreditsAsync(
        long personId, PersonCreditRole role, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<TmdbListItem>>([]);

    public Task<IReadOnlyList<TmdbListItem>> DiscoverByConceptAsync(
        bool movies, IReadOnlyList<int> genreIds, IReadOnlyList<int> keywordIds,
        string? region, DateTime? releasedAfter, int page,
        IReadOnlyList<int>? excludeGenreIds = null, IReadOnlyList<int>? excludeKeywordIds = null,
        DateTime? releasedBefore = null, int? maxRuntimeMinutes = null, TmdbDiscoverOptions? options = null,
        CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<TmdbListItem>>([]);

    public Task<IReadOnlyList<TmdbWatchProvider>> GetWatchProvidersAsync(
        long tmdbId, bool movie, string region, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<TmdbWatchProvider>>([]);
}
