using System.Text.Json;
using FrostAura.Reel.Domain.CrossReference;
using FrostAura.Reel.Infrastructure.Adapters;

namespace FrostAura.Reel.Tests.CrossReference;

/// <summary>
/// The parser, which is the real schema. A strict <c>json_schema</c> constrains each field
/// independently and cannot express "a keep may not be justified by tone-mismatch" or "that id was
/// not in this batch"; and the schema is downgraded to plain <c>json_object</c> the first time a
/// provider rejects it, after which the parser is the ONLY validation there is.
///
/// <para><b>One bad entry costs exactly one entry.</b> That is the defect these tests exist for:
/// the re-ranker once threw <c>InvalidOperationException</c> out of <c>GetString()</c> on a single
/// numeric <c>titleId</c> and silently cost every other title in the batch its model verdict. At
/// batch 60 that is 59 wasted judgements per malformed entry.</para>
/// </summary>
public class SchemaTests
{
    private static readonly Guid A = new("aaaaaaaa-0000-0000-0000-000000000000");

    private static readonly Guid B = new("bbbbbbbb-0000-0000-0000-000000000000");

    private static readonly Guid Outsider = new("cccccccc-0000-0000-0000-000000000000");

    [Fact]
    public void A_well_formed_batch_parses_every_entry()
    {
        var (entries, rejected) = Parse(
            $$"""
              {"results":[
                {"titleId":"{{A}}","verdict":"keep","reason":"neighbour-loved","confidence":0.82},
                {"titleId":"{{B}}","verdict":"out","reason":"tone-mismatch","confidence":0.91}
              ]}
              """);

        Assert.Equal(0, rejected);
        Assert.Equal(2, entries.Count);
        Assert.Equal(CrossReferenceVerdict.Keep, entries.Single(e => e.TitleId == A).Verdict);
        Assert.Equal(0.91d, entries.Single(e => e.TitleId == B).Confidence, 3);
    }

    [Fact]
    public void An_id_that_was_not_in_the_batch_costs_one_entry_and_no_more()
    {
        // The model remembering or inventing a title. Accepting it would write a verdict about a
        // title nobody asked about, keyed as if it had been judged this round.
        var (entries, rejected) = Parse(
            $$"""
              {"results":[
                {"titleId":"{{Outsider}}","verdict":"out","reason":"tone-mismatch","confidence":0.9},
                {"titleId":"{{A}}","verdict":"keep","reason":"theme-match","confidence":0.7}
              ]}
              """);

        Assert.Equal(1, rejected);
        Assert.Equal(A, Assert.Single(entries).TitleId);
    }

    [Fact]
    public void A_duplicate_id_costs_one_entry_and_the_first_answer_stands()
    {
        var (entries, rejected) = Parse(
            $$"""
              {"results":[
                {"titleId":"{{A}}","verdict":"keep","reason":"neighbour-loved","confidence":0.80},
                {"titleId":"{{A}}","verdict":"out","reason":"tone-mismatch","confidence":0.95}
              ]}
              """);

        Assert.Equal(1, rejected);
        var entry = Assert.Single(entries);
        // Two verdicts for one candidate is not a verdict; the first is kept because picking the
        // "stronger" one would let the model overwrite itself by shouting.
        Assert.Equal(CrossReferenceVerdict.Keep, entry.Verdict);
    }

    [Fact]
    public void A_numeric_titleId_costs_one_entry_and_never_the_batch()
    {
        var (entries, rejected) = Parse(
            $$"""
              {"results":[
                {"titleId":12345,"verdict":"out","reason":"tone-mismatch","confidence":0.9},
                {"titleId":"{{A}}","verdict":"unsure","reason":"insufficient-signal","confidence":0.5},
                {"titleId":"{{B}}","verdict":"out","reason":"pace-mismatch","confidence":0.88}
              ]}
              """);

        Assert.Equal(1, rejected);
        Assert.Equal(2, entries.Count);
    }

    [Theory]
    // keep admits only the three positive reasons.
    [InlineData("keep", "tone-mismatch")]
    [InlineData("keep", "insufficient-signal")]
    [InlineData("keep", "craft-below-bar")]
    // unsure admits only insufficient-signal — everything else is a thing it would have to be sure of.
    [InlineData("unsure", "neighbour-loved")]
    [InlineData("unsure", "genre-mismatch")]
    // out admits no positive reason.
    [InlineData("out", "neighbour-loved")]
    [InlineData("out", "theme-match")]
    public void An_illegal_verdict_and_reason_pair_is_rejected(string verdict, string reason)
    {
        var (entries, rejected) = Parse(
            $$"""
              {"results":[{"titleId":"{{A}}","verdict":"{{verdict}}","reason":"{{reason}}","confidence":0.8}]}
              """);

        Assert.Equal(1, rejected);
        Assert.Empty(entries);
    }

