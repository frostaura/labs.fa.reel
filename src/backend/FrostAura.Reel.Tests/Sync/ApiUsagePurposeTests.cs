using System.Net;
using System.Text;
using System.Text.Json;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Domain.Sync;
using FrostAura.Reel.Infrastructure.Adapters;
using FrostAura.Reel.Infrastructure.Telemetry;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrostAura.Reel.Tests.Sync;

/// <summary>
/// The re-rank purpose family, pinned at both ends: the constants themselves, and the fact that the
/// string a caller passes is the string the ledger actually receives.
///
/// <para>The defect this exists to prevent is silent and expensive. Every re-rank caller once wrote
/// one <c>"rerank"</c> row, so "what does an Ask Reel turn cost" was unanswerable and every
/// per-surface figure derived from the ledger was wrong without looking wrong. Splitting the family
/// only fixes that while the adapter records the purpose it was HANDED — a single reverted line at
/// the record site would collapse the family back to one bucket, and nothing else in the suite
/// would notice, because fits, ordering and every user-visible behaviour are identical either
/// way.</para>
///
/// <para>Asserted through <see cref="IPendingApiUsage.PendingToday"/> rather than the database:
/// <c>ApiUsageRecorder.FlushAsync</c> is internal and runs on a 15s timer, so the in-memory
/// counters are the only synchronous view of what a call just recorded.</para>
/// </summary>
public class ApiUsagePurposeTests
{
    private static readonly Guid TitleA = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public void Every_member_of_the_rerank_family_is_a_distinct_string()
    {
        string[] family =
        [
            ApiUsagePurpose.Rerank,
            ApiUsagePurpose.RerankFeed,
            ApiUsagePurpose.RerankPage,
            ApiUsagePurpose.RerankAsk,
            ApiUsagePurpose.RerankExpand,
        ];

        Assert.Equal(family.Length, family.Distinct(StringComparer.Ordinal).Count());
        Assert.All(family, p => Assert.False(string.IsNullOrWhiteSpace(p)));
    }

