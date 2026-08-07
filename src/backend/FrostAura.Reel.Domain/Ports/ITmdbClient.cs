using FrostAura.Reel.Domain.Ports.Tmdb;

namespace FrostAura.Reel.Domain.Ports;

/// <summary>
/// TMDB port — catalog hydration (metadata, credits, trailer) in one call per title via
/// append_to_response. Search/discover/providers join with their subsystems.
/// </summary>
public interface ITmdbClient
{
    /// <summary>Movie details with credits+videos appended; null when TMDB 404s the id.</summary>
    Task<TmdbTitleDetails?> GetMovieAsync(long tmdbId, CancellationToken ct = default);

    /// <summary>TV details with credits+videos appended; null when TMDB 404s the id.</summary>
    Task<TmdbTitleDetails?> GetTvAsync(long tmdbId, CancellationToken ct = default);

    /// <summary>Discover by genre/recency — the candidate-pool firehose.</summary>
    Task<IReadOnlyList<TmdbListItem>> DiscoverAsync(
        bool movies, int? genreId, string? region, DateTime? releasedAfter, int page, CancellationToken ct = default);

    /// <summary>This week's trending titles (movie or tv).</summary>
    Task<IReadOnlyList<TmdbListItem>> GetTrendingAsync(bool movies, CancellationToken ct = default);

    /// <summary>
    /// TMDB's collaborative recommendations for a title ("people who liked this also liked…") —
    /// real collaborative signal available from day one, without needing our own cross-tenant data.
    /// </summary>
    Task<IReadOnlyList<TmdbListItem>> GetRecommendationsAsync(bool movies, long tmdbId, CancellationToken ct = default);

    /// <summary>Resolve free-text keyword names to TMDB keyword ids (/search/keyword). Interactive priority — drives live Ask Reel.</summary>
    Task<IReadOnlyList<TmdbKeyword>> SearchKeywordsAsync(string query, CancellationToken ct = default);

    /// <summary>Free-text title search (/search/movie or /search/tv). Interactive priority.</summary>
    Task<IReadOnlyList<TmdbListItem>> SearchTitlesAsync(bool movies, string query, int page, CancellationToken ct = default);

    /// <summary>Resolve a person's name to their TMDB record (/search/person) — top match, or null. Interactive priority.</summary>
    Task<TmdbPerson?> SearchPersonAsync(string name, CancellationToken ct = default);

    /// <summary>
    /// A person's filmography (/person/{id}/combined_credits), sliced by <paramref name="role"/>:
    /// Director → crew where job == "Director"; Cast → cast; Any → both. Drives "all movies by X".
    /// Interactive priority.
    /// </summary>
    Task<IReadOnlyList<TmdbListItem>> GetPersonCreditsAsync(long personId, PersonCreditRole role, CancellationToken ct = default);

    /// <summary>
    /// Discover by MULTIPLE genre + keyword ids (OR-joined for recall) — the Ask Reel concept
    /// firehose, distinct from the single-genre feed <see cref="DiscoverAsync"/>. Interactive priority.
    /// <para>
    /// <paramref name="excludeGenreIds"/> / <paramref name="excludeKeywordIds"/> map to TMDB's
    /// <c>without_genres</c> / <c>without_keywords</c>. They matter because a negation applied only
    /// at ranking time can reorder the candidate pool but can never change what is IN it: if the
    /// rejected thing is what the query is closest to, it fills the pool before the exclusion is
    /// ever consulted. Shaping discovery is the only place an exclusion can actually remove
    /// something.
    /// </para>
    /// <paramref name="releasedBefore"/> closes the upper end of a decade query — without it
    /// "80s sci-fi" pulls 1980-to-today ordered by popularity.
    /// </summary>
    Task<IReadOnlyList<TmdbListItem>> DiscoverByConceptAsync(
        bool movies, IReadOnlyList<int> genreIds, IReadOnlyList<int> keywordIds,
        string? region, DateTime? releasedAfter, int page,
        IReadOnlyList<int>? excludeGenreIds = null, IReadOnlyList<int>? excludeKeywordIds = null,
        DateTime? releasedBefore = null, CancellationToken ct = default);

    /// <summary>Watch providers for one title in one region (JustWatch-powered; attribution required).</summary>
    Task<IReadOnlyList<TmdbWatchProvider>> GetWatchProvidersAsync(long tmdbId, bool movie, string region, CancellationToken ct = default);
}
