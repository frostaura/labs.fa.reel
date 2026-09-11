using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FrostAura.Reel.Application.Auth;
using FrostAura.Reel.Application.Jobs;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Search;
using Microsoft.AspNetCore.Mvc.Testing.Handlers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FrostAura.Reel.Tests.IntegrationTests;

/// <summary>
/// The wedged conversation, and the two arms that unwedge it.
///
/// <para><b>The measured failure.</b> A turn was streaming on conversation
/// <c>4aadb3b1-fa38-497c-8928-cd04eb9df0af</c> — <c>SearchTurns</c> sequence 9, assistant row
/// Pending, "a claustrophobic submarine thriller". The API process was killed mid-stream and came
/// back twenty seconds later. Minutes afterwards the row was still Pending, <c>CompletedAt</c> null,
/// <c>FailureCode</c> null — because both writers of a terminal status live in the request pipeline,
/// and the request they belonged to no longer exists anywhere. <c>/ask</c> refuses a new turn while
/// one is Pending on the conversation, so the thread was finished: every retry, every "Try again",
/// every "Check again" and every new question in it answered <c>409 turn_in_progress</c>, and
/// <c>GET /turns/{id}</c> answered <c>202</c> for as long as anyone cared to poll. Not degraded —
/// <b>permanent</b>, with no path anywhere in the system that would ever close the row.</para>
///
/// <para><b>Why two arms and not one.</b> Boot is the only instant where "Pending" is unambiguous:
/// the process that just started owns no streams, so every Pending row belongs to a process that is
/// gone, and the sweep may say <c>server_restarted</c> as a FACT with no clock involved. But a boot
/// that never happens sweeps nothing, so the gate carries a clock-based
/// <c>turn_timed_out</c> guard as well. Each arm alone leaves a reachable wedge; the pair does
/// not.</para>
///
/// <para>Every test here fails against the pre-fix endpoint, and fails for the right reason: the
/// ask is a 409, the replay is a 409, and the poll is a 202 — forever.</para>
/// </summary>
[Collection("postgres")]
public class AskReelStalePendingTests(PostgresFixture fixture, ReelApiFactory api)
    : IClassFixture<ReelApiFactory>
{
    /// <summary>The question the dead turn was answering when the process went away.</summary>
    private const string Hanging = "a claustrophobic submarine thriller";

    /// <summary>The question that was answered before it, so the conversation has a real tail.</summary>
    private const string Answered = "a 90s romantic comedy";

    /// <summary>
    /// Comfortably past the threshold. One minute of slack rather than a millisecond, so the test
    /// is not asserting a boundary the production constant is deliberately generous about.
    /// </summary>
    private static readonly TimeSpan PastTheThreshold =
        SearchTurnFailure.StalePendingAfter + TimeSpan.FromMinutes(1);

    // ── The gate ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The repro, from the user's side: a new question in a thread whose last turn died with the
    /// process. It is answered rather than refused, and the corpse is closed on the way past.
    /// </summary>
    [Fact]
    public async Task A_pending_turn_past_the_threshold_does_not_block_a_new_ask()
    {
        if (!fixture.Available)
        {
            return;
        }

        var (accountId, client) = await SignInAsync($"ask-stale-new-{Guid.NewGuid():N}");
        using (client)
        {
            var (conversationId, wedgedTurnId, _) = await SeedWedgedConversationAsync(accountId, PastTheThreshold);

            using var response = await client.PostAsJsonAsync("/api/search/ask", new
            {
                query = "something with a clever ending",
                conversationId,
                clientTurnId = Guid.NewGuid(),
            });

            // The whole defect in one assertion: this was 409 turn_in_progress, and stayed 409.
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            await response.Content.ReadAsStringAsync();

            await using var db = ScopedContext(accountId);
            var wedged = await db.SearchTurns.AsNoTracking().FirstAsync(t => t.Id == wedgedTurnId);
            Assert.Equal(
                $"{SearchTurnStatus.Failed}/{SearchTurnFailure.TurnTimedOut}",
                $"{wedged.Status}/{wedged.FailureCode}");
            // A terminal row without a terminal timestamp is the same half-record the Pending row
            // was: it reads as unfinished to anything that looks at CompletedAt rather than Status.
            Assert.NotNull(wedged.CompletedAt);

            // And the question actually bought a turn, rather than merely not being refused.
            Assert.Contains(
                await db.SearchTurns.AsNoTracking()
                    .Where(t => t.ConversationId == conversationId && t.Role == "assistant")
                    .Select(t => t.Id)
                    .ToListAsync(),
                id => id != wedgedTurnId);
        }
    }

    /// <summary>
    /// The probe: one field flipped. The SAME seeded conversation with the Pending row written
    /// <i>now</i> instead of four minutes ago is still a 409, and the row is left exactly as it was.
    ///
    /// <para>This is the test that makes the one above mean something. A guard that expired every
    /// Pending row would pass <see cref="A_pending_turn_past_the_threshold_does_not_block_a_new_ask"/>
    /// perfectly and would let two streams write cards under one turn id, colliding on the unique
    /// <c>(TurnId, Rank)</c> — the exact failure the 409 was written to prevent. The age is the only
    /// thing separating the two cases, so the age is the only thing this probe changes.</para>
    /// </summary>
    [Fact]
    public async Task A_pending_turn_inside_the_threshold_still_refuses_the_next_ask()
    {
        if (!fixture.Available)
        {
            return;
        }

        var (accountId, client) = await SignInAsync($"ask-stale-fresh-{Guid.NewGuid():N}");
        using (client)
        {
            var (conversationId, wedgedTurnId, _) = await SeedWedgedConversationAsync(accountId, TimeSpan.Zero);

            using var response = await client.PostAsJsonAsync("/api/search/ask", new
            {
                query = "something with a clever ending",
                conversationId,
                clientTurnId = Guid.NewGuid(),
            });

            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal("turn_in_progress", payload.RootElement.GetProperty("code").GetString());

            await using var db = ScopedContext(accountId);
            var live = await db.SearchTurns.AsNoTracking().FirstAsync(t => t.Id == wedgedTurnId);
            Assert.Equal(SearchTurnStatus.Pending, live.Status);
            Assert.Null(live.FailureCode);
            Assert.Null(live.CompletedAt);
        }
    }

    /// <summary>
    /// "Try again" on the dead turn itself — the same <c>clientTurnId</c>, which is what the client
    /// resends by construction. It proceeds rather than replaying the corpse or refusing.
    ///
    /// <para>The key is released as the row is closed. <c>(ConversationId, ClientTurnId)</c> is a
    /// partial UNIQUE index, so a corpse that kept its key would collide with the retry's own
    /// assistant row on insert — turning a wedged thread into a 500, which is a worse answer to the
    /// same question. The dead row stays in the transcript as <c>Failed/turn_timed_out</c>: it was
    /// asked and it was not answered, and that is what a transcript is for.</para>
    /// </summary>
    [Fact]
    public async Task The_same_client_turn_id_on_a_stale_pending_turn_proceeds()
    {
        if (!fixture.Available)
        {
            return;
        }

        var (accountId, client) = await SignInAsync($"ask-stale-retry-{Guid.NewGuid():N}");
        using (client)
        {
            var (conversationId, wedgedTurnId, clientTurnId) =
                await SeedWedgedConversationAsync(accountId, PastTheThreshold);

            using var response = await client.PostAsJsonAsync("/api/search/ask", new
            {
                query = Hanging,
                conversationId,
                clientTurnId,
            });

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            await response.Content.ReadAsStringAsync();

            await using var db = ScopedContext(accountId);
            var wedged = await db.SearchTurns.AsNoTracking().FirstAsync(t => t.Id == wedgedTurnId);
            Assert.Equal(
                $"{SearchTurnStatus.Failed}/{SearchTurnFailure.TurnTimedOut}",
                $"{wedged.Status}/{wedged.FailureCode}");
            Assert.Null(wedged.ClientTurnId);

            // The key now belongs to the turn that actually ran, and to exactly one row — which is
            // the property the partial unique index exists to hold.
            var carrying = await db.SearchTurns.AsNoTracking()
                .Where(t => t.ConversationId == conversationId && t.ClientTurnId == clientTurnId)
                .ToListAsync();
            Assert.Single(carrying);
            Assert.NotEqual(wedgedTurnId, carrying[0].Id);
        }
    }

    /// <summary>
    /// The reconnect poll. A client that lost the stream asks the turn what happened to it, and a
    /// turn whose process is gone answers that rather than <c>202</c> — which is an instruction to
    /// poll again, and was one this client would have followed until the tab closed.
    ///
    /// <para>The row is closed by the poll, not merely described as closed: a turn the poller has
    /// been told is Failed while the table still says Pending reads as in-flight to every other
    /// surface, and the thread stays shut.</para>
    /// </summary>
    [Fact]
    public async Task Polling_a_stale_pending_turn_reports_it_failed_rather_than_still_running()
    {
        if (!fixture.Available)
        {
            return;
        }

        var (accountId, client) = await SignInAsync($"ask-stale-poll-{Guid.NewGuid():N}");
        using (client)
        {
            var (_, wedgedTurnId, _) = await SeedWedgedConversationAsync(accountId, PastTheThreshold);

            using var response = await client.GetAsync($"/api/search/turns/{wedgedTurnId}");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(
                $"{SearchTurnStatus.Failed}/{SearchTurnFailure.TurnTimedOut}",
                $"{payload.RootElement.GetProperty("status").GetString()}/"
                    + $"{payload.RootElement.GetProperty("failureCode").GetString()}");
            // A failed turn stores no cards, and the poll's shape does not change because of it.
            Assert.Equal(0, payload.RootElement.GetProperty("cards").GetArrayLength());
            Assert.Equal(0, payload.RootElement.GetProperty("belowFloor").GetArrayLength());

            await using var db = ScopedContext(accountId);
            var wedged = await db.SearchTurns.AsNoTracking().FirstAsync(t => t.Id == wedgedTurnId);
            Assert.Equal(SearchTurnStatus.Failed, wedged.Status);
            Assert.NotNull(wedged.CompletedAt);
        }
    }

    // ── The boot sweep ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The startup arm: every Pending row, across every account, closed as
    /// <c>server_restarted</c> — and nothing else touched.
    ///
    /// <para><b>Two accounts, deliberately.</b> The sweep runs with no request and therefore no
    /// account, while every <c>IAccountScoped</c> entity carries a global query filter bound to one.
    /// Today that filter is permissive when the context holds no account, so a sweep written without
    /// <c>IgnoreQueryFilters</c> would pass a single-account test and quietly close only the
    /// founder's rows the moment the filter is tightened. This runs the sweep through a context
    /// scoped to account A and asserts account B's row went too, which is a claim the permissive
    /// filter cannot satisfy by accident.</para>
    ///
    /// <para>The return count is asserted as a floor rather than an equality: the sweep is global by
    /// design, and the suite's own database may hold Pending rows another class seeded.</para>
    /// </summary>
    [Fact]
    public async Task The_startup_reconciliation_closes_every_pending_turn_across_accounts()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountA = await api.SeedAccountAsync($"ask-restart-a-{Guid.NewGuid():N}");
        var accountB = await api.SeedAccountAsync($"ask-restart-b-{Guid.NewGuid():N}");

        var (_, pendingA, _) = await SeedWedgedConversationAsync(accountA, TimeSpan.Zero);
        var (conversationB, pendingB, _) = await SeedWedgedConversationAsync(accountB, PastTheThreshold);

        // The two rows that must survive. A boot that closed finished turns would rewrite history,
        // and a cause that outlives the failure it described reads as current — so the aborted row's
        // own code has to still be its own code afterwards.
        var completedB = await CompletedAssistantTurnIdAsync(conversationB, accountB, SearchTurnStatus.Completed);
        var abortedB = await SeedAbortedTurnAsync(accountB, conversationB);

        int closed;
        await using (var sweepContext = ScopedContext(accountA))
        {
            closed = await PendingTurnReconciliation.ReconcileAsync(sweepContext, DateTime.UtcNow);
        }

        Assert.True(closed >= 2, $"the sweep closed {closed} row(s); it had at least two to close.");

        await using var db = fixture.CreateContext(new AccountContext());
        foreach (var turnId in new[] { pendingA, pendingB })
        {
            var closedTurn = await db.SearchTurns.IgnoreQueryFilters().AsNoTracking()
                .FirstAsync(t => t.Id == turnId);
            Assert.Equal(
                $"{SearchTurnStatus.Failed}/{SearchTurnFailure.ServerRestarted}",
                $"{closedTurn.Status}/{closedTurn.FailureCode}");
            Assert.NotNull(closedTurn.CompletedAt);
        }

        // Age is not a factor on this arm, and that is the point of it: pendingA was written this
        // instant and is still closed, because the process that owned its stream is gone by
        // definition. The gate's threshold answers a different question.
        var untouchedCompleted = await db.SearchTurns.IgnoreQueryFilters().AsNoTracking()
            .FirstAsync(t => t.Id == completedB);
        Assert.Equal(SearchTurnStatus.Completed, untouchedCompleted.Status);
        Assert.Null(untouchedCompleted.FailureCode);

        var untouchedAborted = await db.SearchTurns.IgnoreQueryFilters().AsNoTracking()
            .FirstAsync(t => t.Id == abortedB);
        Assert.Equal(
            $"{SearchTurnStatus.Failed}/{SearchTurnFailure.ClientAborted}",
            $"{untouchedAborted.Status}/{untouchedAborted.FailureCode}");
    }

    /// <summary>An empty table is a no-op that returns zero, not a throw and not a full-table write.</summary>
    [Fact]
    public async Task The_startup_reconciliation_is_a_no_op_when_nothing_is_pending()
    {
        if (!fixture.Available)
        {
            return;
        }

        await using var db = fixture.CreateContext(new AccountContext());
        await PendingTurnReconciliation.ReconcileAsync(db, DateTime.UtcNow);

        // Second pass over a table the first pass just emptied of Pending rows.
        Assert.Equal(0, await PendingTurnReconciliation.ReconcileAsync(db, DateTime.UtcNow));
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A conversation shaped exactly like the reproduced one: an answered exchange, then a question
    /// whose assistant row is still <c>Pending</c>.
    ///
    /// <para><paramref name="age"/> is the ONLY thing that varies between the stale probe and the
    /// live one — the rows, the sequences, the texts and the keys are identical — so a difference in
    /// outcome can only be the age.</para>
    /// </summary>
    private async Task<(Guid ConversationId, Guid PendingTurnId, Guid ClientTurnId)> SeedWedgedConversationAsync(
        Guid accountId, TimeSpan age)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var now = DateTime.UtcNow;
        var pendingAt = now - age;
        var conversation = new SearchConversation
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            Title = Answered,
            CreatedAt = now,
            LastTurnAt = now,
            TurnCount = 4,
        };
        db.SearchConversations.Add(conversation);

        var pending = new SearchTurn
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            ConversationId = conversation.Id,
            Role = "assistant",
            Text = string.Empty,
            CreatedAt = pendingAt,
            Sequence = 3,
            ClientTurnId = Guid.NewGuid(),
            Kind = SearchTurnKind.Ask,
            Status = SearchTurnStatus.Pending,
        };

        db.SearchTurns.AddRange(
            Turn(accountId, conversation.Id, 0, "user", Answered, now, SearchTurnStatus.Completed),
            Turn(accountId, conversation.Id, 1, "assistant", "Here is what I found.", now, SearchTurnStatus.Completed),
            Turn(accountId, conversation.Id, 2, "user", Hanging, pendingAt, SearchTurnStatus.Completed),
            pending);
        await db.SaveChangesAsync();
        return (conversation.Id, pending.Id, pending.ClientTurnId!.Value);
    }

    /// <summary>A turn the user stopped themselves — the row the sweep must not rewrite.</summary>
    private async Task<Guid> SeedAbortedTurnAsync(Guid accountId, Guid conversationId)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var aborted = Turn(
            accountId, conversationId, 5, "assistant", string.Empty, DateTime.UtcNow, SearchTurnStatus.Failed);
        aborted.FailureCode = SearchTurnFailure.ClientAborted;
        aborted.ClientTurnId = Guid.NewGuid();
        db.SearchTurns.Add(aborted);
        await db.SaveChangesAsync();
        return aborted.Id;
    }

    /// <summary>The id of the conversation's first turn in <paramref name="status"/>.</summary>
    private async Task<Guid> CompletedAssistantTurnIdAsync(Guid conversationId, Guid accountId, string status)
    {
        await using var db = ScopedContext(accountId);
        return await db.SearchTurns.AsNoTracking()
            .Where(t => t.ConversationId == conversationId && t.Status == status && t.Role == "assistant")
            .Select(t => t.Id)
            .FirstAsync();
    }

    private static SearchTurn Turn(
        Guid accountId, Guid conversationId, int sequence, string role, string text, DateTime at, string status) =>
        new()
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            ConversationId = conversationId,
            Role = role,
            Text = text,
            CreatedAt = at,
            Sequence = sequence,
            Kind = SearchTurnKind.Ask,
            Status = status,
            CompletedAt = status == SearchTurnStatus.Pending ? null : at,
        };

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
            new Cookie("reel_at", token, "/", api.ClientOptions.BaseAddress.Host));
        return (accountId, api.CreateDefaultClient(cookies));
    }
}
