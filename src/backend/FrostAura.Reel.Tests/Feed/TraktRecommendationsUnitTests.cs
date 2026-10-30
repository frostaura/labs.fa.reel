using System.Net;
using System.Text;
using FrostAura.Reel.Application.Recommendations;
using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Domain.Ports.Trakt;
using FrostAura.Reel.Infrastructure.Adapters;
using FrostAura.Reel.Infrastructure.Telemetry;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrostAura.Reel.Tests.Feed;

/// <summary>
/// The "Trakt recommends" rail at the wire and in its pure parts: what the adapter asks Trakt for,
/// what it makes of the answer, and how the two media lists become one rail without losing Trakt's
/// order. Canned JSON in Trakt's documented shape; no network.
/// </summary>
public class TraktRecommendationsUnitTests
{
    [Fact]
    public async Task Movies_are_asked_for_at_Trakts_maximum_without_collected_or_watchlisted_titles()
    {
        var handler = new CannedHandler(_ => Json("""
            [{"title":"First","year":2021,"ids":{"trakt":11,"slug":"first","imdb":"tt1","tmdb":101},"overview":"o","genres":["drama"],"rating":7.9,"votes":1200},
             {"title":"Second","year":2019,"ids":{"trakt":12,"slug":"second","tmdb":102},"votes":0}]
            """));

        var recs = await Build(handler).GetRecommendationsAsync("token", movies: true, limit: 500, RatePriority.Interactive);

        var uri = Assert.Single(handler.Requests);
        Assert.StartsWith("https://api.trakt.tv/recommendations/movies?", uri, StringComparison.Ordinal);
        // Trakt caps the limit at 100; asking for more is clamped rather than sent.
        Assert.Contains("limit=100", uri, StringComparison.Ordinal);
        Assert.Contains("ignore_collected=true", uri, StringComparison.Ordinal);
        Assert.Contains("ignore_watchlisted=true", uri, StringComparison.Ordinal);
        Assert.Contains("extended=full", uri, StringComparison.Ordinal);
        Assert.True(handler.SawAuthorization);

        Assert.Equal([1, 2], recs.Select(r => r.Rank));
        Assert.Equal(["First", "Second"], recs.Select(r => r.Movie!.Title));
        Assert.All(recs, r => Assert.Null(r.Show));
        Assert.Equal(101, recs[0].Movie!.Ids.Tmdb);
        Assert.Equal(7.9m, recs[0].Movie!.Rating);
    }

    [Fact]
    public async Task Shows_come_from_the_shows_endpoint_and_carry_only_a_show()
    {
        var handler = new CannedHandler(_ => Json("""
            [{"title":"A Show","year":2020,"ids":{"trakt":21,"slug":"a-show","tmdb":201},"network":"HBO"}]
            """));

        var recs = await Build(handler).GetRecommendationsAsync("token", movies: false, limit: 100, RatePriority.Interactive);

        Assert.Contains("recommendations/shows?", Assert.Single(handler.Requests), StringComparison.Ordinal);
        var rec = Assert.Single(recs);
        Assert.Null(rec.Movie);
        Assert.Equal("A Show", rec.Show!.Title);
        Assert.Equal(201, rec.Show.Ids.Tmdb);
    }

    [Fact]
    public async Task A_Trakt_failure_reaches_the_caller_as_an_http_failure_not_an_empty_list()
    {
        var handler = new CannedHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        var failure = await Assert.ThrowsAsync<HttpRequestException>(() =>
            Build(handler).GetRecommendationsAsync("token", movies: true, limit: 100, RatePriority.Interactive));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, failure.StatusCode);
    }

    [Fact]
    public void Interleave_alternates_media_and_keeps_Trakts_order_within_each()
    {
        var movies = new[] { Movie(2, 12), Movie(1, 11), Movie(3, 13) };
        var shows = new[] { Show(1, 21) };

        var merged = TraktRecommendationsService.Interleave(movies, shows);

        Assert.Equal(
            ["m11", "s21", "m12", "m13"],
            merged.Select(r => r.Movie is { } m ? $"m{m.Ids.Trakt}" : $"s{r.Show!.Ids.Trakt}"));
    }

    [Fact]
    public void Interleave_of_one_empty_list_is_the_other_list_in_order()
    {
        Assert.Equal([21, 22], TraktRecommendationsService.Interleave([], [Show(1, 21), Show(2, 22)]).Select(r => r.Show!.Ids.Trakt));
        Assert.Empty(TraktRecommendationsService.Interleave([], []));
    }

    [Fact]
    public void The_sync_marker_moves_only_when_the_stored_activities_do()
    {
        const string activities = """{"all":"2026-10-30T10:00:00.000Z"}""";

        Assert.Equal("none", TraktRecommendationsService.SyncMarker(null));
        Assert.Equal(TraktRecommendationsService.SyncMarker(activities), TraktRecommendationsService.SyncMarker(activities));
        Assert.NotEqual(
            TraktRecommendationsService.SyncMarker(activities),
            TraktRecommendationsService.SyncMarker("""{"all":"2026-10-30T11:00:00.000Z"}"""));
    }

    private static TraktRecommendation Movie(int rank, long traktId) =>
        new(rank, new TraktMovie($"Movie {traktId}", 2020, new TraktIds(traktId, null, null, traktId + 1000), null, null, null, null, null, null, null, 0, null, null, null, null, null), null);

    private static TraktRecommendation Show(int rank, long traktId) =>
        new(rank, null, new TraktShow($"Show {traktId}", 2020, new TraktIds(traktId, null, null, traktId + 1000), null, null, null, null, null, null, null, 0, null, null, null, null, null, null, null));

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static TraktClient Build(CannedHandler handler)
    {
        var services = new ServiceCollection()
            .AddKeyedSingleton<IRateGate>("trakt", new OpenGate())
            .BuildServiceProvider();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["TRAKT_CLIENT_ID"] = "client-id" })
            .Build();
        var recorder = new ApiUsageRecorder(services.GetRequiredService<IServiceScopeFactory>(), NullLogger<ApiUsageRecorder>.Instance);
        return new TraktClient(
            new HttpClient(handler) { BaseAddress = new Uri("https://api.trakt.tv/") },
            services.GetRequiredKeyedService<IRateGate>("trakt"),
            recorder,
            configuration);
    }

    private sealed class OpenGate : IRateGate
    {
        public Task AcquireAsync(RatePriority priority, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class CannedHandler(Func<string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        public bool SawAuthorization { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!.ToString();
            Requests.Add(uri);
            SawAuthorization |= request.Headers.Authorization is not null;
            return Task.FromResult(respond(uri));
        }
    }
}
