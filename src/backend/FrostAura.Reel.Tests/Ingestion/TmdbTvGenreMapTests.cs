using FrostAura.Reel.Application.Ml;
using FrostAura.Reel.Domain.Ports.Tmdb;

namespace FrostAura.Reel.Tests.Ingestion;

/// <summary>
/// The golden set for TMDB's TV genre taxonomy, and the reason it is golden rather than sampled:
/// a discovered SHOW and the SAME show ingested from the user's Trakt library must light the same
/// genre one-hots. Before this the TV map covered 11 of the 25 ingest slugs and was 1:1, so
/// <c>adventure</c>, <c>fantasy</c>, <c>thriller</c>, <c>horror</c> and nine more were TRAIN-ONLY
/// for television — the model could learn "no adventure flag ⇒ discovered candidate", which is a
/// label proxy serving can never reproduce, and no test could see it because an unmapped id is
/// silently dropped by <see cref="TmdbGenres.MapToSlugs"/> rather than throwing.
///
/// <para>TMDB fuses three pairs the movie taxonomy keeps apart (Action &amp; Adventure, Sci-Fi &amp;
/// Fantasy, War &amp; Politics), so the TV map is deliberately one-id-to-many-slugs. That is the
/// difference this file exists to hold.</para>
/// </summary>
public class TmdbTvGenreMapTests
{
    /// <summary>
    /// GET /3/genre/tv/list — the complete TMDB TV genre taxonomy. Sixteen ids; TMDB has not added
    /// one in years, and if it does, this test is the thing that says so instead of the candidate
    /// pool quietly losing a genre.
    /// </summary>
    private static readonly (int Id, string Name)[] TmdbTvGenreList =
    [
        (10759, "Action & Adventure"),
        (16, "Animation"),
        (35, "Comedy"),
        (80, "Crime"),
        (99, "Documentary"),
        (18, "Drama"),
        (10751, "Family"),
        (10762, "Kids"),
        (9648, "Mystery"),
        (10763, "News"),
        (10764, "Reality"),
        (10765, "Sci-Fi & Fantasy"),
        (10766, "Soap"),
        (10767, "Talk"),
        (10768, "War & Politics"),
        (37, "Western"),
    ];

    [Fact]
    public void Every_tmdb_tv_genre_id_maps_to_at_least_one_slug()
    {
        var unmapped = TmdbTvGenreList
            .Where(g => !TmdbGenres.TvIdToSlugs.ContainsKey(g.Id))
            .Select(g => $"{g.Id} ({g.Name})")
            .ToList();

        Assert.Empty(unmapped);

        // The map carries nothing TMDB does not: a stale id would send discover a genre filter that
        // matches nothing and would look, from the outside, exactly like "TMDB has no such shows".
        var known = TmdbTvGenreList.Select(g => g.Id).ToHashSet();
        Assert.All(TmdbGenres.TvIdToSlugs.Keys, id => Assert.Contains(id, known));
        Assert.Equal(TmdbTvGenreList.Length, TmdbGenres.TvIdToSlugs.Count);
    }

    [Fact]
    public void The_fused_tmdb_genres_light_both_of_the_slugs_they_stand_for()
    {
        // The three ids where the taxonomies genuinely disagree. Each used to yield exactly one slug
        // and drop the other on the floor.
        Assert.Equal(["action", "adventure"], TmdbGenres.MapToSlugs(isMovie: false, [10759]));
        Assert.Equal(["science-fiction", "fantasy"], TmdbGenres.MapToSlugs(isMovie: false, [10765]));
        Assert.Equal(["war"], TmdbGenres.MapToSlugs(isMovie: false, [10768]));

        // Kids has no "children" slug in the ingest vocabulary, so it folds into family — and a show
        // tagged both Family and Kids must not yield "family" twice.
        Assert.Equal(["family"], TmdbGenres.MapToSlugs(isMovie: false, [10751, 10762]));
    }

    [Fact]
    public void A_discovered_show_lights_the_same_one_hots_as_the_same_show_from_the_library()
    {
        // The actual defect, stated as the assertion. "The Expanse" arrives from TMDB as
        // 10765 (Sci-Fi & Fantasy) + 18 (Drama) + 10759 (Action & Adventure); from Trakt it arrives
        // already carrying the slugs. The vectors must agree.
        var discovered = TmdbGenres.MapToSlugs(isMovie: false, [10765, 18, 10759]);
        string[] fromTrakt = ["science-fiction", "drama", "action", "adventure", "fantasy"];

        Assert.Equal(
            fromTrakt.Where(FeatureSchema.Genres.Contains).OrderBy(g => g, StringComparer.Ordinal),
            discovered.Where(FeatureSchema.Genres.Contains).OrderBy(g => g, StringComparer.Ordinal));
    }

