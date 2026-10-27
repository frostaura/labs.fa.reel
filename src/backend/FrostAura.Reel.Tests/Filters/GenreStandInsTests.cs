using FrostAura.Reel.Application.Search.Retrieval;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Ports.Tmdb;

namespace FrostAura.Reel.Tests.Filters;

/// <summary>
/// The taxonomy-gap map that makes an ExcludeGenre airtight for TV. The SQL arm is exercised on real
/// Postgres in <c>ContentFilterEligibilityTests</c>; these pin the map's semantics so a careless edit
/// to a stem (or a TMDB taxonomy change) fails here first.
/// </summary>
public class GenreStandInsTests
{
    [Theory]
    [InlineData("romance", new[] { MediaType.Show })]
    [InlineData("horror", new[] { MediaType.Show })]
    [InlineData("history", new[] { MediaType.Show })]
    [InlineData("thriller", new[] { MediaType.Show })]
    [InlineData("music", new[] { MediaType.Show })]
    [InlineData("anime", new[] { MediaType.Movie, MediaType.Show })]
    [InlineData("musical", new[] { MediaType.Movie, MediaType.Show })]
    [InlineData("action", new MediaType[0])]
    [InlineData("fantasy", new MediaType[0])]
    public void A_stand_in_applies_only_to_the_media_whose_taxonomy_cannot_tag_the_genre(string slug, MediaType[] expected)
    {
        Assert.Equal(expected, GenreStandIns.MediaFor(slug));
    }

    [Fact]
    public void Every_slug_with_stand_ins_has_a_medium_to_apply_them_to()
    {
        // A slug both taxonomies can tag would never use its stand-ins — dead patterns read as
        // protection that is not there.
        Assert.All(GenreStandIns.Slugs, slug => Assert.NotEmpty(GenreStandIns.MediaFor(slug)));

        // And the derivation really is from TMDB's own maps: romance has a movie id and no TV id.
        Assert.NotNull(TmdbGenres.SlugToIds["romance"].Movie);
        Assert.Null(TmdbGenres.SlugToIds["romance"].Tv);
    }

    [Theory]
    [InlineData("romance", "romance", true)]
    [InlineData("romance", "teenage romance", true)]
    [InlineData("romance", "romance musical", true)]
    [InlineData("romance", "slow burn romance drama", true)]
    [InlineData("romance", "romantic comedy", true)]
    [InlineData("romance", "bromance", false)]
    [InlineData("romance", "necromancer", false)]
    [InlineData("horror", "body horror", true)]
    [InlineData("horror", "slasher", true)]
    [InlineData("horror", "horrors of war", false)]
    [InlineData("history", "history", true)]
    [InlineData("history", "historical drama", true)]
    [InlineData("history", "period drama", true)]           // 0.36 of such films are History: 8x the base rate
    [InlineData("history", "19th century", true)]           // 0.28: 6x
    [InlineData("history", "victorian england", false)]     // 0.19: under the 0.25 floor
    [InlineData("history", "british history", true)]
    [InlineData("romance", "love triangle", true)]
    [InlineData("romance", "forbidden love", true)]
    [InlineData("romance", "love story", true)]
    [InlineData("romance", "star crossed lovers", true)]      // 13 films, all romance: 10 films is the floor
    [InlineData("romance", "unrequited", false)]
    [InlineData("music", "rock music", true)]
    [InlineData("music", "music competition", true)]
    [InlineData("music", "musician", true)]                 // 0.54 against a 0.03 base rate
    [InlineData("music", "musical", false)]                 // a different genre, not a word match
    [InlineData("music", "soap opera", false)]              // "opera" is an exact label only
    [InlineData("horror", "shark", false)]                  // a setting, not the genre's subject
    [InlineData("horror", "zombie apocalypse", true)]
    [InlineData("thriller", "suspenseful", true)]
    [InlineData("history", "dating history", false)]
    [InlineData("history", "alternate history", false)]
    [InlineData("history", "prehistory", false)]
    [InlineData("anime", "anime", true)]
    [InlineData("anime", "based on anime", false)]
    [InlineData("musical", "jukebox musical", true)]
    [InlineData("musical", "musical instruments", false)]
    [InlineData("action", "action", false)]
    public void A_label_stands_in_by_whole_word_or_exact_label_never_by_substring(string slug, string label, bool expected)
    {
        Assert.Equal(expected, GenreStandIns.Matches(slug, label));
    }

