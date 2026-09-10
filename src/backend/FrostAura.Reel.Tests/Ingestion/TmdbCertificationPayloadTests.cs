using System.Net;
using System.Text;
using FrostAura.Reel.Application.Ingestion;
using FrostAura.Reel.Application.Ml;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Infrastructure.Adapters;
using FrostAura.Reel.Infrastructure.Telemetry;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrostAura.Reel.Tests.Ingestion;

/// <summary>
/// The wire contract for TMDB certification, pinned against the real payload shapes of
/// <c>append_to_response=release_dates</c> (movies) and <c>content_ratings</c> (TV).
///
/// <para>Why this matters more than a normal parse test: before schema v4,
/// <c>Title.Certification</c> was written ONLY by <c>IngestMapper</c>, i.e. only for titles already
/// in the user's Trakt library. Every TMDB-discovered candidate therefore scored
/// <c>certificationOrdinal = 0</c> while every training row had a real value, so the feature was in
/// effect "did this title come from the user's own library" — a label proxy that serving can never
/// reproduce. A silent parse failure here restores that skew without failing anything.</para>
///
/// <para>The two shapes disagree deliberately and that is where a naive parse breaks: movies nest
/// region → several DATED releases, each with its own certification and a release <c>type</c>, and
/// they routinely disagree with each other; TV is one flat rating per region.</para>
/// </summary>
public class TmdbCertificationPayloadTests
{
    /// <summary>
    /// GET /3/movie/550?append_to_response=release_dates — Fight Club, trimmed. The US block is
    /// deliberately ordered with the non-theatrical entry FIRST and carrying a different
    /// certification, which is what makes "prefer type 3" a real assertion rather than a tautology.
    /// </summary>
    private const string MovieWithReleaseDates = """
        {
          "id": 550,
          "title": "Fight Club",
          "original_language": "en",
          "popularity": 60.1,
          "vote_average": 8.4,
          "vote_count": 27000,
          "release_dates": {
            "results": [
              {
                "iso_3166_1": "US",
                "release_dates": [
                  { "certification": "PG-13", "type": 4, "release_date": "2000-06-06T00:00:00.000Z" },
                  { "certification": "R", "type": 3, "release_date": "1999-10-15T00:00:00.000Z" }
                ]
              },
              {
                "iso_3166_1": "gb",
                "release_dates": [
                  { "certification": "18", "type": 3, "release_date": "1999-11-12T00:00:00.000Z" }
                ]
              },
              {
                "iso_3166_1": "ZA",
                "release_dates": [
                  { "certification": "", "type": 3, "release_date": "1999-11-12T00:00:00.000Z" }
                ]
              }
            ]
          }
        }
        """;

    /// <summary>GET /3/tv/1396?append_to_response=content_ratings — Breaking Bad, trimmed.</summary>
    private const string ShowWithContentRatings = """
        {
          "id": 1396,
          "name": "Breaking Bad",
          "original_language": "en",
          "popularity": 300.5,
          "vote_average": 8.9,
          "vote_count": 12000,
          "content_ratings": {
            "results": [
              { "iso_3166_1": "US", "rating": "TV-MA" },
              { "iso_3166_1": "GB", "rating": "18" }
            ]
          }
        }
        """;

    /// <summary>A title TMDB simply has no certification for — the case the presence flag exists for.</summary>
    private const string MovieWithoutCertification = """
        { "id": 9, "title": "Unrated", "popularity": 1.0, "vote_average": 5.0, "vote_count": 60 }
        """;

