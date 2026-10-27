using System.Net;
using System.Text;
using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Domain.Ports.Tmdb;
using FrostAura.Reel.Infrastructure.Adapters;
using FrostAura.Reel.Infrastructure.Telemetry;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrostAura.Reel.Tests.Filters;

/// <summary>
/// TMDB collection data for WS-5's Similar rail (task 10: "seeds from the collection first"): the
/// movie details payload names the collection a film belongs to (<c>belongs_to_collection</c>), and
/// <c>/collection/{id}</c> lists its parts. Pinned against trimmed real payloads, because a renamed
/// field parses as "no collection" and the rail would silently fall back to keywords.
/// </summary>
public class TmdbCollectionPayloadTests
{
    /// <summary>GET /3/movie/603 (The Matrix), trimmed to the fields this test reads.</summary>
    private const string MovieInCollection = """
        {
          "id": 603,
          "title": "The Matrix",
          "original_language": "en",
          "popularity": 80.2,
          "vote_average": 8.2,
          "vote_count": 26000,
          "belongs_to_collection": {
            "id": 2344,
            "name": "The Matrix Collection",
            "poster_path": "/bV9qTVHTVf0gkW0j7p7M0ILD4pG.jpg",
            "backdrop_path": "/bRm2DEgUiYciDw3myHuYFInD7la.jpg"
          }
        }
        """;

    /// <summary>GET /3/movie/550 (Fight Club): TMDB sends <c>belongs_to_collection: null</c> for a standalone film.</summary>
    private const string StandaloneMovie = """
        { "id": 550, "title": "Fight Club", "popularity": 60.1, "vote_average": 8.4, "vote_count": 27000, "belongs_to_collection": null }
        """;

    /// <summary>GET /3/collection/2344, trimmed: parts arrive in TMDB's order, not release order, and one has no date yet.</summary>
    private const string Collection = """
        {
          "id": 2344,
          "name": "The Matrix Collection",
          "parts": [
            { "id": 624860, "title": "The Matrix Resurrections", "media_type": "movie", "popularity": 40.0, "vote_average": 6.4, "vote_count": 6000, "release_date": "2021-12-16", "genre_ids": [878, 28], "original_language": "en" },
            { "id": 603, "title": "The Matrix", "media_type": "movie", "popularity": 80.2, "vote_average": 8.2, "vote_count": 26000, "release_date": "1999-03-31", "genre_ids": [28, 878], "original_language": "en" },
            { "id": 604, "title": "The Matrix Reloaded", "media_type": "movie", "popularity": 50.0, "vote_average": 7.0, "vote_count": 11000, "release_date": "2003-05-15", "genre_ids": [12, 28], "original_language": "en" },
            { "id": 999999001, "title": "The Matrix 5", "media_type": "movie", "popularity": 1.0, "vote_average": 0, "vote_count": 0, "release_date": "", "genre_ids": [] }
          ]
        }
        """;

    [Fact]
    public async Task Movie_details_name_the_collection_the_film_belongs_to()
    {
        var details = await Build(new CannedHandler(MovieInCollection)).GetMovieAsync(603);

        Assert.NotNull(details);
        Assert.Equal(new TmdbCollectionRef(2344, "The Matrix Collection"), details!.Collection);
    }

    [Fact]
    public async Task A_standalone_film_has_no_collection()
    {
        var details = await Build(new CannedHandler(StandaloneMovie)).GetMovieAsync(550);

        Assert.NotNull(details);
        Assert.Null(details!.Collection);
    }

    [Fact]
    public async Task A_collection_lists_its_parts_in_release_order_with_undated_parts_last()
    {
        var handler = new CannedHandler(Collection);

        var collection = await Build(handler).GetCollectionAsync(2344);

        Assert.Equal("/3/collection/2344", handler.LastPath);
        Assert.NotNull(collection);
        Assert.Equal(2344, collection!.Id);
        Assert.Equal("The Matrix Collection", collection.Name);
        Assert.Equal([603L, 604L, 624860L, 999999001L], collection.Parts.Select(p => p.Id));
        Assert.All(collection.Parts, p => Assert.True(p.IsMovie));
        Assert.Equal(new DateTime(1999, 3, 31, 0, 0, 0, DateTimeKind.Utc), collection.Parts[0].ReleasedAt);
        Assert.Null(collection.Parts[^1].ReleasedAt);
    }

    [Fact]
    public async Task An_unknown_collection_is_null_not_an_error()
    {
        var collection = await Build(new CannedHandler("""{"success":false,"status_code":34}""", HttpStatusCode.NotFound))
            .GetCollectionAsync(1);

        Assert.Null(collection);
    }

    [Fact]
    public async Task A_client_that_does_not_know_collections_answers_none()
    {
        ITmdbClient fake = new Fakes.SearchOnlyTmdbClient();

        Assert.Null(await fake.GetCollectionAsync(2344));
    }

    private static TmdbClient Build(CannedHandler handler)
    {
        var gate = new PassThroughRateGate();
        var services = new ServiceCollection().AddKeyedSingleton<IRateGate>("tmdb", gate).BuildServiceProvider();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["TMDB_READ_ACCESS_TOKEN"] = "read-token" })
            .Build();
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.themoviedb.org/3/") };
        var recorder = new ApiUsageRecorder(services.GetRequiredService<IServiceScopeFactory>(), NullLogger<ApiUsageRecorder>.Instance);
        return new TmdbClient(httpClient, gate, recorder, configuration);
    }

    private sealed class PassThroughRateGate : IRateGate
    {
        public Task AcquireAsync(RatePriority priority, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class CannedHandler(string body, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public string LastPath { get; private set; } = string.Empty;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastPath = request.RequestUri?.AbsolutePath ?? string.Empty;
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }
}