    [Fact]
    public void The_sql_predicate_selects_the_same_titles_as_the_in_memory_twin()
    {
        // The expression is what EF translates; compiling it and running it in memory proves the
        // two phrasings agree on media and labels for every slug.
        var titles = new List<Title>();
        string[] labels =
        [
            "romance", "teenage romance", "bromance", "supernatural horror", "slasher", "period drama",
            "dating history", "anime", "based on anime", "jukebox musical", "musical instruments",
            "psychological thriller", "musician", "music video", "love triangle", "british history",
            "joseon dynasty (1392–1910)", "séance", "boys' love (bl)", "rock 'n' roll", "rock music",
        ];
        foreach (var media in new[] { MediaType.Movie, MediaType.Show })
        {
            foreach (var label in labels)
            {
                titles.Add(new Title { Id = Guid.NewGuid(), MediaType = media, Name = label, Keywords = [label] });
            }
        }

        // Overview fixtures (no stand-in keyword), so the compiled overview arm is held to the twin too.
        string[] overviews =
        [
            "Deals with an old romance.", "A bromance.", "Is she\nin love?", "A teenage Crush.", "A crushed car.",
        ];
        foreach (var media in new[] { MediaType.Movie, MediaType.Show })
        {
            foreach (var overview in overviews)
            {
                titles.Add(new Title { Id = Guid.NewGuid(), MediaType = media, Name = "x", Keywords = ["detective"], Overview = overview });
            }

            titles.Add(new Title { Id = Guid.NewGuid(), MediaType = media, Name = "x", Keywords = [], Overview = null });
        }

        foreach (var slug in GenreStandIns.Slugs)
        {
            var compiled = GenreStandIns.CarriesStandIn(slug).Compile();
            foreach (var title in titles)
            {
                Assert.Equal(GenreStandIns.Carries(title, slug), compiled(title));
            }
        }

        // And the fixture is not vacuous: a romance-keyword show is caught, the same film is not, and an
        // overview-only show is caught by the overview arm.
        Assert.Contains(titles, t => GenreStandIns.Carries(t, "romance"));
        Assert.Equal(
            3,
            titles.Count(t => t.MediaType == MediaType.Show && t.Keywords.SequenceEqual(["detective"]) && GenreStandIns.Carries(t, "romance")));
        Assert.DoesNotContain(titles, t => t.MediaType == MediaType.Movie && GenreStandIns.Carries(t, "romance"));
    }

    [Fact]
    public void An_exclusion_reaches_the_genre_its_alias_and_its_stand_ins()
    {
        var childrenShow = new Title { MediaType = MediaType.Show, Genres = ["children"] };
        var familyFilm = new Title { MediaType = MediaType.Movie, Genres = ["family"] };
        var romanceShow = new Title { MediaType = MediaType.Show, Keywords = ["gay romance"] };
        var romanceKeywordFilm = new Title { MediaType = MediaType.Movie, Genres = ["thriller"], Keywords = ["romance"] };
        var suspenseShow = new Title { MediaType = MediaType.Show, Genres = ["suspense"] };

        Assert.True(GenreStandIns.ExcludedBy(childrenShow, "family"));
        Assert.True(GenreStandIns.ExcludedBy(familyFilm, "family"));
        Assert.True(GenreStandIns.ExcludedBy(romanceShow, "romance"));
        Assert.False(GenreStandIns.ExcludedBy(romanceKeywordFilm, "romance"));
        Assert.False(GenreStandIns.ExcludedBy(childrenShow, "animation"));
        Assert.True(GenreStandIns.ExcludedBy(suspenseShow, "thriller"));
        Assert.False(GenreStandIns.ExcludedBy(suspenseShow, "horror"));
    }

