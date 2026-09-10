using System.Globalization;
using FrostAura.Reel.Domain.CrossReference;
using FrostAura.Reel.Domain.Lens;
using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Infrastructure.Adapters;

namespace FrostAura.Reel.Tests.CrossReference;

/// <summary>
/// Pins the cross-reference prompt. A prompt is a contract with a paid model whose answers are
/// persisted under a version number, so a change to what it SAYS is a change to what every stored
/// verdict means — and these tests are the thing that makes such a change deliberate.
///
/// <para>What is pinned, and why each one earns a test: the neighbour clause carries the rating
/// <b>and</b> the similarity (the v5 <c>knownPeopleFraction</c> lesson — "9 at 0.82" and "9 at 0.31"
/// are different claims and one number cannot tell them apart); the declined line appears when
/// there is one and the line vanishes entirely when there is not; a candidate with no embedding has
/// no neighbour clause rather than an empty one; the overview is cut at 200 characters; and every
/// number is invariant, on a machine whose own locale renders 8.4 as "8,4" inside a
/// comma-delimited list.</para>
/// </summary>
public class GoldenPromptTests
{
    private static readonly Guid CandidateId = new("11111111-1111-1111-1111-111111111111");

    private static readonly Guid SecondId = new("22222222-2222-2222-2222-222222222222");

    private static readonly Guid NeighbourId = new("33333333-3333-3333-3333-333333333333");

