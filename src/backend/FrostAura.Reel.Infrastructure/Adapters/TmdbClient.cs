using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Domain.Ports.Tmdb;
using FrostAura.Reel.Domain.Sync;
using FrostAura.Reel.Infrastructure.Telemetry;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FrostAura.Reel.Infrastructure.Adapters;

/// <summary>
/// TMDB adapter — one hydration call per title (append_to_response=credits,videos), v4 read
/// token auth, shared rate gate, usage-counted. Attribution requirement is honoured at the
/// presentation layer (footer + providers payload).
/// </summary>
public class TmdbClient(
    HttpClient httpClient,
    [FromKeyedServices("tmdb")] IRateGate rateGate,
    ApiUsageRecorder usageRecorder,
    IConfiguration configuration) : ITmdbClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    /// <summary>Backoff attempts on HTTP 429 before the throttle is surfaced to the caller.</summary>
    private const int MaxThrottleRetries = 3;

    /// <summary>Ceiling on a single backoff — a hostile Retry-After must not park a background job for an hour.</summary>
    private static readonly TimeSpan MaxThrottleDelay = TimeSpan.FromSeconds(30);

    /// <summary>The concept firehose's own vote-count floor, overridable per call — see the note in
    /// <see cref="DiscoverByConceptAsync"/>.</summary>
    private const int DefaultConceptVoteCount = 10;

    public Task<TmdbTitleDetails?> GetMovieAsync(long tmdbId, CancellationToken ct = default) =>
        GetDetailsAsync($"movie/{tmdbId}?append_to_response=credits,videos,keywords", ct);

    public Task<TmdbTitleDetails?> GetTvAsync(long tmdbId, CancellationToken ct = default) =>
        // aggregate_credits = the full series regulars (across seasons), not one episode's cast —
        // far better signal for cast/crew affinity than plain credits.
        GetDetailsAsync($"tv/{tmdbId}?append_to_response=aggregate_credits,videos,keywords", ct);

    public async Task<IReadOnlyList<TmdbListItem>> DiscoverAsync(
        bool movies, int? genreId, string? region, DateTime? releasedAfter, int page, CancellationToken ct = default)
    {
        var path = movies ? "discover/movie" : "discover/tv";
        var query = $"?sort_by=popularity.desc&include_adult=false&page={page}&vote_count.gte=50";
        if (genreId is { } genre)
        {
            query += $"&with_genres={genre}";
        }

        if (releasedAfter is { } after)
        {
            var dateField = movies ? "primary_release_date" : "first_air_date";
            query += $"&{dateField}.gte={after:yyyy-MM-dd}";
        }

        if (movies && region is { Length: 2 })
        {
            query += $"&region={region}";
        }

        return await GetListAsync(path + query, movies, RatePriority.Reconcile, ct);
    }

    public Task<IReadOnlyList<TmdbListItem>> GetTrendingAsync(bool movies, CancellationToken ct = default) =>
        GetListAsync(movies ? "trending/movie/week" : "trending/tv/week", movies, RatePriority.Reconcile, ct);

    public Task<IReadOnlyList<TmdbListItem>> GetRecommendationsAsync(bool movies, long tmdbId, CancellationToken ct = default) =>
        GetListAsync($"{(movies ? "movie" : "tv")}/{tmdbId}/recommendations", movies, RatePriority.Reconcile, ct);

    public Task<IReadOnlyList<TmdbListItem>> GetSimilarAsync(bool movies, long tmdbId, CancellationToken ct = default) =>
        GetListAsync($"{(movies ? "movie" : "tv")}/{tmdbId}/similar", movies, RatePriority.Reconcile, ct);

    public async Task<IReadOnlyList<TmdbKeyword>> SearchKeywordsAsync(string query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        using var response = await SendAsync($"search/keyword?query={Uri.EscapeDataString(query)}", RatePriority.Interactive, ct);
        if (!response.IsSuccessStatusCode)
        {
            return [];
        }

        var dto = await response.Content.ReadFromJsonAsync<KeywordSearchDto>(JsonOptions, ct);
        return (dto?.Results ?? [])
            .Where(k => k.Id > 0 && !string.IsNullOrWhiteSpace(k.Name))
            .Select(k => new TmdbKeyword(k.Id, k.Name!))
            .ToList();
    }

    public Task<IReadOnlyList<TmdbListItem>> SearchTitlesAsync(bool movies, string query, int page, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return Task.FromResult<IReadOnlyList<TmdbListItem>>([]);
        }

        var path = movies ? "search/movie" : "search/tv";
        var q = $"?query={Uri.EscapeDataString(query)}&include_adult=false&page={page}";
        return GetListAsync(path + q, movies, RatePriority.Interactive, ct);
    }

    public async Task<TmdbPerson?> SearchPersonAsync(string name, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        using var response = await SendAsync($"search/person?query={Uri.EscapeDataString(name)}&include_adult=false", RatePriority.Interactive, ct);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var dto = await response.Content.ReadFromJsonAsync<PersonSearchDto>(JsonOptions, ct);
        // TMDB ranks /search/person by relevance+popularity, so the first valid result is the match.
        var best = (dto?.Results ?? []).FirstOrDefault(p => p.Id > 0 && !string.IsNullOrWhiteSpace(p.Name));
        return best is null ? null : new TmdbPerson(best.Id, best.Name!, best.KnownForDepartment);
    }

    public async Task<IReadOnlyList<TmdbListItem>> GetPersonCreditsAsync(long personId, PersonCreditRole role, CancellationToken ct = default)
    {
        using var response = await SendAsync($"person/{personId}/combined_credits", RatePriority.Interactive, ct);
        if (!response.IsSuccessStatusCode)
        {
            return [];
        }

        var dto = await response.Content.ReadFromJsonAsync<CombinedCreditsDto>(JsonOptions, ct);
        if (dto is null)
        {
            return [];
        }

        var picked = new List<PersonCreditDto>();
        if (role is PersonCreditRole.Cast or PersonCreditRole.Any)
        {
            picked.AddRange(dto.Cast ?? []);
        }

        if (role is PersonCreditRole.Director)
        {
            picked.AddRange((dto.Crew ?? []).Where(c => string.Equals(c.Job, "Director", StringComparison.OrdinalIgnoreCase)));
        }
        else if (role is PersonCreditRole.Any)
        {
            picked.AddRange(dto.Crew ?? []);
        }

        return picked
            // A person can hold several roles on one title (writer+director, etc.) — collapse to one.
            .Where(c => c.Id > 0 && (c.Title ?? c.Name) is not null && c.MediaType is "movie" or "tv")
            .GroupBy(c => (c.MediaType, c.Id))
            .Select(g => g.First())
            .Select(c => new TmdbListItem(
                c.Id,
                c.MediaType == "movie",
                (c.Title ?? c.Name)!,
                c.PosterPath,
                c.BackdropPath,
                c.Overview,
                c.Popularity,
                c.VoteAverage,
                c.VoteCount,
                ParseDate(c.ReleaseDate ?? c.FirstAirDate),
                c.GenreIds ?? []))
            .ToList();
    }

    public Task<IReadOnlyList<TmdbListItem>> DiscoverByConceptAsync(
        bool movies, IReadOnlyList<int> genreIds, IReadOnlyList<int> keywordIds,
        string? region, DateTime? releasedAfter, int page,
        IReadOnlyList<int>? excludeGenreIds = null, IReadOnlyList<int>? excludeKeywordIds = null,
        DateTime? releasedBefore = null, int? maxRuntimeMinutes = null,
        TmdbDiscoverOptions? options = null, CancellationToken ct = default)
    {
        var path = movies ? "discover/movie" : "discover/tv";
        // vote_count.gte defaults lower than the feed's discover (50) — concept queries want recall
        // over the most niche unseen titles, not just the popular ones. The feed build overrides it
        // back to 50 through options, so routing supply through here does not quietly widen its
        // quality floor. Genres + keywords OR-joined.
        var query = $"?sort_by=popularity.desc&include_adult=false&page={page}"
            + $"&vote_count.gte={options?.MinVoteCount ?? DefaultConceptVoteCount}";
        var dateField = movies ? "primary_release_date" : "first_air_date";
        if (genreIds.Count > 0)
        {
            query += $"&with_genres={string.Join('|', genreIds)}";
        }

        if (keywordIds.Count > 0)
        {
            query += $"&with_keywords={string.Join('|', keywordIds)}";
        }

        // with_people is a MOVIE-discover parameter; /discover/tv has no person filter at all, so
        // sending it there would return an unconstrained page that looks like a person arm and is
        // not one. Silently dropping it for TV is the honest reading — the caller's own media plan
        // decides whether a person arm happens at all.
        if (movies && options?.PeopleIds is { Count: > 0 } peopleIds)
        {
            query += $"&with_people={string.Join('|', peopleIds)}";
        }

        // TMDB AND-s the without_* filters, which is what we want: a title is dropped if it carries
        // ANY rejected genre/keyword.
        if (excludeGenreIds is { Count: > 0 })
        {
            query += $"&without_genres={string.Join(',', excludeGenreIds)}";
        }

        if (excludeKeywordIds is { Count: > 0 })
        {
            query += $"&without_keywords={string.Join(',', excludeKeywordIds)}";
        }

        if (releasedAfter is { } after)
        {
            query += $"&{dateField}.gte={after:yyyy-MM-dd}";
        }

        if (releasedBefore is { } before)
        {
            query += $"&{dateField}.lte={before:yyyy-MM-dd}";
        }

        // "Under 90 minutes" — TMDB supports with_runtime.lte on both movie and tv discover
        // (tv reads it against episode run time).
        if (maxRuntimeMinutes is { } maxRuntime)
        {
            query += $"&with_runtime.lte={maxRuntime}";
        }

        // The account's standing content rules. Both are allow/floor semantics rather than
        // exclusions, so an absent rule means "unconstrained", never "exclude everything".
        if (options?.OriginalLanguage is { Length: 2 } language)
        {
            query += $"&with_original_language={language.ToLowerInvariant()}";
        }

        if (options?.MinVoteAverage is { } minVoteAverage)
        {
            // Invariant culture: a decimal formatted under a comma-decimal locale would produce
            // vote_average.gte=6,0, which TMDB reads as a malformed number and ignores.
            query += $"&vote_average.gte={minVoteAverage.ToString(CultureInfo.InvariantCulture)}";
        }

        if (movies && region is { Length: 2 })
        {
            query += $"&region={region}";
        }

        return GetListAsync(path + query, movies, options?.Priority ?? RatePriority.Interactive, ct);
    }

    public async Task<IReadOnlyList<TmdbWatchProvider>> GetWatchProvidersAsync(long tmdbId, bool movie, string region, CancellationToken ct = default)
    {
        using var response = await SendAsync($"{(movie ? "movie" : "tv")}/{tmdbId}/watch/providers", RatePriority.Interactive, ct);
        if (!response.IsSuccessStatusCode)
        {
            return [];
        }

        var dto = await response.Content.ReadFromJsonAsync<WatchProvidersDto>(JsonOptions, ct);
        if (dto?.Results is null || !dto.Results.TryGetValue(region.ToUpperInvariant(), out var regional))
        {
            return [];
        }

        var results = new List<TmdbWatchProvider>();
        void Add(WatchProviderEntryDto[]? entries, Domain.Providers.AvailabilityKind kind)
        {
            foreach (var entry in entries ?? [])
            {
                results.Add(new TmdbWatchProvider(entry.ProviderId, entry.ProviderName ?? "?", entry.LogoPath, entry.DisplayPriority, kind));
            }
        }

        Add(regional.Flatrate, Domain.Providers.AvailabilityKind.Flatrate);
        Add(regional.Rent, Domain.Providers.AvailabilityKind.Rent);
        Add(regional.Buy, Domain.Providers.AvailabilityKind.Buy);
        Add(regional.Free, Domain.Providers.AvailabilityKind.Free);
        Add(regional.Ads, Domain.Providers.AvailabilityKind.Ads);
        return results;
    }

    private sealed record WatchProvidersDto(Dictionary<string, RegionProvidersDto>? Results);

    private sealed record RegionProvidersDto(
        WatchProviderEntryDto[]? Flatrate,
        WatchProviderEntryDto[]? Rent,
        WatchProviderEntryDto[]? Buy,
        WatchProviderEntryDto[]? Free,
        WatchProviderEntryDto[]? Ads);

    private sealed record WatchProviderEntryDto(
        [property: JsonPropertyName("provider_id")] int ProviderId,
        [property: JsonPropertyName("provider_name")] string? ProviderName,
        [property: JsonPropertyName("logo_path")] string? LogoPath,
        [property: JsonPropertyName("display_priority")] int DisplayPriority);

    private async Task<IReadOnlyList<TmdbListItem>> GetListAsync(string path, bool movies, RatePriority priority, CancellationToken ct)
    {
        using var response = await SendAsync(path, priority, ct);
        response.EnsureSuccessStatusCode();
        var dto = await response.Content.ReadFromJsonAsync<ListDto>(JsonOptions, ct);

        return (dto?.Results ?? [])
            .Where(r => r.Id > 0 && (r.Title ?? r.Name) is not null)
            .Select(r => new TmdbListItem(
                r.Id,
                movies,
                (r.Title ?? r.Name)!,
                r.PosterPath,
                r.BackdropPath,
                r.Overview,
                r.Popularity,
                r.VoteAverage,
                r.VoteCount,
                ParseDate(r.ReleaseDate ?? r.FirstAirDate),
                r.GenreIds ?? [],
                r.OriginalLanguage))
            .ToList();
    }

    /// <summary>
    /// Sends an authenticated GET, retrying HTTP 429 with bounded exponential backoff and honouring
    /// Retry-After when TMDB sends it. 5xx / 408 / socket failures are already retried by the typed
    /// client's standard resilience handler (see Infrastructure DependencyInjection); 429 is *not*
    /// in that handler's transient predicate, so a throttled response used to fall straight through
    /// to EnsureSuccessStatusCode and surface as a hard job failure. The caller owns the response.
    /// GETs only — no non-idempotent request is ever replayed.
    /// </summary>
    private async Task<HttpResponseMessage> SendAsync(string path, RatePriority priority, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            // Re-acquired per attempt: a retry is a fresh call against both the rate gate and the
            // daily Fair-Use ledger.
            await rateGate.AcquireAsync(priority, ct);
            usageRecorder.Record(ApiProvider.Tmdb);

            using var request = AuthGet(path);
            var response = await httpClient.SendAsync(request, ct);
            if (response.StatusCode != HttpStatusCode.TooManyRequests || attempt >= MaxThrottleRetries)
            {
                return response;
            }

            var delay = ThrottleDelay(response, attempt);
            response.Dispose();
            await Task.Delay(delay, ct);
        }
    }

    /// <summary>Retry-After when present (Trakt/TMDB both send it on 429), else 0.4s · 2^attempt, capped.</summary>
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

    private HttpRequestMessage AuthGet(string path)
    {
        var token = configuration["TMDB_READ_ACCESS_TOKEN"]
            ?? throw new InvalidOperationException("TMDB_READ_ACCESS_TOKEN is required.");
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private static DateTime? ParseDate(string? value) =>
        DateTime.TryParse(value, out var parsed) ? DateTime.SpecifyKind(parsed, DateTimeKind.Utc) : null;

    private sealed record KeywordSearchDto(KeywordResultDto[]? Results);

    private sealed record KeywordResultDto(int Id, string? Name);

    private sealed record PersonSearchDto(PersonResultDto[]? Results);

    private sealed record PersonResultDto(
        long Id,
        string? Name,
        [property: JsonPropertyName("known_for_department")] string? KnownForDepartment);

    private sealed record CombinedCreditsDto(PersonCreditDto[]? Cast, PersonCreditDto[]? Crew);

    private sealed record PersonCreditDto(
        long Id,
        string? Title,
        string? Name,
        [property: JsonPropertyName("media_type")] string? MediaType,
        [property: JsonPropertyName("poster_path")] string? PosterPath,
        [property: JsonPropertyName("backdrop_path")] string? BackdropPath,
        string? Overview,
        decimal Popularity,
        [property: JsonPropertyName("vote_average")] decimal VoteAverage,
        [property: JsonPropertyName("vote_count")] int VoteCount,
        [property: JsonPropertyName("release_date")] string? ReleaseDate,
        [property: JsonPropertyName("first_air_date")] string? FirstAirDate,
        [property: JsonPropertyName("genre_ids")] int[]? GenreIds,
        string? Job);

    private sealed record ListDto(ListResultDto[]? Results);

    private sealed record ListResultDto(
        long Id,
        string? Title,
        string? Name,
        [property: JsonPropertyName("poster_path")] string? PosterPath,
        [property: JsonPropertyName("backdrop_path")] string? BackdropPath,
        string? Overview,
        decimal Popularity,
        [property: JsonPropertyName("vote_average")] decimal VoteAverage,
        [property: JsonPropertyName("vote_count")] int VoteCount,
        [property: JsonPropertyName("release_date")] string? ReleaseDate,
        [property: JsonPropertyName("first_air_date")] string? FirstAirDate,
        [property: JsonPropertyName("genre_ids")] int[]? GenreIds,
        [property: JsonPropertyName("original_language")] string? OriginalLanguage = null);

    private async Task<TmdbTitleDetails?> GetDetailsAsync(string path, CancellationToken ct)
    {
        using var response = await SendAsync(path, RatePriority.Backfill, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        var dto = await response.Content.ReadFromJsonAsync<DetailsDto>(JsonOptions, ct);
        if (dto is null)
        {
            return null;
        }

        // TV sends aggregate_credits (series-wide roles); movies send plain credits. Normalise both
        // into the same cast/crew shape.
        TmdbCastMember[] cast;
        TmdbCrewMember[] crew;
        if (dto.AggregateCredits is { } agg)
        {
            cast = (agg.Cast ?? [])
                .OrderBy(c => c.Order)
                .Take(12)
                .Select(c => new TmdbCastMember(c.Id, c.Name ?? string.Empty, c.Roles?.FirstOrDefault()?.Character, c.Order, c.ProfilePath, c.KnownForDepartment))
                .ToArray();
            crew = (agg.Crew ?? [])
                .Where(c => c.Department is "Writing" || (c.Jobs ?? []).Any(j => j.Job is "Director" or "Writer" or "Screenplay" or "Novel"))
                .Take(12)
                .Select(c => new TmdbCrewMember(
                    c.Id, c.Name ?? string.Empty,
                    (c.Jobs ?? []).FirstOrDefault(j => j.Job is "Director" or "Writer" or "Screenplay" or "Novel")?.Job ?? c.Jobs?.FirstOrDefault()?.Job,
                    c.Department, c.ProfilePath))
                .ToArray();
        }
        else
        {
            cast = (dto.Credits?.Cast ?? [])
                .OrderBy(c => c.Order)
                .Take(12)
                .Select(c => new TmdbCastMember(c.Id, c.Name ?? string.Empty, c.Character, c.Order, c.ProfilePath, c.KnownForDepartment))
                .ToArray();
            crew = (dto.Credits?.Crew ?? [])
                .Where(c => c.Job is "Director" or "Writer" or "Screenplay" or "Novel" || c.Department is "Writing")
                .Take(12)
                .Select(c => new TmdbCrewMember(c.Id, c.Name ?? string.Empty, c.Job, c.Department, c.ProfilePath))
                .ToArray();
        }

        // Official YouTube trailer, else any YouTube trailer.
        var videos = dto.Videos?.Results ?? [];
        var trailer = videos.FirstOrDefault(v => v.Site == "YouTube" && v.Type == "Trailer" && v.Official)
            ?? videos.FirstOrDefault(v => v.Site == "YouTube" && v.Type == "Trailer");

        // TV runtime: episode_run_time array — take the first entry when present.
        var runtime = dto.Runtime ?? dto.EpisodeRunTime?.FirstOrDefault();

        // Keywords: movies nest under keywords.keywords, TV under keywords.results.
        var keywords = (dto.Keywords?.Keywords ?? dto.Keywords?.Results ?? [])
            .Select(k => k.Name?.Trim().ToLowerInvariant())
            .Where(k => !string.IsNullOrEmpty(k))
            .Select(k => k!)
            .Distinct()
            .Take(40)
            .ToArray();

        return new TmdbTitleDetails(
            dto.Id,
            dto.PosterPath,
            dto.BackdropPath,
            dto.Popularity,
            dto.VoteAverage,
            dto.VoteCount,
            runtime == 0 ? null : runtime,
            dto.Overview,
            dto.Tagline,
            dto.NumberOfEpisodes,
            cast,
            crew,
            trailer?.Key,
            keywords,
            dto.OriginalLanguage,
            (dto.SpokenLanguages ?? []).Select(l => l.Iso6391).Where(c => c is not null).Select(c => c!).ToArray(),
            dto.Languages ?? [],
            dto.Title ?? dto.Name,
            ParseDate(dto.ReleaseDate ?? dto.FirstAirDate),
            (dto.Genres ?? []).Select(g => g.Id).ToArray());
    }

    private sealed record DetailsDto(
        long Id,
        [property: JsonPropertyName("original_language")] string? OriginalLanguage,
        // Corroborators for the unreliable original_language — see TmdbLanguage.Resolve. Movies expose
        // spoken_languages only; TV exposes both (verified live against /3/movie and /3/tv).
        [property: JsonPropertyName("spoken_languages")] SpokenLanguageDto[]? SpokenLanguages,
        [property: JsonPropertyName("languages")] string[]? Languages,
        [property: JsonPropertyName("poster_path")] string? PosterPath,
        [property: JsonPropertyName("backdrop_path")] string? BackdropPath,
        decimal Popularity,
        [property: JsonPropertyName("vote_average")] decimal VoteAverage,
        [property: JsonPropertyName("vote_count")] int VoteCount,
        int? Runtime,
        [property: JsonPropertyName("episode_run_time")] int[]? EpisodeRunTime,
        string? Overview,
        string? Tagline,
        [property: JsonPropertyName("number_of_episodes")] int? NumberOfEpisodes,
        CreditsDto? Credits,
        [property: JsonPropertyName("aggregate_credits")] AggregateCreditsDto? AggregateCredits,
        VideosDto? Videos,
        KeywordsDto? Keywords,
        // On-demand detail hydration needs the fields a list row would have carried: display title
        // (movies: title, TV: name), release date, and full genre objects (details payloads have no
        // genre_ids array — they inline genres[].id in the same id space).
        string? Title = null,
        string? Name = null,
        [property: JsonPropertyName("release_date")] string? ReleaseDate = null,
        [property: JsonPropertyName("first_air_date")] string? FirstAirDate = null,
        GenreDto[]? Genres = null);

    private sealed record GenreDto(int Id, string? Name);

    private sealed record SpokenLanguageDto([property: JsonPropertyName("iso_639_1")] string? Iso6391);

    private sealed record CreditsDto(CastDto[]? Cast, CrewDto[]? Crew);

    private sealed record AggregateCreditsDto(AggCastDto[]? Cast, AggCrewDto[]? Crew);

    private sealed record AggCastDto(
        long Id,
        string? Name,
        int Order,
        [property: JsonPropertyName("profile_path")] string? ProfilePath,
        [property: JsonPropertyName("known_for_department")] string? KnownForDepartment,
        AggRoleDto[]? Roles);

    private sealed record AggRoleDto(string? Character);

    private sealed record AggCrewDto(
        long Id,
        string? Name,
        string? Department,
        [property: JsonPropertyName("profile_path")] string? ProfilePath,
        AggJobDto[]? Jobs);

    private sealed record AggJobDto(string? Job);

    private sealed record CastDto(
        long Id,
        string? Name,
        string? Character,
        int Order,
        [property: JsonPropertyName("profile_path")] string? ProfilePath,
        [property: JsonPropertyName("known_for_department")] string? KnownForDepartment);

    private sealed record CrewDto(
        long Id,
        string? Name,
        string? Job,
        string? Department,
        [property: JsonPropertyName("profile_path")] string? ProfilePath);

    private sealed record VideosDto(VideoDto[]? Results);

    private sealed record KeywordsDto(KeywordDto[]? Keywords, KeywordDto[]? Results);

    private sealed record KeywordDto(string? Name);

    private sealed record VideoDto(string? Site, string? Type, string? Key, bool Official);
}
