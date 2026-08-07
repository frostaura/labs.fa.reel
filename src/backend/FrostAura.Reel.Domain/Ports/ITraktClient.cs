using FrostAura.Reel.Domain.Ports.Trakt;

namespace FrostAura.Reel.Domain.Ports;

/// <summary>
/// Trakt API port. OAuth + profile now; ingestion/write-back methods join with the sync
/// subsystem. Every call rides the shared priority rate gate inside the adapter.
/// </summary>
public interface ITraktClient
{
    Task<TraktTokenResponse> ExchangeCodeAsync(string code, CancellationToken ct = default);

    Task<TraktTokenResponse> RefreshTokenAsync(string refreshToken, CancellationToken ct = default);

    Task<TraktUserSettings> GetUserSettingsAsync(string accessToken, CancellationToken ct = default);

    /// <summary>
    /// Every watched movie. Trakt paginates this endpoint (enforced after 2026-06-30) and an
    /// unpaginated request returns only the first page with a 200 OK, so the adapter walks every
    /// page via X-Pagination-Page-Count. Item metadata comes from TMDB, not from here.
    /// </summary>
    Task<IReadOnlyList<TraktWatchedMovie>> GetWatchedMoviesAsync(string accessToken, RatePriority priority, CancellationToken ct = default);

    /// <summary>
    /// Every watched show incl. season/episode progress. Requested with ?extended=progress —
    /// extended=full is now ignored and progress is no longer a default — and walked page by page.
    /// </summary>
    Task<IReadOnlyList<TraktWatchedShow>> GetWatchedShowsAsync(string accessToken, RatePriority priority, CancellationToken ct = default);

    /// <summary>
    /// Every rating across movies/shows/seasons/episodes with extended=full subjects, walked page
    /// by page — this collection is paginated exactly like the watched endpoints.
    /// </summary>
    Task<IReadOnlyList<TraktRatingItem>> GetRatingsAsync(string accessToken, RatePriority priority, CancellationToken ct = default);

    /// <summary>Watched progress for one show — aired/completed counts + the next episode.</summary>
    Task<TraktShowProgress?> GetShowProgressAsync(string accessToken, long showTraktId, RatePriority priority, CancellationToken ct = default);

    /// <summary>The 1-call delta gate: raw /sync/last_activities JSON for snapshot diffing.</summary>
    Task<string> GetLastActivitiesRawAsync(string accessToken, RatePriority priority, CancellationToken ct = default);

    /// <summary>
    /// History events for one item — /sync/history/{type}/{traktId} (type is "movies" | "shows"),
    /// optionally windowed by start_at/end_at, walked page by page like every other collection.
    /// Each event carries the id needed for a surgical /sync/history/remove.
    /// </summary>
    Task<IReadOnlyList<TraktHistoryItem>> GetHistoryAsync(string accessToken, string type, long traktId, DateTime? startAt, DateTime? endAt, RatePriority priority, CancellationToken ct = default);

    /// <summary>Resolves a TMDB id to its Trakt id (discovery titles lack one until write-back).</summary>
    Task<long?> ResolveTraktIdByTmdbAsync(string accessToken, long tmdbId, bool movie, RatePriority priority, CancellationToken ct = default);

    /// <summary>Posts a batched /sync/* payload (ratings, history, watchlist — add or remove endpoints).</summary>
    Task PostSyncBatchAsync(string accessToken, string endpoint, object payload, RatePriority priority, CancellationToken ct = default);

    /// <summary>Finds or creates the named personal list and returns its Trakt id (existing lists are paged through).</summary>
    Task<long> EnsureListAsync(string accessToken, string name, string description, RatePriority priority, CancellationToken ct = default);

    /// <summary>Adds or removes items on a personal list.</summary>
    Task PostListItemsAsync(string accessToken, long listId, object payload, bool remove, RatePriority priority, CancellationToken ct = default);
}
