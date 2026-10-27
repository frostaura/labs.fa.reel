using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Domain.Ports.Trakt;

namespace FrostAura.Reel.Tests.IntegrationTests.SyntheticWeek;

/// <summary>
/// The synthetic week's Trakt (R1-36): an in-memory profile the driver writes the week's activity into,
/// served the way Trakt serves it — a cheap <c>/sync/last_activities</c> whose category stamps move when
/// something changed, and full-list reads behind it. Nothing leaves the process.
///
/// <para><b>Only the week's own items are listed.</b> The account's history before the week is already in
/// the database, and every ingest Reel runs off a delta is an upsert (watched movies, ratings, the
/// watchlist): a list that omits an old item never removes it. Shows are the exception —
/// <c>IngestShowsAsync</c> reconciles away progress rows missing from the snapshot — so the episode stamp
/// never moves and a show read throws, loudly, instead of quietly emptying the account's progress.</para>
///
/// <para><b>Reel's own writes move the stamps</b> they would move on Trakt (a rating posted bumps
/// <c>movies.rated_at</c>, a watchlist add bumps <c>watchlist.updated_at</c>), so the next delta sees its
/// own echo exactly as it would live. The write itself is recorded, not applied: the echo's list read
/// returns the week's items, and the ingest already tells Reel's echoes apart.</para>
/// </summary>
public sealed class WeekTraktClient : ITraktClient
{
    private readonly Lock _gate = new();
    private readonly List<TraktWatchedMovie> _watched = [];
    private readonly List<TraktRatingItem> _ratings = [];
    private readonly List<TraktWatchlistItem> _watchlist = [];
    private readonly Dictionary<(string Category, string Field), DateTime> _stamps = [];

    public WeekTraktClient(DateTime start)
    {
        foreach (var key in StampKeys)
        {
            _stamps[key] = start;
        }
    }

    /// <summary>Every call, by name, for the run log (a Trakt call nobody expected is a finding).</summary>
    public ConcurrentDictionary<string, int> Calls { get; } = new(StringComparer.Ordinal);

    /// <summary>Every write Reel sent, by endpoint.</summary>
    public ConcurrentDictionary<string, int> Writes { get; } = new(StringComparer.Ordinal);

    private static readonly (string Category, string Field)[] StampKeys =
    [
        ("movies", "watched_at"), ("movies", "rated_at"), ("movies", "watchlisted_at"), ("movies", "hidden_at"),
        ("shows", "rated_at"), ("shows", "watchlisted_at"), ("shows", "hidden_at"), ("shows", "dropped_at"),
        ("seasons", "rated_at"), ("seasons", "hidden_at"),
        ("episodes", "watched_at"), ("episodes", "rated_at"),
        ("watchlist", "updated_at"),
    ];

