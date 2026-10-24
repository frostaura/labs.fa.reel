using FrostAura.Reel.Application.Ranking;
using FrostAura.Reel.Domain.Catalog;

namespace FrostAura.Reel.Tests.Ml;

/// <summary>
/// WS-5 task 4 — the why-line itself: the era template is gone (the feature stays in the model),
/// the article agrees with the genre, an anchored line names the evidence that admitted the card,
/// and <see cref="FeedWhyGuard"/> refuses the stub, the era template, the a/an defect and the
/// hedges observed on live cards. Each case is a sentence that actually shipped.
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
            Context([("decadeAffinity", 2.1f), ("tmdbPopularityLog", 0.4f)], 2010, 0));

        Assert.DoesNotContain("decade", sentence, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("right now", sentence);
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
    public void A_hedge_against_its_own_pick_is_refused(string why)
    {
        Assert.True(FeedWhyGuard.IsHedged(why));
        Assert.False(FeedWhyGuard.Accepts(why));

        // A hedge is a fresh-line problem; a persisted hedged line is not invented, so it may serve.
        Assert.True(FeedWhyGuard.IsServable(why));
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
