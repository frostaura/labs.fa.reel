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

    /// <summary>
    /// One page of the catalogue bulk seed's walk: discover ordered by <c>vote_count.desc</c>,
    /// constrained to a SINGLE release year and a vote-count floor, on the
    /// <see cref="RatePriority.Backfill"/> lane and billed to purpose <c>seed</c>.
    ///
    /// <para>
    /// It is a separate method rather than another parameter on <see cref="DiscoverAsync"/> for
    /// three reasons, each of which is a defect if it is fudged. The sort order is
    /// <c>vote_count.desc</c>, not <c>popularity.desc</c>: popularity is a present-day trailing
    /// metric that reshuffles daily, so a multi-day resumable walk over it revisits and misses rows
    /// unpredictably, while vote count is monotone enough to walk once. The year window is CLOSED
    /// at both ends, because TMDB caps discover at page 500 (10,000 results) for any one query and
    /// the unpartitioned corpus is an order of magnitude past that — the partition is what keeps
    /// every query under the wall. And the caller needs <c>total_pages</c> back, which the list
    /// helpers discard.
    /// </para>
    ///
    /// <para>
    /// Deliberately UNFILTERED by language. The rows are global; an account's language rule is
    /// applied at eligibility, where it can be relaxed per account without re-acquiring supply.
    /// </para>
    /// </summary>
    Task<TmdbDiscoverPage> DiscoverSeedPageAsync(
        bool movies, int year, int minVoteCount, int page, CancellationToken ct = default);

    /// <summary>This week's trending titles (movie or tv).</summary>
    Task<IReadOnlyList<TmdbListItem>> GetTrendingAsync(bool movies, CancellationToken ct = default);

    /// <summary>
    /// TMDB's collaborative recommendations for a title ("people who liked this also liked…") —
    /// real collaborative signal available from day one, without needing our own cross-tenant data.
    /// </summary>
    Task<IReadOnlyList<TmdbListItem>> GetRecommendationsAsync(bool movies, long tmdbId, CancellationToken ct = default);

    /// <summary>
    /// TMDB's <c>similar</c> list for a title — a DIFFERENT signal to
    /// <see cref="GetRecommendationsAsync"/>, not a synonym: recommendations are behavioural
    /// ("people who liked this also liked…"), similar is content-derived from genres and keywords.
    /// Running both over the same seed set widens the pool along two independent axes for one extra
    /// call per seed, which is why the seed arms alternate between them rather than picking one.
    /// </summary>
    Task<IReadOnlyList<TmdbListItem>> GetSimilarAsync(bool movies, long tmdbId, CancellationToken ct = default);

    /// <summary>Resolve free-text keyword names to TMDB keyword ids (/search/keyword). Interactive priority — drives live Ask Reel.</summary>
    Task<IReadOnlyList<TmdbKeyword>> SearchKeywordsAsync(string query, CancellationToken ct = default);

    /// <summary>
    /// <see cref="SearchKeywordsAsync"/> that tells "TMDB answered: no such keyword" from "TMDB did not
    /// answer" (a 429, a 5xx), which <see cref="SearchKeywordsAsync"/> reports identically as an empty
    /// list. A caller that caches a miss, or that would drop a topic for want of a keyword id, needs the
    /// difference: an outage must never be remembered as an answer. The default treats every list as
    /// an answer, which is what an in-memory fake means; the HTTP adapter overrides it.
    /// </summary>
    async Task<TmdbKeywordSearch> SearchKeywordsCheckedAsync(string query, CancellationToken ct = default) =>
        new(await SearchKeywordsAsync(query, ct), Answered: true);

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
    /// <paramref name="maxRuntimeMinutes"/> maps to <c>with_runtime.lte</c> ("under 90 minutes") —
    /// discovery-side only; the caller still post-filters the final pool, because the semantic arm
    /// bypasses TMDB entirely.
    /// <para>
    /// <paramref name="options"/> carries the account's standing content rules (language, crowd-rating
    /// floor, vote-count floor), the optional person constraint, and the rate-gate lane. It is how the
    /// feed build reuses this method instead of growing a second query builder next to it — the whole
    /// point of routing supply through here. Omitting it reproduces the original Ask Reel behaviour.
    /// </para>
    /// </summary>
    Task<IReadOnlyList<TmdbListItem>> DiscoverByConceptAsync(
        bool movies, IReadOnlyList<int> genreIds, IReadOnlyList<int> keywordIds,
        string? region, DateTime? releasedAfter, int page,
        IReadOnlyList<int>? excludeGenreIds = null, IReadOnlyList<int>? excludeKeywordIds = null,
        DateTime? releasedBefore = null, int? maxRuntimeMinutes = null,
        TmdbDiscoverOptions? options = null, CancellationToken ct = default);

    /// <summary>
    /// A movie collection and its parts (<c>/collection/{id}</c>), for the Similar rail's
    /// collection-first seed (WS-5 task 10): the id comes from <see cref="TmdbTitleDetails.Collection"/>.
    /// Null when TMDB 404s the id. The default answers "no collection", which is what an in-memory
    /// fake means; the HTTP adapter overrides it.
    /// </summary>
    Task<TmdbCollection?> GetCollectionAsync(long collectionId, CancellationToken ct = default) =>
        Task.FromResult<TmdbCollection?>(null);

    /// <summary>Watch providers for one title in one region (JustWatch-powered; attribution required).</summary>
    Task<IReadOnlyList<TmdbWatchProvider>> GetWatchProvidersAsync(long tmdbId, bool movie, string region, CancellationToken ct = default);
}
