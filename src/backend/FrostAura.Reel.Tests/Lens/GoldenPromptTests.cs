using FrostAura.Reel.Application.Lens;
using FrostAura.Reel.Domain.Lens;
using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Infrastructure.Adapters;

namespace FrostAura.Reel.Tests.Lens;

/// <summary>
/// Golden-prompt snapshots — what the model is actually shown.
///
/// <para>A prompt is the least reviewable artefact in the product: it is assembled from a dozen
/// places, it never fails loudly, and a field that silently stops reaching it degrades the answers
/// by a degree nobody attributes to the change that caused it. The rerank prompt spent months
/// carrying twenty tokens of taste and nothing else for exactly that reason. So the payload is
/// pinned: if a field stops arriving, a test says so, by name.</para>
///
/// <para>Pinned against the deterministic stub and pure builders — nothing here reaches OpenRouter,
/// and nothing in the suite ever may.</para>
/// </summary>
public class GoldenPromptTests
{
    private static readonly TasteFacts Facts = new(
        UserMean: 6.74m,
        RatingsCount: 2044,
        MovieRatedShare: 0.62m,
        TopGenres:
        [
            new GenreFact("thriller", 8.6m, 214),
            new GenreFact("crime", 8.4m, 180),
            new GenreFact("science-fiction", 8.2m, 96),
        ],
        DriftingToward: [new DriftFact("drama", 0.42m)],
        LovedAnchors:
        [
            new LovedFact("Heat", 1995, 10),
            new LovedFact("Sicario", 2015, 10),
            new LovedFact("Arrival", 2016, 9),
        ],
        DeclinedLine: "Recently declined, do not recommend anything too similar: Emily in Paris (wrong tone)",
        VerifiedPeople:
        [
            new PersonFact("Denis Villeneuve", "Director", 9.2m, 4),
            new PersonFact("Roger Deakins", "Cinematographer", 8.8m, 6),
        ],
        Texture: new TextureFact(0.72m, 0.31m, 0.78m, 0.66m, 0.14m, 0.35m, 0.48m, 118),
        Contrarian: new ContrarianFact(0.37m, [new GenreOffset("crime", 0.7m)]),
        PreferenceTags: ["cerebral sci-fi", "practical effects"]);

    private static readonly LensTitleInput Title = new(
        TitleId: Guid.Parse("11111111-1111-1111-1111-111111111111"),
        Name: "Sicario: Day of the Soldado",
        Year: 2018,
        MediaType: "Movie",
        RuntimeMinutes: 122,
        Genres: ["crime", "thriller", "action"],
        Tone: "bleak",
        Era: "contemporary",
        Themes: ["cartel violence", "moral compromise"],
        Darkness: 0.81m,
        Pacing: 0.62m,
        Complexity: 0.55m,
        EmotionalIntensity: 0.70m,
        Humor: 0.05m,
        Optimism: 0.18m,
        EnsembleVsSolo: 0.40m,
        Director: "Stefano Sollima",
        Writer: "Taylor Sheridan",
        TopCast: ["Benicio del Toro", "Josh Brolin", "Isabela Merced"],
        Overview: "A task force escalates the war on the cartels.",
        Keywords: ["cartel", "border", "black ops"],
        Predicted: 8.6m,
        PredictionError: 1.2m);

    // ── The narrative prompt ────────────────────────────────────────────────────────────────

