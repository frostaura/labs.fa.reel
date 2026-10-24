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
/// The discover sort order is a golden query parameter like every other: TMDB ignores a misspelt
/// <c>sort_by</c> and returns its default order, so a wrong value would read as "the sort made no
/// difference". The release-date sort names a different field per medium.
/// </summary>
public class TmdbDiscoverSortTests
{
    [Theory]
    [InlineData(null, true, "sort_by=popularity.desc")]
    [InlineData(TmdbDiscoverSort.Popularity, false, "sort_by=popularity.desc")]
    [InlineData(TmdbDiscoverSort.VoteCount, true, "sort_by=vote_count.desc")]
    [InlineData(TmdbDiscoverSort.VoteAverage, false, "sort_by=vote_average.desc")]
    [InlineData(TmdbDiscoverSort.Newest, true, "sort_by=primary_release_date.desc")]
    [InlineData(TmdbDiscoverSort.Newest, false, "sort_by=first_air_date.desc")]
    public async Task The_sort_reaches_tmdb_by_its_exact_name(TmdbDiscoverSort? sort, bool movies, string expected)
    {
        var handler = new RecordingHandler();
        var client = Build(handler);
        var options = sort is { } chosen ? new TmdbDiscoverOptions(Sort: chosen) : null;

        await client.DiscoverByConceptAsync(movies, [], [], null, null, 1, options: options);

        Assert.Contains(expected, handler.LastUri!.Query, StringComparison.Ordinal);
    }

    private static TmdbClient Build(RecordingHandler handler)
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

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public Uri? LastUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{ "results": [] }""", Encoding.UTF8, "application/json"),
            });
        }
    }
}
