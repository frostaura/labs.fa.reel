using FrostAura.Reel.Application.Search.AskReel;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Ports;

namespace FrostAura.Reel.Tests.Search;

/// <summary>
/// What a follow-up inherits: the live "feel-good movie" → "no superheroes please" exchange put two
/// series among the picks of a conversation about a movie.
/// </summary>
public class AskReelCarryTests
{
    [Theory]
    [InlineData("no superheroes please", true)]
    [InlineData("something funnier", true)]
    [InlineData("any shows instead?", false)]
    [InlineData("a series this time", false)]
    [InlineData("either is fine", false)]
    [InlineData("TV please", false)]
    // "show me" is the verb: a follow-up phrased as a command still carries the medium.
    [InlineData("show me something without superheroes", true)]
    [InlineData("can you show us a few more", true)]
    [InlineData("show me a series this time", false)]
    public void A_follow_up_silent_about_media_keeps_the_previous_single_type(string message, bool carried)
    {
        var media = AskReelCarry.Media([], [MediaType.Movie], message);
        Assert.Equal(carried ? [MediaType.Movie] : [], media);
    }

    /// <summary>
    /// HREV1-ASK-02 follow-on: "show" in "show me" / "show us" is the verb, so "show me samurai
    /// films" names films only and "show me something feel-good" names no medium; a "show" that a
    /// determiner owns ("a cosy show me and my wife can watch") is still the noun.
    /// </summary>
    [Theory]
    [InlineData("show me samurai films", MediaType.Movie)]
    [InlineData("can you show me Liam Neeson movies", MediaType.Movie)]
    [InlineData("SHOW US kaiju flicks", MediaType.Movie)]
    [InlineData("Show us some zombie series", MediaType.Show)]
    [InlineData("show me a show about chefs", MediaType.Show)]
    [InlineData("a cosy show me and my wife can watch", MediaType.Show)]
    [InlineData("any show us kids would like", MediaType.Show)]
    [InlineData("I loved those films, show me more", MediaType.Movie)]
    [InlineData("show me something feel-good", null)]
    [InlineData("show me movies or shows", null)]
    [InlineData("zombie shows", MediaType.Show)]
    public void The_verb_show_names_no_medium(string message, MediaType? expected) =>
        Assert.Equal(expected is { } media ? [media] : [], AskReelCarry.MediaNamed(message));

    [Fact]
    public void A_definite_reading_or_an_ambiguous_previous_one_is_never_overridden()
    {
        Assert.Equal([MediaType.Show], AskReelCarry.Media([MediaType.Show], [MediaType.Movie], "more like that"));
        Assert.Empty(AskReelCarry.Media([], [MediaType.Movie, MediaType.Show], "more like that"));
        Assert.Empty(AskReelCarry.Media([], null, "more like that"));
        Assert.Equal([MediaType.Movie], AskReelCarry.Media([MediaType.Movie, MediaType.Show], [MediaType.Movie], "more like that"));
    }

    /// <summary>The previous reading comes off the stored row: serialised exactly as the turn writes it.</summary>
    [Fact]
    public void A_stored_intent_round_trips_and_garbage_reads_as_none()
    {
        var intent = new SearchIntent(
            ["comedy"], ["feel-good"], [MediaType.Movie], "feel-good movie", 1990, null,
            ExcludeKeywords: ["superhero"]);
        var parsed = AskReelCarry.Parse(System.Text.Json.JsonSerializer.Serialize(intent));

        Assert.NotNull(parsed);
        Assert.Equal([MediaType.Movie], parsed!.MediaTypes);
        Assert.Equal(1990, parsed.MinYear);
        Assert.Equal(["superhero"], parsed.ExcludeKeywords);
        Assert.Null(AskReelCarry.Parse("{not json"));
        Assert.Null(AskReelCarry.Parse(null));
        Assert.Null(AskReelCarry.Parse("  "));
    }
}
