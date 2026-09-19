using System.Net;
using System.Text;
using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Domain.Ports.Tmdb;
using FrostAura.Reel.Infrastructure.Adapters;
using FrostAura.Reel.Infrastructure.Telemetry;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrostAura.Reel.Tests.Ingestion;

/// <summary>
/// The wire contract for the two language corroborators, pinned against real TMDB payload shapes
/// captured live on 2026-08-07. This is a separate concern from
/// <see cref="TmdbLanguageCorroborationTests"/>: that suite pins the RULE, this one pins that the
/// adapter actually hands the rule the data.
///
/// The two fields have different shapes and different availability, which is exactly how a
/// silently-empty parse would slip through: <c>spoken_languages</c> is an array of OBJECTS keyed
/// <c>iso_639_1</c> and appears on both endpoints, while <c>languages</c> is a flat array of
/// strings and appears on TV only. The client's snake_case naming policy maps
/// <c>spoken_languages</c> and <c>languages</c> automatically but would render <c>Iso6391</c> as
/// "iso6391", so that one member carries an explicit JsonPropertyName — drop it and every code
/// comes back null with no error anywhere.
/// </summary>
public class TmdbLanguagePayloadTests
{
    /// <summary>GET /3/tv/95439 — "El final del paraíso", trimmed to the language fields.</summary>
    private const string TelenovelaTv = """
        {
          "id": 95439,
          "name": "El final del paraíso",
          "original_language": "en",
          "languages": ["es"],
          "spoken_languages": [{ "english_name": "Spanish", "iso_639_1": "es", "name": "Español" }],
          "origin_country": ["US"],
          "popularity": 5.5,
          "vote_average": 6.4,
          "vote_count": 30
        }
        """;

    /// <summary>GET /3/movie/550 — Fight Club. Note the absence of a "languages" key entirely.</summary>
    private const string EnglishMovie = """
        {
          "id": 550,
          "title": "Fight Club",
          "original_language": "en",
          "spoken_languages": [{ "english_name": "English", "iso_639_1": "en", "name": "English" }],
          "popularity": 60.1,
          "vote_average": 8.4,
          "vote_count": 27000
        }
        """;

    [Fact]
    public async Task The_tv_endpoint_yields_both_corroborators_and_the_resolved_language_contradicts_tmdb()
    {
        var details = await BuildTmdb(TelenovelaTv).GetTvAsync(95439);

        Assert.NotNull(details);
        Assert.Equal("en", details.OriginalLanguage);
        Assert.Equal(["es"], details.SpokenLanguages!);
        Assert.Equal(["es"], details.Languages!);
        Assert.Equal("es", TmdbLanguage.Resolve(details.OriginalLanguage, details.SpokenLanguages, details.Languages));
    }

    [Fact]
    public async Task The_movie_endpoint_yields_spoken_languages_even_though_it_never_sends_languages()
    {
        var details = await BuildTmdb(EnglishMovie).GetMovieAsync(550);

        Assert.NotNull(details);
        Assert.Equal("en", details.OriginalLanguage);
        Assert.Equal(["en"], details.SpokenLanguages!);
        Assert.Empty(details.Languages!);
        Assert.Equal("en", TmdbLanguage.Resolve(details.OriginalLanguage, details.SpokenLanguages, details.Languages));
    }

    private static TmdbClient BuildTmdb(string body)
    {
        var services = new ServiceCollection()
            .AddKeyedSingleton<IRateGate>("tmdb", new PassthroughRateGate())
            .BuildServiceProvider();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["TMDB_READ_ACCESS_TOKEN"] = "read-token" })
            .Build();
        var httpClient = new HttpClient(new CannedHandler(body)) { BaseAddress = new Uri("https://api.themoviedb.org/3/") };
        var recorder = new ApiUsageRecorder(services.GetRequiredService<IServiceScopeFactory>(), NullLogger<ApiUsageRecorder>.Instance);
        return new TmdbClient(httpClient, services.GetRequiredKeyedService<IRateGate>("tmdb"), recorder, configuration);
    }

    private sealed class PassthroughRateGate : IRateGate
    {
        public Task AcquireAsync(RatePriority priority, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class CannedHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
    }
}
