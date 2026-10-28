using FrostAura.Reel.Application.Search.Retrieval;

namespace FrostAura.Reel.Tests.Filters;

/// <summary>
/// HREV5-ASK-04 and HREV5-RET-04 without a database: a rejected word reaches its whole curated
/// synonym group (and nothing else), and a full-text hit is prose-only exactly when the overview is
/// the only field that matched.
/// </summary>
public class NegatedConceptsTests
{
    [Theory]
    [InlineData("mafia")]
    [InlineData("Mafia")]
    [InlineData("mafias")]
    [InlineData("mafia stuff")]
    [InlineData("mafia movies")]
    [InlineData("gangsters")]
    [InlineData("organized crime")]
    [InlineData("mobster")]
    public void A_rejected_word_in_a_synonym_group_reaches_the_whole_group(string rejected)
    {
        var stems = NegatedConcepts.Stems([rejected]);

        Assert.Equal(["mafia", "gangster", "mob", "mobster", "organized crime"], stems);
    }

    [Theory]
    [InlineData("crime boss")]
    [InlineData("turtles")]
    [InlineData("stuff")]
    [InlineData("")]
    [InlineData("   ")]
    public void A_word_outside_every_group_widens_to_nothing(string rejected) =>
        Assert.Empty(NegatedConcepts.Stems([rejected]));

    [Theory]
    [InlineData("romance")]
    [InlineData("horror")]
    [InlineData("comedies")]
    public void A_genre_word_is_rejected_as_a_genre_and_is_not_widened_here(string rejected) =>
        Assert.Empty(NegatedConcepts.Stems([rejected]));

    [Fact]
    public void Several_rejections_union_their_groups_once()
    {
        var stems = NegatedConcepts.Stems(["mafia", "gangster", "ninjas", "crime boss"]);

        Assert.Equal(["mafia", "gangster", "mob", "mobster", "organized crime", "ninja", "shinobi", "kunoichi"], stems);
        Assert.Empty(NegatedConcepts.Stems(null));
    }

    [Theory]
    [InlineData("gangster movies")]
    [InlineData("gangsters")]
    [InlineData("the mob")]
    public void A_group_the_request_itself_asks_for_is_not_widened(string askedWords)
    {
        // "Gangster films, no mafia": one group on both sides; the rejection keeps its literal reading.
        var asked = new[] { new ConceptTerm(askedWords, [], []) };

        Assert.Empty(NegatedConcepts.Stems(["mafia"], asked));
        Assert.Equal(["ninja", "shinobi", "kunoichi"], NegatedConcepts.Stems(["mafia", "ninjas"], asked));
        Assert.Equal(5, NegatedConcepts.Stems(["mafia"], [new ConceptTerm("crime dramas", [], [])]).Count);
    }

    [Theory]
    [InlineData("organized crime", true)]
    [InlineData("gangster film", true)]
    [InlineData("mobsters", true)]
    [InlineData("russian mob", true)]
    [InlineData("italian mafia", true)]
    [InlineData("mobile phone", false)]
    [InlineData("crime boss", false)]
    [InlineData("mobility", false)]
    public void The_widened_reach_matches_a_tag_as_the_positive_concept_does(string keyword, bool reached)
    {
        // The keyword twin of the SQL predicate (HardConstraintFilter.KeywordPattern): word start to a
        // word end or an inflection, a plural only for a short stem.
        var stems = NegatedConcepts.Stems(["mafia"]);

        Assert.Equal(reached, stems.Any(stem => HardConstraintFilter.KeywordMatches(keyword, stem)));
        Assert.NotNull(HardConstraintFilter.NegatedConceptPredicate(stems));
        Assert.Null(HardConstraintFilter.NegatedConceptPredicate([]));
    }

    [Theory]
    [InlineData(false, false, false, true, true)]
    [InlineData(true, false, false, true, false)]
    [InlineData(false, true, false, true, false)]
    [InlineData(false, false, true, true, false)]
    [InlineData(true, false, false, false, false)]
    [InlineData(false, false, false, false, false)]
    public void A_hit_is_prose_only_when_the_overview_alone_matched(bool name, bool keywords, bool themes, bool overview, bool proseOnly) =>
        Assert.Equal(proseOnly, new FullTextHit { InName = name, InKeywords = keywords, InThemes = themes, InOverview = overview }.ProseOnly);
}
