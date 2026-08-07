using System.Text.Json;
using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Infrastructure.Adapters;

namespace FrostAura.Reel.Tests.Search;

/// <summary>
/// The discovery-spec parser — single-sourced between the interpreter and the agent. Covers the
/// new person dimension that powers "all movies by a director" (the rest of the contract — genre
/// vocabulary, media mapping — is exercised through the live-search integration tests).
/// </summary>
public class SearchIntentParserTests
{
    private static readonly SearchIntent EmptyFallback = new([], [], [], "fallback", null, null);

    private static SearchIntent Parse(string json) =>
        SearchIntentParser.FromJson(JsonDocument.Parse(json).RootElement, "the query", EmptyFallback);

    [Fact]
    public void Extracts_a_director_with_role()
    {
        var intent = Parse("""{ "people": [{ "name": "Christopher Nolan", "role": "director" }] }""");

        var person = Assert.Single(intent.People!);
        Assert.Equal("Christopher Nolan", person.Name);
        Assert.Equal(PersonRole.Director, person.Role);
    }

    [Theory]
    [InlineData("director", PersonRole.Director)]
    [InlineData("cast", PersonRole.Cast)]
    [InlineData("actor", PersonRole.Cast)]
    [InlineData("starring", PersonRole.Cast)]
    [InlineData("", PersonRole.Any)]
    [InlineData("producer", PersonRole.Any)]
    public void Maps_role_strings(string role, PersonRole expected)
    {
        var intent = Parse($$"""{ "people": [{ "name": "Someone", "role": "{{role}}" }] }""");
        Assert.Equal(expected, Assert.Single(intent.People!).Role);
    }

    [Fact]
    public void A_person_only_query_is_not_swallowed_by_the_empty_discovery_fallback()
    {
        // No genres/keywords — a name IS the signal, so the heuristic fallback must NOT take over.
        var intent = Parse("""{ "people": [{ "name": "Greta Gerwig", "role": "director" }], "genres": [], "keywords": [] }""");

        Assert.NotEmpty(intent.People!);
        Assert.Equal("Greta Gerwig", intent.People![0].Name);
    }

    [Fact]
    public void No_people_key_yields_an_empty_list()
    {
        var intent = Parse("""{ "genres": ["thriller"], "keywords": ["heist"] }""");
        Assert.Empty(intent.People ?? []);
    }

    [Fact]
    public void Maps_reference_titles_preserving_case()
    {
        // Case survives parsing on purpose: these names go into the rerank prompt and TMDB title
        // search, where "blade runner" reads wrong — unlike genres/keywords, which lowercase.
        var intent = Parse("""{ "genres": ["science-fiction"], "referenceTitles": ["Blade Runner", "Severance"] }""");

        Assert.Equal(["Blade Runner", "Severance"], intent.ReferenceTitles);
    }

    [Fact]
    public void Reference_titles_are_deduped_and_capped_at_three()
    {
        var intent = Parse(
            """{ "genres": ["drama"], "referenceTitles": ["Heat", "heat", "Ronin", "Collateral", "Thief"] }""");

        Assert.Equal(["Heat", "Ronin", "Collateral"], intent.ReferenceTitles);
    }

    [Fact]
    public void A_reference_only_query_is_not_swallowed_by_the_empty_discovery_fallback()
    {
        // "More like Blade Runner" may carry no genre or keyword at all — the reference IS the
        // discovery signal, exactly like a named person.
        var intent = Parse("""{ "genres": [], "keywords": [], "referenceTitles": ["Blade Runner"] }""");

        Assert.Equal(["Blade Runner"], intent.ReferenceTitles);
    }

    [Fact]
    public void Maps_the_max_runtime_bound()
    {
        var intent = Parse("""{ "genres": ["comedy"], "maxRuntimeMinutes": 90 }""");
        Assert.Equal(90, intent.MaxRuntimeMinutes);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-30")]
    [InlineData("2001")] // a year, not a runtime — a model misfire is dropped, not applied
    [InlineData("\"90\"")]
    public void An_implausible_runtime_is_dropped(string raw)
    {
        var intent = Parse($$"""{ "genres": ["comedy"], "maxRuntimeMinutes": {{raw}} }""");
        Assert.Null(intent.MaxRuntimeMinutes);
    }

    [Fact]
    public void The_runtime_bound_survives_the_empty_discovery_fallback()
    {
        // Nothing usable but a runtime cap: the heuristic supplies the pull signal, the cap stays.
        var intent = Parse("""{ "genres": [], "keywords": [], "maxRuntimeMinutes": 95 }""");
        Assert.Equal(95, intent.MaxRuntimeMinutes);
    }
}
