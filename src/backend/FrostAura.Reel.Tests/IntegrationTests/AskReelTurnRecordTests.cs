using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FrostAura.Reel.Application.Auth;
using FrostAura.Reel.Application.Search.AskReel;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Lens;
using FrostAura.Reel.Domain.Library;
using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Domain.Ports.Tmdb;
using FrostAura.Reel.Domain.Search;
using FrostAura.Reel.Domain.Sync;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Mvc.Testing.Handlers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FrostAura.Reel.Tests.IntegrationTests;

/// <summary>
/// The turn record across turns, through the real endpoint (WS-2 tasks 1a–1c, 1g): numbered handles
/// in display order, a no-repeat set keyed on the title, a degraded reply never read back as
/// history, an aborted turn that keeps what it showed, a heartbeat through silence, and the stage,
/// model and mode columns written.
/// </summary>
/// <remarks>
/// Inherits <see cref="EmbeddedCatalogTestBase"/>: these turns embed the titles they discover.
/// </remarks>
[Collection("postgres")]
public class AskReelTurnRecordTests(PostgresFixture fixture, ReelApiFactory api)
    : EmbeddedCatalogTestBase(fixture), IClassFixture<ReelApiFactory>
{
    private const long TmdbIdFirst = 9_750_001;

    /// <summary>
    /// "More like the second one": the model is shown the last answer's cards as handles in the
    /// order they were on screen, and its history line says what that answer showed (A-4, B-8).
    /// The second turn then shows none of the first turn's titles: the no-repeat set holds them.
    /// </summary>
    [Fact]
    public async Task The_model_reads_the_last_answer_as_numbered_handles_in_screen_order()
    {
        if (!Fixture.Available)
        {
            return;
        }

        var agent = new RecordingAgent();
        using var factory = With(agent, new ScriptedTmdbClient(Discoverable(TmdbIdFirst, 6)));
        var (accountId, client) = await SignInAsync(factory, $"turn-handles-{Guid.NewGuid():N}");
        using (client)
        {
            await MarkExistingTitlesSeenAsync(accountId);

            var first = await AskAsync(client, new { query = "something futuristic", clientTurnId = Guid.NewGuid() });
            var conversationId = Frame(first, "conversation").GetProperty("id").GetGuid();
            var shownNames = Frame(first, "done").GetProperty("results").EnumerateArray()
                .Select(c => c.GetProperty("name").GetString()!)
                .ToList();
            Assert.Equal(6, shownNames.Count);

            var second = await AskAsync(client, new
            {
                query = "more like the second one",
                conversationId,
                clientTurnId = Guid.NewGuid(),
            });

            var call = agent.Calls[^1];
            Assert.Equal(shownNames.Select((n, i) => $"T1.{i + 1} {n} (2012)"), call.Shown);
            var lastAssistant = Assert.Single(call.History, t => t.Role == "assistant");
            Assert.EndsWith(
                $" [Shown: {string.Join(" · ", shownNames.Select((n, i) => $"T1.{i + 1} {n} (2012)"))}]",
                lastAssistant.Text,
                StringComparison.Ordinal);

            var secondNames = Frame(second, "done").GetProperty("results").EnumerateArray()
                .Select(c => c.GetProperty("name").GetString()!);
            Assert.Empty(secondNames.Intersect(shownNames));
        }
    }

    /// <summary>
    /// The stage timings, the models, the effort and the mode are on the row (J-13, M-8) — a degraded
    /// turn is a query, not a log line that rotated away.
    /// </summary>
    [Fact]
    public async Task A_turn_records_its_timings_models_effort_and_mode()
    {
        if (!Fixture.Available)
        {
            return;
        }

        var agent = new RecordingAgent();
        using var factory = With(agent, new ScriptedTmdbClient(Discoverable(TmdbIdFirst + 100, 4)));
        var (accountId, client) = await SignInAsync(factory, $"turn-record-{Guid.NewGuid():N}");
        using (client)
        {
            await MarkExistingTitlesSeenAsync(accountId);
            var stream = await AskAsync(client, new { query = "something futuristic", clientTurnId = Guid.NewGuid() });
            var turnId = Frame(stream, "conversation").GetProperty("turnId").GetGuid();

            await using var db = Scoped(accountId);
            var turn = await db.SearchTurns.SingleAsync(t => t.Id == turnId);
            Assert.Equal(SearchTurnStatus.Completed, turn.Status);
            Assert.Equal(SearchTurnMode.Picks, turn.Mode);
            Assert.Null(turn.DegradedFlags);
            // R1-09: the effort the purpose catalog sends at — "none" for both the plan and the judge —
            // never a hard-coded "provider-default".
            Assert.Equal(Application.Llm.ReasoningEfforts.None, turn.ReasoningEffort);
            using var timings = JsonDocument.Parse(turn.StageTimingsJson!);
            Assert.True(timings.RootElement.TryGetProperty("interpret", out _));
            Assert.True(timings.RootElement.TryGetProperty("rerank", out _));
            using var models = JsonDocument.Parse(turn.ModelIds!);
            Assert.Equal(RecordingAgent.Model, models.RootElement.GetProperty("plan").GetString());
            Assert.Equal(RecordingAgent.Model, models.RootElement.GetProperty("judge").GetString());

            var cards = await db.SearchTurnCards.Where(c => c.TurnId == turnId).ToListAsync();
            Assert.Equal(4, cards.Count);
            Assert.All(cards, c =>
            {
                Assert.Equal(SearchTurnCardTier.Pick, c.Tier);
                Assert.Equal("yes", c.Satisfies);
                Assert.Equal("Llm", c.FitSource);
            });
        }
    }

    /// <summary>
    /// A degraded reply is recorded as degraded and never read back as history: the next turn's
    /// model sees the question that was asked and not the "I couldn't think that one through" line
    /// (B-5). The resume payload marks the row degraded.
    /// </summary>
    [Fact]
    public async Task A_degraded_reply_is_flagged_and_left_out_of_history()
    {
        if (!Fixture.Available)
        {
            return;
        }

        var agent = new RecordingAgent(degradeFirst: true);
        using var factory = With(agent, new ScriptedTmdbClient(Discoverable(TmdbIdFirst + 200, 3)));
        var (accountId, client) = await SignInAsync(factory, $"turn-degraded-{Guid.NewGuid():N}");
        using (client)
        {
            await MarkExistingTitlesSeenAsync(accountId);
            var first = await AskAsync(client, new { query = "something futuristic", clientTurnId = Guid.NewGuid() });
            var conversationId = Frame(first, "conversation").GetProperty("id").GetGuid();
            Assert.True(Frame(first, "assistant-message").GetProperty("degraded").GetBoolean());

            await AskAsync(client, new { query = "and something brooding", conversationId, clientTurnId = Guid.NewGuid() });

            var history = agent.Calls[^1].History;
            Assert.DoesNotContain(history, t => t.Text.Contains(AgentReply.DegradedReply, StringComparison.Ordinal));
            Assert.Contains(history, t => t is { Role: "user", Text: "something futuristic" });

            await using var db = Scoped(accountId);
            var degraded = await db.SearchTurns
                .Where(t => t.ConversationId == conversationId && t.Role == "assistant")
                .OrderBy(t => t.Sequence)
                .FirstAsync();
            Assert.Equal(SearchTurnDegraded.Plan, degraded.DegradedFlags);

            using var resume = await client.GetAsync($"/api/search/conversations/{conversationId}");
            using var payload = JsonDocument.Parse(await resume.Content.ReadAsStringAsync());
            var rows = payload.RootElement.GetProperty("turns").EnumerateArray().ToList();
            Assert.True(rows.First(r => r.GetProperty("role").GetString() == "assistant").GetProperty("degraded").GetBoolean());
        }
    }

    /// <summary>
    /// The founder's turn-1 abort (A-6, B-3): a follow-up cancels a turn whose cards are already on
    /// screen. Those cards — and the reply — are persisted with the failure, so the next turn's history
    /// and no-repeat set contain them.
    /// </summary>
    [Fact]
    public async Task An_aborted_turn_keeps_the_cards_it_had_shown()
    {
        if (!Fixture.Available)
        {
            return;
        }

        var agent = new RecordingAgent(slowAfterFirstBatch: TimeSpan.FromSeconds(20));
        using var factory = With(agent, new ScriptedTmdbClient(Discoverable(TmdbIdFirst + 300, 12)));
        var (accountId, client) = await SignInAsync(factory, $"turn-abort-cards-{Guid.NewGuid():N}");
        using (client)
        {
            await MarkExistingTitlesSeenAsync(accountId);
            var clientTurnId = Guid.NewGuid();
            Guid conversationId;
            var shownBeforeAbort = new List<string>();
            using (var abort = new CancellationTokenSource())
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, "/api/search/ask")
                {
                    Content = JsonContent.Create(new { query = "something futuristic", clientTurnId }),
                };
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, abort.Token);
                await using var body = await response.Content.ReadAsStreamAsync(abort.Token);
                using var reader = new StreamReader(body, Encoding.UTF8);
                conversationId = Guid.Empty;
                string? eventName = null;
                while (await reader.ReadLineAsync(abort.Token) is { } line)
                {
                    if (line.StartsWith("event: ", StringComparison.Ordinal))
                    {
                        eventName = line["event: ".Length..];
                    }
                    else if (line.StartsWith("data: ", StringComparison.Ordinal))
                    {
                        using var data = JsonDocument.Parse(line["data: ".Length..]);
                        if (eventName == "conversation")
                        {
                            conversationId = data.RootElement.GetProperty("id").GetGuid();
                        }
                        else if (eventName == "candidate")
                        {
                            shownBeforeAbort.Add(data.RootElement.GetProperty("name").GetString()!);
                            // The first judge batch is four titles; the next ones are held back.
                            if (shownBeforeAbort.Count == AskReelJudge.FirstBatchSize)
                            {
                                break;
                            }
                        }
                    }
                }

                await abort.CancelAsync();
            }

            Assert.Equal(AskReelJudge.FirstBatchSize, shownBeforeAbort.Count);
            var failed = await EventuallyAsync(async () =>
            {
                await using var db = Scoped(accountId);
                return await db.SearchTurns.FirstOrDefaultAsync(t => t.ClientTurnId == clientTurnId);
            }, turn => turn is { Status: SearchTurnStatus.Failed });

            Assert.NotNull(failed);
            Assert.Equal("Failed/client_aborted", $"{failed!.Status}/{failed.FailureCode}");
            Assert.Equal(RecordingAgent.Reply, failed.Text);
            await using (var db = Scoped(accountId))
            {
                var stored = await db.SearchTurnCards
                    .Where(c => c.TurnId == failed.Id)
                    .OrderBy(c => c.Rank)
                    .Join(db.Titles, c => c.TitleId, t => t.Id, (c, t) => t.Name)
                    .ToListAsync();
                Assert.Equal(shownBeforeAbort, stored);
            }

            // The follow-up reads them back: the reply in its history, the cards as handles.
            agent.SlowAfterFirstBatch = null;
            var next = await AskAsync(client, new { query = "none of the above", conversationId, clientTurnId = Guid.NewGuid() });
            var call = agent.Calls[^1];
            Assert.Contains(call.History, t => t.Role == "assistant" && t.Text.StartsWith(RecordingAgent.Reply, StringComparison.Ordinal));
            Assert.Equal(shownBeforeAbort.Select((n, i) => $"T1.{i + 1} {n} (2012)"), call.Shown);
            var nextNames = Frame(next, "done").GetProperty("results").EnumerateArray().Select(c => c.GetProperty("name").GetString()!);
            Assert.Empty(nextNames.Intersect(shownBeforeAbort));
        }
    }

    /// <summary>
    /// D9: an interrupted turn is recorded like a finished one — the judge flag for a batch that had
    /// already failed, the stage timings to the moment of the abort, the models and the effort, and
    /// the mode of what it showed.
    /// </summary>
    [Fact]
    public async Task An_aborted_turn_records_its_degraded_flags_timings_and_mode()
    {
        if (!Fixture.Available)
        {
            return;
        }

        // Three batches: the first fails at once, the second answers, the third is slow — so the
        // abort lands after a failed batch and a shown card, before the judge has finished.
        var agent = new RecordingAgent(failFirstBatch: true, slowAfterFirstBatch: TimeSpan.FromSeconds(20));
        using var factory = With(agent, new ScriptedTmdbClient(Discoverable(TmdbIdFirst + 500, 18)));
        var (accountId, client) = await SignInAsync(factory, $"turn-abort-flags-{Guid.NewGuid():N}");
        using (client)
        {
            await MarkExistingTitlesSeenAsync(accountId);
            var clientTurnId = Guid.NewGuid();
            var secondAnswered = 0;
            using (var abort = new CancellationTokenSource())
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, "/api/search/ask")
                {
                    Content = JsonContent.Create(new { query = "something futuristic", clientTurnId }),
                };
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, abort.Token);
                await using var body = await response.Content.ReadAsStreamAsync(abort.Token);
                using var reader = new StreamReader(body, Encoding.UTF8);
                // Read until the second batch's six cards are on the wire. The failed batch returned
                // at once, so its flag was recorded before the first of them went out.
                while (await reader.ReadLineAsync(abort.Token) is { } line)
                {
                    if (line.StartsWith("event: candidate", StringComparison.Ordinal) && ++secondAnswered == 6)
                    {
                        break;
                    }
                }

                await abort.CancelAsync();
            }

            var failed = await EventuallyAsync(async () =>
            {
                await using var db = Scoped(accountId);
                return await db.SearchTurns.FirstOrDefaultAsync(t => t.ClientTurnId == clientTurnId);
            }, turn => turn is { Status: SearchTurnStatus.Failed });

            Assert.NotNull(failed);
            Assert.Equal(SearchTurnFailure.ClientAborted, failed!.FailureCode);
            Assert.Equal(6, secondAnswered);
            // The failed batch was the first (four titles): one batch of three, and its four titles
            // counted missing out of the sixteen judged (R1-01).
            Assert.Equal("judge:1/3,judge-missing:4/16", failed.DegradedFlags);
            Assert.Equal(SearchTurnMode.Picks, failed.Mode);
            Assert.Equal(Application.Llm.ReasoningEfforts.None, failed.ReasoningEffort);
            using var timings = JsonDocument.Parse(failed.StageTimingsJson!);
            Assert.True(timings.RootElement.TryGetProperty("interpret", out _));
            using var models = JsonDocument.Parse(failed.ModelIds!);
            Assert.Equal(RecordingAgent.Model, models.RootElement.GetProperty("judge").GetString());
        }
    }

    /// <summary>
    /// D8: three tray-only answers in a row used to push the last real answer out of "the second one";
    /// answers with picks and tray-only answers are now cut to the last three of each, separately.
    /// R1-04: a tray-only answer gets handles of its own — its tray cards, in the order the screen
    /// renders them — because they are the only thing it showed, and "the second one you showed me"
    /// after a none-found answer means one of them.
    /// </summary>
    [Fact]
    public async Task Tray_only_answers_never_push_real_picks_out_of_the_handles()
    {
        if (!Fixture.Available)
        {
            return;
        }

        var agent = new RecordingAgent();
        using var factory = With(agent, new ScriptedTmdbClient(Discoverable(TmdbIdFirst + 600, 3)));
        var (accountId, client) = await SignInAsync(factory, $"turn-handles-trays-{Guid.NewGuid():N}");
        using (client)
        {
            await MarkExistingTitlesSeenAsync(accountId);
            var (conversationId, picks) = await SeedPicksThenTrayOnlyAnswersAsync(accountId);

            await AskAsync(client, new { query = "more like the second one", conversationId, clientTurnId = Guid.NewGuid() });

            var call = agent.Calls[^1];
            Assert.Equal(picks.Select((n, i) => $"T1.{i + 1} {n} (2015)"), call.Shown.Where(l => l.StartsWith("T1.", StringComparison.Ordinal)));

            // Newest answer first; each tray-only answer's two tray cards as .1 and .2, in rank order.
            var trayLines = call.Shown.Where(l => !l.StartsWith("T1.", StringComparison.Ordinal)).ToList();
            Assert.Equal(6, trayLines.Count);
            for (var answer = 4; answer >= 2; answer--)
            {
                var ofAnswer = trayLines.Where(l => l.StartsWith($"T{answer}.", StringComparison.Ordinal)).ToList();
                Assert.Equal(2, ofAnswer.Count);
                Assert.StartsWith($"T{answer}.1 Handles {answer - 1}.0 ", ofAnswer[0], StringComparison.Ordinal);
                Assert.StartsWith($"T{answer}.2 Handles {answer - 1}.1 ", ofAnswer[1], StringComparison.Ordinal);
            }

            Assert.Equal("T4.1", call.Shown[0][..4]);
        }
    }

    /// <summary>
    /// The endpoint half of the media carry: the second turn's reading names no media, the first one's
    /// named movies, and the stored reading of the second turn is movies.
    /// </summary>
    [Fact]
    public async Task A_follow_up_inherits_the_media_of_the_answer_before_it()
    {
        if (!Fixture.Available)
        {
            return;
        }

        var agent = new RecordingAgent(mediaByCall: [[MediaType.Movie], []]);
        using var factory = With(agent, new ScriptedTmdbClient(Discoverable(TmdbIdFirst + 700, 8)));
        var (accountId, client) = await SignInAsync(factory, $"turn-media-carry-{Guid.NewGuid():N}");
        using (client)
        {
            await MarkExistingTitlesSeenAsync(accountId);
            var first = await AskAsync(client, new { query = "a feel-good movie", clientTurnId = Guid.NewGuid() });
            var conversationId = Frame(first, "conversation").GetProperty("id").GetGuid();
            var second = await AskAsync(client, new { query = "no superheroes please", conversationId, clientTurnId = Guid.NewGuid() });
            var turnId = Frame(second, "conversation").GetProperty("turnId").GetGuid();

            await using var db = Scoped(accountId);
            var stored = await db.SearchTurns.SingleAsync(t => t.Id == turnId);
            Assert.Equal([MediaType.Movie], Application.Search.AskReel.AskReelCarry.Parse(stored.IntentJson)!.MediaTypes);
        }
    }

    /// <summary>
    /// Silence is never longer than the heartbeat: a turn whose model takes longer than five seconds
    /// sends a `phase` frame marked as a heartbeat, which re-arms the client's watchdog through the
    /// parser that `:keepalive` comments never reach (B-10).
    /// </summary>
    [Fact]
    public async Task A_silent_turn_sends_a_phase_heartbeat()
    {
        if (!Fixture.Available)
        {
            return;
        }

        var agent = new RecordingAgent(interpretDelay: TimeSpan.FromSeconds(6.5));
        using var factory = With(agent, new ScriptedTmdbClient(Discoverable(TmdbIdFirst + 400, 2)));
        var (accountId, client) = await SignInAsync(factory, $"turn-heartbeat-{Guid.NewGuid():N}");
        using (client)
        {
            await MarkExistingTitlesSeenAsync(accountId);
            var stream = await AskAsync(client, new { query = "something futuristic", clientTurnId = Guid.NewGuid() });

            var beat = Assert.Single(
                stream, f => f.Event == "phase" && f.Data.TryGetProperty("heartbeat", out var h) && h.GetBoolean());
            Assert.Equal("searching", beat.Data.GetProperty("stage").GetString());
            var doneAt = stream.FindIndex(f => f.Event == "done");
            Assert.Equal(stream.Count - 1, doneAt);
        }
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────

    private WebApplicationFactory<Program> With(ISearchAgent agent, ITmdbClient tmdb) =>
        api.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<ISearchAgent>();
            services.AddSingleton(agent);
            services.RemoveAll<ITmdbClient>();
            services.AddSingleton(tmdb);
        }));

    private async Task<(Guid AccountId, HttpClient Client)> SignInAsync(WebApplicationFactory<Program> factory, string slug)
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

    private Infrastructure.Persistence.ReelDbContext Scoped(Guid accountId)
    {
        var scope = new AccountContext();
        scope.SetAccount(accountId);
        return Fixture.CreateContext(scope);
    }

    /// <summary>
    /// A conversation whose first answer showed two picks and whose next three showed trays only — a
    /// near-miss tray, a floor tray, an unchecked tray.
    /// </summary>
    private async Task<(Guid ConversationId, IReadOnlyList<string> Picks)> SeedPicksThenTrayOnlyAnswersAsync(Guid accountId)
    {
        await using var db = Fixture.CreateContext(new AccountContext());
        var now = DateTime.UtcNow;
        var conversation = new SearchConversation
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            Title = "picks, then trays",
            CreatedAt = now,
            LastTurnAt = now,
            TurnCount = 8,
            LastCardsAt = now,
        };
        db.SearchConversations.Add(conversation);
        var picks = new List<string>();
        string[] tiers = [SearchTurnCardTier.Pick, SearchTurnCardTier.NearMiss, SearchTurnCardTier.BelowFloor, SearchTurnCardTier.Unchecked];
        for (var answer = 0; answer < tiers.Length; answer++)
        {
            db.SearchTurns.Add(new SearchTurn
            {
                Id = Guid.NewGuid(),
                AccountId = accountId,
                ConversationId = conversation.Id,
                Role = "user",
                Text = $"question {answer}",
                CreatedAt = now,
                Sequence = answer * 2,
                Kind = SearchTurnKind.Ask,
                Status = SearchTurnStatus.Completed,
                CompletedAt = now,
            });
            var assistant = new SearchTurn
            {
                Id = Guid.NewGuid(),
                AccountId = accountId,
                ConversationId = conversation.Id,
                Role = "assistant",
                Text = $"answer {answer}",
                CreatedAt = now,
                Sequence = (answer * 2) + 1,
                ClientTurnId = Guid.NewGuid(),
                Kind = SearchTurnKind.Ask,
                Status = SearchTurnStatus.Completed,
                CompletedAt = now,
                Mode = answer == 0 ? SearchTurnMode.Picks : SearchTurnMode.NoneFound,
            };
            db.SearchTurns.Add(assistant);
            for (var rank = 0; rank < 2; rank++)
            {
                var tmdbId = Random.Shared.NextInt64(1_000_000_000, long.MaxValue);
                var title = new Title
                {
                    Id = Guid.NewGuid(),
                    MediaType = MediaType.Movie,
                    TmdbId = tmdbId,
                    TraktSlug = $"handles-{tmdbId}",
                    Name = $"Handles {answer}.{rank} {tmdbId}",
                    Year = 2015,
                    Genres = ["science-fiction"],
                    Language = "en",
                };
                db.Titles.Add(title);
                if (answer == 0)
                {
                    picks.Add(title.Name);
                }

                db.SearchTurnCards.Add(new SearchTurnCard
                {
                    Id = Guid.NewGuid(),
                    AccountId = accountId,
                    ConversationId = conversation.Id,
                    TurnId = assistant.Id,
                    TitleId = title.Id,
                    TmdbId = tmdbId,
                    Rank = rank,
                    Tier = tiers[answer],
                    BelowFloor = tiers[answer] != SearchTurnCardTier.Pick,
                    Blend = 0.5m,
                    WhySource = "None",
                    CreatedAt = now,
                });
            }
        }

        await db.SaveChangesAsync();
        return (conversation.Id, picks);
    }

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

    private static async Task<T?> EventuallyAsync<T>(Func<Task<T?>> read, Func<T?, bool> accept)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        T? value = default;
        while (DateTime.UtcNow < deadline)
        {
            value = await read();
            if (accept(value))
            {
                return value;
            }

            await Task.Delay(100);
        }

        return value;
    }

    private sealed record SseFrame(string Event, JsonElement Data);

    private static JsonElement Frame(IReadOnlyList<SseFrame> frames, string name) =>
        Assert.Single(frames, f => f.Event == name).Data;

    private static async Task<List<SseFrame>> AskAsync(HttpClient client, object body)
    {
        using var response = await client.PostAsJsonAsync("/api/search/ask", body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var frames = new List<SseFrame>();
        foreach (var chunk in (await response.Content.ReadAsStringAsync()).Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            if (chunk.StartsWith(':'))
            {
                continue;
            }

            var lines = chunk.Split('\n');
            var name = lines.First(l => l.StartsWith("event: ", StringComparison.Ordinal))["event: ".Length..];
            var data = lines.First(l => l.StartsWith("data: ", StringComparison.Ordinal))["data: ".Length..];
            frames.Add(new SseFrame(name, JsonDocument.Parse(data).RootElement.Clone()));
        }

        return frames;
    }

    private static IReadOnlyList<TmdbListItem> Discoverable(long firstTmdbId, int count) =>
        Enumerable.Range(0, count)
            .Select(i => new TmdbListItem(
                firstTmdbId + i, true, $"Record Title {firstTmdbId + i}", "/r.jpg", null,
                "A futuristic search for what is real.", 40m - i, 6.5m, 900,
                new DateTime(2012, 6, 16, 0, 0, 0, DateTimeKind.Utc), [878], "en"))
            .ToList();

    /// <summary>One interpret call, as the model saw it.</summary>
    private sealed record InterpretCall(IReadOnlyList<ChatTurn> History, IReadOnlyList<string> Shown);

    /// <summary>
    /// A model that records what it is shown and says yes to every title. Optionally degraded on the
    /// first reading, slow to start, or slow on every judge batch after the first.
    /// </summary>
    private sealed class RecordingAgent(
        bool degradeFirst = false, TimeSpan? interpretDelay = null, TimeSpan? slowAfterFirstBatch = null,
        bool failFirstBatch = false, IReadOnlyList<IReadOnlyList<MediaType>>? mediaByCall = null) : ISearchAgent
    {
        public const string Model = "recording-agent";

        public const string Reply = "Futuristic picks, coming up.";

        private readonly object _gate = new();
        private int _batches;
        private bool _degradedOnce;

        public List<InterpretCall> Calls { get; } = [];

        public TimeSpan? SlowAfterFirstBatch { get; set; } = slowAfterFirstBatch;

        public bool IsAvailable => true;

        public string ModelId => Model;

        public async Task<AgentReply> InterpretAsync(
            IReadOnlyList<ChatTurn> history, string message, string tasteSummary, IReadOnlyList<string> shownTitles,
            CancellationToken ct = default, string? standingFilters = null)
        {
            lock (_gate)
            {
                Calls.Add(new InterpretCall([.. history], [.. shownTitles]));
            }

            if (interpretDelay is { } wait)
            {
                await Task.Delay(wait, ct);
            }

            int call;
            lock (_gate)
            {
                call = Calls.Count - 1;
            }

            var media = mediaByCall is { Count: > 0 } byCall ? byCall[Math.Min(call, byCall.Count - 1)] : [];
            var intent = new SearchIntent(["science-fiction"], ["futuristic"], media, "futuristic", null, null);
            if (degradeFirst && !_degradedOnce)
            {
                _degradedOnce = true;
                return new AgentReply(AgentReply.DegradedReply, intent, intent, Degraded: true, AgentFailureKind.Truncated);
            }

            return new AgentReply(Reply, intent, intent);
        }

        public async Task<IReadOnlyList<RerankResult>> RerankBatchAsync(
            IReadOnlyList<RerankInput> titles, string query, LensPersona persona, string? anchorName = null,
            string usagePurpose = ApiUsagePurpose.Rerank, CancellationToken ct = default)
        {
            var index = Interlocked.Increment(ref _batches);
            if (index == 1 && failFirstBatch)
            {
                // The adapter's own fallback for a batch the model did not answer.
                return [.. titles.Select(t => new RerankResult(t.TitleId, 0.66d, ResultSource.Fallback, "A pick.", ResultSource.Fallback))];
            }

            // With a failing first batch, the second still answers promptly and only the third is slow.
            if (index > (failFirstBatch ? 2 : 1) && SlowAfterFirstBatch is { } slow)
            {
                await Task.Delay(slow, ct);
            }

            return titles
                .Select(t => new RerankResult(t.TitleId, 0.8d, ResultSource.Llm, "Fits.", ResultSource.Llm, Satisfies.Yes))
                .ToList();
        }
    }

    private sealed class ScriptedTmdbClient(IReadOnlyList<TmdbListItem> rows) : ITmdbClient
    {
        // TMDB knows every concept the fixture asks about, so the discovery arm pulls (R1-52).
        public Task<IReadOnlyList<TmdbKeyword>> SearchKeywordsAsync(string query, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TmdbKeyword>>([new TmdbKeyword(9_000_000 + ((query.GetHashCode(StringComparison.Ordinal) & 0x7fffffff) % 1_000_000), query)]);

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
