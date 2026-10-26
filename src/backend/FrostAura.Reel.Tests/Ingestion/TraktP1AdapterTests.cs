using System.Net;
using System.Text;
using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Infrastructure.Adapters;
using FrostAura.Reel.Infrastructure.Telemetry;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrostAura.Reel.Tests.Ingestion;

/// <summary>
/// The Trakt reads WS-4's P1 added (task 8), pinned at the wire: what each one asks for and what it
/// makes of Trakt's answer. Canned JSON in Trakt's documented shapes; no network.
/// </summary>
public class TraktP1AdapterTests
{
    [Fact]
    public async Task Show_progress_asks_for_full_episodes_and_reads_the_next_episodes_air_date()
    {
        var handler = new CannedHandler(_ => Json("""
            {"aired": 20, "completed": 12,
             "next_episode": {"season": 2, "number": 3, "title": "Three", "ids": {"trakt": 1},
                              "first_aired": "2024-03-07T02:00:00.000Z"}}
            """));
        var progress = await Build(handler).GetShowProgressAsync("token", 42, RatePriority.Backfill);

        Assert.Contains("extended=full", handler.Requests.Single());
        Assert.Equal(new DateTime(2024, 3, 7, 2, 0, 0, DateTimeKind.Utc), progress!.NextEpisode!.FirstAired);
        Assert.Equal(DateTimeKind.Utc, progress.NextEpisode.FirstAired!.Value.Kind);
    }

    [Fact]
    public async Task The_watchlist_reads_movies_and_shows_with_when_each_was_listed()
    {
        var handler = new CannedHandler(uri => uri.Contains("watchlist/movies")
            ? Json("""[{"rank":1,"id":11,"listed_at":"2026-01-02T03:04:05.000Z","type":"movie","movie":{"title":"M","year":2020,"ids":{"trakt":100,"slug":"m","tmdb":500}}}]""")
            : Json("""[{"rank":1,"id":12,"listed_at":"2026-02-02T03:04:05.000Z","type":"show","show":{"title":"S","year":2021,"ids":{"trakt":200,"slug":"s","tmdb":600}}}]"""));

        var items = await Build(handler).GetWatchlistAsync("token", RatePriority.DeltaPoll);

        Assert.Equal(2, items.Count);
        Assert.Equal(100, items[0].Movie!.Ids.Trakt);
        Assert.Equal(new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc), items[0].ListedAt);
        Assert.Equal(600, items[1].Show!.Ids.Tmdb);
        Assert.All(handler.Requests, r => Assert.Contains("page=1", r));
    }

    [Fact]
    public async Task Hidden_items_carry_the_section_they_were_read_from()
    {
        var handler = new CannedHandler(_ => Json("""[{"hidden_at":"2025-05-05T05:05:05.000Z","type":"show","show":{"title":"S","year":2010,"ids":{"trakt":300,"tmdb":700}}}]"""));

        var items = await Build(handler).GetHiddenItemsAsync("token", "dropped", RatePriority.Backfill);

        Assert.Contains("users/hidden/dropped", handler.Requests.Single());
        Assert.Equal("dropped", Assert.Single(items).Section);
        Assert.Equal(300, items[0].Show!.Ids.Trakt);
    }

    [Fact]
    public async Task A_catalogue_rating_is_one_tokenless_search_when_trakt_returns_the_rating()
    {
        var handler = new CannedHandler(_ => Json("""[{"type":"movie","score":1,"movie":{"title":"M","ids":{"trakt":900,"slug":"m-2001","imdb":"tt1"},"rating":7.81234,"votes":4321}}]"""));

        var rating = await Build(handler).GetCatalogRatingAsync(55, movie: true, RatePriority.Backfill);

        var request = Assert.Single(handler.Requests);
        Assert.Contains("search/tmdb/55?type=movie&extended=full", request);
        Assert.False(handler.SawAuthorization);
        Assert.Equal(new(900, "m-2001", "tt1", 7.81234m, 4321), rating);
    }

    [Fact]
    public async Task Without_a_rating_on_the_search_hit_the_ratings_endpoint_is_asked()
    {
        var handler = new CannedHandler(uri => uri.Contains("search/")
            ? Json("""[{"type":"show","show":{"title":"S","ids":{"trakt":901,"slug":"s"}}}]""")
            : Json("""{"rating": 6.5, "votes": 12, "distribution": {}}"""));

        var rating = await Build(handler).GetCatalogRatingAsync(56, movie: false, RatePriority.Backfill);

        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains("shows/901/ratings", handler.Requests[1]);
        Assert.Equal(6.5m, rating!.Rating);
        Assert.Equal(12, rating.Votes);
    }

    [Fact]
    public async Task A_title_trakt_does_not_know_is_null()
    {
        var handler = new CannedHandler(_ => Json("[]"));
        Assert.Null(await Build(handler).GetCatalogRatingAsync(57, movie: true, RatePriority.Backfill));
    }

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
