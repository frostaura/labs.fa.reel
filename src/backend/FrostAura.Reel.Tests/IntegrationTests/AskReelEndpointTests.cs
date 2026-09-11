using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using FrostAura.Reel.Application.Auth;
using FrostAura.Reel.Application.Ml.ClosedLoop;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Feed;
using FrostAura.Reel.Domain.Library;
using FrostAura.Reel.Domain.Search;
using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Domain.Ports.Tmdb;
using FrostAura.Reel.Domain.Tenancy;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Mvc.Testing.Handlers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;

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
    /// <summary>The first of this class's scripted TMDB ids — see <see cref="DisposeAsync"/>.</summary>
    private const long ScriptedTmdbIdFirst = 771001;

    /// <summary>How many scripted rows the discovery client can hand back.</summary>
    private const int ScriptedTmdbIdCount = 10;

    /// <summary>The first TMDB id this class SEEDS by hand, above the scripted block.</summary>
    private const long SeededTmdbIdFirst = 772001;

    /// <summary>One past the last id this class may create, in either block. See <see cref="DisposeAsync"/>.</summary>
    private const long CatalogueIdEnd = 773000;

    /// <summary>
    /// Removes this class's catalogue rows, not just its vectors.
    ///
    /// <para><see cref="EmbeddedCatalogTestBase"/> takes the embeddings, which is what it is for. It
    /// is not enough here, and the difference is measurable: leaving ten <c>Title</c> rows behind
    /// turned <c>LiveSearchExpansionTests</c>' documented one-in-four flake into a near-certain
    /// failure in the full suite, because a later class's enrichment sweep embeds whatever Pending
    /// rows it finds — including these — and those vectors then sit in the shared HNSW index while
    /// the semantic-arm tests run. A filtered HNSW scan collects a bounded window of graph
    /// candidates BEFORE the query's predicate is applied, so a sibling's rows change which titles
    /// an unrelated account's search ever gets to consider, and no amount of eligibility isolation
    /// in the victim can reach it.</para>
    ///
    /// <para>Exposure rows are deliberately not touched: they are append-only, and this class
    /// writes none anyway — its accounts have no Active artifact, so the turn logs nothing.</para>
    /// </summary>
    public override async Task DisposeAsync()
    {
        await base.DisposeAsync();
        if (!Fixture.Available)
        {
            return;
        }

        await using var db = Fixture.CreateContext(new AccountContext());
        var titleIds = await db.Titles
            .Where(t => t.TmdbId >= ScriptedTmdbIdFirst && t.TmdbId < CatalogueIdEnd)
            .Select(t => t.Id)
            .ToListAsync();
        if (titleIds.Count == 0)
        {
            return;
        }

        await db.SearchTurnCards.Where(c => titleIds.Contains(c.TitleId)).ExecuteDeleteAsync();
        await db.Titles.Where(t => titleIds.Contains(t.Id)).ExecuteDeleteAsync();
    }

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

        var scripted = new ScriptedDiscoveryTmdbClient(Discoverable(ScriptedTmdbIdFirst, ScriptedTmdbIdCount));
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

    // ── One turn in flight ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// A second question while one is still being answered is refused, not raced. Two live streams
    /// on one conversation would both compute the same next <c>Sequence</c> and both write cards
    /// under their own turn — the unique index would stop one of them with a 23505 on a person's
    /// screen, which is the failure this 409 exists to replace.
    /// </summary>
    [Fact]
    public async Task A_second_turn_while_one_is_pending_is_a_409()
    {
        if (!Fixture.Available)
        {
            return;
        }

        var (accountId, client) = await SignInAsync(api, $"turn-inflight-{Guid.NewGuid():N}");
        using (client)
        {
            var first = await AskAsync(client, new
            {
                query = "a first question that finishes",
                clientTurnId = Guid.NewGuid(),
            });
            var conversationId = ConversationId(first);

            // A turn stuck Pending, seeded rather than raced: the point is what the endpoint does
            // when one exists, and a real race would make the test decide it by timing.
            await using (var db = ScopedContext(accountId))
            {
                db.SearchTurns.Add(new SearchTurn
                {
                    Id = Guid.NewGuid(),
                    AccountId = accountId,
                    ConversationId = conversationId,
                    Role = "assistant",
                    Text = string.Empty,
                    CreatedAt = DateTime.UtcNow,
                    Sequence = 99,
                    ClientTurnId = Guid.NewGuid(),
                    Kind = SearchTurnKind.Ask,
                    Status = SearchTurnStatus.Pending,
                });
                await db.SaveChangesAsync();
            }

            using var response = await client.PostAsJsonAsync("/api/search/ask", new
            {
                query = "a second question, sent too soon",
                conversationId,
                clientTurnId = Guid.NewGuid(),
            });

            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal("turn_in_progress", payload.RootElement.GetProperty("code").GetString());
        }
    }

    // ── An abandoned stream ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Walking away mid-turn leaves a row that says what happened, not a hole. The Pending row is
    /// stamped Failed with <c>client_aborted</c> on a scope that outlives the cancelled request —
    /// and the conversation's previous answer is still what a resume returns, because a failed turn
    /// stores no cards and therefore cannot become the block that replays.
    /// </summary>
    [Fact]
    public async Task An_aborted_stream_leaves_a_failed_turn_and_the_prior_block_still_replays()
    {
        if (!Fixture.Available)
        {
            return;
        }

        // Slow discovery, so the abort lands mid-turn by construction rather than by luck. The
        // client's cancellation does not reach the server the instant it is raised — TestServer
        // surfaces it on the next write to the response body — so the delay has to outlast the
        // cancellation rather than merely overlap it.
        var scripted = new ScriptedDiscoveryTmdbClient(
            Discoverable(ScriptedTmdbIdFirst, ScriptedTmdbIdCount), discoveryDelay: TimeSpan.FromMilliseconds(1500));
        using var factory = api.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<ITmdbClient>();
            services.AddSingleton<ITmdbClient>(scripted);
        }));

        var (accountId, client) = await SignInAsync(factory, $"turn-abort-{Guid.NewGuid():N}");
        using (client)
        {
            await MarkExistingTitlesSeenAsync(accountId);

            // A completed turn first, so there is a prior block to still be there afterwards.
            var conversationId = await SeedCompletedTurnAsync(accountId, cards: 3);

            using var abort = new CancellationTokenSource(TimeSpan.FromMilliseconds(750));
            var clientTurnId = Guid.NewGuid();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            {
                using var response = await client.PostAsJsonAsync(
                    "/api/search/ask",
                    new { query = "a question nobody waits for", conversationId, clientTurnId },
                    abort.Token);
                await response.Content.ReadAsStringAsync(abort.Token);
            });

            // The write happens on a scope of its own, after the request has already unwound.
            var failed = await EventuallyAsync(async () =>
            {
                await using var db = ScopedContext(accountId);
                return await db.SearchTurns.FirstOrDefaultAsync(t => t.ClientTurnId == clientTurnId);
            }, turn => turn is { Status: SearchTurnStatus.Failed });

            Assert.NotNull(failed);
            // Status and code asserted together so a failure names what it actually found.
            Assert.Equal("Failed/client_aborted", $"{failed!.Status}/{failed.FailureCode}");
            Assert.False(await AnyCardsAsync(accountId, failed.Id));

            // The prior answer is untouched: the failed turn stored nothing, so the block that
            // replays is still the completed ask before it.
            using var resume = await client.GetAsync($"/api/search/conversations/{conversationId}");
            Assert.Equal(HttpStatusCode.OK, resume.StatusCode);
            using var payload = JsonDocument.Parse(await resume.Content.ReadAsStringAsync());
            Assert.Equal(3, payload.RootElement.GetProperty("cards").GetArrayLength());
        }
    }

    // ── The footprint table ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// THE FIXTURE VARIATION: one footprint, one card, one drop — six probes over the same seeded
    /// answer, each touching a different card so a rule that keyed off position rather than title
    /// would fail here.
    ///
    /// <para>The revoked reaction is the row that matters most. A reaction with a <c>RevokedAt</c>
    /// is a statement the user took back, and treating it as live would silently shrink every
    /// replayed answer for anyone who has ever undone a tap. It is also the one case where the
    /// expected answer is "nothing happens", which no other probe can show.</para>
    /// </summary>
    [Theory]
    [InlineData("NotInterested", 0, 1)]
    [InlineData("SaveForLater", 1, 1)]
    [InlineData("Dropped", 2, 1)]
    [InlineData("RevokedNotInterested", 3, 0)]
    [InlineData("Watched", 4, 1)]
    [InlineData("Rated", 5, 1)]
    public async Task A_footprint_on_one_card_drops_exactly_that_card(string footprint, int cardIndex, int expectedRemoved)
    {
        if (!Fixture.Available)
        {
            return;
        }

        var (accountId, client) = await SignInAsync(api, $"turn-footprint-{Guid.NewGuid():N}");
        using (client)
        {
            var conversationId = await SeedCompletedTurnAsync(accountId, cards: 6);
            var titleId = await CardTitleIdAsync(conversationId, cardIndex);
            await ApplyFootprintAsync(accountId, titleId, footprint);

            using var response = await client.GetAsync($"/api/search/conversations/{conversationId}");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = payload.RootElement;

            Assert.Equal(expectedRemoved, root.GetProperty("removed").GetInt32());
            Assert.Equal(6 - expectedRemoved, root.GetProperty("cards").GetArrayLength());

            // A dropped card is GONE, not returned unbadged — the eligibility rule is founder-locked
            // and this is the surface it names.
            var returned = root.GetProperty("cards").EnumerateArray()
                .Select(c => c.GetProperty("titleId").GetGuid())
                .ToList();
            Assert.Equal(expectedRemoved == 0, returned.Contains(titleId));
        }
    }

    // ── Replay costs nothing ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Reading a conversation back writes no exposure rows. A replay is the user looking at the
    /// same serve again; logging it would inflate the denominator of every accuracy number computed
    /// from the log, and nothing downstream could tell the difference afterwards.
    /// </summary>
    [Fact]
    public async Task Replaying_a_conversation_writes_no_exposures()
    {
        if (!Fixture.Available)
        {
            return;
        }

        var (accountId, client) = await SignInAsync(api, $"turn-replay-{Guid.NewGuid():N}");
        using (client)
        {
            var conversationId = await SeedCompletedTurnAsync(accountId, cards: 4);
            var before = await ExposureCountAsync();

            for (var i = 0; i < 3; i++)
            {
                using var response = await client.GetAsync($"/api/search/conversations/{conversationId}");
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            }

            Assert.Equal(before, await ExposureCountAsync());
        }
    }

    // ── Deleting ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Deleting a conversation removes what the user can see and nothing else. The exposure log is
    /// append-only: it records that the product served those cards, which stays true after the
    /// transcript is gone, and the resulting dangling <c>SourceTurnId</c> is the designed state.
    /// </summary>
    [Fact]
    public async Task Deleting_a_conversation_leaves_its_exposures()
    {
        if (!Fixture.Available)
        {
            return;
        }

        var (accountId, client) = await SignInAsync(api, $"turn-delete-{Guid.NewGuid():N}");
        using (client)
        {
            var conversationId = await SeedCompletedTurnAsync(accountId, cards: 3);

            Guid turnId;
            await using (var db = ScopedContext(accountId))
            {
                turnId = await db.SearchTurns
                    .Where(t => t.ConversationId == conversationId && t.Role == "assistant")
                    .Select(t => t.Id)
                    .FirstAsync();
                db.ExposureEvents.Add(new ExposureEvent
                {
                    Id = Guid.NewGuid(),
                    AccountId = accountId,
                    TitleId = Guid.NewGuid(),
                    OccurredAt = DateTime.UtcNow,
                    Surface = ExposureSurface.AskReel,
                    Rank = 0,
                    ModelArtifactId = Guid.NewGuid(),
                    PredictedRating = 8.5m,
                    WhySource = "Llm",
                    SourceTurnId = turnId,
                });
                await db.SaveChangesAsync();
            }

            var before = await ExposureCountAsync();

            using var deleted = await client.DeleteAsync($"/api/search/conversations/{conversationId}");
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

            await using var db2 = ScopedContext(accountId);
            Assert.False(await db2.SearchConversations.AnyAsync(c => c.Id == conversationId));
            Assert.False(await db2.SearchTurns.AnyAsync(t => t.ConversationId == conversationId));
            Assert.False(await db2.SearchTurnCards.AnyAsync(c => c.ConversationId == conversationId));
            Assert.Equal(before, await ExposureCountAsync());
            Assert.True(await db2.ExposureEvents.AnyAsync(e => e.SourceTurnId == turnId));
        }
    }

    // ── Attribution ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A new exposure row carries the turn that served it; a row written without one keeps its
    /// null. The column is forward-only and is never back-filled, so "null" means the surface did
    /// not say — which is a different fact from "no turn" and is unrecoverable once conflated.
    /// </summary>
    [Fact]
    public async Task SourceTurnId_is_set_on_new_rows_while_a_pre_existing_null_row_stays_null()
    {
        if (!Fixture.Available)
        {
            return;
        }

        var accountId = await api.SeedAccountAsync($"turn-source-{Guid.NewGuid():N}");
        var artifactId = Guid.NewGuid();
        var unattributedTitle = Guid.NewGuid();
        var attributedTitle = Guid.NewGuid();
        var turnId = Guid.NewGuid();

        await using (var db = ScopedContext(accountId))
        {
            var log = new ExposureLog(db, NullLogger<ExposureLog>.Instance);

            // The way every other surface writes: no turn, so no attribution.
            await log.RecordAsync(
                accountId, ExposureSurface.FeedHero, feedSnapshotId: null, artifactId,
                DateTime.UtcNow, [new ExposureCard(unattributedTitle, 0, 7m)], CancellationToken.None);

            // The way Ask Reel writes it now.
            await log.RecordAsync(
                accountId, ExposureSurface.AskReel, feedSnapshotId: null, artifactId,
                DateTime.UtcNow, [new ExposureCard(attributedTitle, 0, 8m)], CancellationToken.None, turnId);
        }

        await using var verify = ScopedContext(accountId);
        Assert.Null(await verify.ExposureEvents
            .Where(e => e.TitleId == unattributedTitle).Select(e => e.SourceTurnId).FirstAsync());
        Assert.Equal(turnId, await verify.ExposureEvents
            .Where(e => e.TitleId == attributedTitle).Select(e => e.SourceTurnId).FirstAsync());
    }

    // ── Tenancy on the read side ────────────────────────────────────────────────────────────

    /// <summary>
    /// Another account's conversation is a 404 on every verb, and a DELETE that answers 404 has
    /// also deleted nothing — the assertion that separates "refused" from "refused after the fact".
    /// </summary>
    [Fact]
    public async Task Another_accounts_conversation_is_a_404_on_get_and_delete()
    {
        if (!Fixture.Available)
        {
            return;
        }

        var (ownerId, owner) = await SignInAsync(api, $"turn-owner-{Guid.NewGuid():N}");
        var (_, stranger) = await SignInAsync(api, $"turn-stranger-{Guid.NewGuid():N}");
        using (owner)
        using (stranger)
        {
            var conversationId = await SeedCompletedTurnAsync(ownerId, cards: 2);

            using (var read = await stranger.GetAsync($"/api/search/conversations/{conversationId}"))
            {
                Assert.Equal(HttpStatusCode.NotFound, read.StatusCode);
            }

            using (var removed = await stranger.DeleteAsync($"/api/search/conversations/{conversationId}"))
            {
                Assert.Equal(HttpStatusCode.NotFound, removed.StatusCode);
            }

            await using var db = ScopedContext(ownerId);
            Assert.True(await db.SearchConversations.AnyAsync(c => c.Id == conversationId));
            Assert.Equal(2, await db.SearchTurnCards.CountAsync(c => c.ConversationId == conversationId));
        }
    }

    // ── The reconnect path ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// <c>GET /turns/{id}</c> answers 202 while the turn is running and 200 once it is not, which is
    /// what lets a client that lost the stream tell "not yet" from "nothing".
    /// </summary>
    [Fact]
    public async Task A_pending_turn_answers_202_and_a_completed_one_answers_its_cards()
    {
        if (!Fixture.Available)
        {
            return;
        }

        var (accountId, client) = await SignInAsync(api, $"turn-reconnect-{Guid.NewGuid():N}");
        using (client)
        {
            var conversationId = await SeedCompletedTurnAsync(accountId, cards: 2);
            Guid completedTurnId;
            var pendingTurnId = Guid.NewGuid();
            await using (var db = ScopedContext(accountId))
            {
                completedTurnId = await db.SearchTurns
                    .Where(t => t.ConversationId == conversationId && t.Role == "assistant")
                    .Select(t => t.Id)
                    .FirstAsync();
                db.SearchTurns.Add(new SearchTurn
                {
                    Id = pendingTurnId,
                    AccountId = accountId,
                    ConversationId = conversationId,
                    Role = "assistant",
                    Text = string.Empty,
                    CreatedAt = DateTime.UtcNow,
                    Sequence = 42,
                    ClientTurnId = Guid.NewGuid(),
                    Kind = SearchTurnKind.Ask,
                    Status = SearchTurnStatus.Pending,
                });
                await db.SaveChangesAsync();
            }

            using (var pending = await client.GetAsync($"/api/search/turns/{pendingTurnId}"))
            {
                Assert.Equal(HttpStatusCode.Accepted, pending.StatusCode);
                using var payload = JsonDocument.Parse(await pending.Content.ReadAsStringAsync());
                Assert.Equal(SearchTurnStatus.Pending, payload.RootElement.GetProperty("status").GetString());
            }

            using (var completed = await client.GetAsync($"/api/search/turns/{completedTurnId}"))
            {
                Assert.Equal(HttpStatusCode.OK, completed.StatusCode);
                using var payload = JsonDocument.Parse(await completed.Content.ReadAsStringAsync());
                Assert.Equal(SearchTurnStatus.Completed, payload.RootElement.GetProperty("status").GetString());
                Assert.Equal(2, payload.RootElement.GetProperty("cards").GetArrayLength());
            }
        }
    }

    /// <summary>
    /// A resumed transcript hands back a row's <c>id</c> and, when it failed, its
    /// <c>failureCode</c> — the two things a client needs to do anything but guess.
    ///
    /// <para>Without the code, every unfinished turn renders the same: a thread the user stopped
    /// themselves (<c>client_aborted</c>) is indistinguishable from one that hit a snag, so the
    /// page either apologises for something the user did on purpose or stays silent about a real
    /// failure. Without the id there is no handle at all — a <c>Pending</c> row cannot be polled
    /// through <c>GET /api/search/turns/{id}</c>, which is the whole reconnect path, and the client
    /// is left waiting on a turn it cannot name.</para>
    ///
    /// <para>The code travels only on a <c>Failed</c> row, deliberately: a cause outliving the
    /// failure it described is worse than no cause, because it reads as current.</para>
    /// </summary>
    [Fact]
    public async Task A_resumed_transcript_carries_each_turns_id_and_a_failed_turns_code()
    {
        if (!Fixture.Available)
        {
            return;
        }

        var (accountId, client) = await SignInAsync(api, $"turn-resume-ids-{Guid.NewGuid():N}");
        using (client)
        {
            // Sequences 0/1: the completed ask that still owns the replayed block.
            var conversationId = await SeedCompletedTurnAsync(accountId, cards: 2);

            // Sequences 2/3: a turn the user walked away from. 4/5: one still running.
            var abortedTurnId = Guid.NewGuid();
            var pendingTurnId = Guid.NewGuid();
            await using (var db = ScopedContext(accountId))
            {
                var now = DateTime.UtcNow;
                db.SearchTurns.AddRange(
                    UserTurn(accountId, conversationId, 2, "one I stopped", now),
                    new SearchTurn
                    {
                        Id = abortedTurnId,
                        AccountId = accountId,
                        ConversationId = conversationId,
                        Role = "assistant",
                        Text = string.Empty,
                        CreatedAt = now,
                        Sequence = 3,
                        ClientTurnId = Guid.NewGuid(),
                        Kind = SearchTurnKind.Ask,
                        Status = SearchTurnStatus.Failed,
                        FailureCode = "client_aborted",
                    },
                    UserTurn(accountId, conversationId, 4, "one still running", now),
                    new SearchTurn
                    {
                        Id = pendingTurnId,
                        AccountId = accountId,
                        ConversationId = conversationId,
                        Role = "assistant",
                        Text = string.Empty,
                        CreatedAt = now,
                        Sequence = 5,
                        ClientTurnId = Guid.NewGuid(),
                        Kind = SearchTurnKind.Ask,
                        Status = SearchTurnStatus.Pending,
                    });
                await db.SaveChangesAsync();
            }

            using var response = await client.GetAsync($"/api/search/conversations/{conversationId}");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var turns = payload.RootElement.GetProperty("turns").EnumerateArray().ToList();
            Assert.Equal(6, turns.Count);

            // Every row is addressable, not just the interesting ones: the id is what makes a
            // transcript row a thing the client can ask about.
            foreach (var turn in turns)
            {
                Assert.True(turn.TryGetProperty("id", out var id), "a resumed turn carries no id.");
                Assert.NotEqual(Guid.Empty, id.GetGuid());
            }

            var aborted = turns.Single(t => t.GetProperty("sequence").GetInt32() == 3);
            Assert.Equal(abortedTurnId, aborted.GetProperty("id").GetGuid());
            // Status and code asserted together so a failure names what it actually found.
            Assert.Equal(
                "Failed/client_aborted",
                $"{aborted.GetProperty("status").GetString()}/{aborted.GetProperty("failureCode").GetString()}");

            // The running turn is the one worth polling, so it is the one that most needs its id —
            // and it has no cause, because nothing has gone wrong yet.
            var pending = turns.Single(t => t.GetProperty("sequence").GetInt32() == 5);
            Assert.Equal(pendingTurnId, pending.GetProperty("id").GetGuid());
            Assert.Equal(SearchTurnStatus.Pending, pending.GetProperty("status").GetString());
            Assert.Equal(JsonValueKind.Null, pending.GetProperty("failureCode").ValueKind);

            // A turn that finished says nothing about causes either — present-but-null, one shape.
            var completed = turns.Single(t => t.GetProperty("sequence").GetInt32() == 1);
            Assert.Equal(JsonValueKind.Null, completed.GetProperty("failureCode").ValueKind);

            // The failed turn stored no cards, so it never became the block that replays.
            Assert.Equal(2, payload.RootElement.GetProperty("cards").GetArrayLength());
        }
    }

    /// <summary>One transcript row, the half of a pair the user wrote.</summary>
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

    // ── The artifact on the turn ────────────────────────────────────────────────────────────

    /// <summary>
    /// A turn stamped with an artifact that no longer resolves is treated as stale — the read
    /// re-scores rather than trusting the stored number — and with no model to re-score against,
    /// the stored numbers are what comes back rather than a blanked badge.
    ///
    /// <para>This pins the DECISION and the degradation. It cannot pin the re-scored VALUE without
    /// a trained artifact for the account, which costs a training run to construct and would make
    /// this class depend on the model rather than on the turn record.</para>
    /// </summary>
    [Fact]
    public async Task A_turn_whose_artifact_no_longer_resolves_still_replays_its_numbers()
    {
        if (!Fixture.Available)
        {
            return;
        }

        var (accountId, client) = await SignInAsync(api, $"turn-artifact-{Guid.NewGuid():N}");
        using (client)
        {
            // An artifact id that resolves to nothing — a superseded row, or one hidden by
            // ModelArtifact's feature-schema filter, are indistinguishable here and must be.
            var conversationId = await SeedCompletedTurnAsync(accountId, cards: 2, artifactId: Guid.NewGuid());

            using var response = await client.GetAsync($"/api/search/conversations/{conversationId}");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

            var predictions = payload.RootElement.GetProperty("cards").EnumerateArray()
                .Select(c => c.GetProperty("predictedRating").GetDecimal())
                .ToList();
            Assert.Equal([7.25m, 7.25m], predictions);
        }
    }

    // ── The columns ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The stored card's numbers come back at the precision the columns declare — 2 dp for a
    /// prediction, 3 for a fit, 4 for a blend, mirroring <c>ExposureEvent</c> exactly so a card and
    /// the exposure row describing it can never round to two different numbers.
    /// </summary>
    [Fact]
    public async Task Stored_cards_round_trip_at_the_columns_precision()
    {
        if (!Fixture.Available)
        {
            return;
        }

        var accountId = await api.SeedAccountAsync($"turn-precision-{Guid.NewGuid():N}");
        var conversationId = await SeedCompletedTurnAsync(accountId, cards: 1);

        Guid cardId;
        await using (var db = ScopedContext(accountId))
        {
            var card = await db.SearchTurnCards.FirstAsync(c => c.ConversationId == conversationId);
            cardId = card.Id;
            card.PredictedRating = 7.256m;
            card.LlmFit = 0.87654m;
            card.Blend = 0.123456m;
            await db.SaveChangesAsync();
        }

        await using var verify = ScopedContext(accountId);
        var stored = await verify.SearchTurnCards.AsNoTracking().FirstAsync(c => c.Id == cardId);
        Assert.Equal(7.26m, stored.PredictedRating);
        Assert.Equal(0.877m, stored.LlmFit);
        Assert.Equal(0.1235m, stored.Blend);
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

    /// <summary>How many exposure rows the whole database holds — the gate a replay must not move.</summary>
    private async Task<int> ExposureCountAsync()
    {
        await using var db = Fixture.CreateContext(new AccountContext());
        return await db.ExposureEvents.CountAsync();
    }

    private async Task<bool> AnyCardsAsync(Guid accountId, Guid turnId)
    {
        await using var db = ScopedContext(accountId);
        return await db.SearchTurnCards.AnyAsync(c => c.TurnId == turnId);
    }

    private async Task<Guid> CardTitleIdAsync(Guid conversationId, int rank)
    {
        await using var db = Fixture.CreateContext(new AccountContext());
        return await db.SearchTurnCards
            .Where(c => c.ConversationId == conversationId && c.Rank == rank)
            .Select(c => c.TitleId)
            .FirstAsync();
    }

    /// <summary>
    /// Puts exactly one footprint on one title. The names match the rows of the <c>[Theory]</c>
    /// above; the revoked one is written as a real reaction WITH a <c>RevokedAt</c> rather than as
    /// no row at all, because "took it back" and "never said it" must be distinguishable in the
    /// table for the test to mean anything.
    /// </summary>
    private async Task ApplyFootprintAsync(Guid accountId, Guid titleId, string footprint)
    {
        await using var db = Fixture.CreateContext(new AccountContext());
        var now = DateTime.UtcNow;
        switch (footprint)
        {
            case "Watched":
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
                break;
            case "Rated":
                db.UserRatings.Add(new UserRating
                {
                    Id = Guid.NewGuid(),
                    AccountId = accountId,
                    TitleId = titleId,
                    SubjectType = RatingSubjectType.Movie,
                    Rating = 8,
                    RatedAt = now,
                    FirstRatedAt = now,
                });
                break;
            default:
                db.UserTitleReactions.Add(new UserTitleReaction
                {
                    Id = Guid.NewGuid(),
                    AccountId = accountId,
                    TitleId = titleId,
                    Kind = footprint switch
                    {
                        "SaveForLater" => ReactionKind.SaveForLater,
                        "Dropped" => ReactionKind.Dropped,
                        _ => ReactionKind.NotInterested,
                    },
                    CreatedAt = now,
                    RevokedAt = footprint == "RevokedNotInterested" ? now : null,
                });
                break;
        }

        await db.SaveChangesAsync();
    }

    /// <summary>
    /// A conversation with one completed ask and <paramref name="cards"/> stored cards over real
    /// catalogue rows, seeded directly.
    ///
    /// <para>Direct rather than through a live turn on purpose: what the read side does with a
    /// stored answer is the thing under test, and driving the pipeline to produce one would make
    /// every assertion here depend on the ranker's mood. The rows are the same rows the endpoint
    /// writes.</para>
    /// </summary>
    private async Task<Guid> SeedCompletedTurnAsync(Guid accountId, int cards, Guid? artifactId = null)
    {
        await using var db = Fixture.CreateContext(new AccountContext());
        var now = DateTime.UtcNow;
        var conversation = new Domain.Search.SearchConversation
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            Title = "a seeded thread",
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
            ClientTurnId = Guid.NewGuid(),
            Kind = SearchTurnKind.Ask,
            Status = SearchTurnStatus.Completed,
            Reason = "6 picks from your catalogue.",
            ModelArtifactId = artifactId,
            CompletedAt = now,
        };
        db.SearchTurns.AddRange(
            new SearchTurn
            {
                Id = Guid.NewGuid(),
                AccountId = accountId,
                ConversationId = conversation.Id,
                Role = "user",
                Text = "a seeded question",
                CreatedAt = now,
                Sequence = 0,
                Kind = SearchTurnKind.Ask,
                Status = SearchTurnStatus.Completed,
                CompletedAt = now,
            },
            assistant);

        for (var rank = 0; rank < cards; rank++)
        {
            var tmdbId = SeededTmdbIdFirst + Interlocked.Increment(ref _seededTitleCounter);
            var title = new Title
            {
                Id = Guid.NewGuid(),
                MediaType = MediaType.Movie,
                TmdbId = tmdbId,
                TraktSlug = $"seeded-{tmdbId}",
                Name = $"Seeded Title {tmdbId}",
                Year = 2015,
                Genres = ["science-fiction"],
                Language = "en",
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
                Similarity = 0.42,
                PredictedRating = 7.25m,
                LlmFit = 0.812m,
                Blend = 0.6543m,
                Why = "Seeded why-line.",
                WhySource = "Llm",
                CreatedAt = now,
            });
        }

        await db.SaveChangesAsync();
        return conversation.Id;
    }

    /// <summary>Hands out distinct seeded TMDB ids across every test in the class.</summary>
    private static int _seededTitleCounter;

    /// <summary>
    /// Polls until <paramref name="accept"/> is satisfied or two seconds pass. The turn's write
    /// happens on a scope that outlives the cancelled request, so "not there yet" is an ordinary
    /// state here rather than a failure — but only for as long as it takes the scope to finish.
    /// </summary>
    private static async Task<T?> EventuallyAsync<T>(Func<Task<T?>> read, Func<T?, bool> accept)
    {
        var deadline = DateTime.UtcNow.AddSeconds(8);
        T? value = default;
        while (DateTime.UtcNow < deadline)
        {
            value = await read();
            if (accept(value))
            {
                return value;
            }

            await Task.Delay(50);
        }

        return value;
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
    private sealed class ScriptedDiscoveryTmdbClient(
        IReadOnlyList<TmdbListItem> rows, TimeSpan discoveryDelay = default) : ITmdbClient
    {
        public Task<IReadOnlyList<TmdbKeyword>> SearchKeywordsAsync(string query, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TmdbKeyword>>([]);

        public Task<IReadOnlyList<TmdbListItem>> DiscoverByConceptAsync(
            bool movies, IReadOnlyList<int> genreIds, IReadOnlyList<int> keywordIds,
            string? region, DateTime? releasedAfter, int page,
            IReadOnlyList<int>? excludeGenreIds = null, IReadOnlyList<int>? excludeKeywordIds = null,
            DateTime? releasedBefore = null, int? maxRuntimeMinutes = null, TmdbDiscoverOptions? options = null,
            CancellationToken ct = default) =>
            DiscoverAsync(movies, ct);

        /// <summary>
        /// The delay is what makes the abort test deterministic rather than a race with the
        /// pipeline: it honours the token, so cancelling unwinds the turn exactly the way a client
        /// walking away does.
        /// </summary>
        private async Task<IReadOnlyList<TmdbListItem>> DiscoverAsync(bool movies, CancellationToken ct)
        {
            if (discoveryDelay > TimeSpan.Zero)
            {
                await Task.Delay(discoveryDelay, ct);
            }

            return movies ? rows : [];
        }

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
