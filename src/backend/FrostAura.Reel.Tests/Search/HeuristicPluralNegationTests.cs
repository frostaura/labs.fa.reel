using System.Net;
using System.Text;
using System.Text.Json;
using FrostAura.Reel.Application.Search;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Infrastructure.Adapters;
using FrostAura.Reel.Infrastructure.Telemetry;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrostAura.Reel.Tests.Search;

/// <summary>
/// IREV1-SYS-03. The requested half of a turn's negation model is read ONLY from the interpreter's
/// free deterministic reading, so a plural the scanner cannot singularise is a rejection the turn
/// never enforces. "superheroes" lost only its final "s" and reached the vocabulary as
/// "superheroe", which names nothing — so "no superheroes please" produced no exclusion at all, and
/// with no positive word left the whole rejection became the search string.
///
/// <para>The fixture is varied on purpose: the "-oes" and "-ches" plurals that were broken, the
/// "-ies" and plain "-s" plurals that already worked (so the fallback is proven not to replace
/// them), the singular, a rejection inside a longer sentence, and a plural the vocabulary does not
/// name (so the floor is proven intact).</para>
/// </summary>
public class HeuristicPluralNegationTests
{
    [Theory]
    [InlineData("no superheroes please")]
    [InlineData("no superheroes")]
    [InlineData("nothing with superheroes")]
    [InlineData("No Superheroes, please!")]
    public void An_oes_plural_negation_lands_as_the_vocabulary_singular(string query)
    {
        var intent = Interpreter().Heuristic(query);

        Assert.Equal(["superhero"], intent.ExcludeKeywords);
        // The rejection is never searched for: neither the mangled singular nor the sentence itself.
        Assert.Empty(intent.Keywords);
        Assert.Empty(intent.Genres);
    }

    [Fact]
    public void An_oes_plural_negation_after_a_positive_request_keeps_both_halves()
    {
        var intent = Interpreter().Heuristic("big action films, no superheroes at all");

        Assert.Equal(["superhero"], intent.ExcludeKeywords);
        Assert.Contains("action", intent.Genres);
        Assert.DoesNotContain(intent.Keywords, k => k.StartsWith("superhero", StringComparison.Ordinal));
    }

    [Theory]
    // Broken before: "-ches" lost only its "s" ("witche").
    [InlineData("no witches", "witch")]
    // Worked before, and must not change: the singular, the "-ies" pair, the plain "-s".
    [InlineData("no superhero please", "superhero")]
    [InlineData("no zombies", "zombie")]
    [InlineData("no ghosts", "ghost")]
    [InlineData("no vampires please", "vampire")]
    public void Every_plural_shape_negates_exactly_one_vocabulary_term(string query, string expected)
    {
        var intent = Interpreter().Heuristic(query);

        Assert.Equal([expected], intent.ExcludeKeywords);
    }

    [Theory]
    // A plural the vocabulary does not name is still dropped by the floor — the fallback offers
    // more spellings of a WORD, it never widens what counts as one.
    [InlineData("no tomatoes")]
    [InlineData("nothing with boxes")]
    public void A_plural_outside_the_vocabulary_is_still_not_a_negation(string query)
    {
        var intent = Interpreter().Heuristic(query);

        Assert.Empty(intent.ExcludeKeywords ?? []);
    }

    [Fact]
    public void The_positive_reading_of_the_same_plural_is_untouched()
    {
        // The fallback lives in the negated branch only; a request FOR superheroes is not a rejection.
        var intent = Interpreter().Heuristic("superheroes");

        Assert.Empty(intent.ExcludeKeywords ?? []);
        Assert.NotEmpty(intent.Keywords);
    }

    /// <summary>
    /// The chain the defect broke, with nothing scripted in the middle: the live agent answers with a
    /// model reply that launders the rejection away, so the requested negation can only come from the
    /// interpreter's real heuristic — and a title the catalogue tags "superhero" must leave the picks.
    /// The probe beside it moves one field (the turn's words) and the same title stays a pick, so it
    /// is the negation that moved it, not the fixture.
    /// </summary>
    [Theory]
    [InlineData("feel-good movies, no superheroes please", false)]
    [InlineData("feel-good movies please", true)]
    public async Task The_real_heuristic_carries_the_rejection_through_to_the_partition(string query, bool heroIsAPick)
    {
        var agent = LiveAgent("""{"reply":"Feel-good films coming up.","genres":["comedy"],"keywords":["feel-good"]}""");

        var reply = await agent.InterpretAsync([], query, "likes comedies", []);
        var negations = AskReelNegations.For(FilterRuleSet.Create([], [], [], [], null, null, null), reply.Requested);

        var hero = TitleNamed("Shadow Master", keywords: ["superhero", "martial arts"]);
        var pool = Enumerable.Range(0, 12)
            .Select(i => (TitleNamed($"Calm {i}", keywords: ["friendship"]), 0.9 - (i * 0.01)))
            .Prepend((hero, 0.99))
            .ToList();

        var (ranked, outside) = LiveSearchExpansionService.PartitionByNegation(pool, negations);

        Assert.Equal(heroIsAPick, ranked.Any(x => x.Title.Name == "Shadow Master"));
        Assert.DoesNotContain(outside, x => x.Title.Name == "Shadow Master");
        if (!heroIsAPick)
        {
            Assert.Equal(["superhero"], negations.RequestedKeywords);
            Assert.Contains(
                LiveSearchExpansionService.NegationMatches(hero, negations),
                m => m is { Phrase: "one that matches ‘superhero’", Standing: false });
        }
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────

    private static OpenRouterSearchInterpreter Interpreter() =>
        new(
            new HttpClient(new ThrowingHandler()),
            Recorder(),
            new ConfigurationBuilder().Build(),
            NullLogger<OpenRouterSearchInterpreter>.Instance);

    private static OpenRouterSearchAgent LiveAgent(string modelContent)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OPENROUTER_API_KEY"] = "test-key-not-a-real-one",
                ["TestHelpers:ModelStubMode"] = "false",
            })
            .Build();
        var client = new HttpClient(new CannedHandler(modelContent)) { BaseAddress = new Uri("https://openrouter.invalid/api/v1/") };
        return new OpenRouterSearchAgent(client, Interpreter(), Recorder(), config, NullLogger<OpenRouterSearchAgent>.Instance);
    }

    private static Title TitleNamed(string name, string[] keywords) => new()
    {
        Id = Guid.NewGuid(),
        MediaType = MediaType.Movie,
        Name = name,
        Genres = ["comedy"],
        Keywords = keywords,
        Overview = "A warm story about people.",
    };

    private static ApiUsageRecorder Recorder() =>
        new(
            new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            NullLogger<ApiUsageRecorder>.Instance);

    private sealed class CannedHandler(string content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(new
                    {
                        choices = new[] { new { message = new { role = "assistant", content }, finish_reason = "stop" } },
                    }),
                    Encoding.UTF8,
                    "application/json"),
            });
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new InvalidOperationException("the heuristic must not make a network call");
    }
}
