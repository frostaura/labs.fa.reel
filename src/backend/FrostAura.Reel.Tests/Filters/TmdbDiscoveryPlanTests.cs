using FrostAura.Reel.Application.Search;
using FrostAura.Reel.Application.Search.Retrieval;
using FrostAura.Reel.Domain.Ports.Tmdb;

namespace FrostAura.Reel.Tests.Filters;

/// <summary>
/// The pure halves of the TMDB discovery arm: which messages are searched as titles, how the
/// account's conditions project into one discover call, and how a TMDB list row is judged against
/// them before it has a catalogue row.
/// </summary>
public class TmdbDiscoveryPlanTests
{
    [Theory]
    [InlineData("Inception", "Inception")]
    [InlineData("the dark knight", "the dark knight")]
    [InlineData("Mission: Impossible", "Mission: Impossible")]
    [InlineData("Ocean's Eleven", "Ocean's Eleven")]
    [InlineData("Dances with Wolves", "Dances with Wolves")]
    [InlineData("No Country for Old Men", "No Country for Old Men")]
    [InlineData("something like \"Heat\" but funnier", "Heat")]
    [InlineData("none of the above are ninja movies. I want ninja movies", null)]
    [InlineData("a clever heist movie", null)]
    [InlineData("John Wick but funnier", null)]
    [InlineData("is the first one violent?", null)]
    [InlineData("feel-good sports underdog story about a coach and a team", null)]
    [InlineData("80s sci-fi", null)]
    [InlineData("", null)]
    public void Only_a_title_shaped_message_is_searched_as_a_title(string message, string? expected)
    {
        Assert.Equal(expected, TitleShape.Extract(message));
    }

    [Theory]
    [InlineData("ninja", new[] { "ninja" }, false, null)]
    [InlineData("ninja", new[] { "ninja" }, true, null)]                    // a topic even though a film is called Ninja
    [InlineData("time loop", new[] { "time loop" }, false, null)]
    [InlineData("dinosaurs", new[] { "dinosaur" }, false, null)]             // singular forms compare
    [InlineData("haunted house horror", new[] { "haunted house", "horror" }, false, null)]
    [InlineData("detective hunting a serial killer", new[] { "serial killer", "detective" }, false, null)]
    [InlineData("con artists and grifters", new[] { "con artist" }, false, null)]
    [InlineData("Ninja Assassin", new[] { "ninja" }, true, "Ninja Assassin")]  // names a catalogue title
    [InlineData("Ninja Assassin", new[] { "ninja" }, false, null)]             // half topic, names nothing known
    [InlineData("Arrival", new[] { "alien" }, false, "Arrival")]
    [InlineData("ninja", new string[0], false, "ninja")]
    [InlineData("I want \"Ninja\"", new[] { "ninja" }, false, "Ninja")]       // quoted always wins
    public void Topic_words_and_the_catalogue_decide_between_a_topic_and_a_title(
        string message, string[] topic, bool namesCatalogueTitle, string? expected)
    {
        Assert.Equal(expected, TitleShape.Extract(message, topic, namesCatalogueTitle));
    }

