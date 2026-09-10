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
/// The behavioural contract for dropping the agent's duplicate interpreter call.
///
/// <para>
/// The agent used to <c>await interpreter.InterpretAsync</c> — a second billable OpenRouter
/// completion — on EVERY Ask Reel turn, and then hand the result to
/// <see cref="SearchIntentParser.FromJson"/> as a fallback the parser consumes only when the
/// model's own answer carries no usable signal. The call is now the free deterministic
/// <see cref="ISearchQueryInterpreter.Heuristic"/>, and the paid call survives only on the
/// failure/empty paths.
/// </para>
///
/// <para>
/// This fixture is the "nothing changed" half of that claim: twenty representative queries, each
/// with a realistic model payload — including the thin and empty ones that actually exercise the
/// fallback branch — asserted equal, field for field, to the parser's own reading over the
/// deterministic heuristic. The interpreter here is the real <c>OpenRouterSearchInterpreter</c> in
/// stub mode behind an HTTP handler that throws, so a reinstated network call fails the test rather
/// than quietly costing money.
/// </para>
/// </summary>
public class AskReelIntentFixtureTests
{
    /// <summary>(query, model payload) — thin and empty payloads are deliberate: they are the only
    /// ones where the fallback intent reaches the output at all.</summary>
    public static TheoryData<string, string> Fixture() => new()
    {
        { "shark horrors", """{"reply":"ok","genres":["horror"],"keywords":["shark"]}""" },
        { "80s sci-fi movies", """{"reply":"ok","genres":["science-fiction"],"keywords":["space"],"mediaTypes":["movie"],"minYear":1980,"maxYear":1989,"freeText":"80s sci-fi"}""" },
        { "something tense for tonight", """{"reply":"ok","genres":["thriller"],"keywords":["tense"]}""" },
        { "funny but not stupid", """{"reply":"ok","genres":["comedy"],"keywords":["smart"],"excludeKeywords":["slapstick"]}""" },
        { "no ghosts please", """{"reply":"ok","genres":["horror"],"excludeKeywords":["ghost"]}""" },
        { "less horror more drama", """{"reply":"ok","genres":["drama","horror"],"excludeGenres":["horror"]}""" },
        { "like Blade Runner but more emotional", """{"reply":"ok","genres":["science-fiction"],"referenceTitles":["Blade Runner"],"keywords":["melancholy"]}""" },
        { "under 90 minutes", """{"reply":"ok","keywords":["short"],"maxRuntimeMinutes":90}""" },
        { "movies Christopher Nolan directs", """{"reply":"ok","people":[{"name":"Christopher Nolan","role":"director"}]}""" },
        { "anything with Tom Hardy", """{"reply":"ok","people":[{"name":"Tom Hardy","role":"cast"}]}""" },
        { "prestige tv dramas", """{"reply":"ok","genres":["drama"],"mediaTypes":["tv"],"keywords":["prestige"]}""" },
        { "a heist film", """{"reply":"ok","genres":["crime"],"keywords":["heist"]}""" },
        { "documentaries about the ocean", """{"reply":"ok","genres":["documentary"],"keywords":["ocean"]}""" },
        { "slow burn small town dread", """{"reply":"ok","genres":["thriller"],"keywords":["slow burn","small town"]}""" },
        { "inverted decade bounds", """{"reply":"ok","genres":["science-fiction"],"minYear":1989,"maxYear":1980}""" },
        { "surprise me", """{"reply":"ok"}""" },
        { "shark horrors from the nineties", """{"reply":"ok"}""" },
        { "not a musical", """{"reply":"ok","excludeGenres":["music"]}""" },
        { "korean thrillers", """{"reply":"ok","genres":["thriller"],"keywords":["korean"]}""" },
        { "kids animation", """{"reply":"ok","genres":["animation","family"],"mediaTypes":["movie"]}""" },
    };

    [Theory]
    [MemberData(nameof(Fixture))]
    public async Task Parsed_intent_is_the_parser_over_the_free_heuristic(string query, string payload)
    {
        var interpreter = StubInterpreter();
        var agent = Agent(new CannedHandler(Ok(payload)), interpreter);

        var actual = (await agent.InterpretAsync([], query, "likes thrillers", [])).Intent;

        using var doc = JsonDocument.Parse(payload);
        var expected = SearchIntentParser.FromJson(doc.RootElement, query, interpreter.Heuristic(query));

        Assert.Equal(expected.Genres, actual.Genres);
        Assert.Equal(expected.Keywords, actual.Keywords);
        Assert.Equal(expected.MediaTypes, actual.MediaTypes);
        Assert.Equal(expected.FreeText, actual.FreeText);
        Assert.Equal(expected.MinYear, actual.MinYear);
        Assert.Equal(expected.MaxYear, actual.MaxYear);
        Assert.Equal(expected.MaxRuntimeMinutes, actual.MaxRuntimeMinutes);
        Assert.Equal(expected.ExcludeGenres ?? [], actual.ExcludeGenres ?? []);
        Assert.Equal(expected.ExcludeKeywords ?? [], actual.ExcludeKeywords ?? []);
        Assert.Equal(expected.ReferenceTitles ?? [], actual.ReferenceTitles ?? []);
        Assert.Equal(
            (expected.People ?? []).Select(p => (p.Name, p.Role)),
            (actual.People ?? []).Select(p => (p.Name, p.Role)));
    }

    [Fact]
    public void The_stub_interpreter_is_no_longer_unconditionally_available()
    {
        // Keyless, no stub flag: the interpreter must say so, exactly as the agent and the
        // attribute extractor already do. It still ANSWERS via the heuristic; it just stops
        // presenting a deterministic split as LLM interpretation.
        var keyless = new OpenRouterSearchInterpreter(
            new HttpClient(new ThrowingHandler()),
            Recorder(),
            new ConfigurationBuilder().Build(),
            NullLogger<OpenRouterSearchInterpreter>.Instance);

        Assert.False(keyless.IsAvailable);
        Assert.Equal(["horror"], keyless.Heuristic("shark horrors").Genres);
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────

    /// <summary>The real interpreter in stub mode — the production collaborator, minus the spend.</summary>
    private static OpenRouterSearchInterpreter StubInterpreter() =>
        new(
            new HttpClient(new ThrowingHandler()),
            Recorder(),
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["TestHelpers:ModelStubMode"] = "true" })
                .Build(),
            NullLogger<OpenRouterSearchInterpreter>.Instance);

    private static OpenRouterSearchAgent Agent(CannedHandler handler, ISearchQueryInterpreter interpreter)
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
            client, interpreter, Recorder(), config, NullLogger<OpenRouterSearchAgent>.Instance);
    }

    private static ApiUsageRecorder Recorder() =>
        new(
            new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            NullLogger<ApiUsageRecorder>.Instance);

    private static string Ok(string content) =>
        JsonSerializer.Serialize(new
        {
            choices = new[] { new { message = new { role = "assistant", content }, finish_reason = "stop" } },
        });

    private sealed class CannedHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
    }

    /// <summary>Any network call from the interpreter is a regression, so make it loud.</summary>
    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new InvalidOperationException("the interpreter must not make a network call on the happy path");
    }
}
