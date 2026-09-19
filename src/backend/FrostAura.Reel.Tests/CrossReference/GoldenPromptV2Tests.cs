using System.Globalization;
using FrostAura.Reel.Domain.CrossReference;
using FrostAura.Reel.Domain.Lens;
using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Infrastructure.Adapters;

namespace FrostAura.Reel.Tests.CrossReference;

/// <summary>
/// Pins prompt generation 2 — the experiment, and the four changes each of which answers something
/// the 2026-09-10 live run measured.
///
/// <para>That run judged 1,305 holdout titles across five origins and came back with a
/// false-rule-out rate of 12.0% against a true-rule-out rate of 10.1%: the <c>out</c> verdict
/// carried no information about the outcome, while a third of the pool answered "insufficient
/// signal". v2 (a) leads the candidate line with the neighbour evidence, viewer's score first, and
/// shows five neighbours rather than three so a negative comparison is visible whenever one exists;
/// (b) states the genre affinities explicitly; (c) anchors confidence on the viewer's own base
/// rates; and (d) offers <c>neighbour-mixed</c> so "the evidence disagrees with itself" stops being
/// recorded as "I was shown nothing".</para>
///
/// <para><b>And it pins that v1 did not move.</b> A v1 verdict is a persisted row keyed on version
/// 1, so a v1 row has to keep meaning what it meant — <see cref="GoldenPromptTests"/> is
/// deliberately untouched by this file, and the two assertions at the bottom here say so from the
/// v2 side as well.</para>
/// </summary>
public class GoldenPromptV2Tests
{
    private static readonly Guid CandidateId = new("11111111-1111-1111-1111-111111111111");

    private static readonly Guid LovedNeighbour = new("33333333-3333-3333-3333-333333333333");

    private static readonly Guid DislikedNeighbour = new("44444444-4444-4444-4444-444444444444");

    private static readonly CrossReferenceJudgeOptions V2 =
        new(CrossReferencePromptVersions.Second, GenreAffinity: null, LovedShare: null, DislikedShare: null);

    [Fact]
    public void The_candidate_line_leads_with_the_neighbour_evidence_and_the_viewers_score_first()
    {
        var prompt = OpenRouterCrossReferenceJudge.BuildUserPrompt(
            [Candidate(neighbours: [Neighbour("Argylle", 2024, 3, 0.642d, DislikedNeighbour)])],
            Persona(),
            CrossReferenceRound.First,
            V2);

        // The score is what is being reasoned from; the title is only how it is identified.
        Assert.Contains("you 3/10 · Argylle (2024) · sim 0.64", prompt, StringComparison.Ordinal);

        // …and it comes before the name, not after seven scalars and a cast list. Position is not a
        // subtle lever on a sixty-line prompt.
        var line = CandidateLine(prompt);
        Assert.True(
            line.IndexOf("nearest you rated", StringComparison.Ordinal)
                < line.IndexOf("Candidate Title", StringComparison.Ordinal),
            $"the neighbour clause did not lead the line: {line}");

        // The id still leads, because it is the key the model has to echo back.
        Assert.True(Guid.TryParse(line[..36], out var id) && id == CandidateId, $"line does not lead with an id: {line}");
    }

