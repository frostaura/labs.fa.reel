using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Search;
using FrostAura.Reel.Domain.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace FrostAura.Reel.Tests.IntegrationTests;

/// <summary>
/// What a replay is allowed to be: the same answer, in the same shape, to the SAME question.
///
/// <para>Two things were wrong with it. Its <c>done</c> carried a different key set from a live
/// turn's — no <c>timingsMs</c>, no <c>debug</c>, four extra fields — so one event name had three
/// shapes and every consumer of the terminal frame had an <c>undefined</c> to guard. And it did not
/// look at the question at all: re-sending a key with a completely different sentence returned the
/// first sentence's answer in thirty milliseconds, <c>replayed: true</c>, with nothing anywhere
/// saying the question had changed. The key is the key — but it is the key to one exchange, and a
/// product answering the wrong question with total confidence is the worst failure mode this
/// endpoint has.</para>
/// </summary>
[Collection("postgres")]
public class AskReelReplayContractTests(PostgresFixture fixture, ReelApiFactory api)
    : IClassFixture<ReelApiFactory>
{
    private const string SeededQuestion = "a gripping crime drama with superb acting";

    /// <summary>
    /// The documented terminal-frame keys, plus the three a replay adds. A live <c>done</c> carries
    /// the first eight; the contract's rule is that <c>timingsMs</c> and <c>debug</c> are ALWAYS
    /// present so the client renders one shape.
    /// </summary>
    private static readonly string[] LiveDoneKeys =
        ["results", "streamed", "kept", "droppedByFloor", "belowFloor", "reason", "timingsMs", "debug"];

    [Fact]
    public async Task A_replay_carries_every_key_a_live_done_carries()
    {
        if (!fixture.Available)
        {
            return;
        }

        var (accountId, client) = await SignInAsync($"replay-shape-{Guid.NewGuid():N}");
        using (client)
        {
            var (conversationId, clientTurnId) = await SeedCompletedTurnAsync(accountId, cards: 3);

            var done = await ReplayDoneAsync(client, conversationId, clientTurnId, SeededQuestion);

            foreach (var key in LiveDoneKeys)
            {
                Assert.True(done.TryGetProperty(key, out _), $"the replay's done is missing '{key}'.");
            }

            // Present and null for a caller with no founder entitlement — the same gating a live
            // turn applies, so the key's presence leaks nothing.
            Assert.Equal(JsonValueKind.Null, done.GetProperty("timingsMs").ValueKind);
            Assert.Equal(JsonValueKind.Null, done.GetProperty("debug").ValueKind);

            // And the three a replay is entitled to add, because they are facts a live turn has no
            // way to state.
            Assert.True(done.GetProperty("replayed").GetBoolean());
            Assert.Equal(0, done.GetProperty("removed").GetInt32());
            Assert.False(done.GetProperty("failed").GetBoolean());
        }
    }

    /// <summary>
    /// F5-09. The measured failure: A1's key re-sent with "a completely different question about
    /// westerns" returned A1's crime dramas, in 26 ms, flagged as a replay and otherwise
    /// indistinguishable from a real answer.
    /// </summary>
    [Fact]
    public async Task A_key_reused_for_a_different_question_is_refused_rather_than_answered()
    {
        if (!fixture.Available)
        {
            return;
        }

        var (accountId, client) = await SignInAsync($"replay-reuse-{Guid.NewGuid():N}");
        using (client)
        {
            var (conversationId, clientTurnId) = await SeedCompletedTurnAsync(accountId, cards: 3);
            var turnsBefore = await CountTurnsAsync(accountId, conversationId);

            using var response = await client.PostAsJsonAsync("/api/search/ask", new
            {
                query = "a completely different question about westerns",
                conversationId,
                clientTurnId,
            });

            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal("client_turn_id_reused", payload.RootElement.GetProperty("code").GetString());

            // A refusal writes nothing — the stored turn is left exactly as it was.
            Assert.Equal(turnsBefore, await CountTurnsAsync(accountId, conversationId));
        }
    }

    /// <summary>
    /// The probe: the same key, the same question, differing only by the whitespace the composer
    /// leaves behind. That is a retry — a dropped connection, a StrictMode double-invoke, an
    /// impatient second tap — and it must still replay, or the guard above has turned idempotency
    /// off.
    /// </summary>
    [Fact]
    public async Task The_same_question_with_different_whitespace_still_replays()
    {
        if (!fixture.Available)
        {
            return;
        }

        var (accountId, client) = await SignInAsync($"replay-retry-{Guid.NewGuid():N}");
        using (client)
        {
            var (conversationId, clientTurnId) = await SeedCompletedTurnAsync(accountId, cards: 3);

            var done = await ReplayDoneAsync(client, conversationId, clientTurnId, $"  {SeededQuestion}  ");

            Assert.True(done.GetProperty("replayed").GetBoolean());
            Assert.Equal(3, done.GetProperty("results").GetArrayLength());
        }
    }

    /// <summary>
    /// F5-10, at the endpoint. A conversation that received the same sentence in different case
    /// stored ONE user turn and answered the second submission as a <c>page</c> — a fresh grid with
    /// no question recorded, and a resumed transcript showing a single ask where two had been made.
    /// Capitals are something a person typed on purpose; the conservative reading of "did they ask
    /// again" is the one that records the asking.
    /// </summary>
    [Fact]
    public async Task A_retyped_question_in_different_case_is_a_new_ask()
    {
        if (!fixture.Available)
        {
            return;
        }

        var (accountId, client) = await SignInAsync($"replay-case-{Guid.NewGuid():N}");
        using (client)
        {
            var (conversationId, _) = await SeedCompletedTurnAsync(accountId, cards: 3);

            // A live turn through the harness reaches no catalogue, so it FAILS — which is fine and
            // is not what this asserts: kind is derived, and both rows are written, before the
            // stream opens at all.
            using var response = await client.PostAsJsonAsync("/api/search/ask", new
            {
                query = SeededQuestion.ToUpperInvariant(),
                conversationId,
                clientTurnId = Guid.NewGuid(),
            });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            await using var db = ScopedContext(accountId);
            var turns = await db.SearchTurns
                .Where(t => t.ConversationId == conversationId)
                .OrderBy(t => t.Sequence)
                .Select(t => new { t.Role, t.Kind, t.Text })
                .ToListAsync();

            // Two user rows, two questions. Before this, the capitalised submission wrote one
            // assistant row of Kind='page' and the transcript lost the question entirely.
            Assert.Equal(2, turns.Count(t => t.Role == "user"));
            Assert.Equal(SearchTurnKind.Ask, turns[^1].Kind);
            Assert.Contains(turns, t => t.Role == "user" && t.Text == SeededQuestion.ToUpperInvariant());
        }
    }

    /// <summary>
    /// F4-06, the read half. The client restores the notice and the "under your conditions" pill
    /// from the same payload that restores the cards, on both routes that hand a stored turn back —
    /// and as a JSON OBJECT, not a string, so nobody parses it twice.
    /// </summary>
    [Fact]
    public async Task A_resumed_thread_and_a_reconnected_turn_both_carry_the_stored_notice()
    {
        if (!fixture.Available)
        {
            return;
        }

        var (accountId, client) = await SignInAsync($"replay-conflict-{Guid.NewGuid():N}");
        using (client)
        {
            var (conversationId, _) = await SeedCompletedTurnAsync(accountId, cards: 2, conflict: StoredConflict);

            using var resume = await client.GetAsync($"/api/search/conversations/{conversationId}");
            Assert.Equal(HttpStatusCode.OK, resume.StatusCode);
            using var resumed = JsonDocument.Parse(await resume.Content.ReadAsStringAsync());
            AssertNotice(resumed.RootElement.GetProperty("conflict"));

            var turnId = await NewestAssistantTurnIdAsync(accountId, conversationId);
            using var reconnect = await client.GetAsync($"/api/search/turns/{turnId}");
            Assert.Equal(HttpStatusCode.OK, reconnect.StatusCode);
            using var turn = JsonDocument.Parse(await reconnect.Content.ReadAsStringAsync());
            AssertNotice(turn.RootElement.GetProperty("conflict"));
        }
    }

    /// <summary>The probe: a thread that had no collision carries a null, not an empty object.</summary>
    [Fact]
    public async Task A_thread_with_no_collision_resumes_with_a_null_notice()
    {
        if (!fixture.Available)
        {
            return;
        }

        var (accountId, client) = await SignInAsync($"replay-no-conflict-{Guid.NewGuid():N}");
        using (client)
        {
            var (conversationId, _) = await SeedCompletedTurnAsync(accountId, cards: 2);

            using var resume = await client.GetAsync($"/api/search/conversations/{conversationId}");
            using var resumed = JsonDocument.Parse(await resume.Content.ReadAsStringAsync());

            Assert.Equal(JsonValueKind.Null, resumed.RootElement.GetProperty("conflict").ValueKind);
        }
    }

    /// <summary>
    /// IR1S-05: a resumed thread shows a stored why-line only where a new turn would. Lines written
    /// before today's guards carried hedges on pick cards ("…, though the show leans on mystery"),
    /// and opening Recent rendered them. Varied over the verdict and the tray: a hedged line on a
    /// "yes" pick, on a row older than verdicts, and on a held-back match is blanked; a clean line
    /// stays; a partial verdict's honest negative stays in the near-miss tray; a third-person leak
    /// goes whatever the verdict. Both resume routes are asserted, since both project the same rows.
    /// </summary>
    [Fact]
    public async Task A_resumed_thread_never_replays_a_line_a_new_turn_would_discard()
    {
        if (!fixture.Available)
        {
            return;
        }

        const string Hedged = "A slow-burn mystery with your kind of payoff, though the show leans on procedure.";
        const string Clean = "A slow-burn sci-fi mystery that rewards your patience.";
        const string Leak = "A slow-burn mystery that fits their taste for puzzles.";
        var (accountId, client) = await SignInAsync($"replay-hedge-{Guid.NewGuid():N}");
        using (client)
        {
            var (conversationId, _) = await SeedCompletedTurnAsync(accountId, cards: 0, shaped:
            [
                (Hedged, "yes", SearchTurnCardTier.Pick),
                (Clean, "yes", SearchTurnCardTier.Pick),
                (Hedged, null, SearchTurnCardTier.Pick),
                (Hedged, "yes", SearchTurnCardTier.BelowFloor),
                (Hedged, "partial", SearchTurnCardTier.NearMiss),
                (Leak, "partial", SearchTurnCardTier.NearMiss),
            ]);

            using var resume = await client.GetAsync($"/api/search/conversations/{conversationId}");
            Assert.Equal(HttpStatusCode.OK, resume.StatusCode);
            using var resumed = JsonDocument.Parse(await resume.Content.ReadAsStringAsync());
            var turnId = await NewestAssistantTurnIdAsync(accountId, conversationId);
            using var reconnect = await client.GetAsync($"/api/search/turns/{turnId}");
            Assert.Equal(HttpStatusCode.OK, reconnect.StatusCode);
            using var turn = JsonDocument.Parse(await reconnect.Content.ReadAsStringAsync());

            foreach (var root in new[] { resumed.RootElement, turn.RootElement })
            {
                Assert.Equal([null, Clean, null], Whys(root.GetProperty("cards")));
                Assert.Equal([null], Whys(root.GetProperty("belowFloor")));
                Assert.Equal([Hedged, null], Whys(root.GetProperty("nearMiss")));
            }
        }

        static List<string?> Whys(JsonElement cards) =>
            cards.EnumerateArray().Select(c => c.GetProperty("why").GetString()).ToList();
    }

    /// <summary>The notice as a turn would have stored it — genres, keywords, year bounds, prose.</summary>
    private const string StoredConflict =
        """
        {"excludedGenres":["comedy"],"excludedKeywords":["anime"],"minYear":2000,"maxYear":null,
         "message":"Your conditions permanently exclude comedy and anime, so nothing you asked for there can appear. You can change that under Conditions."}
        """;

    private static void AssertNotice(JsonElement conflict)
    {
        // An OBJECT, not a string: the column is jsonb and the client reads fields off it. A
        // double-encoded payload works until somebody's notice renders as a quoted blob.
        Assert.Equal(JsonValueKind.Object, conflict.ValueKind);
        Assert.Equal(["comedy"], conflict.GetProperty("excludedGenres").EnumerateArray().Select(g => g.GetString()));
        Assert.Equal(["anime"], conflict.GetProperty("excludedKeywords").EnumerateArray().Select(k => k.GetString()));
        Assert.Equal(2000, conflict.GetProperty("minYear").GetInt32());
        Assert.Contains("Conditions", conflict.GetProperty("message").GetString()!, StringComparison.Ordinal);
    }

    private async Task<Guid> NewestAssistantTurnIdAsync(Guid accountId, Guid conversationId)
    {
        await using var db = ScopedContext(accountId);
        return await db.SearchTurns
            .Where(t => t.ConversationId == conversationId && t.Role == "assistant")
            .OrderByDescending(t => t.Sequence)
            .Select(t => t.Id)
            .FirstAsync();
    }

    private static async Task<JsonElement> ReplayDoneAsync(
        HttpClient client, Guid conversationId, Guid clientTurnId, string query)
    {
        using var response = await client.PostAsJsonAsync("/api/search/ask", new
        {
            query,
            conversationId,
            clientTurnId,
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        var raw = await response.Content.ReadAsStringAsync();
        var match = Regex.Match(
            raw, @"event: done\ndata: (?<data>\{.*?\})\n\n", RegexOptions.Singleline, TimeSpan.FromSeconds(2));
        Assert.True(match.Success, $"no done frame in the replay stream: {raw}");
        return JsonDocument.Parse(match.Groups["data"].Value).RootElement.Clone();
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
        return fixture.CreateContext(scope);
    }

    private async Task<(Guid AccountId, HttpClient Client)> SignInAsync(string slug)
    {
        var accountId = await api.SeedAccountAsync(slug);
        return (accountId, await api.CreateSignedInClientAsync(accountId));
    }

    /// <summary>
    /// A completed exchange with a known <c>clientTurnId</c>: the question as a user row, the answer
    /// as an assistant row, and its cards. Seeded rather than driven through the pipeline, because
    /// what is under test is the replay branch and the harness reaches no catalogue.
    /// </summary>
    private async Task<(Guid ConversationId, Guid ClientTurnId)> SeedCompletedTurnAsync(
        Guid accountId, int cards, string? conflict = null,
        IReadOnlyList<(string? Why, string? Satisfies, string? Tier)>? shaped = null)
    {
        shaped ??= [.. Enumerable.Range(0, cards).Select(_ => ((string?)null, (string?)null, (string?)null))];
        cards = shaped.Count;
        await using var db = fixture.CreateContext(new AccountContext());
        var now = DateTime.UtcNow;
        var clientTurnId = Guid.NewGuid();
        var conversation = new SearchConversation
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            Title = SeededQuestion,
            CreatedAt = now,
            LastTurnAt = now,
            TurnCount = 2,
            LastCardsAt = cards > 0 ? now : null,
        };
        db.SearchConversations.Add(conversation);

        var assistant = new SearchTurn
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            ConversationId = conversation.Id,
            Role = "assistant",
            Text = "Here is what I found.",
            CreatedAt = now,
            Sequence = 1,
            ClientTurnId = clientTurnId,
            Kind = SearchTurnKind.Ask,
            Status = SearchTurnStatus.Completed,
            Reason = "3 picks ranked for you, from 40 pulled",
            ConflictJson = conflict,
            CompletedAt = now,
        };
        db.SearchTurns.AddRange(
            new SearchTurn
            {
                Id = Guid.NewGuid(),
                AccountId = accountId,
                ConversationId = conversation.Id,
                Role = "user",
                Text = SeededQuestion,
                CreatedAt = now,
                Sequence = 0,
                Kind = SearchTurnKind.Ask,
                Status = SearchTurnStatus.Completed,
                CompletedAt = now,
            },
            assistant);

        for (var rank = 0; rank < cards; rank++)
        {
            var tmdbId = 781000L + Random.Shared.NextInt64(1, 900000);
            var title = new Title
            {
                Id = Guid.NewGuid(),
                MediaType = MediaType.Movie,
                TmdbId = tmdbId,
                TraktSlug = $"replay-contract-{Guid.NewGuid():N}",
                Name = $"Replay Title {tmdbId}",
                Year = 2015,
                Genres = ["science-fiction"],
                Language = "en",
                CreatedAt = now,
            };
            db.Titles.Add(title);
            db.SearchTurnCards.Add(new SearchTurnCard
            {
                Id = Guid.NewGuid(),
                AccountId = accountId,
                ConversationId = conversation.Id,
                TurnId = assistant.Id,
                TitleId = title.Id,
                TmdbId = tmdbId,
                Rank = rank,
                PredictedRating = 7.1m,
                Blend = 0.5m,
                Why = shaped[rank].Why,
                Satisfies = shaped[rank].Satisfies,
                Tier = shaped[rank].Tier,
                BelowFloor = shaped[rank].Tier == SearchTurnCardTier.BelowFloor,
                CreatedAt = now,
            });
        }

        await db.SaveChangesAsync();
        return (conversation.Id, clientTurnId);
    }
}
