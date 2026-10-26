using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Domain.Ports.Trakt;
using Microsoft.Extensions.Logging;

namespace FrostAura.Reel.Infrastructure.Eval;

/// <summary>
/// The eval stack's <see cref="ITraktClient"/> (program §4.12, WS-7 task 1): Trakt is OFFLINE in this
/// process. Every call fails locally with the same <see cref="HttpRequestException"/> a dead network
/// produces — before any socket is opened — so the eval stack can never rotate the founder's
/// single-use refresh token, never write to his Trakt profile, and never read from it.
///
/// <para><b>Why fail rather than answer.</b> An empty watched list or a silently accepted write would be
/// read by ingestion and the outbox as truth (an "unwatched everything" delta, a write-back marked
/// synced). Failing the way an unreachable Trakt fails keeps every caller on the path it already
/// handles: the job fails or retries, the snapshot data is left exactly as it was.</para>
///
/// <para>Selected by <c>TRAKT_CLIENT_ID=<see cref="EvalClientId"/></c>, which the eval descriptor and
/// <c>scripts/eval/run-api.sh</c> set; no real Trakt application has that id, so no other stack can
/// select it by accident.</para>
/// </summary>
public sealed class OfflineTraktClient(ILogger<OfflineTraktClient> logger) : ITraktClient
{
    /// <summary>The Trakt client id the eval stack runs with; it selects this client.</summary>
    public const string EvalClientId = "eval-no-trakt";

    private int _refused;

    /// <summary>How many Trakt calls this process refused (each one is also logged as a warning).</summary>
    public int Refused => Volatile.Read(ref _refused);

    public Task<TraktTokenResponse> ExchangeCodeAsync(string code, CancellationToken ct = default) =>
        Task.FromException<TraktTokenResponse>(Refuse("oauth/token (code exchange)"));

    public Task<TraktTokenResponse> RefreshTokenAsync(string refreshToken, CancellationToken ct = default) =>
        Task.FromException<TraktTokenResponse>(Refuse("oauth/token (refresh)"));

    public Task<TraktUserSettings> GetUserSettingsAsync(string accessToken, CancellationToken ct = default) =>
        Task.FromException<TraktUserSettings>(Refuse("users/settings"));

    public Task<IReadOnlyList<TraktWatchedMovie>> GetWatchedMoviesAsync(string accessToken, RatePriority priority, CancellationToken ct = default) =>
        Task.FromException<IReadOnlyList<TraktWatchedMovie>>(Refuse("sync/watched/movies"));

    public Task<IReadOnlyList<TraktWatchedShow>> GetWatchedShowsAsync(string accessToken, RatePriority priority, CancellationToken ct = default) =>
        Task.FromException<IReadOnlyList<TraktWatchedShow>>(Refuse("sync/watched/shows"));

    public Task<IReadOnlyList<TraktRatingItem>> GetRatingsAsync(string accessToken, RatePriority priority, CancellationToken ct = default) =>
        Task.FromException<IReadOnlyList<TraktRatingItem>>(Refuse("sync/ratings"));

    public Task<TraktShowProgress?> GetShowProgressAsync(string accessToken, long showTraktId, RatePriority priority, CancellationToken ct = default) =>
        Task.FromException<TraktShowProgress?>(Refuse("shows/progress/watched"));

    public Task<string> GetLastActivitiesRawAsync(string accessToken, RatePriority priority, CancellationToken ct = default) =>
        Task.FromException<string>(Refuse("sync/last_activities"));

    public Task<IReadOnlyList<TraktHistoryItem>> GetHistoryAsync(string accessToken, string type, long traktId, DateTime? startAt, DateTime? endAt, RatePriority priority, CancellationToken ct = default) =>
        Task.FromException<IReadOnlyList<TraktHistoryItem>>(Refuse($"sync/history/{type}"));

    public Task<long?> ResolveTraktIdByTmdbAsync(string accessToken, long tmdbId, bool movie, RatePriority priority, CancellationToken ct = default) =>
        Task.FromException<long?>(Refuse("search/tmdb"));

    public Task PostSyncBatchAsync(string accessToken, string endpoint, object payload, RatePriority priority, CancellationToken ct = default) =>
        Task.FromException(Refuse(endpoint));

    public Task<long> EnsureListAsync(string accessToken, string name, string description, RatePriority priority, CancellationToken ct = default) =>
        Task.FromException<long>(Refuse("users/me/lists"));

    public Task PostListItemsAsync(string accessToken, long listId, object payload, bool remove, RatePriority priority, CancellationToken ct = default) =>
        Task.FromException(Refuse(remove ? "users/me/lists/items/remove" : "users/me/lists/items"));

    private HttpRequestException Refuse(string call)
    {
        var count = Interlocked.Increment(ref _refused);
        logger.LogWarning("Trakt is offline on this stack (TRAKT_CLIENT_ID={ClientId}): refused {Call} without opening a socket ({Count} refused so far).", EvalClientId, call, count);
        return new HttpRequestException($"Trakt is offline on this stack (TRAKT_CLIENT_ID={EvalClientId}); {call} was refused before any network call.");
    }
}
