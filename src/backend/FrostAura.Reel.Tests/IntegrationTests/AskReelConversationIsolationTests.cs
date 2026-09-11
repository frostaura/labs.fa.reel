using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using FrostAura.Reel.Application.Auth;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Library;
using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Domain.Search;
using FrostAura.Reel.Tests.Fakes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Mvc.Testing.Handlers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FrostAura.Reel.Tests.IntegrationTests;

/// <summary>
/// An interpretation belongs to the conversation it was made in, and must never be served into
/// another one.
///
/// <para><b>The turn that produced this class.</b> In one thread the founder had earlier said "only
/// shows", so the interpreter — which is handed the last turns of the transcript — read the next
/// sentence, "thrillers, no horror", as being about television: <c>MediaTypes: [Show]</c>, and a
/// reply promising "tense thriller shows". Correct: history is supposed to shape it. He then pressed
/// <b>+ New</b>, got a thread with no history at all, and typed the identical sentence. The stored
/// <c>IntentJson</c> came back <c>MediaTypes: [Show]</c> again, the reply was byte-identical, and
/// every card was TV. The fresh conversation had been answered out of the other thread's
/// context.</para>
///
/// <para><b>Why.</b> <c>LiveSearchExpansionService.InterpretKey</c> was a function of the QUESTION —
/// account, text, standing filters, media filter, model — and the interpretation is a function of
/// the question AND the conversation it sits in. Two different contexts therefore shared one cache
/// entry for the ten-minute TTL, and the second one to ask won.</para>
///
/// <para><b>What holds it now, and what must not regress.</b> The history that reaches the prompt is
/// digested onto the key, so a different transcript is a different entry — and an IDENTICAL
/// transcript is still the same entry, which is the ~21 s → ~2.5 s repeat the cache was built for.
/// Both facts are asserted here, because a fix that made every turn a miss would satisfy the first
/// and silently throw away the second.</para>
/// </summary>
/// <remarks>
/// <para>The catalogue is <see cref="SilentTmdbClient"/> and every pre-existing title is marked seen,
/// so no turn in this class produces a card. That is deliberate and load-bearing: cards land in the
/// conversation's no-repeat set, which is ALSO on the interpret key, so a test whose two
/// conversations differed in their shown sets would pass on either half of the fix. With no cards
/// anywhere, both conversations carry an empty shown set and the transcript is the only difference
/// between them.</para>
///
/// <para>Inherits <see cref="EmbeddedCatalogTestBase"/> for the collection's shared-HNSW discipline
/// even though a card-less turn embeds no title — the base class is the rule for anything in the
/// <c>postgres</c> collection that runs the real pipeline, and a future edit that starts returning
/// rows must not have to remember it.</para>
/// </remarks>
[Collection("postgres")]
public class AskReelConversationIsolationTests(PostgresFixture fixture, ReelApiFactory api)
    : EmbeddedCatalogTestBase(fixture), IClassFixture<ReelApiFactory>
{
    /// <summary>
    /// The founder's repro, through the real endpoint: a shaped thread, then <b>+ New</b> and the
    /// same sentence. The fresh thread must get the unshaped answer.
    /// </summary>
    [Fact]
    public async Task A_fresh_conversation_does_not_inherit_another_threads_interpretation()
    {
        if (!Fixture.Available)
        {
            return;
        }

        var agent = new HistoryShapedSearchAgent();
        using var factory = WithAgent(agent);
        var (accountId, client) = await SignInAsync(factory, $"ask-leak-{Guid.NewGuid():N}");
        using (client)
        {
            await MarkExistingTitlesSeenAsync(accountId);

            // Thread A, turn 1 — the sentence that shapes everything after it in this thread.
            var opener = await AskAsync(client, new
            {
                query = $"{HistoryShapedSearchAgent.ShowsOnlyMarker}, please",
                clientTurnId = Guid.NewGuid(),
            });
            var threadA = ConversationId(opener);
            Assert.Equal(HistoryShapedSearchAgent.UnshapedReply, AssistantText(opener));

            // Thread A, turn 2 — the same sentence the fresh thread will ask. History shapes it,
            // which is correct and is the entry that used to leak.
            var shaped = await AskAsync(client, new
            {
                query = "thrillers, no horror",
                conversationId = threadA,
                clientTurnId = Guid.NewGuid(),
            });
            Assert.Equal(HistoryShapedSearchAgent.ShowsReply, AssistantText(shaped));

            // + New. Same account, same sentence, no history.
            var fresh = await AskAsync(client, new
            {
                query = "thrillers, no horror",
                clientTurnId = Guid.NewGuid(),
            });
            var threadB = ConversationId(fresh);
            Assert.NotEqual(threadA, threadB);

            // On the wire — the sentence the person actually reads.
            Assert.Equal(HistoryShapedSearchAgent.UnshapedReply, AssistantText(fresh));

            // And in the record, which is what a reload and every downstream read see.
            Assert.Equal(HistoryShapedSearchAgent.UnshapedReply, await StoredReplyAsync(accountId, threadB));

            // Three distinct contexts, three interpretations: nothing was served from another
            // thread's entry.
            Assert.Equal(3, agent.Calls);
        }
    }

    /// <summary>
    /// The other half, and the reason the key is a digest of the history rather than simply the
    /// conversation id: two threads that are genuinely the SAME context — the same question with
    /// the same (empty) transcript behind it — still share one interpretation.
    ///
    /// <para>This is the win the cache exists for. A byte-identical repeat used to re-buy the
    /// interpret call, then re-pull TMDB behind a drifted intent, and the founder measured the
    /// difference at roughly 21 s versus 2.5 s. Keying on the conversation ID would have fixed the
    /// leak above and thrown this away, because a new thread has a new id however identical its
    /// context is.</para>
    /// </summary>
    [Fact]
    public async Task Two_fresh_conversations_asking_the_same_thing_still_share_one_interpretation()
    {
        if (!Fixture.Available)
        {
            return;
        }

        var agent = new HistoryShapedSearchAgent();
        using var factory = WithAgent(agent);
        var (accountId, client) = await SignInAsync(factory, $"ask-repeat-{Guid.NewGuid():N}");
        using (client)
        {
            await MarkExistingTitlesSeenAsync(accountId);

            var first = await AskAsync(client, new
            {
                query = "thrillers, no horror",
                clientTurnId = Guid.NewGuid(),
            });
            var second = await AskAsync(client, new
            {
                query = "thrillers, no horror",
                clientTurnId = Guid.NewGuid(),
            });

            Assert.NotEqual(ConversationId(first), ConversationId(second));
            Assert.Equal(AssistantText(first), AssistantText(second));
            Assert.Equal(1, agent.Calls);
        }
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The real host with the interpreter and the catalogue swapped. Both have to go: the stock
    /// stub agent answers every sentence identically, which makes a leaked interpretation
    /// indistinguishable from a correct one, and the real <see cref="ITmdbClient"/> in this harness
    /// reaches <c>api.themoviedb.org</c> with a blank bearer.
    /// </summary>
    private WebApplicationFactory<Program> WithAgent(ISearchAgent agent) =>
        api.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<ISearchAgent>();
            services.AddSingleton(agent);
            services.RemoveAll<ITmdbClient>();
            services.AddSingleton<ITmdbClient, SilentTmdbClient>();
        }));

    /// <summary>
    /// A seeded account plus a client carrying its session cookie, against the SWAPPED host.
    /// <c>WithWebHostBuilder</c> hands back a plain factory, so the token is minted by the base
    /// host's own <see cref="SessionService"/> — same secret, same database, so both accept it.
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

    /// <summary>The assistant text the conversation's single answered turn actually stored.</summary>
    private async Task<string> StoredReplyAsync(Guid accountId, Guid conversationId)
    {
        var scope = new AccountContext();
        scope.SetAccount(accountId);
        await using var db = Fixture.CreateContext(scope);
        return await db.SearchTurns
            .Where(t => t.ConversationId == conversationId && t.Role == "assistant")
            .OrderByDescending(t => t.Sequence)
            .Select(t => t.Text)
            .FirstAsync();
    }

    private static Guid ConversationId(SseStream stream) =>
        Assert.Single(stream.Frames, f => f.Event == "conversation").Data.GetProperty("id").GetGuid();

    private static string AssistantText(SseStream stream) =>
        Assert.Single(stream.Frames, f => f.Event == "assistant-message").Data.GetProperty("text").GetString()!;

    private static async Task<SseStream> AskAsync(HttpClient client, object body)
    {
        using var response = await client.PostAsJsonAsync("/api/search/ask", body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        return ParseSse(await response.Content.ReadAsStringAsync());
    }

    private sealed record SseFrame(string Event, JsonElement Data);

    private sealed record SseStream(string Raw, IReadOnlyList<SseFrame> Frames);

    /// <summary>Parses the body the way a browser does; comment frames are keepalives and are skipped.</summary>
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
}