    [Fact]
    public void The_plan_projects_language_floor_votes_years_and_exclusions_per_medium()
    {
        var rules = FilterRuleSet.Create(
            excludeGenres: ["romance", "animation", "fantasy"],
            includeGenres: null,
            excludeKeywords: null,
            languages: ["en"],
            minCrowdRating: 6.0m,
            minYear: 2000,
            maxYear: null);
        var request = new TmdbDiscoveryRequest(
            Guid.NewGuid(), ["science-fiction"], [], ["horror"], [], MediaScope.Any, new YearRange(1995, 2010), null, null, null, 40);

        var movies = StandingDiscoverPlan.For(rules, movies: true, request);
        Assert.Equal([878], movies.GenreIds);
        Assert.Equal([10749, 16, 14, 27], movies.ExcludeGenreIds);
        Assert.Equal(2000, movies.ReleasedAfter!.Value.Year);
        Assert.Equal(2010, movies.ReleasedBefore!.Value.Year);
        Assert.Equal("en", movies.Options.OriginalLanguage);
        Assert.Equal(6.0m, movies.Options.MinVoteAverage);
        Assert.Equal(TmdbDiscoveryArm.DefaultMinVoteCount, movies.Options.MinVoteCount);

        // TV: no romance or horror id; animation is 16; fantasy would be 10765 — the same fused id as
        // the science fiction the request asked for, so it is left to the eligibility check instead of
        // emptying the request at the source.
        var shows = StandingDiscoverPlan.For(rules, movies: false, request);
        Assert.Equal([10765], shows.GenreIds);
        Assert.Equal([16], shows.ExcludeGenreIds);
    }

    [Theory]
    [InlineData(new string[0], "en")]
    [InlineData(new[] { "fr" }, "fr")]
    [InlineData(new[] { "*" }, null)]
    [InlineData(new[] { "en", "fr" }, null)]
    public void Only_a_single_language_projects_into_the_fetch(string[] languages, string? expected)
    {
        var rules = FilterRuleSet.Create(null, null, null, languages, null, null, null);
        var plan = StandingDiscoverPlan.For(
            rules, movies: true, new TmdbDiscoveryRequest(Guid.NewGuid(), [], [], [], [], MediaScope.Any, null, null, null, null, 10));
        Assert.Equal(expected, plan.Options.OriginalLanguage);
    }

    [Fact]
    public void A_list_row_is_judged_by_the_same_calls_the_eligibility_builder_makes()
    {
        var rules = FilterRuleSet.Create(
            excludeGenres: ["comedy", "family"],
            includeGenres: null,
            excludeKeywords: ["zombie"],
            languages: null,
            minCrowdRating: 6.0m,
            minYear: 2000,
            maxYear: 2020);

        TmdbListItem Row(
            decimal vote = 7m, string? language = "en", int year = 2010, int[]? genres = null, string name = "A film",
            string? overview = null, bool movie = true, bool dated = true) =>
            new(1, movie, name, null, null, overview, 1m, vote, 100,
                dated ? new DateTime(year, 6, 1, 0, 0, 0, DateTimeKind.Utc) : null, genres ?? [18], language);

        Assert.True(StandingDiscoverPlan.Admits(rules, Row()));
        Assert.True(StandingDiscoverPlan.Admits(rules, Row(dated: false)));           // undated passes a year window
        Assert.False(StandingDiscoverPlan.Admits(rules, Row(language: null)));        // unknown language is not English
        Assert.False(StandingDiscoverPlan.Admits(rules, Row(language: "ja")));
        Assert.False(StandingDiscoverPlan.Admits(rules, Row(vote: 0m)));              // unrated fails a floor
        Assert.False(StandingDiscoverPlan.Admits(rules, Row(vote: 5.9m)));
        Assert.False(StandingDiscoverPlan.Admits(rules, Row(year: 1999)));
        Assert.False(StandingDiscoverPlan.Admits(rules, Row(year: 2021)));
        Assert.False(StandingDiscoverPlan.Admits(rules, Row(genres: [35, 18])));      // comedy
        Assert.False(StandingDiscoverPlan.Admits(rules, Row(genres: [10762], movie: false))); // TV Kids → family
        Assert.False(StandingDiscoverPlan.Admits(rules, Row(name: "Zombie Town")));
        Assert.False(StandingDiscoverPlan.Admits(rules, Row(overview: "A zombie outbreak.")));

        var allowList = FilterRuleSet.Create(null, ["horror"], null, null, null, null, null);
        Assert.True(StandingDiscoverPlan.Admits(allowList, Row(genres: [27])));
        Assert.False(StandingDiscoverPlan.Admits(allowList, Row(genres: [18])));
    }
}
