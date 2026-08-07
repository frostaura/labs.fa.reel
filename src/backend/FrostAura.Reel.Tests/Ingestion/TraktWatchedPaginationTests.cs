using System.Net;
using System.Net.Http.Json;
using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Infrastructure.Adapters;
using FrostAura.Reel.Infrastructure.Telemetry;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrostAura.Reel.Tests.Ingestion;

/// <summary>
/// Regression for the silent library truncation that Trakt's 2026-06-30 watched-endpoint change
/// introduced: an unpaginated request returns only the first 100 items with a 200 OK, so a library
/// of thousands ingested as exactly 100 movies and 100 shows with nothing in the logs. These tests
/// pin the two properties that failure violated — every page is walked, and the page count comes
/// from Trakt's header rather than from the limit we asked for.
/// </summary>
public class TraktWatchedPaginationTests
{
    [Fact]
    public async Task Watched_movies_walks_every_page_reported_by_the_pagination_header()
    {
        var handler = new PagingHandler(pageCount: 3, itemsPerPage: 250);
        var client = BuildClient(handler);

        var movies = await client.GetWatchedMoviesAsync("token", RatePriority.Backfill);

        Assert.Equal(750, movies.Count);
        Assert.Equal([1, 2, 3], handler.RequestedPages);
    }

    [Fact]
    public async Task Watched_shows_request_season_progress_because_it_is_no_longer_a_default()
    {
        var handler = new PagingHandler(pageCount: 1, itemsPerPage: 1);
        var client = BuildClient(handler);

        await client.GetWatchedShowsAsync("token", RatePriority.Backfill);

        // extended=full is ignored by Trakt now; without extended=progress every show ingests
        // with zero watched episodes and Continue Watching silently empties.
        Assert.Contains("extended=progress", handler.RequestedUris[0]);
    }

    [Fact]
    public async Task A_response_without_pagination_headers_is_taken_as_the_only_page()
    {
        var handler = new PagingHandler(pageCount: null, itemsPerPage: 12);
        var client = BuildClient(handler);

        var movies = await client.GetWatchedMoviesAsync("token", RatePriority.Backfill);

        Assert.Equal(12, movies.Count);
        Assert.Single(handler.RequestedPages);
    }

    private static TraktClient BuildClient(PagingHandler handler)
    {
        var services = new ServiceCollection()
            .AddKeyedSingleton<IRateGate>("trakt", new PassthroughRateGate())
            .BuildServiceProvider();

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TRAKT_CLIENT_ID"] = "client-id",
                ["TRAKT_CLIENT_SECRET"] = "client-secret",
                ["TRAKT_REDIRECT_URI"] = "http://localhost/callback",
            })
            .Build();

        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.trakt.tv/") };
        var recorder = new ApiUsageRecorder(services.GetRequiredService<IServiceScopeFactory>(), NullLogger<ApiUsageRecorder>.Instance);

        return new TraktClient(httpClient, services.GetRequiredKeyedService<IRateGate>("trakt"), recorder, configuration);
    }

    private sealed class PassthroughRateGate : IRateGate
    {
        public Task AcquireAsync(RatePriority priority, CancellationToken ct = default) => Task.CompletedTask;
    }

    /// <summary>Serves <paramref name="pageCount"/> pages; a null page count omits the headers entirely.</summary>
    private sealed class PagingHandler(int? pageCount, int itemsPerPage) : HttpMessageHandler
    {
        public List<int> RequestedPages { get; } = [];

        public List<string> RequestedUris { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!.ToString();
            RequestedUris.Add(uri);

            var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query);
            var page = int.Parse(query["page"] ?? "1");
            RequestedPages.Add(page);

            var items = Enumerable.Range(0, itemsPerPage).Select(i => new
            {
                plays = 1,
                last_watched_at = "2026-01-01T00:00:00.000Z",
                movie = new { title = $"Movie {page}-{i}", year = 2026, ids = new { trakt = (page * 1000) + i, slug = $"m-{page}-{i}" } },
                show = new { title = $"Show {page}-{i}", year = 2026, ids = new { trakt = (page * 1000) + i, slug = $"s-{page}-{i}" } },
            });

            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(items) };
            if (pageCount is { } count)
            {
                response.Headers.Add("X-Pagination-Page", page.ToString());
                response.Headers.Add("X-Pagination-Page-Count", count.ToString());
            }

            return Task.FromResult(response);
        }
    }
}
