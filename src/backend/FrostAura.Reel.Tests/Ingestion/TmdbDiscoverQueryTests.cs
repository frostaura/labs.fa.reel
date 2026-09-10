using System.Globalization;
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
/// The exact query string the discover endpoint sends — golden tests, because every one of these
/// parameters is invisible from the outside. A misspelt or dropped TMDB parameter does not error:
/// TMDB ignores what it does not recognise and returns a perfectly valid, perfectly unfiltered page,
/// which downstream looks exactly like "discovery found nothing eligible today".
///
/// <para>
/// That is not hypothetical. The feed's discover sent no language, no year bound, no crowd-rating
/// floor and no genre exclusions at all, while the account's eligibility rule enforced all four —
/// so ~92% of every page fetched was rejected after the fact, on every build, for ever. These tests
/// pin the projection so it cannot silently regress to that.
/// </para>
///
/// <para>Movie and TV are asserted separately: the date field name differs
/// (<c>primary_release_date</c> vs <c>first_air_date</c>), the genre id spaces differ, and
/// <c>with_people</c> and <c>region</c> exist on movie discover only.</para>
/// </summary>
public class TmdbDiscoverQueryTests
{
    [Fact]
    public async Task Movie_discover_carries_every_projected_content_rule()
    {
        var (client, handler) = Build();

        await client.DiscoverByConceptAsync(
            movies: true,
            genreIds: [53],
            keywordIds: [],
            region: "ZA",
            releasedAfter: new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            page: 3,
            excludeGenreIds: [16, 35, 99],
            excludeKeywordIds: null,
            releasedBefore: new DateTime(2010, 12, 31, 0, 0, 0, DateTimeKind.Utc),
            maxRuntimeMinutes: null,
            options: new TmdbDiscoverOptions("en", 6.0m, 50, null, RatePriority.Reconcile));

        var query = handler.LastUri!.Query;
        Assert.StartsWith("/3/discover/movie", handler.LastUri.AbsolutePath, StringComparison.Ordinal);
        Assert.Contains("page=3", query, StringComparison.Ordinal);
        Assert.Contains("with_genres=53", query, StringComparison.Ordinal);
        Assert.Contains("without_genres=16,35,99", query, StringComparison.Ordinal);
        Assert.Contains("primary_release_date.gte=2000-01-01", query, StringComparison.Ordinal);
        Assert.Contains("primary_release_date.lte=2010-12-31", query, StringComparison.Ordinal);
        Assert.Contains("with_original_language=en", query, StringComparison.Ordinal);
        Assert.Contains("vote_average.gte=6.0", query, StringComparison.Ordinal);
        // The feed's own quality floor, carried explicitly — the concept endpoint's default is 10,
        // so routing supply through it must not quietly drop the floor to a fifth of what it was.
        Assert.Contains("vote_count.gte=50", query, StringComparison.Ordinal);
        Assert.Contains("region=ZA", query, StringComparison.Ordinal);
        Assert.DoesNotContain("vote_count.gte=10", query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Tv_discover_uses_the_air_date_field_and_the_tv_genre_id_space()
    {
        var (client, handler) = Build();

        await client.DiscoverByConceptAsync(
            movies: false,
            genreIds: [10765],
            keywordIds: [],
            region: "ZA",
            releasedAfter: new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            page: 1,
            excludeGenreIds: [16, 35],
            excludeKeywordIds: null,
            releasedBefore: null,
            maxRuntimeMinutes: null,
            options: new TmdbDiscoverOptions("en", 6.0m, 50, [525], RatePriority.Reconcile));

        var query = handler.LastUri!.Query;
        Assert.StartsWith("/3/discover/tv", handler.LastUri.AbsolutePath, StringComparison.Ordinal);
        Assert.Contains("first_air_date.gte=2000-01-01", query, StringComparison.Ordinal);
        Assert.Contains("with_genres=10765", query, StringComparison.Ordinal);
        Assert.Contains("without_genres=16,35", query, StringComparison.Ordinal);
        Assert.Contains("with_original_language=en", query, StringComparison.Ordinal);
        Assert.Contains("vote_average.gte=6.0", query, StringComparison.Ordinal);
        Assert.DoesNotContain("primary_release_date", query, StringComparison.Ordinal);
        // /discover/tv has no person parameter and no region parameter. Sending either would return
        // an unconstrained page that reads downstream as a person arm and is not one.
        Assert.DoesNotContain("with_people", query, StringComparison.Ordinal);
        Assert.DoesNotContain("region=", query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_person_arm_or_joins_its_people_on_movie_discover()
    {
        var (client, handler) = Build();

        await client.DiscoverByConceptAsync(
            movies: true, [], [], region: null, releasedAfter: null, page: 1,
            options: new TmdbDiscoverOptions(PeopleIds: [525, 1032, 138]));

        // Pipe, not comma: comma is AND on TMDB, which would ask for films all three worked on —
        // a handful of titles at best, and none of them the "more by this director" the arm is for.
        Assert.Contains("with_people=525%7C1032%7C138", handler.LastUri!.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task With_no_options_the_query_is_byte_for_byte_the_old_ask_reel_query()
    {
        var (client, handler) = Build();

        await client.DiscoverByConceptAsync(
            movies: true, [28, 12], [9715], "ZA", new DateTime(1980, 1, 1, 0, 0, 0, DateTimeKind.Utc), 1,
            [16], [1234], new DateTime(1989, 12, 31, 0, 0, 0, DateTimeKind.Utc), 90);

        Assert.Equal(
            "?sort_by=popularity.desc&include_adult=false&page=1&vote_count.gte=10"
            + "&with_genres=28%7C12&with_keywords=9715&without_genres=16&without_keywords=1234"
            + "&primary_release_date.gte=1980-01-01&primary_release_date.lte=1989-12-31"
            + "&with_runtime.lte=90&region=ZA",
            Uri.UnescapeDataString(handler.LastUri!.Query).Replace("|", "%7C", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_crowd_floor_is_formatted_invariantly()
    {
        var previous = Thread.CurrentThread.CurrentCulture;
        try
        {
            // A comma-decimal locale would render vote_average.gte=6,5 — which TMDB reads as a
            // malformed number and ignores, silently returning an unfiltered page.
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("af-ZA");
            var (client, handler) = Build();

            await client.DiscoverByConceptAsync(
                movies: true, [], [], null, null, 1, options: new TmdbDiscoverOptions(MinVoteAverage: 6.5m));

            Assert.Contains("vote_average.gte=6.5", handler.LastUri!.Query, StringComparison.Ordinal);
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = previous;
        }
    }

    [Fact]
    public async Task Similar_and_recommendations_are_different_endpoints()
    {
        var (client, handler) = Build();

        await client.GetSimilarAsync(movies: true, 550);
        var similar = handler.LastUri!.AbsolutePath;
        await client.GetRecommendationsAsync(movies: true, 550);

        Assert.Equal("/3/movie/550/similar", similar);
        Assert.Equal("/3/movie/550/recommendations", handler.LastUri.AbsolutePath);
    }

    [Fact]
    public async Task Supply_calls_drain_the_reconcile_lane_and_ask_reel_keeps_the_interactive_one()
    {
        var (client, _, gate) = BuildWithGate();

        await client.DiscoverByConceptAsync(movies: true, [], [], null, null, 1);
        await client.DiscoverByConceptAsync(
            movies: true, [], [], null, null, 1,
            options: new TmdbDiscoverOptions(Priority: RatePriority.Reconcile));

        // Forty background calls per build promoted into the lane live search shares would be a
        // supply win paid for with interactive latency.
        Assert.Equal([RatePriority.Interactive, RatePriority.Reconcile], gate.Acquired);
    }

    /// <summary>
    /// The date bounds are the one parameter in this projection whose CORRECTNESS depends on the
    /// host's culture rather than only its formatting: ar-SA defaults to the Umm al-Qura calendar,
    /// so an unqualified <c>yyyy-MM-dd</c> writes a Hijri year — 2000-01-01 leaves as 1420-09-24.
    /// TMDB parses that happily and returns the wrong two decades of films, with no error anywhere.
    /// Same class of silent-ignore failure as <c>vote_average.gte=6,0</c>, which is why the numeric
    /// half of this file was already invariant; the dates were not, until 2026-09-10.
    /// </summary>
    [Theory]
    [InlineData("de-DE")]
    [InlineData("fr-FR")]
    [InlineData("ar-SA")]
    public async Task The_discover_dates_stay_gregorian_under_a_hostile_host_calendar(string culture)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            var (client, handler) = Build();

            await client.DiscoverByConceptAsync(
                movies: true,
                genreIds: [],
                keywordIds: [],
                region: null,
                releasedAfter: new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                page: 1,
                releasedBefore: new DateTime(2010, 12, 31, 0, 0, 0, DateTimeKind.Utc),
                options: new TmdbDiscoverOptions("en", 6.0m, 50, null, RatePriority.Reconcile));

            var conceptQuery = handler.LastUri!.Query;
            Assert.Contains("primary_release_date.gte=2000-01-01", conceptQuery, StringComparison.Ordinal);
            Assert.Contains("primary_release_date.lte=2010-12-31", conceptQuery, StringComparison.Ordinal);
            Assert.Contains("vote_average.gte=6.0", conceptQuery, StringComparison.Ordinal);

            // The plain-discover path builds its own query string and must agree.
            await client.DiscoverAsync(
                movies: false,
                genreId: null,
                region: null,
                releasedAfter: new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                page: 1);

            Assert.Contains("first_air_date.gte=2000-01-01", handler.LastUri!.Query, StringComparison.Ordinal);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    private static (TmdbClient Client, RecordingHandler Handler) Build()
    {
        var (client, handler, _) = BuildWithGate();
        return (client, handler);
    }

    private static (TmdbClient Client, RecordingHandler Handler, RecordingRateGate Gate) BuildWithGate()
    {
        var gate = new RecordingRateGate();
        var services = new ServiceCollection()
            .AddKeyedSingleton<IRateGate>("tmdb", gate)
            .BuildServiceProvider();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["TMDB_READ_ACCESS_TOKEN"] = "read-token" })
            .Build();
        var handler = new RecordingHandler();
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.themoviedb.org/3/") };
        var recorder = new ApiUsageRecorder(services.GetRequiredService<IServiceScopeFactory>(), NullLogger<ApiUsageRecorder>.Instance);
        return (new TmdbClient(httpClient, gate, recorder, configuration), handler, gate);
    }

    private sealed class RecordingRateGate : IRateGate
    {
        public List<RatePriority> Acquired { get; } = [];

        public Task AcquireAsync(RatePriority priority, CancellationToken ct = default)
        {
            Acquired.Add(priority);
            return Task.CompletedTask;
        }
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