    [Theory]
    [InlineData("keep", "neighbour-loved")]
    [InlineData("keep", "tone-match")]
    [InlineData("keep", "theme-match")]
    [InlineData("unsure", "insufficient-signal")]
    [InlineData("out", "neighbour-disliked")]
    [InlineData("out", "format-mismatch")]
    [InlineData("out", "craft-below-bar")]
    public void A_legal_verdict_and_reason_pair_is_accepted(string verdict, string reason)
    {
        var (entries, rejected) = Parse(
            $$"""
              {"results":[{"titleId":"{{A}}","verdict":"{{verdict}}","reason":"{{reason}}","confidence":0.8}]}
              """);

        Assert.Equal(0, rejected);
        Assert.Single(entries);
    }

    [Theory]
    [InlineData(-0.5)]
    [InlineData(1.5)]
    [InlineData(85)]
    public void A_confidence_on_another_scale_is_rejected_rather_than_clamped(double confidence)
    {
        // Clamping would pin every such entry to 1.0 and flatten exactly the spread the field
        // exists to carry — the same call the re-ranker's fit parser makes, for the same reason.
        var (entries, rejected) = Parse(
            $$"""
              {"results":[{"titleId":"{{A}}","verdict":"out","reason":"tone-mismatch","confidence":{{confidence.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}]}
              """);

        Assert.Equal(1, rejected);
        Assert.Empty(entries);
    }

    [Theory]
    [InlineData(-0.005, 0d)]
    [InlineData(1.008, 1d)]
    public void A_confidence_just_outside_the_range_is_clamped_rather_than_rejected(double raw, double expected)
    {
        // The tolerance band exists for float printing, not for a different scale.
        var (entries, rejected) = Parse(
            $$"""
              {"results":[{"titleId":"{{A}}","verdict":"out","reason":"tone-mismatch","confidence":{{raw.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}]}
              """);

        Assert.Equal(0, rejected);
        Assert.Equal(expected, Assert.Single(entries).Confidence, 4);
    }

    [Fact]
    public void An_omitted_candidate_gets_no_row_rather_than_an_invented_unsure()
    {
        // Absence IS the resume signal: the job's only state is an anti-join over the verdict
        // table, so filling in a skipped candidate would mark it done forever.
        var (entries, _) = Parse(
            $$"""
              {"results":[{"titleId":"{{A}}","verdict":"keep","reason":"tone-match","confidence":0.7}]}
              """);

        Assert.Equal(A, Assert.Single(entries).TitleId);
        Assert.DoesNotContain(entries, e => e.TitleId == B);
    }

    [Fact]
    public void Unparseable_or_shapeless_content_yields_nothing_and_never_throws()
    {
        foreach (var content in new[] { "not json at all", "[]", "{}", "{\"results\":\"nope\"}", "{\"results\":[3,4]}" })
        {
            var (entries, _) = Parse(content);
            Assert.Empty(entries);
        }
    }

    [Fact]
    public void The_schema_bounds_the_array_and_closes_every_object()
    {
        // A free-text reason field would roughly triple completion tokens and trip
        // finish_reason=length at batch 60 — the closed vocabulary is a cost decision, and the
        // schema is where it is enforced rather than merely requested.
        var json = JsonSerializer.SerializeToElement(OpenRouterCrossReferenceJudge.ResultsSchema(60));
        var schema = json.GetProperty("json_schema");
        Assert.True(schema.GetProperty("strict").GetBoolean());

        var results = schema.GetProperty("schema").GetProperty("properties").GetProperty("results");
        Assert.Equal(60, results.GetProperty("maxItems").GetInt32());

        var item = results.GetProperty("items");
        Assert.False(item.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(
            ["titleId", "verdict", "reason", "confidence"],
            item.GetProperty("required").EnumerateArray().Select(e => e.GetString() ?? string.Empty).ToArray());

        var reasons = item.GetProperty("properties").GetProperty("reason").GetProperty("enum")
            .EnumerateArray().Select(e => e.GetString() ?? string.Empty).ToArray();
        Assert.Equal(CrossReferenceReasons.All, reasons);
    }

    private static (IReadOnlyList<Domain.Ports.CrossReferenceEntry> Entries, int Rejected) Parse(string content) =>
        OpenRouterCrossReferenceJudge.ParseResults(content, new HashSet<Guid> { A, B });
}
