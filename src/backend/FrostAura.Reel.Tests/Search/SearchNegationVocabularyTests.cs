using FrostAura.Reel.Application.Search;
using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Infrastructure.Adapters;
using FrostAura.Reel.Infrastructure.Telemetry;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrostAura.Reel.Tests.Search;

/// <summary>
/// R3-01 — the negation scanner had no floor, so it quoted the user's own stop words back at them
/// as things they had asked to skip.
///
/// <para>Measured on a live turn. The question contained "…no jump scares, <b>nothing set in
/// space</b>…" and "…I do <b>not</b> mind slow pacing <b>at all</b>…", and three cards came back in
/// the reserve channel captioned by the product itself: <i>"Closest match we could find, but it's
/// one that matches 'set' — which you asked to skip"</i> and the same sentence for <i>'all'</i>.
/// The user asked to skip neither. Worse than the wrong caption: a soft negation on a word that
/// appears in a large share of every overview in the catalogue demotes real matches out of the
/// picks — <c>Ripper Street</c>, a crime/mystery/drama exactly answering the request, was pushed
/// into the reserve for containing the word "set".</para>
///
/// <para><b>The rule this pins.</b> A user-stated negation is a negation only when its object
/// RESOLVES: to a genre the catalogue knows (slug or alias), or to a concept in
/// <see cref="SearchVocabulary"/>'s closed negatable vocabulary. A bare stop word or an
/// unresolvable token is neither — it is dropped entirely rather than demoted to a soft exclusion,
/// because the caption quotes the extracted token verbatim and a token too generic to caption is
/// too generic to negate.</para>
/// </summary>
public class SearchNegationVocabularyTests
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

    /// <summary>The repro, reduced to the clause that produced the caption.</summary>
    [Fact]
    public void A_negated_phrase_whose_words_resolve_to_nothing_produces_no_negation_at_all()
    {
        var intent = Interpret("nothing set in space");

        Assert.Empty(intent.ExcludeGenres ?? []);
        Assert.Empty(intent.ExcludeKeywords ?? []);
    }

    /// <summary>
    /// The second half of the same turn — "I do not mind slow pacing at all". "all" is the token
    /// that would match a very large share of any catalogue, and it was being applied as a soft
    /// exclusion on every card.
    /// </summary>
    [Fact]
    public void A_negator_trailing_off_into_stop_words_negates_none_of_them()
    {
        var intent = Interpret("a tense thriller, I do not mind slow pacing at all");

        Assert.Empty(intent.ExcludeKeywords ?? []);
        Assert.DoesNotContain("all", intent.ExcludeKeywords ?? []);
        Assert.DoesNotContain("set", intent.ExcludeKeywords ?? []);
        // The positive half of the sentence is untouched by the floor.
        Assert.Contains("thriller", intent.Genres);
    }

    /// <summary>The probe: one word moved. A resolvable concept in the same shape IS a negation.</summary>
    [Fact]
    public void A_negated_concept_the_vocabulary_knows_is_still_a_negation()
    {
        var intent = Interpret("no ghosts");

        // The scanner singularises before it stores, so the concept lands as "ghost".
        Assert.Contains("ghost", intent.ExcludeKeywords ?? []);
        Assert.Empty(intent.ExcludeGenres ?? []);
    }

    /// <summary>The probe: the same shape again, one vocabulary up. A genre word is a genre negation.</summary>
    [Fact]
    public void A_negated_genre_is_a_genre_negation_not_a_keyword_one()
    {
        var intent = Interpret("no comedy");

        Assert.Contains("comedy", intent.ExcludeGenres ?? []);
        Assert.Empty(intent.ExcludeKeywords ?? []);
    }

    /// <summary>
    /// "nothing", "none" and "never" are negators the scanner did not know, which is why the live
    /// repro's "nothing set in space" ran on as a POSITIVE pull for "set" and "space" in the first
    /// place. They open a scope now — and the floor is what keeps that from becoming the very bug
    /// this fixes.
    /// </summary>
    [Theory]
    [InlineData("nothing with zombies")]
    [InlineData("never any zombies")]
    public void The_scanner_knows_the_negators_the_repro_used(string query)
    {
        Assert.Contains("zombie", Interpret(query).ExcludeKeywords ?? []);
    }

    /// <summary>
    /// The floor is applied where the CAPTION is built too, not only where the scan happens: a
    /// negation that reaches <see cref="AskReelNegations"/> by some other route still cannot print
    /// a stop word as something the user asked to skip.
    /// </summary>
    [Fact]
    public void A_requested_negation_that_resolves_to_nothing_never_reaches_the_caption()
    {
        var negations = AskReelNegations.FromRequest(keywords: ["set", "all", "ghosts"]);

        Assert.Equal(["ghosts"], negations.RequestedKeywords);
    }

    /// <summary>
    /// And the account's OWN rules are never floored. A standing exclusion is a row the user wrote
    /// in the conditions editor behind a PIN; the product does not get to decide their word was too
    /// generic to honour.
    /// </summary>
    [Fact]
    public void A_standing_exclusion_is_never_floored()
    {
        var negations = AskReelNegations.FromStanding(keywords: ["set"]);

        Assert.Equal(["set"], negations.StandingKeywords);
    }
}
