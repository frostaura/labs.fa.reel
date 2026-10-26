using FrostAura.Reel.Application.Ranking;
using FrostAura.Reel.Domain.Catalog;

namespace FrostAura.Reel.Tests.Ml;

/// <summary>
/// WS-5 task 4 — the why-line itself: the era template is gone (the feature stays in the model),
/// the article agrees with the genre, an anchored line names the evidence that admitted the card,
/// and <see cref="FeedWhyGuard"/> refuses the stub, the era template, the a/an defect, the
/// "the anchor" jargon and the hedges observed on live cards, while praise that merely contains a
/// hedge word still passes. Most cases are sentences that actually shipped.
/// </summary>
public class WhyLineHonestyTests
{
    [Theory]
    [InlineData(1994)]
    [InlineData(2008)]
    [InlineData(2023)]
    public void The_decade_feature_never_becomes_a_sentence(int year)
    {
        for (var i = 0; i < 12; i++)
        {
            for (var seed = 0; seed <= 3; seed++)
            {
                var sentence = ExplanationTemplater.BuildSentence(Context([("decadeAffinity", 2.1f)], year, i), seed);
                Assert.DoesNotMatch("decade|your era", sentence);
            }
        }
    }

    [Fact]
    public void The_decade_feature_falls_through_to_the_next_real_reason()
    {
        var sentence = ExplanationTemplater.BuildSentence(
            Context([("decadeAffinity", 2.1f), ("userGenreAffinity", 0.4f)], 2010, 0));

        Assert.DoesNotContain("decade", sentence, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("drama", sentence, StringComparison.OrdinalIgnoreCase); // the genre clause took its place
    }

    [Theory]
    [InlineData("action", "An action pick")]
    [InlineData("adventure", "An adventure pick")]
    [InlineData("animation", "An animation pick")]
    [InlineData("thriller", "A thriller pick")]
    [InlineData("science-fiction", "A science fiction pick")]
    public void A_genre_anchor_clause_takes_the_right_article(string genre, string expectedOpening)
    {
        var seen = new HashSet<string>();
        for (var i = 0; i < 24; i++)
        {
            for (var seed = 0; seed <= 3; seed++)
            {
                var sentence = ExplanationTemplater.BuildSentence(
                    Context([], 2015, i, anchor: "Heat", sharedGenre: genre), seed);
                Assert.DoesNotMatch("^A [aeiou]", sentence);
                seen.Add(sentence);
            }
        }

        // The article variant is actually reached — the test is not vacuous.
        Assert.Contains(seen, s => s.StartsWith(expectedOpening, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("person", "Michael Mann", "director", "Michael Mann")]
    [InlineData("person", "Eric Roth", "writer", "Eric Roth")]
    [InlineData("person", "Al Pacino", "cast", "Al Pacino")]
    [InlineData("keyword", "heist", null, "heist")]
    [InlineData("theme", "obsession", null, "obsession")]
    public void An_anchored_line_names_the_evidence_that_admitted_the_card(
        string kind, string label, string? role, string expected)
    {
        var evidence = new[] { new SharedEvidence(kind, label, role) };
        for (var i = 0; i < 6; i++)
        {
            var sentence = ExplanationTemplater.BuildSentence(
                Context([], 2015, i, anchor: "Heat", sharedGenre: "crime", evidence: evidence));
            Assert.Contains(expected, sentence, StringComparison.Ordinal);
            Assert.Contains("Heat", sentence, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("A action pick aligned with your taste.")]
    [InlineData("A adventure pick aligned with your taste.")]
    [InlineData("An action pick aligned with your taste.")]
    [InlineData("A thriller pick aligned with your taste")]
    public void The_stub_sentence_is_refused_in_every_spelling(string why)
    {
        Assert.True(FeedWhyGuard.IsStub(why));
        Assert.False(FeedWhyGuard.IsServable(why));
        Assert.False(FeedWhyGuard.Accepts(why));
        Assert.Equal(string.Empty, FeedWhyGuard.Servable(why));
    }

    [Theory]
    [InlineData("Peak 1990s — your era, and you rate Tony Gilroy 7.8 on average.")]
    [InlineData("From the 2010s, a decade you rate highly.")]
    public void The_retired_era_template_is_not_served_from_an_old_snapshot(string why)
    {
        Assert.False(FeedWhyGuard.IsServable(why));
    }

    [Theory]
    [InlineData("A action pick with the same gritty streak as CHiPS.")]
    [InlineData("A adventure for a quiet night.")]
    public void A_line_opening_A_plus_vowel_is_refused(string why)
    {
        Assert.True(FeedWhyGuard.HasWrongArticle(why));
        Assert.False(FeedWhyGuard.Accepts(why));
    }

    [Theory]
    [InlineData("A slow-burn legal chess match, though this complex series may suit you less than a brisk film.")]
    [InlineData("A quieter detour from your usual thrillers.")]
    [InlineData("Not an action sprint, but the dialogue crackles.")]
    [InlineData("It might not land, but the cast is strong.")]
    [InlineData("Worth it if you can get past the pacing.")]
    // Caught live on the slot run, 2026-10-24 — and it also matches the acceptance stub regex.
    [InlineData("A widowed suburban mom selling weed gives you a crime hook, but the comedy-led tone is less aligned with your tastes.")]
    [InlineData("Sharp writing, though less of a fit for your usual pace.")]
    // The reviewers' list: every one passed the guard at 6722420 and shipped on live cards.
    [InlineData("Hushpuppy faces a crumbling world alongside her ailing father; you may appreciate the adventure, though its dreamlike drama moves less briskly.")]
    [InlineData("Its stylish 1960s Madison Avenue rivalries offer rich drama, but the slow-burn workplace focus may not deliver your preferred pace.")]
    [InlineData("Frank and Sean’s journey through Ireland offers emotional drama and a compelling father-son rift, but little of the brisk action you favor.")]
    [InlineData("A boy’s murder and a coastal town’s secrets echo a crime-solving tension, though this melancholic, slow series may test your patience.")]
    [InlineData("Your taste for brisk adventure may enjoy a bride and an alien plot, though its playful sci-fi is far from your gothic favourites.")]
    [InlineData("You get a colorful fantasy quest as Shrek and Donkey search for a new heir, though its comedy is less your style.")]
    // Shapes with no concessive conjunction at all.
    [InlineData("Hope Mikaelson’s school of witches offers you abundant fantasy, but its teen drama and series format are weaker fits.")]
    [InlineData("Andy McNally’s rookie police work gives you crime and danger, but its character drama is a softer match.")]
    [InlineData("You get superpowered action with the PJ Masks, but this young-kids series offers lighter sci-fi than you seek.")]
    [InlineData("A grieving law student weighing murder makes this a dark, inward thriller rather than the action-forward film you want.")]
    [InlineData("Dr. Lucien Blake investigates as a town police surgeon, but you usually favor brisk films over this mystery-drama series.")]
    [InlineData("Shrek’s quest brings fantasy adventure, yet Shrek the Third centers more on comedy than your preferred intensity.")]
    [InlineData("Though hopeful, Samantha’s grief makes this earnest drama heavier than your feel-good request suggests.")]
    [InlineData("Its curses and creatures bring a horror edge, and the kid-centered anthology format is a weaker fit.")]
    public void A_hedge_against_its_own_pick_is_refused_fresh_or_persisted(string why)
    {
        Assert.True(FeedWhyGuard.IsHedged(why));
        Assert.False(FeedWhyGuard.Accepts(why));

        // A persisted hedged line is refused at serve time too: it is the card arguing against
        // itself whether it was written tonight or a week ago, and the deploy purge rebuilds it.
        Assert.False(FeedWhyGuard.IsServable(why));
        Assert.Equal(string.Empty, FeedWhyGuard.Servable(why));
    }

    [Theory]
    // Praise that merely contains a hedge word — each pinned so the guard cannot drift into
    // blanking good copy.
    [InlineData("Nothing less than a masterclass in tension from Michael Mann.")]
    [InlineData("No less than three heists, each tighter than the last.")]
    [InlineData("Detour is a lean noir you rate in the same breath as Double Indemnity.")]
    [InlineData("Despite its quiet start, it builds to the kind of finale you rate highly.")]
    [InlineData("With a little of the Coen brothers' gallows humour you rate so well.")]
    [InlineData("Brutal and brisk, but without the gore you tend to skip.")]
    [InlineData("A survival story far from home, with the relentless pacing you rate highly.")]
    [InlineData("Yet another Tony Gilroy script, and you rate him 7.8 on average.")]
    public void Praise_that_contains_a_hedge_word_still_passes(string why)
    {
        Assert.False(FeedWhyGuard.IsHedged(why));
        Assert.True(FeedWhyGuard.Accepts(why));
    }

    [Theory]
    [InlineData("Sherlock faces Moriarty, giving you the anchor’s Holmes-versus-Moriarty crime intrigue.")]
    [InlineData("You get a Baskerville curse, a direct Holmes mystery with the anchor's gothic dread.")]
    [InlineData("Shares the anchor's slow-burn small-town dread.")]
    public void Prompt_jargon_about_the_anchor_is_refused(string why)
    {
        Assert.True(FeedWhyGuard.UsesAnchorJargon(why));
        Assert.False(FeedWhyGuard.Accepts(why));
        Assert.False(FeedWhyGuard.IsServable(why));
    }

    [Theory]
    [InlineData("A unique heist built on misdirection.")]
    [InlineData("A one-off special from the Sherlock team.")]
    [InlineData("A useful reminder of why you rate Tony Gilroy.")]
    [InlineData("A universal story of grief, told the way you like it.")]
    [InlineData("A eulogy of a film, and you rate Clint Eastwood 8.1.")]
    public void A_vowel_letter_with_a_consonant_sound_takes_A(string why)
    {
        Assert.False(FeedWhyGuard.HasWrongArticle(why));
        Assert.True(FeedWhyGuard.Accepts(why));
    }

    [Theory]
    [InlineData("based on novel or book")]
    [InlineData("based on comic")]
    [InlineData("based on true story")]
    [InlineData("biography")]
    [InlineData("woman director")]
    [InlineData("murder")]
    public void A_production_fact_or_a_near_universal_keyword_is_not_evidence(string keyword)
    {
        var anchor = new AnchorRowGate.Facts([], [keyword], []);
        var candidate = new AnchorRowGate.Facts([], [keyword], []);

        Assert.False(AnchorRowGate.IsDistinctiveKeyword(keyword));
        Assert.Empty(AnchorRowGate.Between(anchor, candidate));
    }

    [Theory]
    [InlineData("identity")]
    [InlineData("friendship")]
    [InlineData("survival")]
    [InlineData("family")]
    public void A_near_universal_theme_is_not_evidence(string theme)
    {
        var anchor = new AnchorRowGate.Facts([], [], [theme]);
        var candidate = new AnchorRowGate.Facts([], [], [theme]);

        Assert.Empty(AnchorRowGate.Between(anchor, candidate));
    }

    [Fact]
    public void A_specific_keyword_or_theme_still_is_evidence()
    {
        var anchor = new AnchorRowGate.Facts([], ["heist", "murder"], ["obsession", "identity"]);
        var candidate = new AnchorRowGate.Facts([], ["murder", "heist"], ["identity", "obsession"]);

        Assert.Equal(
            [new SharedEvidence("keyword", "heist"), new SharedEvidence("theme", "obsession")],
            AnchorRowGate.Between(anchor, candidate));
    }

    [Fact]
    public void The_model_is_told_the_link_the_gate_checked_and_the_anchor_is_named()
    {
        var evidence = new[]
        {
            new SharedEvidence("person", "Michael Mann", "director"),
            new SharedEvidence("keyword", "heist"),
        };

        var context = AnchorEvidencePrompt.ContextFor(evidence, "Heat");

        Assert.NotNull(context);
        Assert.Equal(["same director as Heat: Michael Mann", "shares the heist premise with Heat"], context!.TopContributions);
        Assert.DoesNotContain(context.TopContributions, c => c.Contains("anchor", StringComparison.OrdinalIgnoreCase));

        // Off an anchored row nothing changes: no evidence, no context.
        Assert.Null(AnchorEvidencePrompt.ContextFor(null, "Heat"));
        Assert.Null(AnchorEvidencePrompt.ContextFor([], null));
    }

    [Fact]
    public void Stored_evidence_round_trips_and_a_damaged_document_reads_as_none()
    {
        IReadOnlyList<SharedEvidence> evidence =
            [new SharedEvidence("person", "Al Pacino", "cast"), new SharedEvidence("theme", "obsession")];

        Assert.Equal(evidence, AnchorRowGate.Deserialize(AnchorRowGate.Serialize(evidence)));
        Assert.Contains("\"kind\":\"person\"", AnchorRowGate.Serialize(evidence), StringComparison.Ordinal);
        Assert.Null(AnchorRowGate.Deserialize(null));
        Assert.Empty(AnchorRowGate.Deserialize("{not json")!);
    }

    [Theory]
    [InlineData("You rate Tony Gilroy 7.8 on average, and the crowd backs it too (7.9 on Trakt).")]
    [InlineData("An unflinching heist that plays like Heat with a conscience.")]
    [InlineData("Same writer as Arrival: Eric Heisserer.")]
    public void An_honest_line_passes(string why)
    {
        Assert.True(FeedWhyGuard.Accepts(why));
        Assert.Equal(why, FeedWhyGuard.Servable(why));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Nothing_is_not_a_line(string? why)
    {
        Assert.False(FeedWhyGuard.Accepts(why));
        Assert.Equal(string.Empty, FeedWhyGuard.Servable(why));
    }

    private static ExplanationTemplater.Context Context(
        (string Feature, float Contribution)[] contributions,
        int year,
        int titleSeed,
        string? anchor = null,
        string? sharedGenre = null,
        IReadOnlyList<SharedEvidence>? evidence = null) =>
        new(
            new Title
            {
                Id = new Guid(titleSeed + 1, 0, 0, [0, 0, 0, 0, 0, 0, 0, 0]),
                Name = "Sample",
                Year = year,
                Genres = ["drama"],
            },
            contributions,
            ExplanationTemplater.NoPersons,
            new HashSet<string> { "drama" },
            anchor,
            sharedGenre,
            Tone: null,
            AnchorEvidence: evidence);
}
