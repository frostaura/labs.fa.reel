using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Infrastructure.Adapters;
using FrostAura.Reel.Infrastructure.Telemetry;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrostAura.Reel.Tests.Search;

/// <summary>
/// Regression for Ask Reel answering the opposite of the question. The intent parser had no notion
/// of negation, so every rejected term became a positive discovery signal: "more thriller and less
/// horror, I don't want ghosts I want serial killers" resolved to genres:[horror],
/// keywords:[ghosts] and went to TMDB to fetch ghost stories. Genres and keywords are FETCH
/// signals — a negated term landing in them is not a ranking nuisance, it actively sources the
/// wrong catalogue.
/// </summary>
public class SearchNegationTests
{
    private static SearchIntent Interpret(string query)
    {
        // No API key → the deterministic heuristic path, which is exactly what this pins.
        var configuration = new ConfigurationBuilder().AddInMemoryCollection([]).Build();
        var services = new ServiceCollection().BuildServiceProvider();
        var recorder = new ApiUsageRecorder(
            services.GetRequiredService<IServiceScopeFactory>(), NullLogger<ApiUsageRecorder>.Instance);
        var interpreter = new OpenRouterSearchInterpreter(
            new HttpClient(), recorder, configuration, NullLogger<OpenRouterSearchInterpreter>.Instance);
        return interpreter.InterpretAsync(query).GetAwaiter().GetResult();
    }

    [Fact]
    public void A_negated_genre_never_becomes_a_positive_pull_signal()
    {
        var intent = Interpret("more thriller and less horror");

        Assert.Contains("thriller", intent.Genres);
        Assert.DoesNotContain("horror", intent.Genres);
        Assert.Contains("horror", intent.ExcludeGenres ?? []);
    }

    [Fact]
    public void A_rejected_concept_is_excluded_and_the_wanted_one_survives_the_same_sentence()
    {
        // The exact phrasing that produced ghost stories.
        var intent = Interpret("I don't want ghosts I want serial killers");

        Assert.Contains("ghost", intent.ExcludeKeywords ?? []);
        Assert.DoesNotContain("ghost", intent.Keywords);
        // "I want …" closes the negation scope — the ask after the pivot must stay positive.
        Assert.Contains(intent.Keywords, k => k.Contains("killer", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_plain_request_is_unaffected_by_the_negation_machinery()
    {
        var intent = Interpret("dark thriller serial killer");

        Assert.Contains("thriller", intent.Genres);
        Assert.Empty(intent.ExcludeGenres ?? []);
        Assert.Empty(intent.ExcludeKeywords ?? []);
    }

    [Fact]
    public void A_turn_that_is_only_a_rejection_does_not_search_for_the_rejected_thing()
    {
        // The "no positive signal → use the whole phrase as a keyword" fallback must not fire here,
        // or "no horror" would be sent to TMDB as the search string "no horror".
        var intent = Interpret("no horror");

        Assert.Contains("horror", intent.ExcludeGenres ?? []);
        Assert.DoesNotContain("no horror", intent.Keywords);
        Assert.DoesNotContain("horror", intent.Genres);
    }
}
