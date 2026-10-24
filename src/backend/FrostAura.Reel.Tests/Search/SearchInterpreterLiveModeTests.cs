using System.Net;
using System.Text;
using System.Text.Json;
using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Infrastructure.Adapters;
using FrostAura.Reel.Infrastructure.Telemetry;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrostAura.Reel.Tests.Search;

/// <summary>
/// The LIVE fallback interpreter path against a canned HTTP handler — the second of the two
/// prompt/parser pairs that must stay in step field-for-field (Ask Reel routes through the
/// agent's own prompt; this one serves everything else). Phase-8 pin: the prompt asks for
/// referenceTitles and maxRuntimeMinutes, and a model answer carrying them reaches the intent.
/// </summary>
public class SearchInterpreterLiveModeTests
{
    [Fact]
    public async Task The_system_prompt_requests_reference_titles_and_the_runtime_bound()
    {
        var handler = new CannedHandler(Ok("""{"genres":["science-fiction"]}"""));

        await Interpreter(handler).InterpretAsync("like blade runner, under 90 minutes");

        var system = SystemMessageOf(handler.Bodies[0]);
        Assert.Contains("referenceTitles", system, StringComparison.Ordinal);
        Assert.Contains("maxRuntimeMinutes", system, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reference_titles_and_runtime_bound_map_into_the_intent()
    {
        var payload = """
            {"genres":["science-fiction"],"keywords":["melancholy"],
             "referenceTitles":["Blade Runner","Severance"],"maxRuntimeMinutes":90,
             "freeText":"like blade runner"}
            """;
        var interpreter = Interpreter(new CannedHandler(Ok(payload)));

        var intent = await interpreter.InterpretAsync("like Blade Runner but more emotional, under 90 minutes");

        Assert.Equal(["Blade Runner", "Severance"], intent.ReferenceTitles);
        Assert.Equal(90, intent.MaxRuntimeMinutes);
    }

    // ── The recovery path's conversation ────────────────────────────────────────────────────

    [Fact]
    public async Task The_history_overload_carries_the_conversation_into_the_user_message()
    {
        // Ask Reel's recovery path. A fallback blind to the conversation reads "the second one" as
        // a keyword and forgets the negation made a turn earlier — a different question answered.
        var handler = new CannedHandler(Ok("""{"genres":["action"]}"""));
        IReadOnlyList<ChatTurn> history =
        [
            new("user", "ninja movies, nothing with superheroes"),
            new("assistant", "Three ninja films coming up."),
        ];

        await Interpreter(handler).InterpretAsync("more like the second one", history, default, "never horror");

        var user = UserMessageOf(handler.Bodies[0]);
        Assert.Contains("user: ninja movies, nothing with superheroes", user, StringComparison.Ordinal);
        Assert.Contains("assistant: Three ninja films coming up.", user, StringComparison.Ordinal);
        Assert.Contains("never turn a reference or a negation into a keyword", user, StringComparison.Ordinal);
        Assert.EndsWith("Request: more like the second one", user, StringComparison.Ordinal);
        // The standing rules still lead, exactly as the shared line words them.
        Assert.StartsWith(OpenRouterSearchAgent.StandingFiltersLine("never horror"), user, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_interface_overload_dispatches_to_the_live_adapter_rather_than_the_history_blind_default()
    {
        // The port's default for the history overload ignores the history so test doubles stay
        // valid. Called through the INTERFACE — the way the agent calls it — the live adapter must
        // be the one that answers, or the conversation is silently dropped in production.
        var handler = new CannedHandler(Ok("""{"genres":["action"]}"""));
        ISearchQueryInterpreter port = Interpreter(handler);

        await port.InterpretAsync("the first one", [new ChatTurn("user", "samurai films")]);

        Assert.Contains("user: samurai films", UserMessageOf(handler.Bodies[0]), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, "something scary")]
    [InlineData("never horror", "Standing filters (the account's own rules; the answer space is limited to titles that pass them): never horror\nRequest: something scary")]
    public async Task Without_history_the_user_message_is_byte_identical_to_before(string? filters, string expected)
    {
        // Both shapes the message had before the conversation could ride it: the bare query, and
        // the query beneath the standing-filters line. An empty history must change neither.
        var handler = new CannedHandler(Ok("""{"genres":["horror"]}"""));

        await Interpreter(handler).InterpretAsync("something scary", [], default, filters);

        Assert.Equal(expected, UserMessageOf(handler.Bodies[0]));
    }

    // ── helpers (mirrors OpenRouterSearchAgentLiveModeTests) ────────────────────────────────

    private static OpenRouterSearchInterpreter Interpreter(CannedHandler handler)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OPENROUTER_API_KEY"] = "test-key-not-a-real-one",
                ["TestHelpers:ModelStubMode"] = "false",
            })
            .Build();
        var scopeFactory = new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        var recorder = new ApiUsageRecorder(scopeFactory, NullLogger<ApiUsageRecorder>.Instance);
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://openrouter.invalid/api/v1/") };
        return new OpenRouterSearchInterpreter(client, recorder, config, NullLogger<OpenRouterSearchInterpreter>.Instance);
    }

    private static CannedResponse Ok(string content) => new(HttpStatusCode.OK, Envelope(content));

    private static string Envelope(string content) =>
        JsonSerializer.Serialize(new
        {
            choices = new[] { new { message = new { role = "assistant", content }, finish_reason = "stop" } },
        });

    private static string UserMessageOf(string body)
    {
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("messages").EnumerateArray()
            .First(m => m.GetProperty("role").GetString() == "user")
            .GetProperty("content").GetString()!;
    }

    private static string SystemMessageOf(string body)
    {
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("messages").EnumerateArray()
            .First(m => m.GetProperty("role").GetString() == "system")
            .GetProperty("content").GetString()!;
    }

    private sealed record CannedResponse(HttpStatusCode Status, string Body);

    private sealed class CannedHandler(params CannedResponse[] script) : HttpMessageHandler
    {
        private int _index;

        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(ct));
            var canned = script[Math.Min(_index++, script.Length - 1)];
            return new HttpResponseMessage(canned.Status)
            {
                Content = new StringContent(canned.Body, Encoding.UTF8, "application/json"),
            };
        }
    }
}