    [Fact]
    public void Five_neighbours_are_rendered_and_a_disliked_one_is_among_them()
    {
        // Three nearest overall plus up to two nearest low-rated — the shape
        // NeighbourFinder.ForPromptVersion asks the database for.
        var neighbours = new[]
        {
            Neighbour("Sicario", 2015, 10, 0.834d, LovedNeighbour),
            Neighbour("Wind River", 2017, 9, 0.771d, new Guid("55555555-0000-0000-0000-000000000000")),
            Neighbour("Hell or High Water", 2016, 8, 0.702d, new Guid("66666666-0000-0000-0000-000000000000")),
            Neighbour("Argylle", 2024, 3, 0.642d, DislikedNeighbour),
            Neighbour("The Gray Man", 2022, 5, 0.571d, new Guid("77777777-0000-0000-0000-000000000000")),
        };

        var prompt = OpenRouterCrossReferenceJudge.BuildUserPrompt(
            [Candidate(neighbours: neighbours)], Persona(), CrossReferenceRound.First, V2);

        var clause = CandidateLine(prompt);
        // Counted on the "n/10 · " shape rather than on "you ", which the clause LABEL also contains.
        Assert.Equal(5, clause.Split("/10 · ", StringSplitOptions.None).Length - 1);
        Assert.Contains("you 10/10 · Sicario (2015)", clause, StringComparison.Ordinal);
        // The negative evidence the first run's three-nearest window would usually have missed.
        Assert.Contains("you 3/10 · Argylle (2024)", clause, StringComparison.Ordinal);
        Assert.Contains("you 5/10 · The Gray Man (2022)", clause, StringComparison.Ordinal);
    }

    [Fact]
    public void The_evidence_mapping_asks_for_five_neighbours_at_version_two_and_three_at_version_one()
    {
        // The mapping from "which prompt" to "which neighbours" is part of what a stored verdict
        // means, so it lives in one place and both the backtest and the job read it from there.
        var v1 = Application.CrossReference.NeighbourFinder.ForPromptVersion(
            CrossReferencePromptVersions.First, asOf: null);
        Assert.Equal(3, v1.K);
        Assert.False(v1.IncludeNearestDisliked);

        var v2 = Application.CrossReference.NeighbourFinder.ForPromptVersion(
            CrossReferencePromptVersions.Second, asOf: null);
        Assert.Equal(3, v2.K);
        Assert.True(v2.IncludeNearestDisliked);
        Assert.Equal(2, v2.DislikedTake);
        // 5 rather than 4: on an account whose lowest scores cluster at 5 a ceiling of 4 shows the
        // judge no negative evidence at all, which is what the first run measured.
        Assert.Equal((short)5, v2.DislikedRatingCeiling);
        Assert.Equal(0.55d, v2.DislikedSimilarityFloor);
        Assert.Equal(3 + 2, v2.K + v2.DislikedTake);
    }