    /// <summary>The activity snapshot as Trakt's JSON, which the driver also stores as the account's cursor.</summary>
    public string LastActivitiesJson()
    {
        lock (_gate)
        {
            var root = _stamps.GroupBy(kv => kv.Key.Category).ToDictionary(
                g => g.Key,
                g => g.ToDictionary(kv => kv.Key.Field, kv => kv.Value.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture)));
            return JsonSerializer.Serialize(root);
        }
    }

    /// <summary>The user watched a movie elsewhere (Plex, the cinema) and Trakt scrobbled it.</summary>
    public void Watch(TraktMovie movie, DateTime at)
    {
        lock (_gate)
        {
            var existing = _watched.FindIndex(w => w.Movie.Ids.Trakt == movie.Ids.Trakt);
            var plays = existing >= 0 ? _watched[existing].Plays + 1 : 1;
            var entry = new TraktWatchedMovie(plays, at, at, movie);
            if (existing >= 0)
            {
                _watched[existing] = entry;
            }
            else
            {
                _watched.Add(entry);
            }

            Bump("movies", "watched_at", at);
        }
    }

    /// <summary>The user rated a movie on Trakt.</summary>
    public void Rate(TraktMovie movie, short rating, DateTime at)
    {
        lock (_gate)
        {
            _ratings.RemoveAll(r => r.Movie?.Ids.Trakt == movie.Ids.Trakt);
            _ratings.Add(new TraktRatingItem(at, rating, "movie", movie, null, null, null));
            Bump("movies", "rated_at", at);
        }
    }

    /// <summary>The user added a movie to the Trakt watchlist.</summary>
    public void Watchlist(TraktMovie movie, DateTime at)
    {
        lock (_gate)
        {
            _watchlist.Add(new TraktWatchlistItem(_watchlist.Count + 1, at, "movie", movie, null));
            Bump("movies", "watchlisted_at", at);
            Bump("watchlist", "updated_at", at);
        }
    }

    private void Bump(string category, string field, DateTime at)
    {
        // A stamp only moves forward, and always moves: two writes in one millisecond are two changes.
        var previous = _stamps[(category, field)];
        _stamps[(category, field)] = at > previous ? at : previous.AddMilliseconds(1);
    }

    private T Read<T>(string call, Func<T> answer)
    {
        Calls.AddOrUpdate(call, 1, (_, n) => n + 1);
        lock (_gate)
        {
            return answer();
        }
    }

    public Task<string> GetLastActivitiesRawAsync(string accessToken, RatePriority priority, CancellationToken ct = default) =>
        Task.FromResult(Read("sync/last_activities", LastActivitiesJson));

    public Task<IReadOnlyList<TraktWatchedMovie>> GetWatchedMoviesAsync(string accessToken, RatePriority priority, CancellationToken ct = default) =>
        Task.FromResult(Read<IReadOnlyList<TraktWatchedMovie>>("sync/watched/movies", () => [.. _watched]));

    public Task<IReadOnlyList<TraktRatingItem>> GetRatingsAsync(string accessToken, RatePriority priority, CancellationToken ct = default) =>
        Task.FromResult(Read<IReadOnlyList<TraktRatingItem>>("sync/ratings", () => [.. _ratings]));

    public Task<IReadOnlyList<TraktWatchlistItem>> GetWatchlistAsync(string accessToken, RatePriority priority, CancellationToken ct = default) =>
        Task.FromResult(Read<IReadOnlyList<TraktWatchlistItem>>("sync/watchlist", () => [.. _watchlist]));

    public Task<IReadOnlyList<TraktHiddenItem>> GetHiddenItemsAsync(string accessToken, string section, RatePriority priority, CancellationToken ct = default) =>
        Task.FromResult(Read<IReadOnlyList<TraktHiddenItem>>($"users/hidden/{section}", () => []));

    public Task<IReadOnlyList<TraktHistoryItem>> GetHistoryAsync(string accessToken, string type, long traktId, DateTime? startAt, DateTime? endAt, RatePriority priority, CancellationToken ct = default) =>
        Task.FromResult(Read<IReadOnlyList<TraktHistoryItem>>($"sync/history/{type}", () => []));

    /// <summary>No next episode is known: the back-fill leaves the row as it found it.</summary>
    public Task<TraktShowProgress?> GetShowProgressAsync(string accessToken, long showTraktId, RatePriority priority, CancellationToken ct = default) =>
        Task.FromResult(Read<TraktShowProgress?>("shows/progress/watched", () => null));

    public Task<IReadOnlyList<TraktWatchedShow>> GetWatchedShowsAsync(string accessToken, RatePriority priority, CancellationToken ct = default)
    {
        Calls.AddOrUpdate("sync/watched/shows", 1, (_, n) => n + 1);
        return Task.FromException<IReadOnlyList<TraktWatchedShow>>(new NotSupportedException(
            "The synthetic week never moves the episode stamp: a show snapshot without the account's history would reconcile its progress away."));
    }

    /// <summary>
    /// The Trakt id the week gives a catalogue title that has none (most TMDB-sourced titles): far above
    /// any real Trakt id, one per TMDB id, so the ingest and the outbox agree on it and it never collides.
    /// </summary>
    public static long SyntheticTraktId(long tmdbId) => 9_000_000_000L + tmdbId;

    public Task<long?> ResolveTraktIdByTmdbAsync(string accessToken, long tmdbId, bool movie, RatePriority priority, CancellationToken ct = default) =>
        Task.FromResult(Read<long?>("search/tmdb", () => SyntheticTraktId(tmdbId)));

    public Task<TraktCatalogRating?> GetCatalogRatingAsync(long tmdbId, bool movie, RatePriority priority, CancellationToken ct = default) =>
        Task.FromResult(Read<TraktCatalogRating?>("search/tmdb (catalogue rating)", () => null));

    public Task PostSyncBatchAsync(string accessToken, string endpoint, object payload, RatePriority priority, CancellationToken ct = default)
    {
        Writes.AddOrUpdate(endpoint, 1, (_, n) => n + 1);
        var now = DateTime.UtcNow;
        lock (_gate)
        {
            switch (endpoint)
            {
                case "sync/ratings" or "sync/ratings/remove":
                    Bump("movies", "rated_at", now);
                    Bump("shows", "rated_at", now);
                    break;
                case "sync/history" or "sync/history/remove":
                    Bump("movies", "watched_at", now);
                    break;
                case "sync/watchlist" or "sync/watchlist/remove":
                    Bump("watchlist", "updated_at", now);
                    break;
            }
        }

        return Task.CompletedTask;
    }

    public Task<long> EnsureListAsync(string accessToken, string name, string description, RatePriority priority, CancellationToken ct = default)
    {
        Writes.AddOrUpdate("users/me/lists", 1, (_, n) => n + 1);
        return Task.FromResult(7_000_001L);
    }

    public Task PostListItemsAsync(string accessToken, long listId, object payload, bool remove, RatePriority priority, CancellationToken ct = default)
    {
        Writes.AddOrUpdate(remove ? "users/me/lists/items/remove" : "users/me/lists/items", 1, (_, n) => n + 1);
        return Task.CompletedTask;
    }

    /// <summary>A refresh succeeds with a new synthetic pair: the week never holds a real token.</summary>
    public Task<TraktTokenResponse> RefreshTokenAsync(string refreshToken, CancellationToken ct = default)
    {
        Calls.AddOrUpdate("oauth/token (refresh)", 1, (_, n) => n + 1);
        return Task.FromResult(new TraktTokenResponse("synthetic-week-access", "synthetic-week-refresh", "public", DateTimeOffset.UtcNow.ToUnixTimeSeconds(), 90 * 86_400));
    }

    public Task<TraktTokenResponse> ExchangeCodeAsync(string code, CancellationToken ct = default) =>
        throw new NotSupportedException("The synthetic week never signs anyone in through Trakt.");

    public Task<TraktUserSettings> GetUserSettingsAsync(string accessToken, CancellationToken ct = default) =>
        throw new NotSupportedException("The synthetic week never signs anyone in through Trakt.");
}