    [Fact]
    public void The_family_ceiling_sums_every_member_including_the_legacy_string()
    {
        // Written out rather than prefix-matched, so this is the check that a new member was added
        // to the array as well as to the class — a member the ceiling does not sum bills as free.
        Assert.Contains(ApiUsagePurpose.Rerank, ApiUsagePurpose.RerankFamily);
        Assert.Contains(ApiUsagePurpose.RerankFeed, ApiUsagePurpose.RerankFamily);
        Assert.Contains(ApiUsagePurpose.RerankPage, ApiUsagePurpose.RerankFamily);
        Assert.Contains(ApiUsagePurpose.RerankAsk, ApiUsagePurpose.RerankFamily);
        Assert.Contains(ApiUsagePurpose.RerankExpand, ApiUsagePurpose.RerankFamily);
        Assert.Equal(
            ApiUsagePurpose.RerankFamily.Length,
            ApiUsagePurpose.RerankFamily.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void No_rerank_purpose_collides_with_another_ledger_purpose()
    {
        // The two audit purposes exist precisely so that measuring a thing cannot eat the budget
        // the thing runs on; a copy-paste that gave one of them a family string would do exactly
        // that, and the ledger would still look healthy.
        string[] others =
        [
            ApiUsagePurpose.ExtractAttributes, ApiUsagePurpose.SearchInterpret, ApiUsagePurpose.SearchAgent,
            ApiUsagePurpose.Embed, ApiUsagePurpose.TasteNarrative, ApiUsagePurpose.Lens, ApiUsagePurpose.LensAudit,
            ApiUsagePurpose.CrossReference, ApiUsagePurpose.CrossReferenceEval, ApiUsagePurpose.Seed,
        ];

        Assert.Empty(others.Intersect(ApiUsagePurpose.RerankFamily, StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("rerank-ask")]
    [InlineData("rerank-feed")]
    [InlineData("rerank-page")]
    [InlineData("rerank-expand")]
    public async Task The_purpose_a_caller_passes_is_the_purpose_the_ledger_records(string purpose)
    {
        var handler = new CannedHandler("""{"results":[]}""");
        var recorder = Recorder();

        await Agent(handler, recorder).RerankBatchAsync(
            [new(TitleA, "Alpha", 1984, "movie", ["thriller"], "A tense one.", 9.0m)],
            "something tense", "likes thrillers", anchorName: null, purpose);

        var (calls, _) = recorder.PendingToday(ApiProvider.OpenRouter, accountId: null, purpose);
        Assert.Equal(1, calls);

        // And nothing landed under any sibling — the failure mode is one bucket absorbing the
        // family, which an assertion on the expected bucket alone cannot see.
        foreach (var other in ApiUsagePurpose.RerankFamily.Where(p => !string.Equals(p, purpose, StringComparison.Ordinal)))
        {
            Assert.Equal(0, recorder.PendingToday(ApiProvider.OpenRouter, accountId: null, other).Calls);
        }
    }

    [Fact]
    public async Task A_caller_that_passes_nothing_still_bills_the_legacy_string_the_ceiling_counts()
    {
        // The default is "counted, unattributed", never "free". If this ever answers zero calls
        // under every family member, an unported caller has become invisible spend.
        var handler = new CannedHandler("""{"results":[]}""");
        var recorder = Recorder();

        await Agent(handler, recorder).RerankBatchAsync(
            [new(TitleA, "Alpha", 1984, "movie", ["thriller"], "A tense one.", 9.0m)],
            "something tense", "likes thrillers");

        Assert.Equal(1, recorder.PendingToday(ApiProvider.OpenRouter, accountId: null, ApiUsagePurpose.Rerank).Calls);
    }

    [Fact]
    public async Task The_interpret_call_bills_the_agent_purpose_not_a_rerank_one()
    {
        var handler = new CannedHandler("""{"reply":"ok","genres":["science-fiction"]}""");
        var recorder = Recorder();

        await Agent(handler, recorder).InterpretAsync([], "80s sci-fi movies", "likes sci-fi", []);

        Assert.Equal(1, recorder.PendingToday(ApiProvider.OpenRouter, accountId: null, ApiUsagePurpose.SearchAgent).Calls);
        Assert.All(
            ApiUsagePurpose.RerankFamily,
            p => Assert.Equal(0, recorder.PendingToday(ApiProvider.OpenRouter, accountId: null, p).Calls));
    }

    [Fact]
    public async Task The_standing_filter_summary_is_accepted_and_changes_nothing_yet()
    {
        // The port carries it before any prompt reads it. This pins the "changes nothing" half, so
        // the commit that DOES inject it into the prompt has to move this assertion deliberately.
        var handler = new CannedHandler("""{"reply":"ok","genres":["science-fiction"]}""");
        var withFilters = await Agent(handler, Recorder()).InterpretAsync(
            [], "80s sci-fi movies", "likes sci-fi", [], standingFilters: "never horror; nothing before 1990");

        var plainHandler = new CannedHandler("""{"reply":"ok","genres":["science-fiction"]}""");
        var without = await Agent(plainHandler, Recorder()).InterpretAsync([], "80s sci-fi movies", "likes sci-fi", []);

        Assert.Equal(without.Reply, withFilters.Reply);
        Assert.Equal(plainHandler.Bodies[0], handler.Bodies[0]);
    }

    private static ApiUsageRecorder Recorder()
    {
        var scopeFactory = new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        return new ApiUsageRecorder(scopeFactory, NullLogger<ApiUsageRecorder>.Instance);
    }

    private static OpenRouterSearchAgent Agent(CannedHandler handler, ApiUsageRecorder recorder)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OPENROUTER_API_KEY"] = "test-key-not-a-real-one",
                ["TestHelpers:ModelStubMode"] = "false",
            })
            .Build();
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://openrouter.invalid/api/v1/") };
        return new OpenRouterSearchAgent(
            client, new HeuristicInterpreter(), recorder, config, NullLogger<OpenRouterSearchAgent>.Instance);
    }

    /// <summary>Free and deterministic — the paid interpreter must never be reached on these paths.</summary>
    private sealed class HeuristicInterpreter : ISearchQueryInterpreter
    {
        public bool IsAvailable => true;

        public string ModelId => "fake/heuristic";

        public Task<SearchIntent> InterpretAsync(string query, CancellationToken ct = default) =>
            Task.FromResult(Intent(query));

        public SearchIntent Heuristic(string query) => Intent(query);

        private static SearchIntent Intent(string query) =>
            new([], [query], Array.Empty<MediaType>(), query, null, null);
    }

    private sealed class CannedHandler(string content) : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(ct));
            var envelope = JsonSerializer.Serialize(new
            {
                choices = new[] { new { message = new { role = "assistant", content }, finish_reason = "stop" } },
            });
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(envelope, Encoding.UTF8, "application/json"),
            };
        }
    }
}
