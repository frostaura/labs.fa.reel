using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Infrastructure.Adapters;
using FrostAura.Reel.Infrastructure.Telemetry;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrostAura.Reel.Tests.Search;

/// <summary>
/// Regression for the negation SCOPE, as distinct from <see cref="SearchNegationTests"/> which
/// pins that a negation is detected at all.
///
/// The scope used to be a fixed three-token budget, which silently leaked the tail of any longer
/// rejection: "I don't want horror, gore, zombies or vampires" spent the budget on the first three
/// and read the fourth as a POSITIVE keyword — so discovery went looking for vampires, the exact
/// failure mode negation exists to prevent. The budget only existed because the tokenizer threw
/// commas away, which made a rejection list and a new clause indistinguishable. Clause boundaries
/// now survive tokenization, so both shapes parse correctly and neither needs a word count.
/// </summary>
public class SearchNegationScopeTests
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
    public void A_rejection_list_longer_than_three_items_excludes_every_item()
    {
        var intent = Interpret("I don't want horror, gore, zombies or vampires");

        Assert.Contains("horror", intent.ExcludeGenres ?? []);
        Assert.Contains("gore", intent.ExcludeKeywords ?? []);
        Assert.Contains("zombie", intent.ExcludeKeywords ?? []);

        // The one that used to leak: the fourth rejected term became a positive pull signal.
        Assert.Contains("vampire", intent.ExcludeKeywords ?? []);
        Assert.DoesNotContain("vampire", intent.Keywords);
        Assert.Empty(intent.Genres);
    }

    [Fact]
    public void A_clause_boundary_ends_the_negation_when_the_sentence_moves_on()
    {
        // The counterpart the old word budget got right by accident and a naive "negate until a
        // pivot" rule would get wrong: after the comma this is a fresh request, not more
        // rejections. ("scary" and "funny" are genre aliases, so they resolve to horror/comedy.)
        var intent = Interpret("not scary, something funny");

        Assert.Contains("horror", intent.ExcludeGenres ?? []);
        Assert.Contains("comedy", intent.Genres);
        Assert.DoesNotContain("comedy", intent.ExcludeGenres ?? []);
    }

    [Fact]
    public void A_positive_pivot_still_closes_the_negation()
    {
        var intent = Interpret("I don't want ghosts I want serial killers");

        Assert.Contains("ghost", intent.ExcludeKeywords ?? []);
        Assert.Contains("serial", intent.Keywords);
        Assert.DoesNotContain("serial", intent.ExcludeKeywords ?? []);
    }

    [Theory]
    [InlineData("80s sci-fi", 1980, 1989)]
    [InlineData("something from the 1990s", 1990, 1999)]
    [InlineData("a good film from the eighties", 1980, 1989)]
    [InlineData("nineties thrillers", 1990, 1999)]
    public void A_decade_resolves_to_both_bounds(string query, int expectedMin, int expectedMax)
    {
        // Without an upper bound "80s sci-fi" pulled 1980-to-today ordered by popularity, so modern
        // blockbusters dominated a request explicitly asking for old films.
        var intent = Interpret(query);

        Assert.Equal(expectedMin, intent.MinYear);
        Assert.Equal(expectedMax, intent.MaxYear);
    }

    [Fact]
    public void An_open_ended_year_phrase_sets_only_the_bound_it_names()
    {
        Assert.Equal((2015, null), Bounds(Interpret("horror after 2015")));
        Assert.Equal((null, 2000), Bounds(Interpret("horror before 2000")));

        static (int?, int?) Bounds(SearchIntent i) => (i.MinYear, i.MaxYear);
    }

    [Fact]
    public void A_title_that_looks_like_a_year_is_not_read_as_a_date_filter()
    {
        // "1917", "300" and "2001" are far more often the thing being searched for than a filter.
        var intent = Interpret("1917");

        Assert.Null(intent.MinYear);
        Assert.Null(intent.MaxYear);
    }
}
