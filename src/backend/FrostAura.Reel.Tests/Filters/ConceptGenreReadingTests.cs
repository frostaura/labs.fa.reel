using FrostAura.Reel.Application.Search.Retrieval;
using FrostAura.Reel.Application.Search.Retrieval.Concepts;

namespace FrostAura.Reel.Tests.Filters;

/// <summary>
/// HREV2-RET-03, in memory: a concept the resolver never touched — the stored conversation state's —
/// is read by the predicate the way resolution reads it. Genre words are genres ("romantic" is the
/// romance genre, "soap operas" the soap genre), several genre words are a requirement each unless the
/// user said "or", and a concept requiring exactly what an earlier one does is counted once.
/// </summary>
public class ConceptGenreReadingTests
{
    [Theory]
    [InlineData("romantic", new[] { "romance" })]
    [InlineData("Romantic shows", new[] { "romance" })]
    [InlineData("romances", new[] { "romance" })]
    [InlineData("soap opera", new[] { "soap" })]
    [InlineData("soap operas", new[] { "soap" })]
    [InlineData("Sci-Fi & Fantasy", new[] { "science-fiction", "fantasy" })]
    [InlineData("romance or comedy", new[] { "romance", "comedy" })]
    public void Genre_words_are_one_concept_of_their_genres(string words, string[] genres)
    {
        var read = Assert.Single(HardConstraintFilter.Read([new ConceptTerm(words, [], [])]));
        Assert.Equal(genres, read.Genres);
        Assert.Equal(words, read.UserWords);
        Assert.Empty(HardConstraintFilter.LabelsOf(read));
    }

    [Fact]
    public void Several_genre_words_are_a_requirement_each_and_a_repeated_requirement_counts_once()
    {
        var read = HardConstraintFilter.Read([new ConceptTerm("romantic comedies", [], [])]);
        Assert.Equal([["romance"], ["comedy"]], read.Select(c => c.Genres.ToArray()));

        // "romantic shows to binge" was planned as [romantic, romance]: one requirement, not two.
        var merged = Assert.Single(HardConstraintFilter.Read([new ConceptTerm("romantic", [], []), new ConceptTerm("romance", [], [])]));
        Assert.Equal("romantic", merged.UserWords);
        Assert.Equal(["romance"], merged.Genres);
    }

    [Theory]
    [InlineData("heist")]
    [InlineData("romantic heist")]
    [InlineData("star wars")]
    public void Words_that_are_not_all_genre_words_are_left_to_the_resolver(string words)
    {
        var concept = new ConceptTerm(words, [], []);
        Assert.Null(ConceptResolver.GenreReading(concept));
        Assert.Same(concept, Assert.Single(HardConstraintFilter.Read([concept])));
    }

    [Fact]
    public void A_concept_that_carries_genres_or_exact_labels_is_read_as_it_is()
    {
        var withGenres = new ConceptTerm("romantic", [], ["drama"]);
        var withLabels = new ConceptTerm("romantic", [], [], ["love story"]);
        Assert.Null(ConceptResolver.GenreReading(withGenres));
        Assert.Null(ConceptResolver.GenreReading(withLabels));
        Assert.Equal([withGenres, withLabels], HardConstraintFilter.Read([withGenres, withLabels]));
    }
}
