using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Domain.Ports.Tmdb;

namespace FrostAura.Reel.Tests.Fakes;

/// <summary>
/// A TMDB that records every discover, keyword and title search and answers from a page factory and a
/// keyword table. A phrase in <see cref="UnansweredKeywords"/> gets TMDB's "did not answer" (a 5xx or
/// 429 behind the HTTP adapter); one in <see cref="ThrowingKeywords"/> gets the socket failure the
/// resilience pipeline surfaces as <see cref="HttpRequestException"/>. Used by the TMDB discovery arm's
/// tests.
/// </summary>
public sealed class RecordingTmdbClient : ITmdbClient
{
    public sealed record DiscoverCall(
        bool Movies, int Page, IReadOnlyList<int> KeywordIds, IReadOnlyList<int>? ExcludeGenreIds,
        IReadOnlyList<int>? ExcludeKeywordIds, DateTime? After, DateTime? Before, TmdbDiscoverOptions? Options);

    public Dictionary<string, int> Keywords { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<DiscoverCall> Discovers { get; } = [];

    public List<string> Searches { get; } = [];

    public Func<bool, int, List<TmdbListItem>> PageFactory { get; init; } = (_, _) => [];

    public Func<int, TimeSpan> DiscoverDelay { get; init; } = _ => TimeSpan.Zero;

    public async Task<IReadOnlyList<TmdbListItem>> DiscoverByConceptAsync(
        bool movies, IReadOnlyList<int> genreIds, IReadOnlyList<int> keywordIds,
        string? region, DateTime? releasedAfter, int page,
        IReadOnlyList<int>? excludeGenreIds = null, IReadOnlyList<int>? excludeKeywordIds = null,
        DateTime? releasedBefore = null, int? maxRuntimeMinutes = null, TmdbDiscoverOptions? options = null,
        CancellationToken ct = default)
    {
        lock (Discovers)
        {
            Discovers.Add(new DiscoverCall(
                movies, page, keywordIds, excludeGenreIds, excludeKeywordIds, releasedAfter, releasedBefore, options));
        }

        var delay = DiscoverDelay(page);
        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(delay, ct);
        }

        return PageFactory(movies, page);
    }

    public HashSet<string> UnansweredKeywords { get; } = new(StringComparer.OrdinalIgnoreCase);

    public HashSet<string> ThrowingKeywords { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<string> KeywordSearches { get; } = [];

    public async Task<IReadOnlyList<TmdbKeyword>> SearchKeywordsAsync(string query, CancellationToken ct = default) =>
        (await SearchKeywordsCheckedAsync(query, ct)).Results;

    /// <summary>How long a keyword search for a phrase takes; honours cancellation, as the HTTP adapter does.</summary>
    public Func<string, TimeSpan> KeywordDelay { get; init; } = _ => TimeSpan.Zero;

    public async Task<TmdbKeywordSearch> SearchKeywordsCheckedAsync(string query, CancellationToken ct = default)
    {
        lock (KeywordSearches)
        {
            KeywordSearches.Add(query);
        }

        if (KeywordDelay(query) is { } delay && delay > TimeSpan.Zero)
        {
            await Task.Delay(delay, ct);
        }

        if (ThrowingKeywords.Contains(query))
        {
            throw new HttpRequestException("connection reset");
        }

        if (UnansweredKeywords.Contains(query))
        {
            return new TmdbKeywordSearch([], Answered: false);
        }

        return new TmdbKeywordSearch(
            Keywords.TryGetValue(query, out var id) ? [new TmdbKeyword(id, query)] : [], Answered: true);
    }

    public Task<IReadOnlyList<TmdbListItem>> SearchTitlesAsync(bool movies, string query, int page, CancellationToken ct = default)
    {
        lock (Searches)
        {
            Searches.Add(query);
        }

        return Task.FromResult<IReadOnlyList<TmdbListItem>>([]);
    }

    public Task<TmdbTitleDetails?> GetMovieAsync(long tmdbId, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<TmdbTitleDetails?> GetTvAsync(long tmdbId, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<IReadOnlyList<TmdbListItem>> DiscoverAsync(
        bool movies, int? genreId, string? region, DateTime? releasedAfter, int page, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task<TmdbDiscoverPage> DiscoverSeedPageAsync(bool movies, int year, int minVoteCount, int page, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task<IReadOnlyList<TmdbListItem>> GetTrendingAsync(bool movies, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task<IReadOnlyList<TmdbListItem>> GetRecommendationsAsync(bool movies, long tmdbId, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task<IReadOnlyList<TmdbListItem>> GetSimilarAsync(bool movies, long tmdbId, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task<TmdbPerson?> SearchPersonAsync(string name, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<IReadOnlyList<TmdbListItem>> GetPersonCreditsAsync(long personId, PersonCreditRole role, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task<IReadOnlyList<TmdbWatchProvider>> GetWatchProvidersAsync(long tmdbId, bool movie, string region, CancellationToken ct = default) =>
        throw new NotSupportedException();
}
