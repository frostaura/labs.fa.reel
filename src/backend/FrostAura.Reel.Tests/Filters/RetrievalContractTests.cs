using FrostAura.Reel.Application.Search;
using FrostAura.Reel.Application.Search.Retrieval;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Filters;
using FrostAura.Reel.Domain.Ports;

namespace FrostAura.Reel.Tests.Filters;

/// <summary>
/// The database-free half of the retrieval contract: how a v1 intent reads as hard constraints, how
/// stored media scopes degrade, how evidence names the tag that matched, and the plain-words labels a
/// relaxation chip carries. The counting half is pinned on Postgres in <c>SupplyReporterTests</c>.
/// </summary>
public class RetrievalContractTests
{
    [Fact]
    public void Intent_keywords_become_one_concept_whose_labels_are_alternatives()
    {
        var intent = Intent(keywords: ["Ninja", " shinobi ", "ninja"], genres: ["action"]);

        var hard = IntentConstraints.FromIntent(intent);

        var concept = Assert.Single(hard.Concepts);
        Assert.Equal(["ninja", "shinobi"], concept.CatalogueLabels);
        // Beside a keyword, a genre is the interpreter's flavour guess, not a requirement.
        Assert.Empty(concept.Genres);
        Assert.Equal("ninja shinobi", concept.UserWords);
    }

    [Fact]
    public void Intent_genres_become_the_concept_only_when_there_are_no_keywords()
    {
        var hard = IntentConstraints.FromIntent(Intent(genres: ["Horror", "thriller"]), userWords: "scary films");

        var concept = Assert.Single(hard.Concepts);
        Assert.Empty(concept.CatalogueLabels);
        Assert.Equal(["horror", "thriller"], concept.Genres);
        Assert.Equal("scary films", concept.UserWords);
    }

    [Fact]
    public void Intent_with_no_topic_has_no_concepts_and_negations_are_never_constraints()
    {
        var intent = Intent() with { ExcludeGenres = ["romance"], ExcludeKeywords = ["ghost"] };

        var hard = IntentConstraints.FromIntent(intent);

        Assert.Empty(hard.Concepts);
        Assert.False(hard.HasConcepts);
        Assert.Null(hard.Years);
    }

    [Fact]
    public void Intent_people_years_runtime_and_media_carry_over()
    {
        var intent = Intent(keywords: ["heist"]) with
        {
            MinYear = 1990,
            MaxYear = 1999,
            MaxRuntimeMinutes = 110,
            MediaTypes = [MediaType.Show],
            People = [new PersonQuery(" Michael Mann ", PersonRole.Director), new PersonQuery(" ", PersonRole.Any)],
        };

        var hard = IntentConstraints.FromIntent(intent);

        Assert.Equal(new YearRange(1990, 1999), hard.Years);
        Assert.Equal(110, hard.MaxRuntimeMinutes);
        Assert.Equal(MediaScope.Shows, hard.Media);
        var person = Assert.Single(hard.People);
        Assert.Equal(new PersonConstraint("Michael Mann", null, PersonRole.Director), person);

        // The request's own media filter beats the interpreter's guess.
        Assert.Equal(MediaScope.Movies, IntentConstraints.FromIntent(intent, [MediaType.Movie]).Media);
        Assert.Equal(MediaScope.Any, IntentConstraints.FromIntent(intent, [MediaType.Movie, MediaType.Show]).Media);
    }

    [Theory]
    [InlineData("Movies", MediaScope.Movies)]
    [InlineData("shows", MediaScope.Shows)]
    [InlineData("Any", MediaScope.Any)]
    [InlineData("Documentaries", MediaScope.Any)]
    [InlineData("7", MediaScope.Any)]
    [InlineData("", MediaScope.Any)]
    [InlineData(null, MediaScope.Any)]
    public void Stored_media_scopes_map_unknown_values_to_any(string? stored, MediaScope expected) =>
        Assert.Equal(expected, MediaScopes.Parse(stored));

    [Fact]
    public void Evidence_names_the_tag_that_matched_not_the_label()
    {
        var title = new Title { Keywords = ["female ninja", "ninja", "katana"], Genres = ["action", "drama"] };
        var hard = HardConstraints.None with
        {
            Concepts = [new ConceptTerm("ninja", ["ninja"], ["action"]), new ConceptTerm("swords", [], ["horror"])],
            People = [new PersonConstraint(" Sho Kosugi ", null, PersonRole.Cast)],
        };

        Assert.Equal(
            ["keyword:female ninja", "keyword:ninja", "genre:action", "person:Sho Kosugi/cast"],
            HardConstraintFilter.EvidenceFor(title, hard));
    }

    [Fact]
    public void A_concept_without_labels_matches_on_the_users_own_words()
    {
        Assert.Equal(["time loop"], HardConstraintFilter.LabelsOf(new ConceptTerm(" Time Loop ", [], [])));
        Assert.Equal(["shinobi", "ninja"], HardConstraintFilter.LabelsOf(new ConceptTerm("ninja", ["Shinobi", "ninja", ""], [])));
    }

    [Fact]
    public void Clearing_floor_is_what_is_left_of_eligible()
    {
        var report = new SupplyReport(20, 6, 2, 7, 5, 1, 2, [], 3, 50, 6.0m, KnockoutsMeasured: true);

        Assert.Equal(2, report.ClearingFloor);
        Assert.Equal(report.MatchingInCatalogue, report.AlreadySeen + report.SavedOrDismissed + report.ExcludedByConditions + report.Eligible);
    }

    [Theory]
    [InlineData(FilterKind.ExcludeGenre, "comedy", "excluded genre: comedy")]
    [InlineData(FilterKind.IncludeGenre, "drama", "only these genres: drama")]
    [InlineData(FilterKind.ExcludeKeyword, "zombie", "excluded keyword: zombie")]
    [InlineData(FilterKind.Language, "en", "English only")]
    [InlineData(FilterKind.Language, "en,fr", "languages: en,fr")]
    [InlineData(FilterKind.MinCrowdRating, "6.0", "crowd rating at least 6.0")]
    [InlineData(FilterKind.MinYear, "2000", "released 2000 or later")]
    [InlineData(FilterKind.MaxYear, "2010", "released 2010 or earlier")]
    public void Knockout_labels_say_the_rule_in_plain_words(FilterKind kind, string value, string expected) =>
        Assert.Equal(expected, SupplyReporter.LabelFor(new FilterRule(kind, value)));

    private static SearchIntent Intent(IReadOnlyList<string>? keywords = null, IReadOnlyList<string>? genres = null) =>
        new(genres ?? [], keywords ?? [], [], string.Empty, null, null);
}
