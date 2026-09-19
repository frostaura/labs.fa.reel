using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Domain.Ports.Trakt;

namespace FrostAura.Reel.Tests.Fakes;

/// <summary>
/// Shared configurable <see cref="ITraktClient"/> fake (promoted from the private stub in
/// ShowProgressReconcileTests). Collections are settable per test; history reads and sync
/// write-backs are recorded for assertion. Anything a test did not configure throws, so an
/// unexpected Trakt call fails loudly instead of silently no-oping.
/// </summary>
public sealed class StubTraktClient : ITraktClient
{
    /// <summary>Returned by <see cref="GetWatchedShowsAsync"/>; unset ⇒ the call throws.</summary>
    public IReadOnlyList<TraktWatchedShow>? WatchedShows { get; set; }

    /// <summary>Returned by <see cref="GetHistoryAsync"/>; set to [] for "no events"; unset ⇒ the call throws.</summary>
    public IReadOnlyList<TraktHistoryItem>? History { get; set; }

    /// <summary>Every <see cref="GetHistoryAsync"/> call, in order.</summary>
    public List<(string Type, long TraktId, DateTime? StartAt, DateTime? EndAt)> HistoryCalls { get; } = [];

    /// <summary>Every <see cref="PostSyncBatchAsync"/> call, in order.</summary>
    public List<(string Endpoint, object Payload)> SyncBatches { get; } = [];

    public Task<IReadOnlyList<TraktWatchedShow>> GetWatchedShowsAsync(string accessToken, RatePriority priority, CancellationToken ct = default) =>
        Task.FromResult(WatchedShows ?? throw new NotSupportedException("WatchedShows not configured."));

    public Task<IReadOnlyList<TraktHistoryItem>> GetHistoryAsync(string accessToken, string type, long traktId, DateTime? startAt, DateTime? endAt, RatePriority priority, CancellationToken ct = default)
    {
        HistoryCalls.Add((type, traktId, startAt, endAt));
        return Task.FromResult(History ?? throw new NotSupportedException("History not configured."));
    }

    public Task PostSyncBatchAsync(string accessToken, string endpoint, object payload, RatePriority priority, CancellationToken ct = default)
    {
        SyncBatches.Add((endpoint, payload));
        return Task.CompletedTask;
    }

    public Task<TraktTokenResponse> ExchangeCodeAsync(string code, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<TraktTokenResponse> RefreshTokenAsync(string refreshToken, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<TraktUserSettings> GetUserSettingsAsync(string accessToken, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<IReadOnlyList<TraktWatchedMovie>> GetWatchedMoviesAsync(string accessToken, RatePriority priority, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<IReadOnlyList<TraktRatingItem>> GetRatingsAsync(string accessToken, RatePriority priority, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<TraktShowProgress?> GetShowProgressAsync(string accessToken, long showTraktId, RatePriority priority, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<string> GetLastActivitiesRawAsync(string accessToken, RatePriority priority, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<long?> ResolveTraktIdByTmdbAsync(string accessToken, long tmdbId, bool movie, RatePriority priority, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<long> EnsureListAsync(string accessToken, string name, string description, RatePriority priority, CancellationToken ct = default) => throw new NotSupportedException();

    public Task PostListItemsAsync(string accessToken, long listId, object payload, bool remove, RatePriority priority, CancellationToken ct = default) => throw new NotSupportedException();
}
