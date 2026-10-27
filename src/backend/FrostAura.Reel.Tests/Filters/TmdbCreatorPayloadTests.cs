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
/// R1-19: a show's creators (<c>created_by</c>) come through the TV details payload, so hydration can
/// store them as creator credits and a show can be "by" someone (WS-5's Similar rail, WS-10A's creator
/// line). Pinned against trimmed real payloads, because a renamed field parses as "no creators" and
/// every show would silently stay authorless.
/// </summary>
public class TmdbCreatorPayloadTests
{
    /// <summary>GET /3/tv/1396 (Breaking Bad), trimmed to the fields this test reads.</summary>
    private const string ShowWithCreator = """
        {
          "id": 1396,
          "name": "Breaking Bad",
          "original_language": "en",
          "popularity": 120.5,
          "vote_average": 8.9,
          "vote_count": 14000,
          "created_by": [
            { "id": 66633, "credit_id": "52542286760ee31328001a7b", "name": "Vince Gilligan", "original_name": "Vince Gilligan", "gender": 2, "profile_path": "/z3E0DhBg1V1PZVEtS9vfFPzOWYB.jpg" }
          ]
        }
        """;

    /// <summary>GET /3/tv/1399 (Game of Thrones), trimmed: two creators, one with no profile image.</summary>
    private const string ShowWithTwoCreators = """
        {
          "id": 1399,
          "name": "Game of Thrones",
          "popularity": 300.1,
          "vote_average": 8.4,
          "vote_count": 23000,
          "created_by": [
            { "id": 9813, "credit_id": "5256c8c219c2956ff604858a", "name": "David Benioff", "gender": 2, "profile_path": "/xvNN5huL0X8yJ7h3IZfGG4O2zBD.jpg" },
            { "id": 228068, "credit_id": "552e611e9251413fea000901", "name": "D. B. Weiss", "gender": 2, "profile_path": null }
          ]
        }
        """;

    /// <summary>A show TMDB lists no creator for: <c>created_by: []</c>.</summary>
    private const string ShowWithoutCreator = """
        { "id": 2316, "name": "The Office", "popularity": 90.0, "vote_average": 8.6, "vote_count": 4000, "created_by": [] }
        """;

    /// <summary>GET /3/movie/550 (Fight Club): the movie details endpoint has no <c>created_by</c> at all.</summary>
    private const string Movie = """
        { "id": 550, "title": "Fight Club", "popularity": 60.1, "vote_average": 8.4, "vote_count": 27000 }
        """;

    [Fact]
    public async Task Tv_details_name_the_shows_creator()
    {
        var details = await Build(ShowWithCreator).GetTvAsync(1396);

        Assert.NotNull(details);
        Assert.Equal([new TmdbCreator(66633, "Vince Gilligan", "/z3E0DhBg1V1PZVEtS9vfFPzOWYB.jpg")], details!.Creators);
    }

    [Fact]
    public async Task Several_creators_keep_tmdbs_order_and_a_missing_profile_is_null()
    {
        var details = await Build(ShowWithTwoCreators).GetTvAsync(1399);

        Assert.Equal(
            [new TmdbCreator(9813, "David Benioff", "/xvNN5huL0X8yJ7h3IZfGG4O2zBD.jpg"), new TmdbCreator(228068, "D. B. Weiss", null)],
            details!.Creators);
    }

    [Theory]
    [InlineData(ShowWithoutCreator, false)]
    [InlineData(Movie, true)]
    public async Task No_creator_is_an_empty_list_never_null(string payload, bool movie)
    {
        var client = Build(payload);
        var details = movie ? await client.GetMovieAsync(550) : await client.GetTvAsync(2316);

        Assert.NotNull(details!.Creators);
        Assert.Empty(details.Creators);
    }

    /// <summary>
    /// Fakes and other callers build <see cref="TmdbTitleDetails"/> positionally and never pass
    /// creators. They must still read as "no creators", not null, so a consumer iterating
    /// <c>Creators</c> cannot throw on a details object that did not come from the adapter.
    /// </summary>
    [Theory]
    [InlineData(1396L, "Breaking Bad")]
    [InlineData(550L, null)]
    public void Details_built_without_creators_read_as_an_empty_list(long id, string? name)
    {
        var details = new TmdbTitleDetails(id, null, null, 0, 0, 0, null, null, null, null, [], [], null, [], Name: name);

        Assert.NotNull(details.Creators);
        Assert.Empty(details.Creators);
    }

    [Fact]
    public void Details_given_null_creators_read_as_an_empty_list_and_with_keeps_them()
    {
        var bare = new TmdbTitleDetails(1, null, null, 0, 0, 0, null, null, null, null, [], [], null, [], Creators: null);
        var credited = bare with { Creators = [new TmdbCreator(66633, "Vince Gilligan", null)] };

        Assert.Empty(bare.Creators);
        Assert.Equal([new TmdbCreator(66633, "Vince Gilligan", null)], credited.Creators);
    }

    private static TmdbClient Build(string body)
    {
        var gate = new PassThroughRateGate();
        var services = new ServiceCollection().AddKeyedSingleton<IRateGate>("tmdb", gate).BuildServiceProvider();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["TMDB_READ_ACCESS_TOKEN"] = "read-token" })
            .Build();
        var httpClient = new HttpClient(new CannedHandler(body)) { BaseAddress = new Uri("https://api.themoviedb.org/3/") };
        var recorder = new ApiUsageRecorder(services.GetRequiredService<IServiceScopeFactory>(), NullLogger<ApiUsageRecorder>.Instance);
        return new TmdbClient(httpClient, gate, recorder, configuration);
    }

    private sealed class PassThroughRateGate : IRateGate
    {
        public Task AcquireAsync(RatePriority priority, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class CannedHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }
}
