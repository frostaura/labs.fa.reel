using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Sync;
using Microsoft.EntityFrameworkCore;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace FrostAura.Reel.Tests.IntegrationTests;

/// <summary>
/// The first tests through <see cref="ReelApiFactory"/>, and deliberately the boring ones.
///
/// <para>Ask Reel's pipeline is being rewritten concurrently (the floor, the conflict frame, stage
/// timings, the turn record), so anything this class asserted about ranking, card counts or
/// <c>done.reason</c> would be re-written within the week and would teach the next reader that
/// endpoint tests are churn. What it pins instead is the contract that must hold no matter what
/// the pipeline does: who may ask, what an empty ask costs, that the wire is valid SSE, that one
/// account cannot resume another's conversation, the resume payload's shape — and that running all
/// of it bills nothing.</para>
///
/// <para>The money gate runs last by construction; see <see cref="MoneyGateLastOrderer"/>.</para>
/// </summary>
[Collection("postgres")]
[TestCaseOrderer(MoneyGateLastOrderer.TypeName, MoneyGateLastOrderer.AssemblyName)]
public class AskReelEndpointHarnessTests(PostgresFixture fixture, ReelApiFactory api)
    : IClassFixture<ReelApiFactory>, IAsyncLifetime
{
    /// <summary>Every account this class has signed in, so the money gate can name them all.</summary>
    private static readonly List<Guid> TouchedAccounts = [];

    /// <summary>
    /// OpenRouter rows in the ledger table before this class ran anything. The money gate asserts
    /// the count is UNCHANGED rather than zero, because the whole integration suite shares one
    /// database and other classes — <c>ApiUsageLedgerTests</c>, the lens pipeline — legitimately
    /// write OpenRouter rows into it. Asserting zero passed in isolation and failed in the suite,
    /// which is the worst of both: a gate that only holds when nothing else has run is not a gate.
    /// </summary>
    private static int? _openRouterRowBaseline;

    /// <summary>Runs before every test in the class; the first one to arrive takes the baseline.</summary>
    public async Task InitializeAsync()
    {
        if (!fixture.Available || _openRouterRowBaseline is not null)
        {
            return;
        }

        await using var db = fixture.CreateContext(new AccountContext());
        _openRouterRowBaseline = await db.ExternalApiUsages.CountAsync(u => u.Provider == ApiProvider.OpenRouter);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ── Who may ask ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Unauthenticated_ask_returns_401()
    {
        if (!fixture.Available)
        {
            return;
        }

        using var client = api.CreateClient();
        using var response = await client.PostAsJsonAsync("/api/search/ask", new { query = "something tense" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ── What an empty ask costs ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task An_empty_query_writes_no_turn_and_costs_nothing()
    {
        if (!fixture.Available)
        {
            return;
        }

        var (accountId, client) = await SignInAsync("harness-empty");
        using (client)
        {
            var stream = await AskAsync(client, new { query = "   " });

            // The endpoint answers — an empty ask degrades, it does not error.
            var done = Assert.Single(stream.Frames, f => f.Event == "done");
            Assert.Equal("empty query", done.Data.GetProperty("reason").GetString());
            // …and it never opened a conversation, so nothing to emit one for.
            Assert.DoesNotContain(stream.Frames, f => f.Event == "conversation");
        }

        await using var db = ScopedContext(accountId);
        Assert.Equal(0, await db.SearchConversations.CountAsync());
        Assert.Equal(0, await db.SearchTurns.CountAsync());
        AssertNoOpenRouterSpend(accountId);
    }

    // ── The wire ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The SSE grammar, asserted as a grammar rather than as a list of expected events: every frame
    /// is <c>event: &lt;name&gt;</c>, then <c>data: &lt;json&gt;</c>, then a blank line, and the body
    /// ends on a frame boundary. A client parser splits on the blank line and <c>JSON.parse</c>s the
    /// data, so a stray bare newline or a half-flushed frame is a broken chat, not a cosmetic
    /// defect — and it is exactly the kind of thing a pipeline rewrite breaks by accident.
    /// </summary>
    [Fact]
    public async Task The_stream_is_well_formed_sse()
    {
        if (!fixture.Available)
        {
            return;
        }

        var (_, client) = await SignInAsync("harness-sse");
        using (client)
        {
            var stream = await AskAsync(client, new { query = "a tense heist with a clever ending" });

            Assert.EndsWith("\n\n", stream.Raw, StringComparison.Ordinal);
            Assert.NotEmpty(stream.Frames);
            // Every event name is a lowercase SSE token and every payload is a JSON object; the
            // frame splitter in ParseSse would have thrown already had the grammar not held.
            Assert.All(stream.Frames, frame =>
            {
                Assert.Matches("^[a-z][a-z-]*$", frame.Event);
                Assert.Equal(JsonValueKind.Object, frame.Data.ValueKind);
            });
            // A turn always terminates, and terminates once.
            Assert.Equal("done", stream.Frames[^1].Event);
            Assert.Single(stream.Frames, f => f.Event == "done");
        }
    }

    // ── Tenancy ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Handing account B account A's conversation id must not resume A's conversation, must not
    /// append to it, and must not 500 — the endpoint scopes the lookup by account, so B simply gets
    /// a conversation of its own.
    /// </summary>
    [Fact]
    public async Task Another_accounts_conversation_id_is_not_resumed()
    {
        if (!fixture.Available)
        {
            return;
        }

        var (accountA, clientA) = await SignInAsync("harness-tenancy-a");
        var (accountB, clientB) = await SignInAsync("harness-tenancy-b");

        Guid conversationA;
        using (clientA)
        {
            var streamA = await AskAsync(clientA, new { query = "account A asks about slow-burn thrillers" });
            conversationA = Assert.Single(streamA.Frames, f => f.Event == "conversation").Data.GetProperty("id").GetGuid();
        }

        Guid conversationB;
        using (clientB)
        {
            var streamB = await AskAsync(clientB, new
            {
                query = "account B tries to hijack the transcript",
                conversationId = conversationA,
            });
            conversationB = Assert.Single(streamB.Frames, f => f.Event == "conversation").Data.GetProperty("id").GetGuid();
        }

        Assert.NotEqual(conversationA, conversationB);

        await using var dbA = ScopedContext(accountA);
        var turnsA = await dbA.SearchTurns.Where(t => t.ConversationId == conversationA).Select(t => t.Text).ToListAsync();
        Assert.DoesNotContain(turnsA, text => text.Contains("hijack", StringComparison.Ordinal));

        await using var dbB = ScopedContext(accountB);
        // B's own conversation is B's, and B can see nothing of A's — the global query filter, not
        // the endpoint, is what makes the second assertion true.
        Assert.Equal(accountB, await dbB.SearchConversations.Where(c => c.Id == conversationB).Select(c => c.AccountId).FirstAsync());
        Assert.False(await dbB.SearchConversations.AnyAsync(c => c.Id == conversationA));
    }

    // ── Resume ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Latest_conversation_has_the_documented_shape()
    {
        if (!fixture.Available)
        {
            return;
        }

        const string question = "something warm for a rainy Sunday";
        var (_, client) = await SignInAsync("harness-latest");
        using (client)
        {
            var ask = await AskAsync(client, new { query = question });
            var conversationId = Assert.Single(ask.Frames, f => f.Event == "conversation").Data.GetProperty("id").GetGuid();

            using var response = await client.GetAsync("/api/search/conversations/latest");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = payload.RootElement;
            Assert.Equal(conversationId, root.GetProperty("id").GetGuid());
            Assert.Equal(question, root.GetProperty("title").GetString());

            var turns = root.GetProperty("turns").EnumerateArray().ToList();
            Assert.NotEmpty(turns);
            // Oldest first, and the opening turn is the user's, verbatim.
            Assert.Equal("user", turns[0].GetProperty("role").GetString());
            Assert.Equal(question, turns[0].GetProperty("text").GetString());
            // Exactly two fields on the wire — role and text. A client that started depending on a
            // third would break the moment the projection changed.
            Assert.All(turns, turn => Assert.Equal(2, turn.EnumerateObject().Count()));
        }
    }

    // ── The money gate ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The reason this harness exists. Every test above drove a full Ask Reel turn through the real
    /// pipeline; none of them may have cost anything.
    ///
    /// <para>Two assertions, because one of them alone would be a lie. The persisted ledger is
    /// checked for an UNCHANGED row count rather than a zero one — the suite shares one database
    /// and other classes write OpenRouter rows into it legitimately — but with the hosted services
    /// removed nothing drains the recorder to Postgres (and <c>ApiUsageRecorder.FlushAsync</c> is
    /// <c>internal</c>, so a test cannot force it), which means a table check on its own would pass
    /// even if the run had spent a fortune. So the in-memory counters are checked too, per account
    /// and in aggregate, across every purpose the ledger knows about.</para>
    /// </summary>
    [Fact]
    public async Task The_whole_class_writes_zero_openrouter_ledger_rows()
    {
        if (!fixture.Available)
        {
            return;
        }

        // Never depend on having run last: do the work this gate is guarding, here, as well.
        var (accountId, client) = await SignInAsync("harness-money-gate");
        using (client)
        {
            await AskAsync(client, new { query = "surprise me with something clever" });
        }

        await using var db = fixture.CreateContext(new AccountContext());
        var openRouterRows = await db.ExternalApiUsages.CountAsync(u => u.Provider == ApiProvider.OpenRouter);
        Assert.Equal(_openRouterRowBaseline, openRouterRows);

        foreach (var touched in TouchedAccounts.Append(accountId).Distinct())
        {
            AssertNoOpenRouterSpend(touched);
        }

        // And nothing under any account at all, including the null-account catalogue-wide bucket
        // that the per-account loop above cannot see.
        foreach (var purpose in LedgerPurposes)
        {
            var (calls, tokens) = api.UsageRecorder.PendingTodayAllAccounts(ApiProvider.OpenRouter, purpose);
            Assert.Equal((0L, 0L), (calls, tokens));
        }
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Every <see cref="ApiUsagePurpose"/> constant, read off the type rather than listed, so a
    /// purpose added tomorrow is covered by this gate without anyone remembering to come back.
    /// </summary>
    private static readonly string[] LedgerPurposes = typeof(ApiUsagePurpose)
        .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
        .Where(f => f.IsLiteral && f.FieldType == typeof(string))
        .Select(f => (string)f.GetRawConstantValue()!)
        .ToArray();

    private void AssertNoOpenRouterSpend(Guid accountId)
    {
        foreach (var purpose in LedgerPurposes)
        {
            var (calls, tokens) = api.UsageRecorder.PendingToday(ApiProvider.OpenRouter, accountId, purpose);
            Assert.Equal((0L, 0L), (calls, tokens));
        }
    }

    private async Task<(Guid AccountId, HttpClient Client)> SignInAsync(string slug)
    {
        var signedIn = await api.CreateSignedInAccountAsync($"{slug}-{Guid.NewGuid():N}");
        TouchedAccounts.Add(signedIn.AccountId);
        return signedIn;
    }

    private Infrastructure.Persistence.ReelDbContext ScopedContext(Guid accountId)
    {
        var scope = new AccountContext();
        scope.SetAccount(accountId);
        return fixture.CreateContext(scope);
    }

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
    /// Parses the body as SSE the way a browser does, and fails on anything a browser would
    /// silently drop: a frame that is not exactly <c>event:</c> + <c>data:</c>, a payload that is
    /// not JSON, or trailing bytes after the last blank line.
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
                Assert.Equal(string.Empty, chunk); // nothing may follow the final frame boundary
                break;
            }

            var match = Regex.Match(chunk, @"^event: (?<event>[a-z][a-z-]*)\ndata: (?<data>\{.*\})$", RegexOptions.Singleline);
            Assert.True(match.Success, $"Malformed SSE frame: {chunk}");
            // The payload is one line. System.Text.Json escapes newlines inside strings, so a raw
            // one here means two frames were run together by a missing blank line — which the
            // greedy `\{.*\}` above would otherwise absorb silently.
            var data = match.Groups["data"].Value;
            Assert.False(data.Contains('\n'), $"SSE data spans more than one line — frame boundary lost: {chunk}");
            frames.Add(new SseFrame(match.Groups["event"].Value, JsonDocument.Parse(data).RootElement.Clone()));
        }

        return new SseStream(raw, frames);
    }
}

/// <summary>
/// Puts the money gate last so its name is honest: it asserts that the WHOLE class spent nothing,
/// which is only true if it has already seen the whole class run. Everything else keeps its
/// discovered order — this orderer sorts on one predicate and nothing more.
/// </summary>
public sealed class MoneyGateLastOrderer : ITestCaseOrderer
{
    public const string TypeName = "FrostAura.Reel.Tests.IntegrationTests.MoneyGateLastOrderer";
    public const string AssemblyName = "FrostAura.Reel.Tests";

    public IEnumerable<TTestCase> OrderTestCases<TTestCase>(IEnumerable<TTestCase> testCases)
        where TTestCase : ITestCase =>
        testCases.OrderBy(c => c.TestMethod.Method.Name.Contains("ledger", StringComparison.Ordinal) ? 1 : 0);
}
