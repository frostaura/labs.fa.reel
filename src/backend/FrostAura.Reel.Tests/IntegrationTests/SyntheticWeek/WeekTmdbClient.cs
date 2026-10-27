using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Domain.Ports.Tmdb;
using FrostAura.Reel.Domain.Providers;
using FrostAura.Reel.Tests.Fakes;
using Npgsql;

namespace FrostAura.Reel.Tests.IntegrationTests.SyntheticWeek;

/// <summary>
/// The synthetic week's TMDB (R1-36): offline, and steady. Catalogue reads answer empty like
/// <see cref="SilentTmdbClient"/> (no title arrives from outside the clone), but a watch-provider lookup
/// answers with the offers the clone already holds for that title — TMDB in a week where nothing changed
/// on the services. Silent there would be wrong, not neutral: <c>AvailabilitySnapshot.ReplaceAsync</c>
/// replaces a title's rows with what the lookup returned, so an empty answer would delete every refreshed
/// title's offers and the week would measure its own fake.
/// </summary>
internal sealed class WeekTmdbClient(string connectionString) : ITmdbClient
{
    private readonly SilentTmdbClient _silent = new();

    public int ProviderLookups;

    public async Task<IReadOnlyList<TmdbWatchProvider>> GetWatchProvidersAsync(long tmdbId, bool movie, string region, CancellationToken ct = default)
    {
        Interlocked.Increment(ref ProviderLookups);
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(
            """
            select p."TmdbProviderId", p."Name", p."LogoPath", p."DisplayPriority", a."Kind"
            from "TitleAvailabilities" a
            join "StreamingProviders" p on p."Id" = a."ProviderId"
            join "Titles" t on t."Id" = a."TitleId"
            where t."TmdbId" = @tmdb and t."MediaType" = @media and a."Region" = @region
            """, connection);
        command.Parameters.AddWithValue("tmdb", tmdbId);
        command.Parameters.AddWithValue("media", movie ? "Movie" : "Show");
        command.Parameters.AddWithValue("region", region);
        var offers = new List<TmdbWatchProvider>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            offers.Add(new TmdbWatchProvider(
                reader.GetInt32(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetInt32(3),
                Enum.Parse<AvailabilityKind>(reader.GetString(4))));
        }

        return offers;
    }

    public Task<TmdbTitleDetails?> GetMovieAsync(long tmdbId, CancellationToken ct = default) => _silent.GetMovieAsync(tmdbId, ct);

    public Task<TmdbTitleDetails?> GetTvAsync(long tmdbId, CancellationToken ct = default) => _silent.GetTvAsync(tmdbId, ct);

    public Task<IReadOnlyList<TmdbListItem>> DiscoverAsync(bool movies, int? genreId, string? region, DateTime? releasedAfter, int page, CancellationToken ct = default) =>
        _silent.DiscoverAsync(movies, genreId, region, releasedAfter, page, ct);

    public Task<TmdbDiscoverPage> DiscoverSeedPageAsync(bool movies, int year, int minVoteCount, int page, CancellationToken ct = default) =>
        _silent.DiscoverSeedPageAsync(movies, year, minVoteCount, page, ct);

    public Task<IReadOnlyList<TmdbListItem>> GetTrendingAsync(bool movies, CancellationToken ct = default) => _silent.GetTrendingAsync(movies, ct);

    public Task<IReadOnlyList<TmdbListItem>> GetRecommendationsAsync(bool movies, long tmdbId, CancellationToken ct = default) =>
        _silent.GetRecommendationsAsync(movies, tmdbId, ct);

    public Task<IReadOnlyList<TmdbListItem>> GetSimilarAsync(bool movies, long tmdbId, CancellationToken ct = default) =>
        _silent.GetSimilarAsync(movies, tmdbId, ct);

    public Task<IReadOnlyList<TmdbKeyword>> SearchKeywordsAsync(string query, CancellationToken ct = default) => _silent.SearchKeywordsAsync(query, ct);

    public Task<IReadOnlyList<TmdbListItem>> SearchTitlesAsync(bool movies, string query, int page, CancellationToken ct = default) =>
        _silent.SearchTitlesAsync(movies, query, page, ct);

    public Task<TmdbPerson?> SearchPersonAsync(string name, CancellationToken ct = default) => _silent.SearchPersonAsync(name, ct);

    public Task<IReadOnlyList<TmdbListItem>> GetPersonCreditsAsync(long personId, PersonCreditRole role, CancellationToken ct = default) =>
        _silent.GetPersonCreditsAsync(personId, role, ct);

    public Task<IReadOnlyList<TmdbListItem>> DiscoverByConceptAsync(
        bool movies, IReadOnlyList<int> genreIds, IReadOnlyList<int> keywordIds,
        string? region, DateTime? releasedAfter, int page,
        IReadOnlyList<int>? excludeGenreIds = null, IReadOnlyList<int>? excludeKeywordIds = null,
        DateTime? releasedBefore = null, int? maxRuntimeMinutes = null,
        TmdbDiscoverOptions? options = null, CancellationToken ct = default) =>
        _silent.DiscoverByConceptAsync(movies, genreIds, keywordIds, region, releasedAfter, page, excludeGenreIds, excludeKeywordIds, releasedBefore, maxRuntimeMinutes, options, ct);
}
