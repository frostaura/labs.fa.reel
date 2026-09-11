using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FrostAura.Reel.Application.Auth;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Search;
using Microsoft.AspNetCore.Mvc.Testing.Handlers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FrostAura.Reel.Tests.IntegrationTests;

/// <summary>
/// R3-02 — what "Show me more" means after a Stop.
///
/// <para><b>The measured failure.</b> Settle an answer, ask a follow-up, press Stop, then press
/// "Show me more" on the kept answer. The client correctly re-sends the ANSWERED question — but
/// <c>kind</c> was derived by comparing it to the conversation's last USER turn, and the Stop had
/// made that the stopped question. So the server wrote a new <b>ask</b>, and three things broke at
/// once: the transcript gained a question nobody typed, the stopped turn stopped being the tail and
/// lost its failure row along with "That one didn't finish before you left", and the next resume
/// returned only the newest ask's block — 42 of the 66 cards on screen vanished on reload.</para>
///
/// <para><b>Two fixes, in the order they should be reached.</b> A client that is rendering the
/// answer it wants more of knows exactly which one it is, so it may say so: <c>pageOf</c> names the
/// assistant turn, and the server VERIFIES rather than trusts it — the caller's, in this
/// conversation, <c>Completed</c>, and answering exactly the question being re-sent. Where no
/// <c>pageOf</c> is sent the comparison still runs, but against the last ANSWERED ask rather than
/// the last one typed, because a turn nobody waited for is not the thing another page would be a
/// page OF.</para>
/// </summary>
[Collection("postgres")]
public class AskReelPagingTests(PostgresFixture fixture, ReelApiFactory api)
    : IClassFixture<ReelApiFactory>
{
    private const string Answered = "a 90s romantic comedy";

    private const string Stopped = "something tense set at sea";

    /// <summary>
    /// The repro, end to end. Every assertion here fails on the pre-<c>pageOf</c> endpoint, and
    /// fails for the right reason: the row is an <c>ask</c>, a user row appears beside it, and the
    /// stopped exchange is no longer the tail.
    /// </summary>
    [Fact]
    public async Task A_page_after_a_stop_extends_the_answered_turn_and_writes_no_user_row()
    {
        if (!fixture.Available)
        {
            return;
        }

        var (accountId, client) = await SignInAsync($"ask-page-stop-{Guid.NewGuid():N}");
        using (client)
        {
            var (conversationId, answeredTurnId, stoppedTurnId) = await SeedStoppedTailAsync(accountId);

            using var response = await client.PostAsJsonAsync("/api/search/ask", new
            {
                query = Answered,
                conversationId,
                clientTurnId = Guid.NewGuid(),
                pageOf = answeredTurnId,
            });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            await response.Content.ReadAsStringAsync();

            await using var db = ScopedContext(accountId);
            var turns = await db.SearchTurns
                .Where(t => t.ConversationId == conversationId)
                .OrderBy(t => t.Sequence)
                .ToListAsync();

            // Five rows, not six: a page adds the assistant row alone.
            Assert.Equal(5, turns.Count);
            Assert.Equal(2, turns.Count(t => t.Role == "user"));
            Assert.Equal([Answered, Stopped], turns.Where(t => t.Role == "user").Select(t => t.Text));

            var page = turns[^1];
            Assert.Equal("assistant", page.Role);
            Assert.Equal(SearchTurnKind.Page, page.Kind);

            // The stopped turn keeps its failure, which is what the transcript renders "That one
            // didn't finish before you left" from.
            var stopped = turns.Single(t => t.Id == stoppedTurnId);
            Assert.Equal(
                $"{SearchTurnStatus.Failed}/client_aborted",
                $"{stopped.Status}/{stopped.FailureCode}");
        }
    }

    /// <summary>
    /// The probe: one field removed. The same submission with no <c>pageOf</c> is still a page,
    /// because the inferred fallback now compares against the last ANSWERED ask. A client that has
    /// not learned the field yet is not punished for it.
    /// </summary>
    [Fact]
    public async Task Without_page_of_the_fallback_compares_against_the_last_answered_ask()
    {
        if (!fixture.Available)
        {
            return;
        }

        var (accountId, client) = await SignInAsync($"ask-page-fallback-{Guid.NewGuid():N}");
        using (client)
        {
            var (conversationId, _, _) = await SeedStoppedTailAsync(accountId);

            using var response = await client.PostAsJsonAsync("/api/search/ask", new
            {
                query = Answered,
                conversationId,
                clientTurnId = Guid.NewGuid(),
            });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            await response.Content.ReadAsStringAsync();

            await using var db = ScopedContext(accountId);
            var turns = await db.SearchTurns
                .Where(t => t.ConversationId == conversationId)
                .OrderBy(t => t.Sequence)
                .ToListAsync();

            Assert.Equal(5, turns.Count);
            Assert.Equal(SearchTurnKind.Page, turns[^1].Kind);
        }
    }

    /// <summary>
    /// The probe the other way: a genuinely NEW question after a Stop is still an ask, so the
    /// fallback has not been widened into "anything that matches anything".
    /// </summary>
    [Fact]
    public async Task A_new_question_after_a_stop_is_still_an_ask()
    {
        if (!fixture.Available)
        {
            return;
        }

        var (accountId, client) = await SignInAsync($"ask-page-newask-{Guid.NewGuid():N}");
        using (client)
        {
            var (conversationId, _, _) = await SeedStoppedTailAsync(accountId);

            using var response = await client.PostAsJsonAsync("/api/search/ask", new
            {
                query = "a quiet documentary about mountains",
                conversationId,
                clientTurnId = Guid.NewGuid(),
            });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            await response.Content.ReadAsStringAsync();

            await using var db = ScopedContext(accountId);
            var turns = await db.SearchTurns
                .Where(t => t.ConversationId == conversationId)
                .OrderBy(t => t.Sequence)
                .ToListAsync();

            Assert.Equal(6, turns.Count);
            Assert.Equal(SearchTurnKind.Ask, turns[^1].Kind);
            Assert.Equal("a quiet documentary about mountains", turns[^2].Text);
        }
    }

    /// <summary>
    /// A turn from another conversation is a 404, exactly as another account's conversation id is:
    /// "not yours", "not there" and "not in this thread" are one answer and none of them leaks which.
    /// </summary>
    [Fact]
    public async Task A_page_of_naming_a_turn_from_another_conversation_is_a_404()
    {
        if (!fixture.Available)
        {
            return;
        }

        var (accountId, client) = await SignInAsync($"ask-page-foreign-{Guid.NewGuid():N}");
        using (client)
        {
            var (conversationId, _, _) = await SeedStoppedTailAsync(accountId);
            var (_, otherTurnId, _) = await SeedStoppedTailAsync(accountId);

            using var response = await client.PostAsJsonAsync("/api/search/ask", new
            {
                query = Answered,
                conversationId,
                clientTurnId = Guid.NewGuid(),
                pageOf = otherTurnId,
            });

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            await AssertCodeAsync(response, "page_of_not_found");
            await AssertNoNewTurnAsync(accountId, conversationId, expected: 4);
        }
    }

    /// <summary>
    /// A page EXTENDS an answer, and a turn that failed has none. Appending to it would put cards
    /// under a row the transcript renders as a failure — which is the shape this whole fix exists
    /// to stop producing.
    /// </summary>
    [Fact]
    public async Task A_page_of_naming_a_failed_turn_is_a_409()
    {
        if (!fixture.Available)
        {
            return;
        }

        var (accountId, client) = await SignInAsync($"ask-page-failed-{Guid.NewGuid():N}");
        using (client)
        {
            var (conversationId, _, stoppedTurnId) = await SeedStoppedTailAsync(accountId);

            using var response = await client.PostAsJsonAsync("/api/search/ask", new
            {
                query = Stopped,
                conversationId,
                clientTurnId = Guid.NewGuid(),
                pageOf = stoppedTurnId,
            });

            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            await AssertCodeAsync(response, "page_of_not_completed");
            await AssertNoNewTurnAsync(accountId, conversationId, expected: 4);
        }
    }

    /// <summary>
    /// A page writes no user row, so a page carrying a DIFFERENT question would put a question into
    /// the thread that the transcript can never show and the next resume can never explain. The
    /// client is told to send it as a new question instead.
    /// </summary>
    [Fact]
    public async Task A_page_of_whose_query_is_not_that_turns_question_is_a_409()
    {
        if (!fixture.Available)
        {
            return;
        }

        var (accountId, client) = await SignInAsync($"ask-page-mismatch-{Guid.NewGuid():N}");
        using (client)
        {
            var (conversationId, answeredTurnId, _) = await SeedStoppedTailAsync(accountId);

            using var response = await client.PostAsJsonAsync("/api/search/ask", new
            {
                query = "a completely different question about westerns",
                conversationId,
                clientTurnId = Guid.NewGuid(),
                pageOf = answeredTurnId,
            });

            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            await AssertCodeAsync(response, "page_query_mismatch");
            await AssertNoNewTurnAsync(accountId, conversationId, expected: 4);
        }
    }

    /// <summary>Whitespace differs freely — the comparison is trimmed and ordinal, like every other.</summary>
    [Fact]
    public async Task A_page_of_tolerates_the_whitespace_a_composer_adds()
    {
        if (!fixture.Available)
        {
            return;
        }

        var (accountId, client) = await SignInAsync($"ask-page-trim-{Guid.NewGuid():N}");
        using (client)
        {
            var (conversationId, answeredTurnId, _) = await SeedStoppedTailAsync(accountId);

            using var response = await client.PostAsJsonAsync("/api/search/ask", new
            {
                query = $"  {Answered}  ",
                conversationId,
                clientTurnId = Guid.NewGuid(),
                pageOf = answeredTurnId,
            });

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            await response.Content.ReadAsStringAsync();
            await AssertNoNewTurnAsync(accountId, conversationId, expected: 5);
        }
    }

    /// <summary>A <c>pageOf</c> that is not a UUID is a coded 400, like every other malformed id.</summary>
    [Theory]
    [InlineData("\"not-a-uuid\"")]
    [InlineData("\"00000000-0000-0000-0000-000000000000\"")]
    [InlineData("12345")]
    public async Task A_malformed_page_of_is_a_coded_400(string rawJson)
    {
        if (!fixture.Available)
        {
            return;
        }

        var (_, client) = await SignInAsync($"ask-page-badid-{Guid.NewGuid():N}");
        using (client)
        {
            using var response = await client.PostAsync(
                "/api/search/ask",
                new StringContent(
                    $$"""
                      {"query":"{{Answered}}","clientTurnId":"{{Guid.NewGuid()}}","pageOf":{{rawJson}}}
                      """,
                    System.Text.Encoding.UTF8,
                    "application/json"));

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            await AssertCodeAsync(response, "page_of_invalid");
        }
    }

    /// <summary>
    /// The conversation exactly as a Stop leaves it: an answered exchange, then a question whose
    /// assistant row is <c>Failed/client_aborted</c>. Four rows, and the tail is the stopped one.
    /// </summary>
    private async Task<(Guid ConversationId, Guid AnsweredTurnId, Guid StoppedTurnId)> SeedStoppedTailAsync(
        Guid accountId)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var now = DateTime.UtcNow;
        var conversation = new SearchConversation
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            Title = Answered,
            CreatedAt = now,
            LastTurnAt = now,
            TurnCount = 4,
            LastCardsAt = now,
        };
        db.SearchConversations.Add(conversation);

        var answered = new SearchTurn
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            ConversationId = conversation.Id,
            Role = "assistant",
            Text = "Here is what I found.",
            CreatedAt = now,
            Sequence = 1,
            ClientTurnId = Guid.NewGuid(),
            Kind = SearchTurnKind.Ask,
            Status = SearchTurnStatus.Completed,
            Reason = "7 picks ranked for you.",
            CompletedAt = now,
        };
        var stopped = new SearchTurn
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            ConversationId = conversation.Id,
            Role = "assistant",
            Text = string.Empty,
            CreatedAt = now,
            Sequence = 3,
            ClientTurnId = Guid.NewGuid(),
            Kind = SearchTurnKind.Ask,
            Status = SearchTurnStatus.Failed,
            FailureCode = "client_aborted",
        };

        db.SearchTurns.AddRange(
            UserTurn(accountId, conversation.Id, 0, Answered, now),
            answered,
            UserTurn(accountId, conversation.Id, 2, Stopped, now),
            stopped);
        await db.SaveChangesAsync();
        return (conversation.Id, answered.Id, stopped.Id);
    }

    private static SearchTurn UserTurn(Guid accountId, Guid conversationId, int sequence, string text, DateTime now) =>
        new()
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            ConversationId = conversationId,
            Role = "user",
            Text = text,
            CreatedAt = now,
            Sequence = sequence,
            Kind = SearchTurnKind.Ask,
            Status = SearchTurnStatus.Completed,
            CompletedAt = now,
        };

    private static async Task AssertCodeAsync(HttpResponseMessage response, string expectedCode)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        using var payload = JsonDocument.Parse(body);
        Assert.Equal(expectedCode, payload.RootElement.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(payload.RootElement.GetProperty("error").GetString()));
        Assert.DoesNotContain("FrostAura.Reel", body, StringComparison.Ordinal);
    }

    private async Task AssertNoNewTurnAsync(Guid accountId, Guid conversationId, int expected)
    {
        await using var db = ScopedContext(accountId);
        Assert.Equal(expected, await db.SearchTurns.CountAsync(t => t.ConversationId == conversationId));
    }

    private Infrastructure.Persistence.ReelDbContext ScopedContext(Guid accountId)
    {
        var scope = new AccountContext();
        scope.SetAccount(accountId);
        return fixture.CreateContext(scope);
    }

    private async Task<(Guid AccountId, HttpClient Client)> SignInAsync(string slug)
    {
        var accountId = await api.SeedAccountAsync(slug);
        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Infrastructure.Persistence.ReelDbContext>();
        var account = await db.Accounts.IgnoreQueryFilters().FirstAsync(a => a.Id == accountId);
        var token = scope.ServiceProvider.GetRequiredService<SessionService>().MintAccessToken(account, DateTime.UtcNow);

        var cookies = new CookieContainerHandler();
        cookies.Container.Add(
            api.ClientOptions.BaseAddress,
            new System.Net.Cookie("reel_at", token, "/", api.ClientOptions.BaseAddress.Host));
        return (accountId, api.CreateDefaultClient(cookies));
    }
}
