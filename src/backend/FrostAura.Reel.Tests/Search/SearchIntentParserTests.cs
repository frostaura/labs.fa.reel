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
    private static readonly SearchIntent EmptyFallback = new([], [], [], "fallback", null);

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
}
