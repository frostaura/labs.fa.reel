using System.Diagnostics;
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
/// Both typed clients called EnsureSuccessStatusCode with no special case for throttling, and 429
/// is NOT part of the standard resilience handler's transient predicate (that covers 5xx / 408 /
/// socket errors). So a rate-limit response — the single most likely failure when hydrating
/// thousands of titles — became an unhandled exception that failed the whole job instead of a
/// backoff. These tests pin the backoff, its bound, and that Retry-After wins over the default.
/// </summary>
public class ThrottleBackoffTests
{
    [Fact]
    public async Task Tmdb_retries_a_429_and_succeeds_on_the_next_attempt()
    {
        var handler = new ThrottlingHandler(throttleCount: 1, TmdbBody);
        var client = BuildTmdb(handler);

        var details = await client.GetMovieAsync(42);

        Assert.NotNull(details);
        Assert.Equal(2, handler.Attempts);
    }

    [Fact]
    public async Task Tmdb_gives_up_after_a_bounded_number_of_retries()
    {
        var handler = new ThrottlingHandler(throttleCount: 99, TmdbBody);
        var client = BuildTmdb(handler);

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetMovieAsync(42));

        // One initial call plus three retries — bounded, so a permanently throttled provider does
        // not park a background job indefinitely.
        Assert.Equal(4, handler.Attempts);
    }

    [Fact]
    public async Task Retry_after_is_honoured_over_the_default_backoff()
    {
        // Default first backoff is 400ms; the header asks for a full second.
        var handler = new ThrottlingHandler(throttleCount: 1, TmdbBody, retryAfterSeconds: 1);
        var client = BuildTmdb(handler);

        var stopwatch = Stopwatch.StartNew();
        await client.GetMovieAsync(42);
        stopwatch.Stop();

        Assert.True(
            stopwatch.Elapsed >= TimeSpan.FromMilliseconds(900),
            $"Expected the Retry-After hint to be waited out; waited {stopwatch.ElapsedMilliseconds}ms.");
    }

    [Fact]
    public async Task Trakt_retries_a_429_while_walking_pages()
    {
        var handler = new ThrottlingHandler(throttleCount: 1, TraktRatingsBody);
        var client = BuildTrakt(handler);

        var ratings = await client.GetRatingsAsync("token", RatePriority.Backfill);

        Assert.Single(ratings);
        Assert.Equal(2, handler.Attempts);
    }

    [Fact]
    public async Task Trakt_gives_up_after_a_bounded_number_of_retries()
    {
        var handler = new ThrottlingHandler(throttleCount: 99, TraktRatingsBody);
        var client = BuildTrakt(handler);

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetRatingsAsync("token", RatePriority.Backfill));

        Assert.Equal(4, handler.Attempts);
    }

    private static object TmdbBody() => new
    {
        id = 42,
        poster_path = "/p.jpg",
        popularity = 9.5,
        vote_average = 7.4,
        vote_count = 120,
        runtime = 99,
        overview = "An overview.",
    };

    private static object TraktRatingsBody() => new[]
    {
        new
        {
            rated_at = "2026-01-01T00:00:00.000Z",
            rating = 9,
            type = "movie",
            movie = new { title = "A movie", year = 2026, ids = new { trakt = 1, slug = "a-movie" } },
        },
    };

    private static TmdbClient BuildTmdb(HttpMessageHandler handler)
    {
        var services = new ServiceCollection()
            .AddKeyedSingleton<IRateGate>("tmdb", new PassthroughRateGate())
            .BuildServiceProvider();

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["TMDB_READ_ACCESS_TOKEN"] = "read-token" })
            .Build();

        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.themoviedb.org/3/") };
        var recorder = new ApiUsageRecorder(services.GetRequiredService<IServiceScopeFactory>(), NullLogger<ApiUsageRecorder>.Instance);

        return new TmdbClient(httpClient, services.GetRequiredKeyedService<IRateGate>("tmdb"), recorder, configuration);
    }

    private static TraktClient BuildTrakt(HttpMessageHandler handler)
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

    /// <summary>429s the first <paramref name="throttleCount"/> attempts, then serves the body.</summary>
    private sealed class ThrottlingHandler(int throttleCount, Func<object> body, int? retryAfterSeconds = null) : HttpMessageHandler
    {
        public int Attempts { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Attempts++;
            if (Attempts <= throttleCount)
            {
                var throttled = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                if (retryAfterSeconds is { } seconds)
                {
                    throttled.Headers.Add("Retry-After", seconds.ToString());
                }

                return Task.FromResult(throttled);
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(body()) });
        }
    }
}
