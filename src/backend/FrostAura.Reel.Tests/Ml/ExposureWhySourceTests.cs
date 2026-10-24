using FrostAura.Reel.Application.Ml.ClosedLoop;

namespace FrostAura.Reel.Tests.Ml;

/// <summary>
/// The typed why-source contract the exposure writers (feed, Ask, digest) code against. The stored
/// names are pinned literally here because the log is append-only: a renamed name would split one
/// source into two for every accuracy query that ever groups by it.
/// </summary>
public class ExposureWhySourceTests
{
    private static readonly string[] StoredVocabulary =
        ["Unknown", "Llm", "Cache", "Templater", "Preserved", "Generic", "Digest", "Facet", "Fallback", "None"];

    [Fact]
    public void The_stored_vocabulary_is_pinned_name_for_name()
    {
        Assert.Equal(StoredVocabulary, ExposureWhySource.All.Select(s => s.Name).ToArray());
    }

    [Fact]
    public void Every_kind_has_exactly_one_typed_source_and_a_distinct_name()
    {
        var kinds = Enum.GetValues<WhySourceKind>();

        Assert.Equal(kinds.Length, ExposureWhySource.All.Count);
        Assert.Equal(kinds, ExposureWhySource.All.Select(s => s.Kind).ToArray());
        Assert.Equal(kinds.Length, ExposureWhySource.All.Select(s => s.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void Every_name_fits_the_varchar_32_column()
    {
        Assert.All(ExposureWhySource.All, s => Assert.InRange(s.Name.Length, 1, 32));
    }

    [Fact]
    public void An_unset_source_is_unknown_on_the_struct_and_on_the_card()
    {
        Assert.Equal(ExposureWhySource.Unknown, default);
        Assert.Equal(WhySourceKind.Unknown, default(ExposureWhySource).Kind);
        Assert.Equal("Unknown", default(ExposureWhySource).Name);

        var card = new ExposureCard(Guid.NewGuid(), 0, 7m);
        Assert.Equal(ExposureWhySource.Unknown, card.WhySource);
    }

    [Fact]
    public void A_typed_source_on_the_card_is_the_one_it_was_given()
    {
        var card = new ExposureCard(Guid.NewGuid(), 3, 7.25m, WhySentence: "Shares the heist keyword.", WhySource: ExposureWhySource.Facet);

        Assert.Equal(WhySourceKind.Facet, card.WhySource.Kind);
        Assert.Equal("Facet", card.WhySource.Name);
    }

    [Theory]
    [InlineData(WhySourceKind.Llm, "Llm")]
    [InlineData(WhySourceKind.Cache, "Cache")]
    [InlineData(WhySourceKind.Templater, "Templater")]
    [InlineData(WhySourceKind.Preserved, "Preserved")]
    [InlineData(WhySourceKind.Generic, "Generic")]
    [InlineData(WhySourceKind.Digest, "Digest")]
    [InlineData(WhySourceKind.Facet, "Facet")]
    [InlineData(WhySourceKind.Fallback, "Fallback")]
    [InlineData(WhySourceKind.None, "None")]
    [InlineData(WhySourceKind.Unknown, "Unknown")]
    public void Every_source_round_trips_through_its_stored_name(WhySourceKind kind, string stored)
    {
        var source = ExposureWhySource.From(kind);

        Assert.Equal(stored, source.Name);
        Assert.Equal(stored, source.ToString());
        string implicitName = source;
        Assert.Equal(stored, implicitName);

        Assert.True(ExposureWhySource.TryParse(stored, out var parsed));
        Assert.Equal(source, parsed);
        Assert.Equal(source, ExposureWhySource.Parse(stored));
    }

    [Theory]
    [InlineData("llm", WhySourceKind.Llm)]
    [InlineData("FALLBACK", WhySourceKind.Fallback)]
    [InlineData("  Templater ", WhySourceKind.Templater)]
    public void Parsing_tolerates_case_and_surrounding_whitespace(string stored, WhySourceKind expected)
    {
        Assert.True(ExposureWhySource.TryParse(stored, out var parsed));
        Assert.Equal(expected, parsed.Kind);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Llm2")]
    [InlineData("GenreRail")]
    [InlineData("ModelV3")]
    public void Any_value_a_reader_does_not_know_maps_to_unknown_without_throwing(string? stored)
    {
        // Legacy rows hold the empty string; a newer image may add a name this one has never seen.
        // Both must read back as Unknown, which is what makes an image rollback safe.
        Assert.False(ExposureWhySource.TryParse(stored, out var parsed));
        Assert.Equal(ExposureWhySource.Unknown, parsed);
        Assert.Equal(ExposureWhySource.Unknown, ExposureWhySource.Parse(stored));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(10)]
    [InlineData(99)]
    public void An_undefined_kind_is_recorded_as_unknown_never_as_a_number(int raw)
    {
        var source = ExposureWhySource.From((WhySourceKind)raw);

        Assert.Equal(ExposureWhySource.Unknown, source);
        Assert.Equal("Unknown", source.Name);
    }

    [Fact]
    public void Equality_is_by_kind()
    {
        Assert.True(ExposureWhySource.Llm == ExposureWhySource.From(WhySourceKind.Llm));
        Assert.True(ExposureWhySource.Llm != ExposureWhySource.Cache);
        Assert.False(ExposureWhySource.Fallback.Equals((object)"Fallback"));
        Assert.Equal(ExposureWhySource.All.Count, ExposureWhySource.All.Select(s => s.GetHashCode()).Distinct().Count());
    }
}
