using System.Text.Json;
using FrostAura.Reel.Domain.Lens;
using FrostAura.Reel.Infrastructure.Adapters;

namespace FrostAura.Reel.Tests.Lens;

/// <summary>
/// One test per field of the lens contract, including every way a model can answer badly while
/// looking like it answered.
///
/// <para>The parser is the backstop for the strict <c>json_schema</c>, and it is not decoration:
/// not every provider route implements structured outputs, and the adapter permanently degrades to
/// <c>json_object</c> the first time one refuses. From that moment the ONLY thing standing between
/// a malformed answer and a cached piece of user-facing copy is this code — the same reasoning that
/// put a parser behind the attribute extractor's schema.</para>
/// </summary>
public class LensParserTests
{
    private static string Json(object value) =>
        JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    private static string Valid(
        string lens = "You get the slow-burn procedural register you keep coming back to, told straight.",
        string hook = "The bleak crime piece you have been circling.",
        object? caution = null,
        object[]? anchors = null,
        string mood = "heavy",
        string when = "tonight-alone") =>
        Json(new
        {
            lens,
            hook,
            caution,
            anchors = anchors ?? [],
            mood,
            whenToWatch = when,
        });

    [Fact]
    public void A_complete_answer_parses()
    {
        var (note, problems) = OpenRouterLensWriter.ParseLens(Valid(
            caution: "It takes its time before it commits.",
            anchors: [new { title = "Sicario", relation = "same moral flatness, less momentum" }]));

        Assert.NotNull(note);
        Assert.Empty(problems);
        Assert.Equal("heavy", note!.Mood);
        Assert.Equal("tonight-alone", note.WhenToWatch);
        Assert.Single(note.Anchors);
        Assert.Equal("Sicario", note.Anchors[0].Title);
    }

    [Fact]
    public void A_null_caution_is_the_model_declining_to_object_and_is_allowed()
    {
        var (note, _) = OpenRouterLensWriter.ParseLens(Valid(caution: null));
        Assert.NotNull(note);
        Assert.Null(note!.Caution);
    }

    [Fact]
    public void An_empty_caution_is_treated_as_no_caution_not_as_a_failure()
    {
        var (note, _) = OpenRouterLensWriter.ParseLens(Valid(caution: "   "));
        Assert.NotNull(note);
        Assert.Null(note!.Caution);
    }

    [Theory]
    [InlineData("lens")]
    [InlineData("hook")]
    public void A_missing_required_field_is_fatal(string field)
    {
        using var doc = JsonDocument.Parse(Valid());
        var stripped = doc.RootElement.EnumerateObject()
            .Where(p => p.Name != field)
            .ToDictionary(p => p.Name, p => (object?)p.Value.Clone());

        var (note, problems) = OpenRouterLensWriter.ParseLens(Json(stripped));

        Assert.Null(note);
        Assert.Contains(problems, p => p.Contains(field, StringComparison.Ordinal));
    }

    [Fact]
    public void An_empty_lens_is_a_missing_answer_wearing_a_present_key()
    {
        var (note, problems) = OpenRouterLensWriter.ParseLens(Valid(lens: "  "));
        Assert.Null(note);
        Assert.Contains(problems, p => p.Contains("lens", StringComparison.Ordinal));
    }

    [Fact]
    public void An_over_length_field_is_rejected_rather_than_trimmed()
    {
        // A 600-character "lens" is not a long answer to the question asked, it is an answer to a
        // different one — and cutting it mid-sentence ships the worst of both.
        var (note, problems) = OpenRouterLensWriter.ParseLens(Valid(lens: new string('x', LensNote.MaxLensChars + 1)));
        Assert.Null(note);
        Assert.Contains(problems, p => p.Contains("over the", StringComparison.Ordinal));
    }

    [Fact]
    public void A_hook_over_ninety_characters_is_rejected()
    {
        var (note, _) = OpenRouterLensWriter.ParseLens(Valid(hook: new string('x', LensNote.MaxHookChars + 1)));
        Assert.Null(note);
    }