    [Fact]
    public async Task Certification_rides_on_the_existing_hydration_call_rather_than_a_second_request()
    {
        // The whole point of append_to_response: hydration cost must not change. A separate
        // /release_dates fetch would double the catalogue's hydration bill.
        var handler = new CountingHandler(MovieWithReleaseDates);
        var details = await BuildTmdb(handler).GetMovieAsync(550);

        Assert.NotNull(details);
        Assert.Equal(1, handler.Calls);
        Assert.Contains("append_to_response=credits,videos,keywords,release_dates", handler.LastPath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_tv_call_appends_content_ratings_not_release_dates()
    {
        var handler = new CountingHandler(ShowWithContentRatings);
        await BuildTmdb(handler).GetTvAsync(1396);

        Assert.Contains("content_ratings", handler.LastPath, StringComparison.Ordinal);
        Assert.DoesNotContain("release_dates", handler.LastPath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_movie_prefers_the_theatrical_certification_over_a_later_disagreeing_one()
    {
        var details = await BuildTmdb(new CountingHandler(MovieWithReleaseDates)).GetMovieAsync(550);

        Assert.NotNull(details);
        var byRegion = details.CertificationsByRegion;
        Assert.NotNull(byRegion);

        // Type 3 (Theatrical) wins even though the type-4 entry is listed first: a TV/digital cut
        // can carry an edited-for-broadcast rating, and TMDB returns the list in no useful order.
        Assert.Equal("R", byRegion["US"]);

        // Region codes are upper-cased on the way in, so a lookup never depends on TMDB's casing.
        Assert.Equal("18", byRegion["GB"]);

        // A region whose only entry is a blank certification is DROPPED, not stored as "".
        // TasteMath.CertificationOrdinal("") is 0 — identical to "we never asked — which is exactly
        // the ambiguity hasCertification exists to remove.
        Assert.False(byRegion.ContainsKey("ZA"));
    }

    [Fact]
    public async Task A_show_yields_one_rating_per_region()
    {
        var details = await BuildTmdb(new CountingHandler(ShowWithContentRatings)).GetTvAsync(1396);

        Assert.NotNull(details);
        Assert.Equal("TV-MA", details.CertificationsByRegion!["US"]);
        Assert.Equal("18", details.CertificationsByRegion["GB"]);
    }

    [Fact]
    public async Task A_title_with_no_certification_anywhere_yields_null_rather_than_an_empty_map()
    {
        var details = await BuildTmdb(new CountingHandler(MovieWithoutCertification)).GetMovieAsync(9);

        Assert.NotNull(details);
        Assert.Null(details.CertificationsByRegion);
        Assert.Null(TitleHydrator.ResolveCertification(details.CertificationsByRegion, "ZA"));
    }

    [Fact]
    public void The_account_region_is_preferred_and_us_is_the_fallback()
    {
        var byRegion = new Dictionary<string, string> { ["US"] = "R", ["GB"] = "18" };

        Assert.Equal("18", TitleHydrator.ResolveCertification(byRegion, "GB"));
        Assert.Equal("18", TitleHydrator.ResolveCertification(byRegion, "gb"));
        Assert.Equal("R", TitleHydrator.ResolveCertification(byRegion, "US"));

        // A region TMDB has no entry for falls back to US rather than to nothing — most of the
        // catalogue carries a US rating and almost none carries a ZA one, so "no fallback" would
        // leave the founder's own account with certification permanently unknown.
        Assert.Equal("R", TitleHydrator.ResolveCertification(byRegion, "ZA"));
        Assert.Equal("R", TitleHydrator.ResolveCertification(byRegion, null));

        // …and when even US is absent, the honest answer is null, not a guess from another region.
        Assert.Null(TitleHydrator.ResolveCertification(new Dictionary<string, string> { ["FR"] = "16" }, "ZA"));
    }

    [Fact]
    public async Task Hydration_fills_certification_only_when_trakt_left_it_empty()
    {
        // Trakt stays authoritative for library titles: it is the same taxonomy the user's own
        // ratings were collected under, and overwriting it would move certificationOrdinal on rows
        // the model has already been trained against for no gain.
        var canned = await BuildTmdb(new CountingHandler(MovieWithReleaseDates)).GetMovieAsync(550);
        Assert.NotNull(canned);
        var tmdb = new TitleHydratorResilienceTests.StubTmdb(
            failFor: null, new InvalidOperationException("unused"), details: _ => canned);

        var fromTrakt = new Title { Id = Guid.NewGuid(), TmdbId = 550, MediaType = MediaType.Movie, Certification = "PG-13" };
        var discovered = new Title { Id = Guid.NewGuid(), TmdbId = 550, MediaType = MediaType.Movie };
        var blank = new Title { Id = Guid.NewGuid(), TmdbId = 550, MediaType = MediaType.Movie, Certification = "   " };

        await TitleHydratorResilienceTests.Hydrator(tmdb)
            .HydrateBatchResilientAsync([fromTrakt, discovered, blank], CancellationToken.None, "US");

        Assert.Equal("PG-13", fromTrakt.Certification);
        Assert.Equal("R", discovered.Certification);
        Assert.Equal("R", blank.Certification);

        // And the feature the whole change exists for now reads the same on a candidate as on a
        // training row, instead of "unknown" on one side and a real ordinal on the other.
        Assert.Equal(
            TasteMath.CertificationOrdinal(fromTrakt.Certification),
            TasteMath.CertificationOrdinal("PG-13"));
        Assert.True(TasteMath.CertificationOrdinal(discovered.Certification) > 0f);
    }

    private static TmdbClient BuildTmdb(CountingHandler handler)
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

    private sealed class PassthroughRateGate : IRateGate
    {
        public Task AcquireAsync(RatePriority priority, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class CountingHandler(string body) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        public string LastPath { get; private set; } = string.Empty;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            LastPath = request.RequestUri?.PathAndQuery ?? string.Empty;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }
}