    [Fact]
    public void Every_tv_slug_now_has_a_feature_genre_slot()
    {
        // Until schema v5 this test carried a `filterOnly` escape hatch for reality / news /
        // talk-show / soap: real Trakt genres, mapped here so the account's ExcludeGenre rules could
        // see them, with no slot in FeatureSchema.Genres and therefore no one-hot. v5 gave all four
        // a slot, so the escape hatch is gone and the assertion is the stronger one it always wanted
        // to be — EVERY slug this map can produce lands in the vector.
        var stray = TmdbGenres.TvIdToSlugs.Values
            .SelectMany(slugs => slugs)
            .Distinct(StringComparer.Ordinal)
            .Where(slug => !FeatureSchema.Genres.Contains(slug))
            .ToList();

        Assert.Empty(stray);
    }

    [Fact]
    public void Game_show_is_a_feature_slot_that_tmdb_has_no_id_for()
    {
        // The one unscripted slot TMDB's TV taxonomy cannot fill: it has Reality, Soap, Talk and
        // News ids but no game-show id, so that slot lights through Trakt ingest only. Pinned so the
        // asymmetry is a stated fact rather than a silent hole — and so it FAILS if TMDB ever adds
        // one, which is the moment the map should gain a row.
        Assert.Contains("game-show", FeatureSchema.Genres);
        Assert.DoesNotContain(
            "game-show",
            TmdbGenres.TvIdToSlugs.Values.SelectMany(slugs => slugs),
            StringComparer.Ordinal);

        // The other four DO map, so a TMDB-discovered unscripted show lights the same one-hot its
        // Trakt-ingested counterpart does. That parity is the reason the slots were added.
        foreach (var slug in new[] { "reality", "soap", "talk-show", "news" })
        {
            Assert.Contains(slug, FeatureSchema.Genres);
            Assert.Contains(
                slug,
                TmdbGenres.TvIdToSlugs.Values.SelectMany(slugs => slugs),
                StringComparer.Ordinal);
        }
    }

    [Fact]
    public void The_movie_map_is_unchanged_and_still_one_to_one()
    {
        // The movie taxonomy does not fuse anything, so it stays a plain 1:1 map. Pinned because the
        // TV change reworked the shared reverse-map builder that both sides run through.
        Assert.Equal(19, TmdbGenres.MovieIdToSlug.Count);
        Assert.Equal(["tv-movie"], TmdbGenres.MapToSlugs(isMovie: true, [10770]));
        Assert.Equal(["action"], TmdbGenres.MapToSlugs(isMovie: true, [28]));
        Assert.Equal(["adventure"], TmdbGenres.MapToSlugs(isMovie: true, [12]));
        Assert.Equal([], TmdbGenres.MapToSlugs(isMovie: true, [10759]));
    }

    [Fact]
    public void The_reverse_map_answers_for_both_halves_of_a_fused_genre_and_is_deterministic()
    {
        // Asking TMDB for TV "fantasy" means asking for 10765, which is the closest thing it has and
        // is exactly what the forward map says a 10765 show is. Before this, SlugToIds["fantasy"].Tv
        // was null and AccountDiscoveryRules silently dropped the genre from the TV discover call.
        Assert.Equal(10765, TmdbGenres.SlugToIds["fantasy"].Tv);
        Assert.Equal(10765, TmdbGenres.SlugToIds["science-fiction"].Tv);
        Assert.Equal(10759, TmdbGenres.SlugToIds["adventure"].Tv);
        Assert.Equal(10759, TmdbGenres.SlugToIds["action"].Tv);

        // Movie ids are untouched by the TV rework.
        Assert.Equal(14, TmdbGenres.SlugToIds["fantasy"].Movie);
        Assert.Equal(12, TmdbGenres.SlugToIds["adventure"].Movie);

        // `family` is the one slug two TV ids claim; the tie resolves by id so it cannot depend on
        // dictionary insertion order.
        Assert.Equal(10751, TmdbGenres.SlugToIds["family"].Tv);

        // A slug TV genuinely has no equivalent for stays null rather than borrowing a movie id.
        Assert.Null(TmdbGenres.SlugToIds["horror"].Tv);
    }
}
