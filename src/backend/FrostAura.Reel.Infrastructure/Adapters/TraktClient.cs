using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Domain.Ports.Trakt;
using FrostAura.Reel.Domain.Sync;
using FrostAura.Reel.Infrastructure.Telemetry;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FrostAura.Reel.Infrastructure.Adapters;

/// <summary>
/// Trakt API adapter. Every request acquires the shared priority rate gate first and is
/// counted toward the daily Fair-Use ledger. OAuth + profile surface; ingestion endpoints
/// extend this class alongside the sync subsystem.
/// </summary>
public class TraktClient(
    HttpClient httpClient,
    [FromKeyedServices("trakt")] IRateGate rateGate,
    ApiUsageRecorder usageRecorder,
    IConfiguration configuration) : ITraktClient
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Backoff attempts on HTTP 429 before the throttle is surfaced to the caller.</summary>
    private const int MaxThrottleRetries = 3;

    /// <summary>Ceiling on a single backoff — a hostile Retry-After must not park a background job for an hour.</summary>
    private static readonly TimeSpan MaxThrottleDelay = TimeSpan.FromSeconds(30);

    private string ClientId => configuration["TRAKT_CLIENT_ID"]
        ?? throw new InvalidOperationException("TRAKT_CLIENT_ID is required.");

    private string ClientSecret => configuration["TRAKT_CLIENT_SECRET"]
        ?? throw new InvalidOperationException("TRAKT_CLIENT_SECRET is required.");

    private string RedirectUri => configuration["TRAKT_REDIRECT_URI"]
        ?? throw new InvalidOperationException("TRAKT_REDIRECT_URI is required.");

    public async Task<TraktTokenResponse> ExchangeCodeAsync(string code, CancellationToken ct = default)
    {
        var payload = new Dictionary<string, string>
        {
            ["code"] = code,
            ["client_id"] = ClientId,
            ["client_secret"] = ClientSecret,
            ["redirect_uri"] = RedirectUri,
            ["grant_type"] = "authorization_code",
        };
        return await PostTokenAsync(payload, ct);
    }

    public async Task<TraktTokenResponse> RefreshTokenAsync(string refreshToken, CancellationToken ct = default)
    {
        var payload = new Dictionary<string, string>
        {
            ["refresh_token"] = refreshToken,
            ["client_id"] = ClientId,
            ["client_secret"] = ClientSecret,
            ["redirect_uri"] = RedirectUri,
            ["grant_type"] = "refresh_token",
        };
        return await PostTokenAsync(payload, ct);
    }

    public async Task<TraktUserSettings> GetUserSettingsAsync(string accessToken, CancellationToken ct = default)
    {
        using var response = await SendGetAsync("users/settings", accessToken, RatePriority.Interactive, ct);
        response.EnsureSuccessStatusCode();

        var settings = await response.Content.ReadFromJsonAsync<UserSettingsDto>(JsonOptions, ct)
            ?? throw new InvalidOperationException("Empty Trakt /users/settings response.");

        var user = settings.User ?? throw new InvalidOperationException("Trakt settings response missing user.");
        return new TraktUserSettings(
            user.Ids?.Slug ?? throw new InvalidOperationException("Trakt user missing slug."),
            user.Username ?? user.Ids.Slug,
            user.Name,
            user.Private,
            user.Images?.Avatar?.Full);
    }

    // Trakt paginated the watched endpoints and changed the extended defaults, enforced after
    // 2026-06-30 (trakt/trakt-api discussion 775). Two consequences this code must respect:
    //   * An unpaginated request silently returns only the FIRST 100 items — no error, just a
    //     truncated library. Every page must be walked via X-Pagination-Page-Count.
    //   * extended=full is now ignored, and season progress is no longer included by default.
    //     Season/episode progress requires extended=progress, which Trakt caps at 100 per page.
    // Item metadata (overview, genres, runtime) is deliberately not sourced from here — TMDB
    // enrichment owns it, which is why dropping the dead extended=full costs nothing.
    private const int WatchedPageLimit = 250;
    private const int ProgressPageLimit = 100;

    public async Task<IReadOnlyList<TraktWatchedMovie>> GetWatchedMoviesAsync(string accessToken, RatePriority priority, CancellationToken ct = default) =>
        await GetAllPagesAsync<TraktWatchedMovie>("sync/watched/movies", WatchedPageLimit, accessToken, priority, ct);

    public async Task<IReadOnlyList<TraktWatchedShow>> GetWatchedShowsAsync(string accessToken, RatePriority priority, CancellationToken ct = default) =>
        await GetAllPagesAsync<TraktWatchedShow>("sync/watched/shows?extended=progress", ProgressPageLimit, accessToken, priority, ct);

    // /sync/ratings is a paginated collection too, and it carried the same single-shot bug that
    // capped the watched ingest at 100 items: 200 OK, no error, only the first page. A heavy
    // account's ratings are the single most valuable training signal there is — truncating them
    // silently trains the model on a fraction of the user's taste.
    public async Task<IReadOnlyList<TraktRatingItem>> GetRatingsAsync(string accessToken, RatePriority priority, CancellationToken ct = default) =>
        await GetAllPagesAsync<TraktRatingItem>("sync/ratings?extended=full", WatchedPageLimit, accessToken, priority, ct);

    /// <summary>Trakt caps /sync/history at 100 per page, matching the /sync/history/remove batch size.</summary>
    private const int HistoryPageLimit = 100;

    // /sync/history/{type}/{id} is the surgical read that makes history removal safe: it returns
    // the individual event ids inside a watched_at window, so the remove endpoint can delete
    // exactly the plays Reel logged and nothing else. Paginated like every other collection.
    public async Task<IReadOnlyList<TraktHistoryItem>> GetHistoryAsync(string accessToken, string type, long traktId, DateTime? startAt, DateTime? endAt, RatePriority priority, CancellationToken ct = default)
    {
        var path = $"sync/history/{type}/{traktId}";
        var query = new List<string>();
        if (startAt is { } start)
        {
            query.Add($"start_at={Uri.EscapeDataString(start.ToString("O"))}");
        }

        if (endAt is { } end)
        {
            query.Add($"end_at={Uri.EscapeDataString(end.ToString("O"))}");
        }

        if (query.Count > 0)
        {
            path = $"{path}?{string.Join('&', query)}";
        }

        return await GetAllPagesAsync<TraktHistoryItem>(path, HistoryPageLimit, accessToken, priority, ct);
    }

    public async Task<TraktShowProgress?> GetShowProgressAsync(string accessToken, long showTraktId, RatePriority priority, CancellationToken ct = default) =>
        await GetApiAsync<TraktShowProgress>($"shows/{showTraktId}/progress/watched?hidden=false&specials=false", accessToken, priority, ct);

    public async Task<string> GetLastActivitiesRawAsync(string accessToken, RatePriority priority, CancellationToken ct = default)
    {
        using var response = await SendGetAsync("sync/last_activities", accessToken, priority, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct);
    }

    /// <summary>
    /// Walks every page of a paginated Trakt collection. The page count comes from Trakt's
    /// X-Pagination-Page-Count header rather than from the requested limit, because Trakt caps
    /// limits server-side and documents that the applied values — not the requested ones — are
    /// authoritative. A missing header means an unpaginated response: take it and stop.
    /// </summary>
    private async Task<IReadOnlyList<T>> GetAllPagesAsync<T>(
        string path, int limit, string accessToken, RatePriority priority, CancellationToken ct)
    {
        var separator = path.Contains('?') ? '&' : '?';
        var results = new List<T>();

        for (var page = 1; ; page++)
        {
            using var response = await SendGetAsync($"{path}{separator}page={page}&limit={limit}", accessToken, priority, ct);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                break;
            }

            response.EnsureSuccessStatusCode();
            var batch = await response.Content.ReadFromJsonAsync<List<T>>(JsonOptions, ct) ?? [];
            results.AddRange(batch);

            if (!TryReadPaginationHeader(response, "X-Pagination-Page-Count", out var pageCount) || page >= pageCount)
            {
                break;
            }
        }

        return results;
    }

    private static bool TryReadPaginationHeader(HttpResponseMessage response, string name, out int value)
    {
        value = 0;
        return response.Headers.TryGetValues(name, out var values)
            && int.TryParse(values.FirstOrDefault(), out value);
    }

    private async Task<T?> GetApiAsync<T>(string path, string accessToken, RatePriority priority, CancellationToken ct)
        where T : class
    {
        using var response = await SendGetAsync(path, accessToken, priority, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(JsonOptions, ct);
    }

    /// <summary>
    /// Sends an authenticated GET, retrying HTTP 429 with bounded exponential backoff and honouring
    /// Trakt's Retry-After. 5xx / 408 / socket failures are already covered by the typed client's
    /// standard resilience handler (see Infrastructure DependencyInjection); 429 is *not* in that
    /// handler's transient predicate, so a rate-limited sync fell straight through to
    /// EnsureSuccessStatusCode and failed the job. GETs only — /sync/* and list POSTs are not
    /// idempotent and are never replayed here. The caller owns the response.
    /// </summary>
    private async Task<HttpResponseMessage> SendGetAsync(string path, string? accessToken, RatePriority priority, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            // Re-acquired per attempt: a retry is a fresh call against both the rate gate and the
            // daily Fair-Use ledger.
            await rateGate.AcquireAsync(priority, ct);
            usageRecorder.Record(ApiProvider.Trakt);

            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            ApplyApiHeaders(request, accessToken);
            var response = await httpClient.SendAsync(request, ct);
            if (response.StatusCode != System.Net.HttpStatusCode.TooManyRequests || attempt >= MaxThrottleRetries)
            {
                return response;
            }

            var delay = ThrottleDelay(response, attempt);
            response.Dispose();
            await Task.Delay(delay, ct);
        }
    }

    /// <summary>Retry-After when present, else 0.4s · 2^attempt, capped so a hostile header cannot park a sync.</summary>
    private static TimeSpan ThrottleDelay(HttpResponseMessage response, int attempt)
    {
        TimeSpan? hinted = null;
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter?.Delta is { } delta)
        {
            hinted = delta;
        }
        else if (retryAfter?.Date is { } date)
        {
            hinted = date - DateTimeOffset.UtcNow;
        }

        var delay = hinted is { } value && value > TimeSpan.Zero
            ? value
            : TimeSpan.FromMilliseconds(400 * (1 << attempt));
        return delay > MaxThrottleDelay ? MaxThrottleDelay : delay;
    }

    public async Task<long?> ResolveTraktIdByTmdbAsync(string accessToken, long tmdbId, bool movie, RatePriority priority, CancellationToken ct = default)
    {
        var type = movie ? "movie" : "show";
        var results = await GetApiAsync<List<SearchResultDto>>($"search/tmdb/{tmdbId}?type={type}", accessToken, priority, ct);
        var match = results?.FirstOrDefault();
        return movie ? match?.Movie?.Ids?.Trakt : match?.Show?.Ids?.Trakt;
    }

    public async Task PostSyncBatchAsync(string accessToken, string endpoint, object payload, RatePriority priority, CancellationToken ct = default)
    {
        await rateGate.AcquireAsync(priority, ct);
        usageRecorder.Record(ApiProvider.Trakt);

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(payload, options: JsonOptions),
        };
        ApplyApiHeaders(request, accessToken);
        using var response = await httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
    }

    public async Task<long> EnsureListAsync(string accessToken, string name, string description, RatePriority priority, CancellationToken ct = default)
    {
        // Paginated for the same reason as the collections above: on an account with many lists,
        // a single-shot fetch can miss the managed list and silently create a duplicate of it.
        var lists = await GetAllPagesAsync<ListDtoTrakt>("users/me/lists", WatchedPageLimit, accessToken, priority, ct);
        var existing = lists.FirstOrDefault(l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase));
        if (existing?.Ids?.Trakt is { } id)
        {
            return id;
        }

        await rateGate.AcquireAsync(priority, ct);
        usageRecorder.Record(ApiProvider.Trakt);
        using var request = new HttpRequestMessage(HttpMethod.Post, "users/me/lists")
        {
            Content = JsonContent.Create(new
            {
                name,
                description,
                privacy = "private",
                display_numbers = false,
                allow_comments = false,
            }, options: JsonOptions),
        };
        ApplyApiHeaders(request, accessToken);
        using var response = await httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var created = await response.Content.ReadFromJsonAsync<ListDtoTrakt>(JsonOptions, ct);
        return created?.Ids?.Trakt ?? throw new InvalidOperationException("Trakt list creation returned no id.");
    }

    public Task PostListItemsAsync(string accessToken, long listId, object payload, bool remove, RatePriority priority, CancellationToken ct = default) =>
        PostSyncBatchAsync(accessToken, $"users/me/lists/{listId}/items{(remove ? "/remove" : string.Empty)}", payload, priority, ct);

    private sealed record SearchResultDto(
        [property: JsonPropertyName("movie")] SubjectDto? Movie,
        [property: JsonPropertyName("show")] SubjectDto? Show);

    private sealed record SubjectDto([property: JsonPropertyName("ids")] SubjectIdsDto? Ids);

    private sealed record SubjectIdsDto([property: JsonPropertyName("trakt")] long? Trakt);

    private sealed record ListDtoTrakt(
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("ids")] SubjectIdsDto? Ids);

    internal void ApplyApiHeaders(HttpRequestMessage request, string? accessToken)
    {
        request.Headers.Add("trakt-api-version", "2");
        request.Headers.Add("trakt-api-key", ClientId);
        if (accessToken is not null)
        {
            request.Headers.Add("Authorization", $"Bearer {accessToken}");
        }
    }

    private async Task<TraktTokenResponse> PostTokenAsync(Dictionary<string, string> payload, CancellationToken ct)
    {
        await rateGate.AcquireAsync(RatePriority.Interactive, ct);
        usageRecorder.Record(ApiProvider.Trakt);

        using var response = await httpClient.PostAsJsonAsync("oauth/token", payload, JsonOptions, ct);
        if (!response.IsSuccessStatusCode)
        {
            // Surface Trakt's actual error (invalid_grant / redirect_uri mismatch / invalid_client)
            // instead of a bare status code — the token exchange is otherwise a black box.
            var body = await response.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(
                $"Trakt oauth/token failed: {(int)response.StatusCode} {response.StatusCode} — redirect_uri={payload.GetValueOrDefault("redirect_uri")} — body: {body}");
        }

        var token = await response.Content.ReadFromJsonAsync<TokenDto>(JsonOptions, ct)
            ?? throw new InvalidOperationException("Empty Trakt token response.");
        return new TraktTokenResponse(
            token.AccessToken ?? throw new InvalidOperationException("Trakt token response missing access_token."),
            token.RefreshToken ?? throw new InvalidOperationException("Trakt token response missing refresh_token."),
            token.Scope ?? "public",
            token.CreatedAt,
            token.ExpiresIn);
    }

    private sealed record TokenDto(
        [property: JsonPropertyName("access_token")] string? AccessToken,
        [property: JsonPropertyName("refresh_token")] string? RefreshToken,
        [property: JsonPropertyName("scope")] string? Scope,
        [property: JsonPropertyName("created_at")] long CreatedAt,
        [property: JsonPropertyName("expires_in")] long ExpiresIn);

    private sealed record UserSettingsDto([property: JsonPropertyName("user")] UserDto? User);

    private sealed record UserDto(
        [property: JsonPropertyName("username")] string? Username,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("private")] bool Private,
        [property: JsonPropertyName("ids")] UserIdsDto? Ids,
        [property: JsonPropertyName("images")] UserImagesDto? Images);

    private sealed record UserIdsDto([property: JsonPropertyName("slug")] string? Slug);

    private sealed record UserImagesDto([property: JsonPropertyName("avatar")] AvatarDto? Avatar);

    private sealed record AvatarDto([property: JsonPropertyName("full")] string? Full);
}
