using FrostAura.Reel.Application.Ingestion;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Library;
using FrostAura.Reel.Domain.Ports.Trakt;

namespace FrostAura.Reel.Tests.Ingestion;

/// <summary>The pure rules behind WS-4 P1 tasks 8 and 9: TV Movie, next-episode air dates, the refresh projection, the ratings back-fill write.</summary>
public class P1IngestRulesTests
{
    [Fact]
    public void Tmdbs_tv_movie_genre_is_added_and_removed_only_by_tmdb()
    {
        Assert.Equal(["drama", "tv-movie"], TvMovieGenre.Apply(["drama"], [18, TvMovieGenre.TmdbMovieGenreId]));
        Assert.Equal(["drama"], TvMovieGenre.Apply(["drama", "tv-movie"], [18]));
        Assert.Equal(["drama", "tv-movie"], TvMovieGenre.Apply(["drama", "tv-movie"], null));
        Assert.Equal(["drama", "tv-movie"], TvMovieGenre.Apply(["drama", "tv-movie"], [TvMovieGenre.TmdbMovieGenreId]));
    }

    [Fact]
    public void A_trakt_walk_keeps_the_tv_movie_slug_trakt_cannot_know()
    {
        var title = new Title { MediaType = MediaType.Movie, Genres = ["drama", "tv-movie"] };
        IngestMapper.ApplyMovie(title, Movie(["drama", "romance"]));
        Assert.Equal(["drama", "romance", "tv-movie"], title.Genres);

        var theatrical = new Title { MediaType = MediaType.Movie, Genres = ["drama"] };
        IngestMapper.ApplyMovie(theatrical, Movie(["action"]));
        Assert.Equal(["action"], theatrical.Genres);
    }

    [Fact]
    public void The_next_episode_carries_its_air_date_and_a_caught_up_show_clears_it()
    {
        var now = new DateTime(2026, 10, 26, 0, 0, 0, DateTimeKind.Utc);
        var aired = new DateTime(2026, 10, 1, 2, 0, 0, DateTimeKind.Utc);
        var row = new ShowWatchProgress();

        TraktLibraryIngestor.ApplyNextEpisode(row, new TraktShowProgress(10, 4, new TraktNextEpisode(1, 5, "Five", aired)), now);
        Assert.Equal((1, 5, aired), (row.NextEpisodeSeason, row.NextEpisodeNumber, row.NextEpisodeAiredAt));
        Assert.Equal(now, row.UpdatedAt);

        TraktLibraryIngestor.ApplyNextEpisode(row, null, now.AddDays(1));
        Assert.Equal(1, row.NextEpisodeSeason);

        TraktLibraryIngestor.ApplyNextEpisode(row, new TraktShowProgress(10, 10, null), now.AddDays(2));
        Assert.Null(row.NextEpisodeSeason);
        Assert.Null(row.NextEpisodeNumber);
        Assert.Null(row.NextEpisodeAiredAt);
    }

    [Fact]
    public void The_refresh_sweep_cycles_the_current_catalogue_inside_six_months_at_the_default_cap()
    {
        // 38,262 TMDB-backed titles (the 10-22 catalogue) with ~2,000 recent releases, at the
        // shipped HYDRATE_MAX_TITLES_PER_DAY default.
        var projection = MetadataRefreshPolicy.Project(38_262, 2_000, 1_500);
        Assert.True(projection.InsideCacheLimit);
        Assert.Equal(150, projection.CycleDays);
        Assert.InRange(projection.SteadyRefreshesPerDay, 500, 550);

        // A cap the weekly tier alone eats can never cycle; a starved cap says so.
        Assert.False(MetadataRefreshPolicy.Project(38_262, 14_000, 1_500).InsideCacheLimit);
        Assert.False(MetadataRefreshPolicy.Project(38_262, 2_000, 400).InsideCacheLimit);
        Assert.Contains("OUTSIDE", MetadataRefreshPolicy.Project(38_262, 2_000, 400).Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_ratings_back_fill_stamps_every_ask_and_adopts_a_trakt_id_only_when_free()
    {
        var now = new DateTime(2026, 10, 26, 0, 0, 0, DateTimeKind.Utc);
        var title = new Title { MediaType = MediaType.Movie, TmdbId = 5 };

        TraktRatingsBackfill.Apply(title, null, mayAdoptTraktId: true, now);
        Assert.Equal(now, title.TraktRatingCheckedAt);
        Assert.Null(title.TraktRating);

        TraktRatingsBackfill.Apply(title, new TraktCatalogRating(77, "slug", "tt7", 7.5m, 900), mayAdoptTraktId: false, now);
        Assert.Equal((7.5m, 900), (title.TraktRating!.Value, title.TraktVotes));
        Assert.Null(title.TraktId);
        Assert.Equal("tt7", title.ImdbId);

        TraktRatingsBackfill.Apply(title, new TraktCatalogRating(77, "slug", null, null, 0), mayAdoptTraktId: true, now);
        Assert.Equal(77, title.TraktId);
        Assert.Equal("slug", title.TraktSlug);
        Assert.Equal(7.5m, title.TraktRating);
    }

    [Fact]
    public void A_minimal_trakt_object_does_not_zero_the_crowd_votes()
    {
        var title = new Title { MediaType = MediaType.Movie, TraktRating = 7.2m, TraktVotes = 5400 };
        IngestMapper.ApplyMovie(title, Movie(["drama"]));
        Assert.Equal((7.2m, 5400), (title.TraktRating!.Value, title.TraktVotes));

        IngestMapper.ApplyMovie(title, Movie(["drama"]) with { Rating = 7.4m, Votes = 5600 });
        Assert.Equal((7.4m, 5600), (title.TraktRating!.Value, title.TraktVotes));
    }

    private static TraktMovie Movie(string[] genres) => new(
        "M", 2001, new TraktIds(1, "m", null, 2), null, null, null, null, null, null, null, 0, null, null, genres, null, null);
}
