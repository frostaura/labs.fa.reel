using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using FrostAura.Reel.Application.Auth;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Library;
using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Domain.Ports.Tmdb;
using FrostAura.Reel.Domain.Tenancy;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Mvc.Testing.Handlers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FrostAura.Reel.Tests.IntegrationTests;

/// <summary>
/// The turn record's contract, asserted through the real <c>/api/search/ask</c> endpoint.
///
/// <para>Where <see cref="AskReelEndpointHarnessTests"/> pins the things that must hold whatever
/// the pipeline does, this class pins the three properties the turn record exists to create, and
/// each of them is a property the client cannot fake: a retry of the SAME turn is the same turn,
/// a conversation id that is not yours is not a new conversation, and the no-repeat set is the
/// SERVER's memory of what it showed you — not a list the client is trusted to carry.</para>
///
/// <para>All three fail against the pre-turn-record endpoint, and they fail for the right reason:
/// <c>clientTurnId</c> is not a field it binds, an unknown id silently forks a fresh conversation,
/// and <c>shownTmdbIds</c> arrives from the client or does not arrive at all.</para>
///
/// <para>Inherits <see cref="EmbeddedCatalogTestBase"/> because a real turn embeds whatever it
/// discovered: this class leaves vectors in the shared HNSW index exactly as a hand-seeding
/// fixture does, and the source guard cannot see that.</para>
/// </summary>
[Collection("postgres")]
public class AskReelEndpointTests(PostgresFixture fixture, ReelApiFactory api)
    : EmbeddedCatalogTestBase(fixture), IClassFixture<ReelApiFactory>
{
    /// <summary>
    /// A retry is not a second turn. The client mints one <c>clientTurnId</c> per user message and
    /// resends it on a dropped connection, a StrictMode double-invoke or an impatient tap; the
    /// server owes it the same answer and exactly one row.
    /// </summary>
    [Fact]
    public async Task The_same_client_turn_id_replays_and_writes_no_second_turn()
    {
        if (!Fixture.Available)
        {
            return;
        }

        var (accountId, client) = await SignInAsync(api, $"turn-idem-{Guid.NewGuid():N}");
        using (client)
        {
            var clientTurnId = Guid.NewGuid();
            var first = await AskAsync(client, new
            {
                query = "something tense with a clever ending",
                clientTurnId,
            });

            var conversationId = ConversationId(first);
            var turnsAfterFirst = await CountTurnsAsync(accountId, conversationId);
            Assert.True(turnsAfterFirst > 0, "the first ask must have written the turn it is about to replay");

            // Byte-identical retry, same id.
            using var replay = await client.PostAsJsonAsync("/api/search/ask", new
            {
                query = "something tense with a clever ending",
                conversationId,
                clientTurnId,
            });

            Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
            Assert.Equal(turnsAfterFirst, await CountTurnsAsync(accountId, conversationId));
        }
    }

    /// <summary>
    /// A conversation id the caller does not own — deleted, another account's, a stale
    /// <c>localStorage</c> entry after a wipe — is a 404 and nothing else. It must never fork: a
    /// silent new conversation is how a user's transcript splits in two without anyone noticing.
    /// </summary>
    [Fact]
    public async Task An_unknown_conversation_id_is_a_404_and_creates_nothing()
    {
        if (!Fixture.Available)
        {
            return;
        }

        var (accountId, client) = await SignInAsync(api, $"turn-404-{Guid.NewGuid():N}");
        using (client)
        {
            using var response = await client.PostAsJsonAsync("/api/search/ask", new
            {
                query = "resume a conversation that does not exist",
                conversationId = Guid.NewGuid(),
                clientTurnId = Guid.NewGuid(),
            });

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

            using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal("conversation_not_found", payload.RootElement.GetProperty("code").GetString());

            await using var db = ScopedContext(accountId);
            Assert.Equal(0, await db.SearchConversations.CountAsync());
            Assert.Equal(0, await db.SearchTurns.CountAsync());
        }
    }

    /// <summary>
    /// The no-repeat set is the server's own record of the cards it served this conversation. The
    /// request below carries no <c>shownTmdbIds</c> field at all — the field does not exist on the
    /// wire — and the second turn must still not repeat the first turn's picks.
    ///
    /// <para>A client-carried list is unverifiable, capped, lost on reload and trivially wrong
    /// after a second tab; the whole point of the turn record is that the answer comes from
    /// <c>SearchTurnCards</c>.</para>
    /// </summary>
    [Fact]
    public async Task The_no_repeat_set_is_derived_server_side()
    {
        if (!Fixture.Available)
        {
            return;
        }

        var scripted = new ScriptedDiscoveryTmdbClient(Discoverable(firstTmdbId: 771001, count: 10));
        using var factory = api.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<ITmdbClient>();
            services.AddSingleton<ITmdbClient>(scripted);
        }));

        var (accountId, client) = await SignInAsync(factory, $"turn-shown-{Guid.NewGuid():N}");
        using (client)
        {
            // Everything already in the shared catalogue is out of this account's pool, so the only
            // titles that can reach either turn are the scripted client's.
            await MarkExistingTitlesSeenAsync(accountId);

            var first = await AskAsync(client, new
            {
                query = "a brooding futuristic thriller",
                clientTurnId = Guid.NewGuid(),
            });
            var conversationId = ConversationId(first);
            var firstIds = TmdbIds(first);
            Assert.NotEmpty(firstIds);

            var second = await AskAsync(client, new
            {
                query = "something else brooding and futuristic, please",
                conversationId,
                clientTurnId = Guid.NewGuid(),
            });

            Assert.Empty(TmdbIds(second).Intersect(firstIds));
        }
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A seeded account plus a client already carrying its session cookie, against WHICHEVER host
    /// the test is driving. <see cref="ReelApiFactory.CreateSignedInAccountAsync"/> cannot be used
    /// directly for the swapped-catalogue host: <c>WithWebHostBuilder</c> hands back a plain
    /// <see cref="WebApplicationFactory{TEntryPoint}"/>. The token is minted by the base host's own
    /// <see cref="SessionService"/> — same signing secret, same database — so both hosts accept it.
    /// </summary>
    private async Task<(Guid AccountId, HttpClient Client)> SignInAsync(
        WebApplicationFactory<Program> factory, string slug)
    {
        var accountId = await api.SeedAccountAsync(slug);
        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Infrastructure.Persistence.ReelDbContext>();
        var account = await db.Accounts.IgnoreQueryFilters().FirstAsync(a => a.Id == accountId);
        var token = scope.ServiceProvider.GetRequiredService<SessionService>().MintAccessToken(account, DateTime.UtcNow);

        var cookies = new CookieContainerHandler();
        cookies.Container.Add(
            factory.ClientOptions.BaseAddress,
            new Cookie("reel_at", token, "/", factory.ClientOptions.BaseAddress.Host));
        return (accountId, factory.CreateDefaultClient(cookies));
    }

    private async Task<int> CountTurnsAsync(Guid accountId, Guid conversationId)
    {
        await using var db = ScopedContext(accountId);
        return await db.SearchTurns.CountAsync(t => t.ConversationId == conversationId);
    }

    private Infrastructure.Persistence.ReelDbContext ScopedContext(Guid accountId)
    {
        var scope = new AccountContext();
        scope.SetAccount(accountId);
        return Fixture.CreateContext(scope);
    }

    /// <summary>Every title already in the shared catalogue, marked watched for this account.</summary>
    private async Task MarkExistingTitlesSeenAsync(Guid accountId)
    {
        await using var db = Fixture.CreateContext(new AccountContext());
        var now = DateTime.UtcNow;
        foreach (var titleId in await db.Titles.Select(t => t.Id).ToListAsync())
        {
            db.WatchedTitles.Add(new WatchedTitle
            {
                Id = Guid.NewGuid(),
                AccountId = accountId,
                TitleId = titleId,
                Plays = 1,
                IsFullyWatched = true,
                LastWatchedAt = now,
                FirstSyncedAt = now,
                UpdatedAt = now,
            });
        }

        await db.SaveChangesAsync();
    }

    private static IReadOnlyList<TmdbListItem> Discoverable(long firstTmdbId, int count) =>
        Enumerable.Range(0, count)
            .Select(i => new TmdbListItem(
                firstTmdbId + i, true, $"Turn Record Title {firstTmdbId + i}", "/t.jpg", null,
                "A brooding, futuristic search for what is real.", 40m - i, 7.4m, 900,
                new DateTime(2008 + i, 6, 16, 0, 0, 0, DateTimeKind.Utc), [878], "en"))
            .ToList();

    private static Guid ConversationId(SseStream stream) =>
        Assert.Single(stream.Frames, f => f.Event == "conversation").Data.GetProperty("id").GetGuid();

    private static IReadOnlyList<long> TmdbIds(SseStream stream) =>
        Assert.Single(stream.Frames, f => f.Event == "done")
            .Data.GetProperty("results")
            .EnumerateArray()
            .Select(c => c.GetProperty("tmdbId"))
            .Where(id => id.ValueKind == JsonValueKind.Number)
            .Select(id => id.GetInt64())
            .ToList();

    private static async Task<SseStream> AskAsync(HttpClient client, object body)
    {
        using var response = await client.PostAsJsonAsync("/api/search/ask", body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        return ParseSse(await response.Content.ReadAsStringAsync());
    }

    private sealed record SseFrame(string Event, JsonElement Data);

    private sealed record SseStream(string Raw, IReadOnlyList<SseFrame> Frames);

    /// <summary>
    /// Parses the body the way a browser does. Comment frames (a line beginning <c>:</c>) are
    /// keepalives and carry no event, so they are skipped rather than parsed — a parser that
    /// choked on one would be asserting the opposite of what the keepalive is for.
    /// </summary>
    private static SseStream ParseSse(string raw)
    {
        var frames = new List<SseFrame>();
        var chunks = raw.Split("\n\n");
        for (var i = 0; i < chunks.Length; i++)
        {
            var chunk = chunks[i];
            if (i == chunks.Length - 1)
            {
                Assert.Equal(string.Empty, chunk);
                break;
            }

            if (chunk.StartsWith(':'))
            {
                continue;
            }

            var match = Regex.Match(
                chunk,
                @"^(id: (?<id>\d+)\n)?event: (?<event>[a-z][a-z-]*)\ndata: (?<data>\{.*\})$",
                RegexOptions.Singleline);
            Assert.True(match.Success, $"Malformed SSE frame: {chunk}");
            frames.Add(new SseFrame(
                match.Groups["event"].Value,
                JsonDocument.Parse(match.Groups["data"].Value).RootElement.Clone()));
        }

        return new SseStream(raw, frames);
    }

    /// <summary>
    /// A catalogue that answers concept discovery with a fixed set of movies and nothing else, so a
    /// turn through the real endpoint has a real, deterministic pool to rank. Everything beyond
    /// discovery answers empty: the relaxation ladder and the last-resort arm must not quietly
    /// re-introduce titles this test excluded.
    /// </summary>
    private sealed class ScriptedDiscoveryTmdbClient(IReadOnlyList<TmdbListItem> rows) : ITmdbClient
    {
        public Task<IReadOnlyList<TmdbKeyword>> SearchKeywordsAsync(string query, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TmdbKeyword>>([]);

        public Task<IReadOnlyList<TmdbListItem>> DiscoverByConceptAsync(
            bool movies, IReadOnlyList<int> genreIds, IReadOnlyList<int> keywordIds,
            string? region, DateTime? releasedAfter, int page,
            IReadOnlyList<int>? excludeGenreIds = null, IReadOnlyList<int>? excludeKeywordIds = null,
            DateTime? releasedBefore = null, int? maxRuntimeMinutes = null, TmdbDiscoverOptions? options = null,
            CancellationToken ct = default) =>
            Task.FromResult(movies ? rows : []);

        public Task<IReadOnlyList<TmdbListItem>> SearchTitlesAsync(bool movies, string query, int page, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TmdbListItem>>([]);

        public Task<TmdbPerson?> SearchPersonAsync(string name, CancellationToken ct = default) =>
            Task.FromResult<TmdbPerson?>(null);

        public Task<IReadOnlyList<TmdbListItem>> GetPersonCreditsAsync(long personId, PersonCreditRole role, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TmdbListItem>>([]);

        public Task<TmdbTitleDetails?> GetMovieAsync(long tmdbId, CancellationToken ct = default) =>
            Task.FromResult<TmdbTitleDetails?>(null);

        public Task<TmdbTitleDetails?> GetTvAsync(long tmdbId, CancellationToken ct = default) =>
            Task.FromResult<TmdbTitleDetails?>(null);

        public Task<TmdbDiscoverPage> DiscoverSeedPageAsync(bool movies, int year, int minVoteCount, int page, CancellationToken ct = default) =>
            Task.FromResult(new TmdbDiscoverPage([], page, 0, 0));

        public Task<IReadOnlyList<TmdbListItem>> DiscoverAsync(
            bool movies, int? genreId, string? region, DateTime? releasedAfter, int page, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TmdbListItem>>([]);

        public Task<IReadOnlyList<TmdbListItem>> GetTrendingAsync(bool movies, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TmdbListItem>>([]);

        public Task<IReadOnlyList<TmdbListItem>> GetRecommendationsAsync(bool movies, long tmdbId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TmdbListItem>>([]);

        public Task<IReadOnlyList<TmdbListItem>> GetSimilarAsync(bool movies, long tmdbId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TmdbListItem>>([]);

        public Task<IReadOnlyList<TmdbWatchProvider>> GetWatchProvidersAsync(long tmdbId, bool movie, string region, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TmdbWatchProvider>>([]);
    }
}
