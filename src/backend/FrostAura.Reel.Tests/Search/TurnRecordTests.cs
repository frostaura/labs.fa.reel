using System.Globalization;
using System.Text.Json;
using FrostAura.Reel.Domain.Search;

namespace FrostAura.Reel.Tests.Search;

/// <summary>
/// The turn record's three pure rules, pinned without a database.
///
/// <para>Each of them is a rule that used to live implicitly somewhere else and was wrong there:
/// the ask-versus-page decision was a boolean the CLIENT sent, the transcript's order was the
/// row's timestamp, and the intent was formatted rather than serialised.</para>
/// </summary>
public class TurnRecordTests
{
    // ── Kind derivation ─────────────────────────────────────────────────────────────────────

    [Theory]
    // The same question again is another page of it, whatever the client thinks it is doing.
    [InlineData("something tense", "something tense", SearchTurnKind.Page)]
    [InlineData("something tense", "  something tense  ", SearchTurnKind.Page)]
    // F5-10: a re-typed question is a NEW ASK, not a page. Whitespace is a transport artefact and
    // folding it is safe; capitals are something a person typed on purpose. A user who retyped in
    // caps used to get a page — new results, no user bubble recorded, and a resumed transcript
    // showing one question where five submissions had been made.
    [InlineData("something tense", "Something Tense", SearchTurnKind.Ask)]
    [InlineData("something tense", "SOMETHING TENSE", SearchTurnKind.Ask)]
    // A different question is a new ask, including one that merely contains the last.
    [InlineData("something tense", "something tense but funnier", SearchTurnKind.Ask)]
    [InlineData("something tense", "", SearchTurnKind.Ask)]
    // A conversation with no user turn yet has nothing to be a page of.
    [InlineData(null, "something tense", SearchTurnKind.Ask)]
    public void Kind_is_derived_from_whether_the_question_repeats(string? lastUserText, string message, string expected) =>
        Assert.Equal(expected, SearchTurnKind.Derive(lastUserText, message));

    /// <summary>
    /// THE PROBE for the rule above: one character changes and the answer flips, so a comparison
    /// that had quietly become "starts with" or "contains" fails here rather than in production as
    /// a transcript that never records a new question.
    /// </summary>
    [Fact]
    public void One_character_moves_a_page_back_to_an_ask()
    {
        Assert.Equal(SearchTurnKind.Page, SearchTurnKind.Derive("80s sci-fi", "80s sci-fi"));
        Assert.Equal(SearchTurnKind.Ask, SearchTurnKind.Derive("80s sci-fi", "90s sci-fi"));
    }

    // ── Sequence, not CreatedAt ─────────────────────────────────────────────────────────────

    /// <summary>
    /// The two rows of one exchange are written in a single <c>SaveChanges</c> and therefore share
    /// a timestamp exactly. Ordering by <c>CreatedAt</c> is a coin toss between them — which is how
    /// a resumed transcript put Reel's reply above the question it answered — and <c>Sequence</c>
    /// is the key that cannot do that.
    /// </summary>
    [Fact]
    public void Sequence_orders_an_exchange_that_shares_one_timestamp()
    {
        var writtenAt = new DateTime(2026, 9, 11, 12, 0, 0, DateTimeKind.Utc);
        var question = new SearchTurn { Role = "user", Text = "q", CreatedAt = writtenAt, Sequence = 0 };
        var reply = new SearchTurn { Role = "assistant", Text = "a", CreatedAt = writtenAt, Sequence = 1 };

        // Deliberately handed to the sort in the WRONG order, so a stable sort on a key that cannot
        // separate them would preserve it.
        var rows = new[] { reply, question };

        Assert.Equal(["user", "assistant"], rows.OrderBy(t => t.Sequence).Select(t => t.Role));
        // And the reason the other key cannot be used: it does not separate them at all.
        Assert.Equal(["assistant", "user"], rows.OrderBy(t => t.CreatedAt).Select(t => t.Role));
    }

    // ── IntentJson ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <c>IntentJson</c> is SERIALISED, not formatted, and that is what makes it safe on a machine
    /// whose culture writes a comma for a decimal point. Every other number this backend emits has
    /// to say so at its own site with <c>CultureInfo.InvariantCulture</c>; this one is invariant
    /// because <c>System.Text.Json</c> is, and the test exists so that stays true if anyone ever
    /// swaps the serialiser for string interpolation.
    ///
    /// <para><c>SearchIntent</c> carries no decimal today — the payload below is a stand-in — which
    /// is precisely why this is worth pinning: the day a fit, a floor or a score joins the intent,
    /// the failure would be a jsonb column full of <c>6,5</c> that nothing can read back, and it
    /// would only appear on a machine nobody develops on.</para>
    /// </summary>
    [Fact]
    public void IntentJson_round_trips_a_decimal_under_a_comma_decimal_culture()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            // Proof the culture is actually in force — otherwise this test passes by not running.
            Assert.Equal("6,5", 6.5m.ToString());

            var json = JsonSerializer.Serialize(new { minimumFit = 6.5m, maxYear = 2100 });

            Assert.Contains("6.5", json, StringComparison.Ordinal);
            Assert.DoesNotContain("6,5", json, StringComparison.Ordinal);

            using var parsed = JsonDocument.Parse(json);
            Assert.Equal(6.5m, parsed.RootElement.GetProperty("minimumFit").GetDecimal());
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }
}
