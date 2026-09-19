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

    // ── Bounds ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Every value here is DROPPED, not clamped: 9999, 10000 and 99999 all sit outside the
    /// (1870, 2100] calendar gate, and a bound that far out is a model misfire rather than a
    /// constraint worth honouring in a narrowed form. Clamping is reserved for a year the gate
    /// admits — see <see cref="An_in_range_future_year_is_clamped_rather_than_dropped"/>.
    ///
    /// <para>The bound is asserted on BOTH ends because only maxYear was ever gated on the upper
    /// side; minYear passed anything above 1870, so a model answering minYear 99999 reached
    /// <c>new DateTime(99999, …)</c> in the discovery service and threw
    /// ArgumentOutOfRangeException out of a live turn.</para>
    /// </summary>
    [Theory]
    [InlineData("maxYear", 99999)]
    [InlineData("maxYear", 10000)]
    [InlineData("maxYear", 9999)]
    [InlineData("minYear", 99999)]
    public void MaxYear_beyond_the_calendar_is_dropped(string field, int year)
    {
        var intent = Parse($$"""{ "genres": ["drama"], "{{field}}": {{year}} }""");

        Assert.Null(field == "maxYear" ? intent.MaxYear : intent.MinYear);
    }

    [Fact]
    public void An_in_range_future_year_is_clamped_rather_than_dropped()
    {
        // 2100 is inside the gate, so it survives as a constraint — narrowed to a year the
        // catalogue could plausibly contain rather than passed to TMDB verbatim.
        var ceiling = DateTime.UtcNow.Year + 5;

        Assert.Equal(ceiling, Parse("""{ "genres": ["drama"], "maxYear": 2100 }""").MaxYear);

        // And the clamp can never invert the window: a lower bound above the clamped ceiling is
        // pulled down with it, instead of leaving min > max, which matches nothing at all.
        var both = Parse("""{ "genres": ["drama"], "minYear": 2050, "maxYear": 2060 }""");
        Assert.Equal(ceiling, both.MaxYear);
        Assert.Equal(ceiling, both.MinYear);
    }

    [Fact]
    public void FreeText_is_capped_at_120_chars()
    {
        // The probe: an emoji straddling the cut. A naive value[..120] splits its surrogate pair
        // and yields a lone high surrogate, which is not a character at all — it reaches TMDB's
        // query string as U+FFFD and silently changes the search.
        var text = new string('a', 119) + "😀" + new string('b', 40);
        var intent = Parse($$"""{ "genres": ["drama"], "freeText": "{{text}}" }""");

        Assert.NotNull(intent.FreeText);
        Assert.True(intent.FreeText!.Length <= 120, $"length was {intent.FreeText.Length}");
        Assert.Equal(new string('a', 119), intent.FreeText); // the emoji did not half-survive
        Assert.DoesNotContain(intent.FreeText, c => char.IsSurrogate(c));

        // A string that fits is returned untouched, emoji and all.
        var short_ = "sharks 😀 in space";
        Assert.Equal(short_, Parse($$"""{ "genres": ["drama"], "freeText": "{{short_}}" }""").FreeText);
    }

    [Fact]
    public void Fallback_branch_carries_the_clamped_year()
    {
        // No genres, keywords, people or references: the heuristic supplies the pull signal and
        // the year bounds ride along. They must be the CLAMPED values — the branch used to hand
        // through whatever the model said, so every bound guard above was bypassed by the one
        // path a failing model actually takes.
        var ceiling = DateTime.UtcNow.Year + 5;
        var intent = Parse("""{ "genres": [], "keywords": [], "minYear": 2050, "maxYear": 2100 }""");

        Assert.Equal(ceiling, intent.MaxYear);
        Assert.Equal(ceiling, intent.MinYear);

        // And an out-of-calendar bound is dropped here too, leaving the fallback's own value.
        Assert.Null(Parse("""{ "genres": [], "keywords": [], "maxYear": 99999 }""").MaxYear);
    }
}