    [Theory]
    [InlineData("mood", "gloomy")]
    [InlineData("whenToWatch", "sunday-brunch")]
    public void An_off_vocabulary_value_is_fatal(string field, string invented)
    {
        // Unlike tone/era on a title — where an unknown label is dropped to null — mood is what the
        // tonight-chips re-sort over, so an invented value is a card no chip can ever find.
        var json = field == "mood" ? Valid(mood: invented) : Valid(when: invented);
        var (note, problems) = OpenRouterLensWriter.ParseLens(json);

        Assert.Null(note);
        Assert.Contains(problems, p => p.Contains(field, StringComparison.Ordinal));
    }

    [Fact]
    public void Vocabulary_matching_is_case_insensitive()
    {
        var (note, _) = OpenRouterLensWriter.ParseLens(Valid(mood: "Cerebral", when: "With-Someone"));
        Assert.NotNull(note);
        Assert.Equal("cerebral", note!.Mood);
        Assert.Equal("with-someone", note.WhenToWatch);
    }

    [Fact]
    public void A_malformed_anchor_entry_sinks_the_anchor_list()
    {
        var (note, problems) = OpenRouterLensWriter.ParseLens(
            Valid(anchors: [new { title = "Sicario" }]));

        Assert.Null(note);
        Assert.Contains(problems, p => p.Contains("anchor", StringComparison.Ordinal));
    }

    [Fact]
    public void Absent_anchors_are_fine_because_a_title_may_have_nothing_to_compare_to()
    {
        using var doc = JsonDocument.Parse(Valid());
        var stripped = doc.RootElement.EnumerateObject()
            .Where(p => p.Name != "anchors")
            .ToDictionary(p => p.Name, p => (object?)p.Value.Clone());

        var (note, _) = OpenRouterLensWriter.ParseLens(Json(stripped));

        Assert.NotNull(note);
        Assert.Empty(note!.Anchors);
    }

    [Fact]
    public void More_than_three_anchors_are_capped()
    {
        var many = Enumerable.Range(0, 6)
            .Select(i => (object)new { title = $"Title {i}", relation = "shares its register" })
            .ToArray();

        var (note, _) = OpenRouterLensWriter.ParseLens(Valid(anchors: many));

        Assert.NotNull(note);
        Assert.Equal(LensNote.MaxAnchors, note!.Anchors.Count);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("[1, 2, 3]")]
    [InlineData("")]
    public void Hostile_bodies_are_rejected_rather_than_thrown_on(string body)
    {
        var (note, problems) = OpenRouterLensWriter.ParseLens(body);
        Assert.Null(note);
        Assert.NotEmpty(problems);
    }

    // ── The narrative parser ────────────────────────────────────────────────────────────────

    private static string Sentences(int count) =>
        Json(new { sentences = Enumerable.Range(0, count).Select(i => $"You do sentence {i}.").ToArray() });

    [Theory]
    [InlineData(6)]
    [InlineData(8)]
    [InlineData(10)]
    public void A_narrative_inside_the_sentence_bounds_parses(int count)
    {
        var parsed = OpenRouterLensWriter.ParseNarrative(Sentences(count));
        Assert.NotNull(parsed);
        Assert.Equal(count, parsed!.Count);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(15)]
    public void A_narrative_outside_the_bounds_is_refused_rather_than_padded_or_cut(int count)
    {
        // Three sentences is not a profile and fifteen is a list. Neither is fixable by editing the
        // answer, so neither is edited.
        Assert.Null(OpenRouterLensWriter.ParseNarrative(Sentences(count)));
    }

    [Fact]
    public void A_narrative_with_blank_entries_is_measured_after_they_are_dropped()
    {
        var json = Json(new
        {
            sentences = new[] { "You watch late.", " ", "You rate hard.", "", "You avoid gore.", "You like slow.", "You reach for crime." },
        });

        // Five real sentences after the blanks go — under the floor, so refused.
        Assert.Null(OpenRouterLensWriter.ParseNarrative(json));
    }

    [Fact]
    public void A_narrative_missing_its_array_is_refused()
    {
        Assert.Null(OpenRouterLensWriter.ParseNarrative(Json(new { text = "You like thrillers." })));
        Assert.Null(OpenRouterLensWriter.ParseNarrative("not json"));
        Assert.Null(OpenRouterLensWriter.ParseNarrative(null));
    }
}
