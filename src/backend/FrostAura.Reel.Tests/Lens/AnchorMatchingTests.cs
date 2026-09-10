using FrostAura.Reel.Application.Lens;

namespace FrostAura.Reel.Tests.Lens;

/// <summary>
/// The anchor matcher — the half of the invented-anchor guard that decides whether a title the
/// model named is a title the account owns.
///
/// <para><b>The failure these pin down was total, not marginal.</b> Every on-demand lens the
/// founder's account generated was refused by the invented-anchor guard, and the stored rows say
/// why: the model anchored on "Frontera", "Lawless", "Amateur" and "The Girl with All the Gifts" —
/// every one of them a title from the DECLINED line, which was the only list of titles anywhere in
/// the prompt, and none of them RATED, which was the only thing the guard accepted. Three separate
/// defects had to line up for that, and each has a case here: the library is too narrow, the
/// normalisation was unreachable, and a single bad anchor sank the whole note.</para>
/// </summary>
public class AnchorMatchingTests
{
    private static readonly string[] LiveLibrary =
    [
        "The Amateur",
        "Lawless",
        "Frontera",
        "The Girl with All the Gifts",
        "Sicario",
        "Amélie",
        "Blade Runner 2049",
        "Blade Runner",
        "Up",
    ];

    private static HashSet<string> Library() => LensGuards.BuildTitleIndex(LiveLibrary);

    // ── The live failure shape ──────────────────────────────────────────────────────────────

    [Theory]
    // The exact four anchors in the two Rejected rows on the founder's account.
    [InlineData("Frontera")]
    [InlineData("Lawless")]
    [InlineData("The Girl with All the Gifts")]
    // "Amateur" is how the model wrote it; the library row is "The Amateur". The old guard
    // compared raw names and called a title the account had watched an invention.
    [InlineData("Amateur")]
    public void The_anchors_that_sank_every_live_note_now_resolve(string anchor) =>
        Assert.True(
            LensGuards.MatchesLibrary(anchor, Library()),
            $"'{anchor}' is in this account's library and must not read as invented.");

    [Fact]
    public void A_leading_article_on_either_side_is_not_a_different_film()
    {
        var library = Library();

        Assert.True(LensGuards.MatchesLibrary("Amateur", library));
        Assert.True(LensGuards.MatchesLibrary("The Lawless", library));
    }

    [Fact]
    public void A_trailing_year_the_model_added_for_clarity_is_not_a_different_film()
    {
        var library = Library();

        Assert.True(LensGuards.MatchesLibrary("Sicario (2015)", library));
        Assert.True(LensGuards.MatchesLibrary("Sicario [2015]", library));
        Assert.True(LensGuards.MatchesLibrary("Sicario 2015", library));
    }

    [Fact]
    public void Case_punctuation_and_diacritics_are_not_differences()
    {
        var library = Library();

        Assert.True(LensGuards.MatchesLibrary("sicario", library));
        Assert.True(LensGuards.MatchesLibrary("\"Sicario\"", library));
        Assert.True(LensGuards.MatchesLibrary("Amelie", library));
        Assert.True(LensGuards.MatchesLibrary("The Girl With All The Gifts", library));
    }

    [Fact]
    public void A_long_name_survives_a_small_typo()
    {
        // Two edits on a name of eight characters or more. Enough for a dropped letter or a
        // plural; not enough to turn one film into another.
        Assert.True(LensGuards.MatchesLibrary("Frontiera", Library()));
    }

    [Fact]
    public void A_short_name_gets_no_fuzzy_tolerance_at_all()
    {
        // "Us" is two edits from "Up" and an entirely different film. Below the length floor the
        // matcher is exact, because at two characters edit distance is most of the word.
        Assert.False(LensGuards.MatchesLibrary("Us", Library()));
    }

    [Fact]
    public void A_title_whose_name_ends_in_a_year_keeps_its_own_identity()
    {
        // Unconditional year-stripping would turn "Blade Runner 2049" into "Blade Runner" — a
        // different film that this account also owns. The exact name stays authoritative.
        Assert.Equal("blade runner 2049", LensGuards.NormaliseTitle("Blade Runner 2049"));
        Assert.Equal("blade runner", LensGuards.NormaliseTitle("Blade Runner"));
        Assert.Contains("blade runner 2049", LensGuards.TitleKeys("Blade Runner 2049"));
    }

    [Fact]
    public void A_title_the_account_has_never_heard_of_still_does_not_match()
    {
        var library = Library();

        Assert.False(LensGuards.MatchesLibrary("Emily in Paris", library));
        Assert.False(LensGuards.MatchesLibrary("Some Film Nobody Rated", library));
    }

    // ── The normaliser itself ───────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("The Amateur", "amateur")]
    [InlineData("  Sicario  ", "sicario")]
    [InlineData("Sicario: Day of the Soldado", "sicario day of the soldado")]
    [InlineData("Amélie", "amelie")]
    [InlineData("A Quiet Place", "quiet place")]
    [InlineData("An Education", "education")]
    [InlineData("Spider-Man", "spider man")]
    [InlineData("Sicario (2015)", "sicario")]
    public void The_normaliser_folds_exactly_the_differences_that_are_not_differences(
        string raw, string expected) =>
        Assert.Equal(expected, LensGuards.NormaliseTitle(raw));

    [Fact]
    public void The_edit_distance_bails_out_rather_than_walking_the_whole_matrix()
    {
        Assert.True(LensGuards.EditDistanceWithin("frontera", "frontiera", 2));
        Assert.False(LensGuards.EditDistanceWithin("frontera", "lawless", 2));
        // Length alone rules this one out before a single row is computed.
        Assert.False(LensGuards.EditDistanceWithin("up", "blade runner 2049", 2));
    }

    [Fact]
    public void An_empty_library_matches_nothing_and_does_not_throw() =>
        Assert.False(LensGuards.MatchesLibrary("Sicario", LensGuards.BuildTitleIndex([])));
}