    [Fact]
    public void The_neighbour_clause_carries_both_the_rating_and_the_similarity()
    {
        var prompt = OpenRouterCrossReferenceJudge.BuildUserPrompt(
            [Candidate(CandidateId, neighbours: [new CrossReferenceNeighbour("Sicario", 2015, 10, 0.834d, NeighbourId)])],
            Persona(),
            CrossReferenceRound.First);

        Assert.Contains("nearest you rated: Sicario (2015) you 10 · sim 0.83", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void A_candidate_with_no_embedding_gets_no_neighbour_clause_at_all()
    {
        var prompt = OpenRouterCrossReferenceJudge.BuildUserPrompt(
            [Candidate(CandidateId, neighbours: [])], Persona(), CrossReferenceRound.First);

        // Not an empty clause, not "nearest you rated: none" — the field is absent, exactly as every
        // other unmeasured field is. An empty list rendered as a label is a claim that the model
        // looked and found nothing.
        Assert.DoesNotContain("nearest you rated", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_fields_are_omitted_rather_than_zero_filled()
    {
        var bare = new CrossReferenceCandidate(
            CandidateId, "Bare Title", null, "Movie", [], null, null, null, [],
            null, null, null, null, null, null, null, null, null, [], [], null);

        var prompt = OpenRouterCrossReferenceJudge.BuildUserPrompt([bare], Persona(), CrossReferenceRound.First);

        // "pacing 0.00" would read as "glacial", not as "nobody measured this".
        Assert.DoesNotContain("pace ", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("dark ", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("predicted", prompt, StringComparison.Ordinal);
        Assert.Contains("Bare Title", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void The_declined_line_rides_the_prompt_when_there_is_one_and_vanishes_when_there_is_not()
    {
        var withLine = OpenRouterCrossReferenceJudge.BuildUserPrompt(
            [Candidate(CandidateId)],
            new LensPersona("Likes thrillers.", "Recently declined, do not recommend anything too similar: Cats", []),
            CrossReferenceRound.First);
        Assert.Contains("Recently declined", withLine, StringComparison.Ordinal);

        var withoutLine = OpenRouterCrossReferenceJudge.BuildUserPrompt(
            [Candidate(CandidateId)], new LensPersona("Likes thrillers.", null, []), CrossReferenceRound.First);
        Assert.DoesNotContain("Recently declined", withoutLine, StringComparison.Ordinal);
    }

    [Fact]
    public void The_overview_is_cut_at_two_hundred_characters()
    {
        var overview = new string('x', 400);
        var prompt = OpenRouterCrossReferenceJudge.BuildUserPrompt(
            [Candidate(CandidateId, overview: overview)], Persona(), CrossReferenceRound.First);

        Assert.Contains(new string('x', 200), prompt, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('x', 201), prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_number_is_invariant_under_a_comma_decimal_culture()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            // de-DE renders 8.4 as "8,4" — a decimal comma inside a comma-delimited list, handed to
            // a model asked to reason about the figure.
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            var prompt = OpenRouterCrossReferenceJudge.BuildUserPrompt(
                [Candidate(
                    CandidateId,
                    neighbours: [new CrossReferenceNeighbour("Sicario", 2015, 10, 0.834d, NeighbourId)])],
                Persona(),
                CrossReferenceRound.First);

            Assert.Contains("sim 0.83", prompt, StringComparison.Ordinal);
            Assert.Contains("predicted 8.4", prompt, StringComparison.Ordinal);
            Assert.Contains("pace 0.70", prompt, StringComparison.Ordinal);
            Assert.DoesNotContain("0,83", prompt, StringComparison.Ordinal);
            Assert.DoesNotContain("8,4", prompt, StringComparison.Ordinal);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void The_batch_order_is_deterministic_and_is_not_the_input_order()
    {
        // Both halves matter. Deterministic, or a golden test and a prompt cache mean nothing;
        // and NOT the caller's order, or the judge is handed the ranking it exists to second-guess
        // and penalises whatever the ranker already placed last, twice.
        var batch = Enumerable.Range(0, 12)
            .Select(i => Candidate(new Guid($"{i:D8}-0000-0000-0000-000000000000")))
            .ToList();

        var once = OpenRouterCrossReferenceJudge.Order(batch, CrossReferenceRound.First).Select(c => c.TitleId).ToList();
        var twice = OpenRouterCrossReferenceJudge.Order(batch, CrossReferenceRound.First).Select(c => c.TitleId).ToList();
        var reversed = OpenRouterCrossReferenceJudge
            .Order([.. Enumerable.Reverse(batch)], CrossReferenceRound.First).Select(c => c.TitleId).ToList();

        Assert.Equal(once, twice);
        // Seeded on the SET of ids, so arrival order cannot change the placement.
        Assert.Equal(once, reversed);
        Assert.NotEqual(batch.Select(c => c.TitleId), once);
        Assert.Equal(batch.Count, once.Count);
    }

    [Fact]
    public void The_two_rounds_place_the_same_batch_differently()
    {
        // Round 2 is a second LOOK, not the same prompt asked twice: different neighbours and a
        // different position for every candidate.
        var batch = Enumerable.Range(0, 12)
            .Select(i => Candidate(new Guid($"{i:D8}-0000-0000-0000-000000000000")))
            .ToList();

        Assert.NotEqual(
            OpenRouterCrossReferenceJudge.Order(batch, CrossReferenceRound.First).Select(c => c.TitleId),
            OpenRouterCrossReferenceJudge.Order(batch, CrossReferenceRound.Second).Select(c => c.TitleId));
    }

    [Fact]
    public void Round_one_rules_out_and_round_two_looks_for_reasons_to_say_no()
    {
        // The two system prompts are different documents, and the difference is the whole reason a
        // second keep counts for anything.
        Assert.Contains(
            "Your only job is to rule candidates OUT",
            OpenRouterCrossReferenceJudge.RoundOneSystemPrompt,
            StringComparison.Ordinal);
        Assert.Contains(
            "You are now looking specifically for reasons",
            OpenRouterCrossReferenceJudge.RoundTwoSystemPrompt,
            StringComparison.Ordinal);
        Assert.Contains(
            "would NOT enjoy them",
            OpenRouterCrossReferenceJudge.RoundTwoSystemPrompt,
            StringComparison.Ordinal);
        Assert.Contains(
            "A second keep is a real",
            OpenRouterCrossReferenceJudge.RoundTwoSystemPrompt,
            StringComparison.Ordinal);

        // Both carry the instruction the design leans on hardest.
        foreach (var prompt in new[]
        {
            OpenRouterCrossReferenceJudge.RoundOneSystemPrompt,
            OpenRouterCrossReferenceJudge.RoundTwoSystemPrompt,
        })
        {
            Assert.Contains("Unsure is the correct answer far more often", prompt, StringComparison.Ordinal);
            Assert.Contains("format-mismatch", prompt, StringComparison.Ordinal);
            Assert.Contains("Never rule something out for being obscure, old, or in another language", prompt, StringComparison.Ordinal);
            Assert.Contains("no prose", prompt, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void One_line_per_candidate_and_the_id_leads_it()
    {
        var prompt = OpenRouterCrossReferenceJudge.BuildUserPrompt(
            [Candidate(CandidateId), Candidate(SecondId)], Persona(), CrossReferenceRound.First);

        var lines = prompt.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var candidateLines = lines.Where(l => l.Contains(" | ", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, candidateLines.Count);
        Assert.All(candidateLines, l => Assert.True(Guid.TryParse(l[..36], out _), $"line does not lead with an id: {l}"));
    }

    private static LensPersona Persona() => new("Likes tense, patient thrillers.", null, []);

    private static CrossReferenceCandidate Candidate(
        Guid id,
        IReadOnlyList<CrossReferenceNeighbour>? neighbours = null,
        string? overview = "A cartel prosecutor is pulled into a black operation.") =>
        new(
            id, "Candidate Title", 2019, "Movie", ["thriller", "drama"], 8.4m,
            "bleak", "2010s", ["revenge", "borderlands"],
            0.80m, 0.70m, 0.60m, 0.75m, 0.10m, 0.20m, 0.40m,
            "Denis Villeneuve", "Taylor Sheridan", ["Emily Blunt", "Benicio del Toro"],
            neighbours ?? [], overview);
}
