using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FrostAura.Reel.Application.Auth;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Filters;
using FrostAura.Reel.Domain.Library;
using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Domain.Sync;
using FrostAura.Reel.Infrastructure.Adapters;
using FrostAura.Reel.Infrastructure.Telemetry;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Mvc.Testing.Handlers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FrostAura.Reel.Tests.IntegrationTests;

/// <summary>
/// <c>GET /api/feed/trakt-recommendations</c> end to end on real Postgres, with the REAL Trakt
/// adapter talking to a fake Trakt over HTTP — no Trakt key exists in a test run, and none is needed.
///
/// <para>What is pinned: Trakt's order survives; everything with an interaction footprint, and
/// everything a standing condition rules out, is gone and counted; a title Reel has never seen is
/// added to the catalogue; Trakt's answer is cached but the filter is not (a reaction after the
/// fetch removes the card with no second Trakt call); and the three honest non-answers — no link,
/// a dead link, an unreachable Trakt with and without an earlier list — are states, never an empty
/// list that pretends there were no recommendations.</para>
/// </summary>
[Collection("postgres")]
public class TraktRecommendationsEndpointTests(PostgresFixture fixture, ReelApiFactory api) : IClassFixture<ReelApiFactory>
{
    private const string Route = "/api/feed/trakt-recommendations";

    [Fact]
    public async Task Keeps_Trakts_order_and_removes_and_counts_everything_seen_set_aside_or_ruled_out()
    {
        if (!fixture.Available)
        {
            return;
        }

        var ids = new Ids();
        var trakt = new FakeTrakt(ids.MoviesJson(), ids.ShowsJson());
        await using var host = Host(trakt);
        var (accountId, client) = await SignInAsync(host, "trakt-recs-order");
        await SeedConnectionAsync(host, accountId);
        await ids.SeedLibraryAsync(fixture, accountId);

        var body = await client.GetFromJsonAsync<JsonElement>(Route);

        Assert.Equal("Ok", body.GetProperty("state").GetString());
        // Interleaved movie, show, movie, show… with Trakt's order kept inside each media type; of
        // the eight Trakt sent only the never-seen movie and the untouched show survive.
        Assert.Equal(
            [ids.NewMovieTmdb, ids.KeptShowTmdb],
            body.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("tmdbId").GetInt64()));
        Assert.Equal(8, body.GetProperty("returnedByTrakt").GetInt32());
        Assert.Equal(2, body.GetProperty("hiddenAsSeen").GetInt32());
        Assert.Equal(2, body.GetProperty("hiddenAsSetAside").GetInt32());
        Assert.Equal(1, body.GetProperty("hiddenByConditions").GetInt32());
        Assert.Equal(1, body.GetProperty("unresolved").GetInt32());
        Assert.NotEqual(JsonValueKind.Null, body.GetProperty("fetchedAt").ValueKind);

        var first = body.GetProperty("items")[0];
        Assert.Equal("Brand New Film", first.GetProperty("name").GetString());
        Assert.Equal(1, first.GetProperty("traktRank").GetInt32());
        // No model, so no "for you" number — null, never a placeholder that reads as a score.
        Assert.Equal(JsonValueKind.Null, first.GetProperty("predictedRating").ValueKind);
        Assert.Equal(string.Empty, first.GetProperty("whyThis").GetString());

        // Both media asked for, once each, as the signed-in user.
        Assert.Equal(2, trakt.Requests.Count);
        Assert.Contains(trakt.Requests, r => r.Contains("recommendations/movies?limit=100", StringComparison.Ordinal));
        Assert.Contains(trakt.Requests, r => r.Contains("recommendations/shows?limit=100", StringComparison.Ordinal));
        Assert.True(trakt.SawAuthorization);

