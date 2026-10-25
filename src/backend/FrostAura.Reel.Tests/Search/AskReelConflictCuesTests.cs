using FrostAura.Reel.Application.Search;
using FrostAura.Reel.Domain.Ports;

namespace FrostAura.Reel.Tests.Search;

/// <summary>
/// A-12: "like John Wick but funnier" against a standing comedy exclusion and "older stuff is fine"
/// against a 2000-onwards rule both reported conflict:false while the reply itself named the rule.
/// The mapping is deterministic, from the user's own words — never a model's collides field.
/// </summary>
public class AskReelConflictCuesTests
{
    private static readonly SearchIntent Empty = new([], [], [], string.Empty, null, null);

    [Theory]
    [InlineData("like John Wick but funnier")]
    [InlineData("the funniest thing you have")]
    [InlineData("something hilarious")]
    public void A_comparative_of_funny_collides_with_a_comedy_exclusion(string raw)
    {
        var askedFor = AskReelStandingFilters.AskedFor(raw, Empty, requested: null);
        Assert.Equal(["comedy"], AskReelStandingFilters.Colliding(["comedy"], askedFor));
    }

    [Theory]
    [InlineData("older stuff is fine", true)]
    [InlineData("some classic noir", true)]
    [InlineData("vintage sci-fi", true)]
    [InlineData("a recent thriller", false)]
    [InlineData("my older brother liked it", true)]
    [InlineData("a modern classic", false)]
    [InlineData("an instant classic from last year", false)]
    [InlineData("new classics please", false)]
    [InlineData("a modern take on classic noir", true)]
    [InlineData("classic, modern either way", true)]
    public void Asking_for_older_titles_collides_with_a_lower_year_bound(string raw, bool collides)
    {
        var rules = FilterRuleSet.Create(null, null, null, null, null, minYear: 2000, maxYear: null);
        Assert.Equal(collides, AskReelStandingFilters.YearWindowCollides(rules, Empty, requested: null, raw));
    }

    [Fact]
    public void Older_never_collides_without_a_lower_bound()
    {
        var rules = FilterRuleSet.Create(null, null, null, null, null, minYear: null, maxYear: 2010);
        Assert.False(AskReelStandingFilters.YearWindowCollides(rules, Empty, requested: null, "older stuff is fine"));
    }
}