    [Fact]
    public void The_narrative_prompt_carries_every_fact_it_was_given()
    {
        var prompt = OpenRouterLensWriter.NarrativePrompt(Facts);

        // Genre affinities as affinities, not counts — the defect the twenty-token summary had.
        Assert.Contains("thriller 8.6 over 214 titles", prompt, StringComparison.Ordinal);
        Assert.Contains("Moving toward lately: drama", prompt, StringComparison.Ordinal);
        Assert.Contains("Heat (1995)", prompt, StringComparison.Ordinal);
        // The declined line, which had never reached the feed's model at all.
        Assert.Contains("Recently declined", prompt, StringComparison.Ordinal);
        // Verified people, with the observation count that earns them their place.
        Assert.Contains("Denis Villeneuve as director, 9.2 over 4", prompt, StringComparison.Ordinal);
        // The texture — the aggregate of scalars the catalogue was enriched with.
        Assert.Contains("darkness 0.72", prompt, StringComparison.Ordinal);
        Assert.Contains("Against the crowd overall: +0.37", prompt, StringComparison.Ordinal);
        Assert.Contains("cerebral sci-fi", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void The_narrative_prompt_is_invariant_culture()
    {
        var previous = Thread.CurrentThread.CurrentCulture;
        try
        {
            // en_ZA renders 8.6 as "8,6" — a decimal comma inside a comma-separated list, handed to
            // a model asked to reason about the figure.
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("en-ZA");
            var prompt = OpenRouterLensWriter.NarrativePrompt(Facts);

            Assert.Contains("8.6", prompt, StringComparison.Ordinal);
            Assert.DoesNotContain("8,6", prompt, StringComparison.Ordinal);
            Assert.Contains("0.72", prompt, StringComparison.Ordinal);
            Assert.DoesNotContain("0,72", prompt, StringComparison.Ordinal);
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = previous;
        }
    }

    [Fact]
    public void The_narrative_prompt_is_byte_stable()
    {
        // Identical inputs must render identically, or a golden test is worthless and a prompt
        // cache never hits.
        Assert.Equal(OpenRouterLensWriter.NarrativePrompt(Facts), OpenRouterLensWriter.NarrativePrompt(Facts));
    }

    // ── The lens prompt ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_lens_prompt_carries_the_persona_and_every_enriched_field()
    {
        var persona = new LensPersona("You reach for bleak, procedural crime.", "Recently declined: Emily in Paris");
        var prompt = OpenRouterLensWriter.LensPrompt(Title, persona);

        Assert.Contains("You reach for bleak, procedural crime.", prompt, StringComparison.Ordinal);
        Assert.Contains("Emily in Paris", prompt, StringComparison.Ordinal);
        Assert.Contains("Sicario: Day of the Soldado (2018)", prompt, StringComparison.Ordinal);
        Assert.Contains("122 min", prompt, StringComparison.Ordinal);
        // The three fields the pipeline paid to extract and almost nothing read.
        Assert.Contains("Tone: bleak", prompt, StringComparison.Ordinal);
        Assert.Contains("Era: contemporary", prompt, StringComparison.Ordinal);
        Assert.Contains("cartel violence", prompt, StringComparison.Ordinal);
        Assert.Contains("darkness 0.81", prompt, StringComparison.Ordinal);
        Assert.Contains("Director Stefano Sollima", prompt, StringComparison.Ordinal);
        Assert.Contains("Writer Taylor Sheridan", prompt, StringComparison.Ordinal);
        Assert.Contains("Benicio del Toro", prompt, StringComparison.Ordinal);
        Assert.Contains("Keywords: cartel", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void The_prediction_rides_the_prompt_as_context_and_says_so_twice()
    {
        var prompt = OpenRouterLensWriter.LensPrompt(Title, new LensPersona("You like crime.", null));

        Assert.Contains("CONTEXT, DO NOT RESTATE", prompt, StringComparison.Ordinal);
        Assert.Contains("predicts 8.6", prompt, StringComparison.Ordinal);
        Assert.Contains("typically wrong by about 1.2", prompt, StringComparison.Ordinal);
        Assert.Contains("never mention the number", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unenriched_title_omits_its_missing_fields_rather_than_zero_filling_them()
    {
        var bare = Title with
        {
            Tone = null,
            Era = null,
            Themes = [],
            Darkness = null,
            Pacing = null,
            Complexity = null,
            EmotionalIntensity = null,
            Humor = null,
            Optimism = null,
            EnsembleVsSolo = null,
            Director = null,
            Writer = null,
            TopCast = [],
            Keywords = [],
            Predicted = null,
            PredictionError = null,
        };
        var prompt = OpenRouterLensWriter.LensPrompt(bare, new LensPersona("You like crime.", null));

        // A title with no attributes should read as unenriched, not as uniformly neutral — a 0.50
        // on every scalar is a fabricated judgement wearing the clothes of a real one.
        Assert.DoesNotContain("Texture", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("Tone:", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("CONTEXT", prompt, StringComparison.Ordinal);
        Assert.Contains("Sicario: Day of the Soldado", prompt, StringComparison.Ordinal);
    }

    // ── The rerank prompt ───────────────────────────────────────────────────────────────────

    private static RerankInput Candidate(string id, string name) => new(
        Guid.Parse($"{id}-0000-0000-0000-000000000000"),
        name,
        2018,
        "Movie",
        ["crime", "thriller"],
        "A task force escalates the war on the cartels.",
        8.6m,
        new RerankTitleContext(
            "bleak", "contemporary", ["cartel violence"],
            0.81m, 0.62m, 0.55m, 0.70m, 0.05m, 0.18m, 0.40m,
            "Stefano Sollima", "Taylor Sheridan", ["Benicio del Toro"],
            ["writerAffinity", "simTopLovedMax", "genre:crime"]));

    [Fact]
    public void The_rerank_prompt_now_carries_what_it_spent_months_leaving_on_the_floor()
    {
        var prompt = OpenRouterSearchAgent.BuildRerankPrompt(
            [Candidate("11111111", "Soldado")],
            "the best titles for you to watch tonight",
            new LensPersona("You reach for bleak, procedural crime.", "Recently declined: Emily in Paris"),
            anchorName: "Sicario");

        Assert.Contains("THE VIEWER", prompt, StringComparison.Ordinal);
        Assert.Contains("You reach for bleak, procedural crime.", prompt, StringComparison.Ordinal);
        // The declined line reached Ask Reel and had never reached the feed.
        Assert.Contains("Emily in Paris", prompt, StringComparison.Ordinal);
        Assert.Contains("tone bleak", prompt, StringComparison.Ordinal);
        Assert.Contains("set contemporary", prompt, StringComparison.Ordinal);
        Assert.Contains("themes cartel violence", prompt, StringComparison.Ordinal);
        Assert.Contains("dark 0.81", prompt, StringComparison.Ordinal);
        Assert.Contains("dir Stefano Sollima", prompt, StringComparison.Ordinal);
        Assert.Contains("picked for writerAffinity", prompt, StringComparison.Ordinal);
        Assert.Contains("predicted 8.6", prompt, StringComparison.Ordinal);
        Assert.Contains("Anchor title: Sicario", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void A_candidate_with_no_context_renders_exactly_as_it_did_before()
    {
        var prompt = OpenRouterSearchAgent.BuildRerankPrompt(
            [Candidate("11111111", "Soldado") with { Context = null }],
            "the best titles for you to watch tonight",
            new LensPersona("You like crime.", null));

        Assert.DoesNotContain("tone ", prompt, StringComparison.Ordinal);
        Assert.Contains("| Soldado (2018) | Movie | crime, thriller", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void The_rerank_prompt_is_byte_stable_for_the_same_batch()
    {
        // The shuffle is deterministic on purpose: a cache key and this test both depend on it, and
        // a random order would make every prompt a new one.
        var batch = new[] { Candidate("11111111", "A"), Candidate("22222222", "B"), Candidate("33333333", "C") };
        var persona = new LensPersona("You like crime.", null);

        Assert.Equal(
            OpenRouterSearchAgent.BuildRerankPrompt(batch, "q", persona),
            OpenRouterSearchAgent.BuildRerankPrompt(batch, "q", persona));
    }

    [Fact]
    public void The_rerank_prompt_does_not_depend_on_the_order_the_caller_supplied()
    {
        // The shuffle exists so the deterministic ranker's own order cannot silently reinforce
        // itself through the judge that was supposed to second-guess it.
        var batch = new[] { Candidate("11111111", "A"), Candidate("22222222", "B"), Candidate("33333333", "C") };
        var persona = new LensPersona("You like crime.", null);

        Assert.Equal(
            OpenRouterSearchAgent.BuildRerankPrompt(batch, "q", persona),
            OpenRouterSearchAgent.BuildRerankPrompt(batch.Reverse().ToArray(), "q", persona));
    }

    // ── The deterministic stubs ─────────────────────────────────────────────────────────────

    [Fact]
    public void The_stub_narrative_is_grounded_in_its_own_facts()
    {
        // A stub that trips the product's own guards makes every downstream test a test of the
        // stub. It is built only from the supplied facts, so it clears the grounding check.
        var sentences = OpenRouterLensWriter.StubNarrative(Facts);

        Assert.InRange(sentences.Count, 6, 10);
        Assert.Null(TasteNarrativeService.Ungrounded(string.Join(" ", sentences), Facts));
    }

    [Fact]
    public void The_stub_lens_clears_every_guard_and_is_deterministic()
    {
        var persona = new LensPersona("You reach for bleak, procedural crime.", null);
        var first = OpenRouterLensWriter.StubLens(Title, persona);
        var second = OpenRouterLensWriter.StubLens(Title, persona);

        Assert.Equal(first, second);
        Assert.Null(LensGuards.Inspect(first, Title.Name));
        Assert.Contains(first.Mood, LensVocabulary.Moods);
        Assert.Contains(first.WhenToWatch, LensVocabulary.WhenToWatch);
    }
}
