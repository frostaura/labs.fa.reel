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
            // Rated well, rarely watched, and absent from MostWatchedGenres — the "when you do
            // watch X" bucket the narrative must never promote to a favourite.
            new GenreFact("holiday", 8.1m, 26),
        ],
        MostWatchedGenres:
        [
            new GenreVolumeFact("thriller", 8.6m, 214, 0.104m),
            new GenreVolumeFact("crime", 8.4m, 180, 0.088m),
            new GenreVolumeFact("science-fiction", 8.2m, 96, 0.047m),
        ],
        DriftingToward: [new DriftFact("drama", 0.42m)],
        LovedAnchors:
        [
            new LovedFact("Heat", 1995, 10),
            new LovedFact("Sicario", 2015, 10),
            new LovedFact("Arrival", 2016, 9),
        ],
        RecentLoves: [new LovedFact("The Zone of Interest", 2023, 9)],
        RecentDislikes: [new LovedFact("Argylle", 2024, 3)],
        DeclinedLine: "Recently declined, do not recommend anything too similar: Emily in Paris (wrong tone)",
        VerifiedPeople:
        [
            new PersonFact("Denis Villeneuve", "Director", 9.2m, 4),
            new PersonFact("Roger Deakins", "Cinematographer", 8.8m, 6),
        ],
        Texture: new TextureFact(0.72m, 0.31m, 0.78m, 0.66m, 0.14m, 0.35m, 0.48m, 118),
        Contrarian: new ContrarianFact(0.37m, [new GenreOffset("crime", 0.7m)]),
        PreferenceTags: ["cerebral sci-fi", "practical effects"],
        // Populated on purpose even though the prompt does not name them: the golden files below are
        // what proves the writer's prompt is unchanged by B1's fact-sheet addition. If a future
        // change starts naming clusters, these goldens go red — which is the point.
        Clusters: [new ClusterFact(["Heat", "Sicario"], 0.41m, 96)]);

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

    // ── The lens SYSTEM prompt: generation 4 ─────────────────────────────────────────────────

    [Fact]
    public void The_lens_prompt_is_stamped_as_generation_four()
    {
        // Half the lens cache key. A rule change that does not move this number leaves every v3 row
        // — third-person taglines and all — being served as fresh forever.
        Assert.Equal(4, OpenRouterLensWriter.CurrentLensPromptVersion);
    }

    [Fact]
    public void The_hook_is_briefed_as_something_said_to_the_viewer_not_as_a_tagline()
    {
        // v3 described the hook as "the single reason to press play tonight" and got posters back:
        // "Jodie Foster turns grief into a tight revenge chase." The brief is the defect, not the
        // model, so the brief is what this pins.
        var prompt = OpenRouterLensWriter.LensSystemPrompt;

        Assert.Contains("THE HOOK IS SAID TO THE VIEWER", prompt, StringComparison.Ordinal);
        Assert.Contains("second person, or an imperative", prompt, StringComparison.Ordinal);
        Assert.Contains("\"You get ...\"", prompt, StringComparison.Ordinal);
        Assert.Contains("\"Expect ...\"", prompt, StringComparison.Ordinal);
        Assert.Contains("\"Give this an evening if ...\"", prompt, StringComparison.Ordinal);
        // The refusal is stated with the live example that produced it.
        Assert.Contains("poster line, not a hook", prompt, StringComparison.Ordinal);
        Assert.Contains("said TO the viewer and never as a tagline", prompt, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("gripping")]
    [InlineData("must-see")]
    [InlineData("unmissable")]
    [InlineData("masterful")]
    [InlineData("stunning")]
    [InlineData("thrilling ride")]
    [InlineData("edge of your seat")]
    [InlineData("tour de force")]
    [InlineData("unforgettable")]
    public void Every_word_the_marketing_guard_refuses_is_banned_by_name_in_the_prompt(string word)
    {
        // The prompt and the deterministic guard must ban exactly the same list. A word the guard
        // refuses and the prompt never mentions is a rejection the model was never warned about,
        // which spends a generation to teach nothing.
        Assert.Contains(word, OpenRouterLensWriter.LensSystemPrompt, StringComparison.Ordinal);
        Assert.Equal(word, LensGuards.FirstMarketingWord("It is " + word + " throughout."));
    }

    [Fact]
    public void The_prompt_asks_for_plain_specific_language_rather_than_a_verdict_on_quality()
    {
        var prompt = OpenRouterLensWriter.LensSystemPrompt;

        Assert.Contains("NO MARKETING VOICE", prompt, StringComparison.Ordinal);
        Assert.Contains("Plain, specific language only", prompt, StringComparison.Ordinal);
        Assert.Contains("rather than asserting how good it is", prompt, StringComparison.Ordinal);
        // The judge's third complaint, verbatim: "it overstates the pacing".
        Assert.Contains("Do not overstate the pacing", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void The_premise_is_stated_to_be_allowed_and_its_development_forbidden()
    {
        // v3 said "first act only" and the copy read as though the premise itself were the leak.
        // A pitch that will not say what the film is about is not spoiler-safe; it is useless.
        var prompt = OpenRouterLensWriter.LensSystemPrompt;

        Assert.Contains("THE PREMISE IS NOT A SPOILER", prompt, StringComparison.Ordinal);
        Assert.Contains("as printed in the official", prompt, StringComparison.Ordinal);
        Assert.Contains("how that premise DEVELOPS", prompt, StringComparison.Ordinal);
        Assert.Contains("Write \"sets up\", never \"reveals\".", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void The_point_of_view_rule_is_explicit_and_carries_its_example()
    {
        // Named by a live slip: "a radio show host turns vigilante and starts pushing back against
        // the city that failed you" — a city that failed HER. An abstract rule did not prevent it;
        // the corrected sentence beside the wrong one is the part that teaches.
        var prompt = OpenRouterLensWriter.LensSystemPrompt;

        Assert.Contains("POINT OF VIEW", prompt, StringComparison.Ordinal);
        Assert.Contains("nothing in the plot ever happens to", prompt, StringComparison.Ordinal);
        Assert.Contains("\"the city that failed her\"", prompt, StringComparison.Ordinal);
        Assert.Contains("never \"the city that failed you\"", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void Generation_four_kept_everything_generation_three_was_getting_right()
    {
        // Specificity 0.91 and grounding 0.95 were what v3 did well. A voice fix that quietly drops
        // the anchor contract, the length discipline or the honest caution trades one regression
        // for another and the means would not say which.
        var prompt = OpenRouterLensWriter.LensSystemPrompt;

        Assert.Contains("copied verbatim", prompt, StringComparison.Ordinal);
        Assert.Contains("An empty array is a correct answer", prompt, StringComparison.Ordinal);
        Assert.Contains("a real reservation is worth more than a polite one", prompt, StringComparison.Ordinal);
        Assert.Contains("FINISH EVERY SENTENCE INSIDE ITS LIMIT", prompt, StringComparison.Ordinal);
        Assert.Contains("NO NUMBERS ANYWHERE IN THE PROSE", prompt, StringComparison.Ordinal);
        // The requested budgets, not the parse ceilings — the numbers the provider truncates against.
        Assert.Contains("<=" + LensNote.RequestLensChars + " chars", prompt, StringComparison.Ordinal);
        Assert.Contains("<=" + LensNote.RequestHookChars + " chars", prompt, StringComparison.Ordinal);
        Assert.Contains("<=" + LensNote.RequestCautionChars + " chars", prompt, StringComparison.Ordinal);
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

        // Field by field: LensNote holds a list, and a record's synthesised equality compares that
        // list by reference, so Assert.Equal on the notes would fail for two identical answers.
        Assert.Equal(first.Lens, second.Lens);
        Assert.Equal(first.Hook, second.Hook);
        Assert.Equal(first.Caution, second.Caution);
        Assert.Equal(first.Mood, second.Mood);
        Assert.Equal(first.WhenToWatch, second.WhenToWatch);
        Assert.Equal(first.Anchors, second.Anchors);

        Assert.Null(LensGuards.Inspect(first, Title.Name));
        Assert.Contains(first.Mood, LensVocabulary.Moods);
        Assert.Contains(first.WhenToWatch, LensVocabulary.WhenToWatch);
    }

    [Fact]
    public void The_stub_lens_anchors_only_on_titles_the_persona_offered()
    {
        // The stub used to return no anchors at all, which left the entire anchor chain — prompt,
        // verifier, strip, persist — unexercised by every test in the suite. That chain is exactly
        // what failed in production.
        var persona = new LensPersona(
            "You reach for bleak, procedural crime.",
            null,
            [
                new LensAnchorCandidate("Sicario", 2015, 10, AnchorStance.Loved),
                new LensAnchorCandidate("Frontera", 2014, null, AnchorStance.Declined),
            ]);
        var note = OpenRouterLensWriter.StubLens(Title, persona);

        Assert.Equal(["Sicario", "Frontera"], note.Anchors.Select(a => a.Title));
        Assert.Null(LensGuards.Inspect(note, Title.Name));
    }

    [Fact]
    public void The_lens_prompt_names_the_anchors_it_permits_with_their_verdicts()
    {
        // The defect this closes: the system prompt has always said "verbatim from the lists
        // below", and there were no lists. The only title names in a lens prompt were the ones in
        // the declined line, so the model anchored on those and the guard refused every note.
        var persona = new LensPersona(
            "You reach for bleak, procedural crime.",
            "Recently declined, do not recommend anything too similar: Frontera (not for me)",
            [
                new LensAnchorCandidate("Sicario", 2015, 10, AnchorStance.Loved),
                new LensAnchorCandidate("Argylle", 2024, 3, AnchorStance.Disliked),
                new LensAnchorCandidate("Frontera", 2014, null, AnchorStance.Declined),
            ]);
        var prompt = OpenRouterLensWriter.LensPrompt(Title, persona);

        Assert.Contains("ANCHORS YOU MAY USE", prompt, StringComparison.Ordinal);
        Assert.Contains("Sicario (2015) — you rated it 10 out of ten", prompt, StringComparison.Ordinal);
        Assert.Contains("Argylle (2024) — you rated it only 3 out of ten", prompt, StringComparison.Ordinal);
        Assert.Contains(
            "Frontera (2014) — you declined it from your feed without rating it",
            prompt,
            StringComparison.Ordinal);
        // The scores in the block are context, exactly as the prediction is.
        Assert.Contains("never restate one", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void A_persona_with_no_anchor_candidates_omits_the_block_entirely()
    {
        // An empty heading is worse than no heading: it tells the model a list exists and then
        // shows it nothing, which is the shape that produced invented anchors in the first place.
        var prompt = OpenRouterLensWriter.LensPrompt(Title, new LensPersona("You like crime.", null));

        Assert.DoesNotContain("ANCHORS YOU MAY USE", prompt, StringComparison.Ordinal);
    }

    // ── The narrative fact sheet: volume before affinity ─────────────────────────────────────

    [Fact]
    public void The_narrative_prompt_leads_with_what_the_account_actually_watches()
    {
        var prompt = OpenRouterLensWriter.NarrativePrompt(Facts);

        var watched = prompt.IndexOf("Mostly watches", StringComparison.Ordinal);
        var highest = prompt.IndexOf("Rates highest", StringComparison.Ordinal);

        Assert.True(watched >= 0, "The fact sheet must state what the account watches most.");
        Assert.True(
            watched < highest,
            "Volume must precede affinity in the fact sheet — the opening sentence of a profile is "
            + "the one sentence that has to be about what the person actually watches.");
        Assert.Contains("thriller, 214 titles (10% of everything rated), rated 8.6", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void A_sparse_but_well_rated_genre_is_labelled_as_sparse_rather_than_as_a_favourite()
    {
        // "Holiday stories sit especially high" for twenty-six holiday films in two thousand
        // ratings is the exact sentence this line exists to make impossible.
        var prompt = OpenRouterLensWriter.NarrativePrompt(Facts);

        Assert.Contains("Watches rarely but rates well", prompt, StringComparison.Ordinal);
        Assert.Contains("holiday (26 titles)", prompt, StringComparison.Ordinal);

        // And it must not be in the volume line, which is what the opening sentence is drawn from.
        var watchedLine = prompt
            .Split('\n')
            .Single(l => l.StartsWith("Mostly watches", StringComparison.Ordinal));
        Assert.DoesNotContain("holiday", watchedLine, StringComparison.Ordinal);
    }

    [Fact]
    public void The_narrative_prompt_names_recent_loves_and_recent_dislikes()
    {
        var prompt = OpenRouterLensWriter.NarrativePrompt(Facts);

        Assert.Contains("Loved lately", prompt, StringComparison.Ordinal);
        Assert.Contains("The Zone of Interest (2023)", prompt, StringComparison.Ordinal);
        Assert.Contains("Rated badly lately: Argylle (2024)", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_title_the_fact_sheet_names_is_one_the_grounding_guard_accepts()
    {
        // A fact added to the prompt and forgotten in GroundedNames does not produce a lenient
        // guard — it produces a guard that refuses every narrative that OBEYS the prompt.
        var grounded = Facts.GroundedNames();

        Assert.Contains("The Zone of Interest", grounded);
        Assert.Contains("Argylle", grounded);
        Assert.Contains("Heat", grounded);
    }

    [Fact]
    public void A_recent_love_named_at_the_start_of_a_sentence_is_not_read_as_an_invention()
    {
        // "Lately" fusing onto the title that follows it extracts as "Lately The Zone", which
        // matches no fact sheet in either direction. With the prompt now REQUIRING recent titles by
        // name, this is the common sentence shape, not an edge case.
        Assert.Null(TasteNarrativeService.Ungrounded(
            "You watch thriller and crime more than anything else. "
            + "Lately The Zone of Interest landed hard for you.",
            Facts));
    }

    [Fact]
    public void A_title_the_facts_do_not_contain_is_still_an_invention_wherever_it_sits()
    {
        // The opener fix must not become a hole: masking the first WORD of a sentence cannot be
        // allowed to excuse the rest of it.
        Assert.NotNull(TasteNarrativeService.Ungrounded(
            "You watch crime more than anything. Lately Blade Runner landed hard for you.",
            Facts));
    }
}
