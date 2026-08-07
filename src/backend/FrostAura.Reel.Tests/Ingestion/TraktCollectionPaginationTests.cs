using System.Net;
using System.Net.Http.Json;
using System.Web;
using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Infrastructure.Adapters;
using FrostAura.Reel.Infrastructure.Telemetry;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrostAura.Reel.Tests.Ingestion;

/// <summary>
/// The watched endpoints were fixed to walk pages after Trakt began enforcing pagination on
/// 2026-06-30, but /sync/ratings and /users/me/lists were left on the single-shot path — the exact
/// pattern that silently capped the watched ingest at 100 items with a 200 OK and no error.
/// Ratings are the training label set, so a truncated fetch trains the model on a slice of the
/// user's taste and nothing anywhere reports a problem. These tests pin both collections to the
/// header-driven page walk.
/// </summary>
public class TraktCollectionPaginationTests
{
    [Fact]
    public async Task Ratings_walk_every_page_reported_by_the_pagination_header()
    {
        var handler = new PagingHandler(pageCount: 4, itemsPerPage: 250);
        var client = BuildClient(handler);

        var ratings = await client.GetRatingsAsync("token", RatePriority.Backfill);

        Assert.Equal(1000, ratings.Count);
        Assert.Equal([1, 2, 3, 4], handler.RequestedPages);
    }

    [Fact]
    public async Task Ratings_keep_extended_full_while_paging()
    {
        var handler = new PagingHandler(pageCount: 1, itemsPerPage: 1);
        var client = BuildClient(handler);

        await client.GetRatingsAsync("token", RatePriority.Backfill);

        // The page/limit pair must be appended to the existing query string, not replace it.
        Assert.Contains("extended=full", handler.RequestedUris[0]);
        Assert.Contains("page=1", handler.RequestedUris[0]);
        Assert.Contains("limit=", handler.RequestedUris[0]);
    }

    [Fact]
    public async Task Ratings_without_pagination_headers_are_taken_as_the_only_page()
    {
        var handler = new PagingHandler(pageCount: null, itemsPerPage: 7);
        var client = BuildClient(handler);

        var ratings = await client.GetRatingsAsync("token", RatePriority.Backfill);

        Assert.Equal(7, ratings.Count);
        Assert.Single(handler.RequestedPages);
    }

    [Fact]
    public async Task Ensure_list_finds_a_managed_list_that_sits_on_a_later_page()
    {
        // A single-shot fetch misses it and creates a SECOND "Reel — Up Next" list.
        var handler = new PagingHandler(pageCount: 3, itemsPerPage: 5, listNameOnLastPage: "Reel — Up Next");
        var client = BuildClient(handler);

        var listId = await client.EnsureListAsync("token", "Reel — Up Next", "desc", RatePriority.Reconcile);

        Assert.Equal(PagingHandler.MatchedListId, listId);
        Assert.Equal([1, 2, 3], handler.RequestedPages);
        Assert.DoesNotContain(handler.Methods, m => m == HttpMethod.Post);
    }

    private static TraktClient BuildClient(HttpMessageHandler handler)
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

    /// <summary>Serves <paramref name="pageCount"/> pages of ratings or lists; a null page count omits the headers.</summary>
    private sealed class PagingHandler(int? pageCount, int itemsPerPage, string? listNameOnLastPage = null) : HttpMessageHandler
    {
        public const long MatchedListId = 555;

        public List<int> RequestedPages { get; } = [];

        public List<string> RequestedUris { get; } = [];

        public List<HttpMethod> Methods { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!.ToString();
            RequestedUris.Add(uri);
            Methods.Add(request.Method);

            var page = int.Parse(HttpUtility.ParseQueryString(request.RequestUri!.Query)["page"] ?? "1");
            RequestedPages.Add(page);

            var isLists = uri.Contains("lists", StringComparison.Ordinal);
            object payload = isLists
                ? Enumerable.Range(0, itemsPerPage).Select(i => new
                {
                    name = page == pageCount && i == 0 ? listNameOnLastPage : $"Other list {page}-{i}",
                    ids = new { trakt = page == pageCount && i == 0 ? MatchedListId : (page * 100) + i },
                })
                : Enumerable.Range(0, itemsPerPage).Select(i => new
                {
                    rated_at = "2026-01-01T00:00:00.000Z",
                    rating = 8,
                    type = "movie",
                    movie = new { title = $"Movie {page}-{i}", year = 2026, ids = new { trakt = (page * 1000) + i, slug = $"m-{page}-{i}" } },
                });

            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(payload) };
            if (pageCount is { } count)
            {
                response.Headers.Add("X-Pagination-Page", page.ToString());
                response.Headers.Add("X-Pagination-Page-Count", count.ToString());
            }

            return Task.FromResult(response);
        }
    }
}