        // The recommendation Reel had never heard of is now a catalogue row, from Trakt's metadata.
        await using var read = fixture.CreateContext(new AccountContext());
        var created = await read.Titles.SingleAsync(t => t.MediaType == MediaType.Movie && t.TmdbId == ids.NewMovieTmdb);
        Assert.Equal(ids.NewMovieTrakt, created.TraktId);
        Assert.Contains("drama", created.Genres);
    }

    [Fact]
    public async Task A_reaction_after_the_fetch_removes_the_card_on_the_next_read_without_asking_Trakt_again()
    {
        if (!fixture.Available)
        {
            return;
        }

        var ids = new Ids();
        var trakt = new FakeTrakt(ids.MoviesJson(), ids.ShowsJson());
        await using var host = Host(trakt);
        var (accountId, client) = await SignInAsync(host, "trakt-recs-react");
        await SeedConnectionAsync(host, accountId);
        await ids.SeedLibraryAsync(fixture, accountId);

        var before = await client.GetFromJsonAsync<JsonElement>(Route);
        Assert.Equal(2, before.GetProperty("items").GetArrayLength());

        await using (var db = fixture.CreateContext(new AccountContext()))
        {
            var kept = await db.Titles.SingleAsync(t => t.MediaType == MediaType.Show && t.TmdbId == ids.KeptShowTmdb);
            db.UserTitleReactions.Add(new UserTitleReaction
            {
                Id = Guid.NewGuid(),
                AccountId = accountId,
                TitleId = kept.Id,
                Kind = ReactionKind.NotInterested,
                CreatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var after = await client.GetFromJsonAsync<JsonElement>(Route);

        Assert.Equal([ids.NewMovieTmdb], after.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("tmdbId").GetInt64()));
        Assert.Equal(3, after.GetProperty("hiddenAsSetAside").GetInt32());
        Assert.Equal(2, trakt.Requests.Count);
    }

    [Fact]
    public async Task An_account_with_no_Trakt_link_is_told_so_and_Trakt_is_never_asked()
    {
        if (!fixture.Available)
        {
            return;
        }

        var trakt = new FakeTrakt("[]", "[]");
        await using var host = Host(trakt);
        var (_, client) = await SignInAsync(host, "trakt-recs-unlinked");

        var body = await client.GetFromJsonAsync<JsonElement>(Route);

        Assert.Equal("NotLinked", body.GetProperty("state").GetString());
        Assert.Equal(0, body.GetProperty("items").GetArrayLength());
        Assert.Empty(trakt.Requests);
    }

    [Fact]
    public async Task A_revoked_link_asks_for_a_relink_rather_than_showing_an_empty_rail()
    {
        if (!fixture.Available)
        {
            return;
        }

        var trakt = new FakeTrakt("[]", "[]");
        await using var host = Host(trakt);
        var (accountId, client) = await SignInAsync(host, "trakt-recs-revoked");
        await SeedConnectionAsync(host, accountId, ConnectionStatus.Revoked);

        var body = await client.GetFromJsonAsync<JsonElement>(Route);

        Assert.Equal("RelinkRequired", body.GetProperty("state").GetString());
        Assert.Equal(0, body.GetProperty("items").GetArrayLength());
        Assert.Empty(trakt.Requests);
    }

    [Fact]
    public async Task An_unreachable_Trakt_says_so_falls_back_to_the_last_list_and_Retry_asks_again()
    {
        if (!fixture.Available)
        {
            return;
        }

        var ids = new Ids();
        var trakt = new FakeTrakt(ids.MoviesJson(), ids.ShowsJson());
        await using var host = Host(trakt);

        // Never answered: Unavailable, with nothing to fall back on.
        var (coldId, cold) = await SignInAsync(host, "trakt-recs-cold");
        await SeedConnectionAsync(host, coldId);
        trakt.Down = true;
        var unavailable = await cold.GetFromJsonAsync<JsonElement>(Route);
        Assert.Equal("Unavailable", unavailable.GetProperty("state").GetString());
        Assert.Equal(0, unavailable.GetProperty("items").GetArrayLength());

        // Inside the cooldown a plain read does not ask again; Retry does, and gets the list.
        var asked = trakt.Requests.Count;
        await cold.GetFromJsonAsync<JsonElement>(Route);
        Assert.Equal(asked, trakt.Requests.Count);
        trakt.Down = false;
        var retried = await cold.GetFromJsonAsync<JsonElement>($"{Route}?retry=true");
        Assert.Equal("Ok", retried.GetProperty("state").GetString());

        // Answered once, then a sync moves the activity marker and Trakt goes down: the last list,
        // labelled Stale, with the time it was fetched.
        var (warmId, warm) = await SignInAsync(host, "trakt-recs-warm");
        await SeedConnectionAsync(host, warmId);
        await ids.SeedLibraryAsync(fixture, warmId);
        var fresh = await warm.GetFromJsonAsync<JsonElement>(Route);
        Assert.Equal("Ok", fresh.GetProperty("state").GetString());

        await using (var db = fixture.CreateContext(new AccountContext()))
        {
            var connection = await db.TraktConnections.IgnoreQueryFilters().SingleAsync(c => c.AccountId == warmId);
            connection.LastActivitiesJson = """{"all":"2026-10-30T12:00:00.000Z"}""";
            await db.SaveChangesAsync();
        }

        trakt.Down = true;
        var stale = await warm.GetFromJsonAsync<JsonElement>(Route);

        Assert.Equal("Stale", stale.GetProperty("state").GetString());
        Assert.NotEqual(0, stale.GetProperty("items").GetArrayLength());
        Assert.Equal(
            fresh.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("titleId").GetGuid()),
            stale.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("titleId").GetGuid()));
        Assert.Equal(fresh.GetProperty("fetchedAt").GetDateTime(), stale.GetProperty("fetchedAt").GetDateTime());
    }

    /// <summary>
    /// The base harness with the real <see cref="TraktClient"/> pointed at <paramref name="trakt"/>, and
    /// inline TMDB hydration closed (the budget at zero) so a new title never reaches for TMDB.
    /// </summary>
    private WebApplicationFactory<Program> Host(FakeTrakt trakt) =>
        api.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("HYDRATE_MAX_TITLES_PER_DAY", "0");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ITraktClient>();
                services.AddSingleton<ITraktClient>(sp => new TraktClient(
                    new HttpClient(trakt) { BaseAddress = new Uri("https://api.trakt.tv/") },
                    sp.GetRequiredKeyedService<IRateGate>("trakt"),
                    sp.GetRequiredService<ApiUsageRecorder>(),
                    new ConfigurationBuilder()
                        .AddInMemoryCollection(new Dictionary<string, string?> { ["TRAKT_CLIENT_ID"] = "fake-trakt-client" })
                        .Build()));
            });
        });

    private async Task<(Guid AccountId, HttpClient Client)> SignInAsync(WebApplicationFactory<Program> host, string slug)
    {
        var accountId = await api.SeedAccountAsync($"{slug}-{Guid.NewGuid():N}");
        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Infrastructure.Persistence.ReelDbContext>();
        var account = await db.Accounts.IgnoreQueryFilters().FirstAsync(a => a.Id == accountId);
        var token = scope.ServiceProvider.GetRequiredService<SessionService>().MintAccessToken(account, DateTime.UtcNow);

        var cookies = new CookieContainerHandler();
        cookies.Container.Add(host.ClientOptions.BaseAddress, new Cookie("reel_at", token, "/", host.ClientOptions.BaseAddress.Host));
        return (accountId, host.CreateDefaultClient(cookies));
    }

    /// <summary>A link sealed by the host's own protector, far from expiry, so no refresh is attempted.</summary>
    private async Task SeedConnectionAsync(WebApplicationFactory<Program> host, Guid accountId, ConnectionStatus status = ConnectionStatus.Active)
    {
        var protector = host.Services.GetRequiredService<ISecretProtector>();
        await using var db = fixture.CreateContext(new AccountContext());
        db.TraktConnections.Add(new TraktConnection
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            AccessTokenEncrypted = protector.Protect("access-" + Guid.NewGuid().ToString("N")),
            RefreshTokenEncrypted = protector.Protect("refresh-" + Guid.NewGuid().ToString("N")),
            ExpiresAt = DateTime.UtcNow.AddDays(60),
            Status = status,
            LastActivitiesJson = """{"all":"2026-10-30T10:00:00.000Z"}""",
        });
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// One test's catalogue, on ids no other test uses: movies a–e plus a brand-new one and one with no
    /// TMDB id, shows f–g. Trakt sends them in a fixed order; the account's library sets each one's fate.
    /// </summary>
    private sealed class Ids
    {
        private readonly long _base = 800_000_000L + (Random.Shared.NextInt64(1, 9_000_000) * 20);

        public long NewMovieTmdb => _base + 1;

        public long NewMovieTrakt => _base + 101;

        public long KeptShowTmdb => _base + 12;

        private long Tmdb(int n) => _base + n;

        private long TraktOf(int n) => _base + 100 + n;

        // Movies, in Trakt's order: 1 new, 2 watched, 3 rated, 4 "not for me", 5 horror (excluded), 9 no TMDB id.
        public string MoviesJson() =>
            $$"""
            [{{Subject(1, "Brand New Film", "drama")}},{{Subject(2, "Watched Film")}},{{Subject(3, "Rated Film")}},
             {{Subject(4, "Declined Film")}},{{Subject(5, "Horror Film", "horror")}},
             {"title":"No Tmdb Film","year":2020,"ids":{"trakt":{{TraktOf(9).ToString(CultureInfo.InvariantCulture)}},"slug":"x"},"language":"en","votes":0}]
            """;

        // Shows, in Trakt's order: 11 saved, 12 untouched.
        public string ShowsJson() => $"[{Subject(11, "Saved Show")},{Subject(12, "Kept Show")}]";

        private string Subject(int n, string title, string genre = "drama") =>
            string.Create(
                CultureInfo.InvariantCulture,
                $$"""{"title":"{{title}}","year":2020,"ids":{"trakt":{{TraktOf(n)}},"slug":"s{{n}}","tmdb":{{Tmdb(n)}}},"genres":["{{genre}}"],"language":"en","votes":0}""");

        public async Task SeedLibraryAsync(PostgresFixture fixture, Guid accountId)
        {
            await using var db = fixture.CreateContext(new AccountContext());
            var now = DateTime.UtcNow;
            var titles = new Dictionary<int, Title>();
            foreach (var (n, media, genre) in new[]
            {
                (2, MediaType.Movie, "drama"), (3, MediaType.Movie, "drama"), (4, MediaType.Movie, "drama"),
                (5, MediaType.Movie, "horror"), (11, MediaType.Show, "drama"), (12, MediaType.Show, "drama"),
            })
            {
                var existing = await db.Titles.FirstOrDefaultAsync(t => t.MediaType == media && t.TmdbId == Tmdb(n));
                titles[n] = existing ?? db.Titles.Add(new Title
                {
                    Id = Guid.NewGuid(),
                    MediaType = media,
                    TmdbId = Tmdb(n),
                    TraktId = TraktOf(n),
                    Name = $"Seeded {n}",
                    // English, because an account with no language rows is English-only by default.
                    Year = 2020,
                    Genres = [genre],
                    Language = "en",
                    CreatedAt = now,
                }).Entity;
            }

            db.WatchedTitles.Add(new WatchedTitle
            {
                Id = Guid.NewGuid(),
                AccountId = accountId,
                TitleId = titles[2].Id,
                Plays = 1,
                IsFullyWatched = true,
                LastWatchedAt = now,
                FirstSyncedAt = now,
                UpdatedAt = now,
            });
            db.UserRatings.Add(new UserRating
            {
                Id = Guid.NewGuid(),
                AccountId = accountId,
                TitleId = titles[3].Id,
                SubjectType = RatingSubjectType.Movie,
                Rating = 6,
                RatedAt = now,
                FirstRatedAt = now,
                SyncedAt = now,
            });
            db.UserTitleReactions.Add(new UserTitleReaction
            {
                Id = Guid.NewGuid(),
                AccountId = accountId,
                TitleId = titles[4].Id,
                Kind = ReactionKind.NotInterested,
                CreatedAt = now,
            });
            db.UserTitleReactions.Add(new UserTitleReaction
            {
                Id = Guid.NewGuid(),
                AccountId = accountId,
                TitleId = titles[11].Id,
                Kind = ReactionKind.SaveForLater,
                CreatedAt = now,
            });
            db.ContentFilters.Add(new ContentFilter
            {
                Id = Guid.NewGuid(),
                AccountId = accountId,
                Kind = FilterKind.ExcludeGenre,
                Value = "horror",
                CreatedAt = now,
            });
            await db.SaveChangesAsync();
        }
    }

    /// <summary>Trakt over HTTP: canned lists per media type, or a 503 for every call while <see cref="Down"/>.</summary>
    private sealed class FakeTrakt(string movies, string shows) : HttpMessageHandler
    {
        private readonly List<string> _requests = [];

        public bool Down { get; set; }

        public bool SawAuthorization { get; private set; }

        public IReadOnlyList<string> Requests
        {
            get
            {
                lock (_requests)
                {
                    return [.. _requests];
                }
            }
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!.ToString();
            lock (_requests)
            {
                _requests.Add(uri);
            }

            SawAuthorization |= request.Headers.Authorization is not null;
            if (Down)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            }

            var body = uri.Contains("recommendations/movies", StringComparison.Ordinal) ? movies
                : uri.Contains("recommendations/shows", StringComparison.Ordinal) ? shows
                : null;
            return Task.FromResult(body is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
}