    [Theory]
    [InlineData("Ally deals with an old romance when she returns home.", true)]       // Chesapeake Shores
    [InlineData("A teenage CRUSH who is now a police officer.", true)]                // Roswell, New Mexico; any case
    [InlineData("Romantic entanglements in a Los Angeles circle.", true)]
    [InlineData("A love story across three generations.", true)]
    [InlineData("She falls in love with the prince.", true)]
    [InlineData("Two strangers fell in love.", true)]
    [InlineData("Is she\nin love with him?", true)]                                  // a newline is a boundary
    [InlineData("A bromance between two detectives.", false)]                         // never inside a word
    [InlineData("A lovely town with a dark secret.", false)]
    [InlineData("A crushed car and a missing driver.", false)]
    [InlineData("Necromancers rule the city.", false)]
    [InlineData("A loving family moves to the coast.", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    public void A_romance_overview_phrase_matches_as_a_whole_word_in_any_case(string? overview, bool expected)
    {
        // HREV1-RET-09: TV romance that TMDB tags in neither the taxonomy nor a keyword is often only
        // in the overview. The phrases are whole words, bounded by any non-letter or the text's edge.
        Assert.Equal(expected, GenreStandIns.MatchesOverview("romance", overview));
    }

    [Fact]
    public void The_overview_arm_applies_only_on_the_media_whose_taxonomy_cannot_tag_the_genre()
    {
        var show = new Title { MediaType = MediaType.Show, Genres = ["drama"], Overview = "Deals with an old romance." };
        var film = new Title { MediaType = MediaType.Movie, Genres = ["drama"], Overview = "Deals with an old romance." };
        var horrorWordShow = new Title { MediaType = MediaType.Show, Genres = ["drama"], Overview = "A horror of a marriage." };

        Assert.True(GenreStandIns.ExcludedBy(show, "romance"));
        Assert.False(GenreStandIns.ExcludedBy(film, "romance"));   // TMDB tags films with romance itself
        Assert.False(GenreStandIns.ExcludedBy(show, "horror"));
        Assert.False(GenreStandIns.ExcludedBy(horrorWordShow, "horror"));   // only romance has overview phrases

        // Every slug with overview phrases also has keyword patterns, so it is in Slugs and every arm
        // that walks Slugs (the eligibility builder, the truth SQL generator) reaches it.
        Assert.All(GenreStandIns.OverviewPhrases.Keys, slug => Assert.Contains(slug, GenreStandIns.Slugs));
    }

    [Fact]
    public void A_soap_is_excluded_by_no_romance_yet_keeps_its_own_chip()
    {
        // HREV1-RET-09: The L Word (drama, soap) and If Loving You Is Wrong (soap) were eligible under
        // "no romance". Soap is TV's one romance-shaped tag.
        var soapShow = new Title { MediaType = MediaType.Show, Genres = ["drama", "soap"] };
        var dramaShow = new Title { MediaType = MediaType.Show, Genres = ["drama"] };

        Assert.True(GenreStandIns.ExcludedBy(soapShow, "romance"));
        Assert.False(GenreStandIns.ExcludedBy(dramaShow, "romance"));
        Assert.False(GenreStandIns.ExcludedBy(soapShow, "horror"));
        Assert.True(GenreStandIns.ExcludedBy(soapShow, "soap"));
        Assert.Contains("soap", GenreStandIns.AliasesOf("romance"));

        // "no soaps" stays a condition of its own; children and suspense are only other names.
        Assert.Contains("soap", GenreStandIns.AliasesWithTheirOwnChip);
        Assert.DoesNotContain("children", GenreStandIns.AliasesWithTheirOwnChip);
        Assert.DoesNotContain("suspense", GenreStandIns.AliasesWithTheirOwnChip);
    }

    [Fact]
    public void Every_pattern_has_a_tmdb_keyword_id()
    {
        var missing = GenreStandIns.Patterns.Values
            .SelectMany(patterns => patterns)
            .Select(p => p.Text)
            .Where(text => !GenreStandIns.TmdbKeywordIds.ContainsKey(text))
            .ToList();
        Assert.Empty(missing);
        Assert.All(GenreStandIns.TmdbKeywordIds.Values, id => Assert.True(id > 0));
    }

    [Fact]
    public void A_slug_without_stand_ins_yields_a_predicate_that_matches_nothing()
    {
        var predicate = GenreStandIns.CarriesStandIn("action").Compile();
        Assert.False(predicate(new Title { MediaType = MediaType.Show, Keywords = ["action"] }));
    }
}