    [Fact]
    public void A_candidate_with_no_neighbours_still_gets_no_clause_at_all()
    {
        var prompt = OpenRouterCrossReferenceJudge.BuildUserPrompt(
            [Candidate(neighbours: [])], Persona(), CrossReferenceRound.First, V2);

        // v2 moved the clause; it did not start rendering an empty one. A label with nothing after
        // it is a claim that the model looked and found nothing.
        Assert.DoesNotContain("nearest you rated", prompt, StringComparison.Ordinal);
        Assert.Contains("Candidate Title", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void The_genre_affinity_line_rides_the_viewer_block_when_there_is_one()
    {
        var withLine = OpenRouterCrossReferenceJudge.BuildUserPrompt(
            [Candidate()],
            Persona(),
            CrossReferenceRound.First,
            V2 with { GenreAffinity = "GENRES YOU RATE HIGHEST: thriller 8.4/10 over 41" });

        Assert.Contains("GENRES YOU RATE HIGHEST: thriller 8.4/10 over 41", withLine, StringComparison.Ordinal);
        // In the viewer block, above the candidates — it describes the reader, not a candidate.
        Assert.True(
            withLine.IndexOf("GENRES YOU RATE HIGHEST", StringComparison.Ordinal)
                < withLine.IndexOf("CANDIDATES", StringComparison.Ordinal),
            "the genre line landed below the candidate header");

        // Omitted entirely when absent, never rendered as an empty heading.
        Assert.DoesNotContain(
            "GENRES YOU RATE",
            OpenRouterCrossReferenceJudge.BuildUserPrompt([Candidate()], Persona(), CrossReferenceRound.First, V2),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Version_one_never_renders_a_genre_line_even_when_one_is_supplied()
    {
        // Belt and braces on the byte-identity promise: a v1 run cannot be made to render v2 input.
        var prompt = OpenRouterCrossReferenceJudge.BuildUserPrompt(
            [Candidate()],
            Persona(),
            CrossReferenceRound.First,
            new CrossReferenceJudgeOptions(
                CrossReferencePromptVersions.First, "GENRES YOU RATE HIGHEST: thriller 8.4/10 over 41", 0.3m, 0.1m));

        Assert.DoesNotContain("GENRES YOU RATE", prompt, StringComparison.Ordinal);
        Assert.Equal(
            OpenRouterCrossReferenceJudge.BuildUserPrompt([Candidate()], Persona(), CrossReferenceRound.First),
            prompt);
    }

    [Fact]
    public void The_system_prompt_states_the_viewers_own_base_rates()
    {
        var system = OpenRouterCrossReferenceJudge.BuildSystemPrompt(
            CrossReferenceRound.First, V2 with { LovedShare = 0.242m, DislikedShare = 0.144m });

        Assert.Contains(
            "This viewer rates about 24% of what they finish 8 or higher and 14% 4 or lower.",
            system,
            StringComparison.Ordinal);
        Assert.Contains("should be rarer than that dislike share", system, StringComparison.Ordinal);
    }

    [Fact]
    public void The_base_rate_sentence_is_omitted_rather_than_zero_filled()
    {
        // "You dislike about 0% of what you finish" is a claim about the viewer that nobody measured
        // — the same rule the candidate line follows for an unextracted scalar.
        var system = OpenRouterCrossReferenceJudge.BuildSystemPrompt(CrossReferenceRound.First, V2);

        Assert.DoesNotContain("This viewer rates about", system, StringComparison.Ordinal);
        Assert.Equal(OpenRouterCrossReferenceJudge.RoundOneSystemPromptV2, system);

        // One share without the other is still not a base rate.
        Assert.DoesNotContain(
            "This viewer rates about",
            OpenRouterCrossReferenceJudge.BuildSystemPrompt(CrossReferenceRound.First, V2 with { LovedShare = 0.24m }),
            StringComparison.Ordinal);
    }

    [Fact]
    public void The_system_prompt_asks_for_a_neighbour_reason_where_the_verdict_is_neighbour_based()
    {
        foreach (var round in new[] { CrossReferenceRound.First, CrossReferenceRound.Second })
        {
            var system = OpenRouterCrossReferenceJudge.BuildSystemPrompt(round, V2);

            Assert.Contains("LEADS with the strongest evidence", system, StringComparison.Ordinal);
            Assert.Contains("\"neighbour-mixed\" where the nearest", system, StringComparison.Ordinal);
            // The distinction the whole reason code exists for: "I was shown nothing" is a fact
            // about the pipeline, "the evidence disagrees" is a fact about the title.
            Assert.Contains(
                "is not the same answer as \"the evidence disagrees with itself\"",
                system,
                StringComparison.Ordinal);
            Assert.Contains("confidence is a probability, not an emphasis", system, StringComparison.Ordinal);

            // …and the v1 instructions the design still leans on are all still there.
            Assert.Contains("Unsure is the correct answer far more often", system, StringComparison.Ordinal);
            Assert.Contains("format-mismatch", system, StringComparison.Ordinal);
            Assert.Contains(
                "Never rule something out for being obscure, old, or in another language",
                system,
                StringComparison.Ordinal);
            Assert.Contains("no prose", system, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Neighbour_mixed_is_a_version_two_reason_and_is_legal_only_for_unsure()
    {
        Assert.DoesNotContain(CrossReferenceReasons.NeighbourMixed, CrossReferenceReasons.All);
        Assert.Contains(CrossReferenceReasons.NeighbourMixed, CrossReferenceReasons.AllV2);

        Assert.DoesNotContain(
            CrossReferenceReasons.NeighbourMixed,
            CrossReferenceReasons.ForVersion(CrossReferencePromptVersions.First));
        Assert.Contains(
            CrossReferenceReasons.NeighbourMixed,
            CrossReferenceReasons.ForVersion(CrossReferencePromptVersions.Second));

        Assert.True(CrossReferenceReasons.IsLegal(CrossReferenceVerdict.Unsure, CrossReferenceReasons.NeighbourMixed));
        // A keep or an out justified by "the evidence disagrees" is an entry whose two fields
        // contradict each other, and reading either half would be inventing which half to believe.
        Assert.False(CrossReferenceReasons.IsLegal(CrossReferenceVerdict.Keep, CrossReferenceReasons.NeighbourMixed));
        Assert.False(CrossReferenceReasons.IsLegal(CrossReferenceVerdict.Out, CrossReferenceReasons.NeighbourMixed));
    }

    [Fact]
    public void A_version_one_response_carrying_a_version_two_reason_costs_exactly_one_entry()
    {
        // The strict schema would refuse it, but the schema is permanently downgraded to plain
        // json_object the first time a provider rejects it — after which the parser is the only
        // validation there is, and it has to hold the version line itself.
        var body =
            $$"""
              {"results":[
                {"titleId":"{{CandidateId}}","verdict":"unsure","reason":"neighbour-mixed","confidence":0.5},
                {"titleId":"{{LovedNeighbour}}","verdict":"keep","reason":"neighbour-loved","confidence":0.8}
              ]}
              """;
        var batch = new HashSet<Guid> { CandidateId, LovedNeighbour };

        var (v1Entries, v1Rejected) = OpenRouterCrossReferenceJudge.ParseResults(
            body, batch, CrossReferencePromptVersions.First);
        Assert.Equal(1, v1Rejected);
        Assert.Equal(LovedNeighbour, Assert.Single(v1Entries).TitleId);

        var (v2Entries, v2Rejected) = OpenRouterCrossReferenceJudge.ParseResults(
            body, batch, CrossReferencePromptVersions.Second);
        Assert.Equal(0, v2Rejected);
        Assert.Equal(2, v2Entries.Count);
    }

    [Fact]
    public void Every_number_in_the_version_two_prompt_is_invariant()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            // de-DE renders 0.64 as "0,64" — a decimal comma inside a prompt whose neighbour items
            // are already separated by "·" and whose fields are separated by "|".
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            var prompt = OpenRouterCrossReferenceJudge.BuildUserPrompt(
                [Candidate(neighbours: [Neighbour("Argylle", 2024, 3, 0.642d, DislikedNeighbour)])],
                Persona(),
                CrossReferenceRound.First,
                V2 with { GenreAffinity = "GENRES YOU RATE HIGHEST: thriller 8.4/10 over 41" });
            var system = OpenRouterCrossReferenceJudge.BuildSystemPrompt(
                CrossReferenceRound.First, V2 with { LovedShare = 0.242m, DislikedShare = 0.144m });

            Assert.Contains("sim 0.64", prompt, StringComparison.Ordinal);
            Assert.Contains("predicted 8.4", prompt, StringComparison.Ordinal);
            Assert.Contains("pace 0.70", prompt, StringComparison.Ordinal);
            Assert.Contains("you 3/10 · Argylle (2024)", prompt, StringComparison.Ordinal);
            Assert.DoesNotContain("0,64", prompt, StringComparison.Ordinal);
            Assert.DoesNotContain("8,4", prompt, StringComparison.Ordinal);
            Assert.Contains("about 24% of what they finish", system, StringComparison.Ordinal);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void The_version_two_schema_offers_the_wider_vocabulary_and_version_one_does_not()
    {
        var v1 = System.Text.Json.JsonSerializer.Serialize(OpenRouterCrossReferenceJudge.ResultsSchema(60));
        var v2 = System.Text.Json.JsonSerializer.Serialize(
            OpenRouterCrossReferenceJudge.ResultsSchema(60, CrossReferencePromptVersions.Second));

        Assert.DoesNotContain("neighbour-mixed", v1, StringComparison.Ordinal);
        Assert.Contains("neighbour-mixed", v2, StringComparison.Ordinal);
        // Still closed, still no free-text field: the cost case for judging the whole pool rests on
        // a closed vocabulary at ~45 completion tokens an entry.
        Assert.DoesNotContain("\"explanation\"", v2, StringComparison.Ordinal);
    }

    [Fact]
    public void Version_one_prompts_are_returned_byte_identical()
    {
        // The stored v1 verdicts are keyed on version 1 and have to keep meaning what they meant.
        Assert.Equal(
            OpenRouterCrossReferenceJudge.RoundOneSystemPrompt,
            OpenRouterCrossReferenceJudge.BuildSystemPrompt(
                CrossReferenceRound.First,
                new CrossReferenceJudgeOptions(CrossReferencePromptVersions.First, "ignored", 0.3m, 0.1m)));
        Assert.Equal(
            OpenRouterCrossReferenceJudge.RoundTwoSystemPrompt,
            OpenRouterCrossReferenceJudge.BuildSystemPrompt(
                CrossReferenceRound.Second,
                new CrossReferenceJudgeOptions(CrossReferencePromptVersions.First, "ignored", 0.3m, 0.1m)));

        // …including the neighbour clause, whose item ordering v2 reversed.
        var v1 = OpenRouterCrossReferenceJudge.BuildUserPrompt(
            [Candidate(neighbours: [Neighbour("Sicario", 2015, 10, 0.834d, LovedNeighbour)])],
            Persona(),
            CrossReferenceRound.First);
        Assert.Contains("nearest you rated: Sicario (2015) you 10 · sim 0.83", v1, StringComparison.Ordinal);
        Assert.DoesNotContain("you 10/10", v1, StringComparison.Ordinal);
    }

    [Fact]
    public void The_prompt_generations_are_a_closed_set()
    {
        // The dev endpoint rejects an unknown version off this. A run recorded under version 7 would
        // be rows nothing can render and nothing will ever judge against again — and the version is
        // a persisted key part, so it cannot be cleaned up later by re-deriving it.
        Assert.Equal([1, 2], CrossReferencePromptVersions.All);
        Assert.True(CrossReferencePromptVersions.IsKnown(CrossReferencePromptVersions.First));
        Assert.True(CrossReferencePromptVersions.IsKnown(CrossReferencePromptVersions.Second));
        Assert.False(CrossReferencePromptVersions.IsKnown(0));
        Assert.False(CrossReferencePromptVersions.IsKnown(3));
        Assert.False(CrossReferencePromptVersions.IsKnown(-1));

        // The serving default moved to v2 on 2026-09-11, and only because it was MEASURED against
        // v1 on the founder's holdout: AUC 0.655 vs 0.611, Spearman 0.190 vs 0.128, keeps later
        // averaging 6.83 vs 6.53, keep -> loved 33.2% vs 28.4% and keep -> disliked 8.1% vs 13.0%.
        // The ordering this test defends is that a generation becomes current by being measured,
        // not by being written — not that the answer is for ever 1.
        Assert.Equal(CrossReferencePromptVersions.Second, OpenRouterCrossReferenceJudge.CurrentPromptVersion);
    }

    private static string CandidateLine(string prompt) => prompt
        .Split('\n', StringSplitOptions.RemoveEmptyEntries)
        .Single(l => l.Contains(" | ", StringComparison.Ordinal));

    private static CrossReferenceNeighbour Neighbour(
        string name, int year, short rating, double similarity, Guid id) =>
        new(name, year, rating, similarity, id);

    private static LensPersona Persona() => new("Likes tense, patient thrillers.", null, []);

    private static CrossReferenceCandidate Candidate(IReadOnlyList<CrossReferenceNeighbour>? neighbours = null) =>
        new(
            CandidateId, "Candidate Title", 2019, "Movie", ["thriller", "drama"], 8.4m,
            "bleak", "2010s", ["revenge", "borderlands"],
            0.80m, 0.70m, 0.60m, 0.75m, 0.10m, 0.20m, 0.40m,
            "Denis Villeneuve", "Taylor Sheridan", ["Emily Blunt", "Benicio del Toro"],
            neighbours ?? [], "A cartel prosecutor is pulled into a black operation.");
}
