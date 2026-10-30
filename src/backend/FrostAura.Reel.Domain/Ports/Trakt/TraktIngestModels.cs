namespace FrostAura.Reel.Domain.Ports.Trakt;

/// <summary>External-id bundle present on every Trakt media object.</summary>
public record TraktIds(long Trakt, string? Slug, string? Imdb, long? Tmdb);

/// <summary>Movie object (extended=full slice Reel persists).</summary>
public record TraktMovie(
    string Title,
    int? Year,
    TraktIds Ids,
    string? Tagline,
    string? Overview,
    DateTime? Released,
    int? Runtime,
    string? Country,
    string? Status,
    decimal? Rating,
    int Votes,
    string? Trailer,
    string? Language,
    string[]? Genres,
    string[]? Subgenres,
    string? Certification);

/// <summary>Show object (extended=full slice Reel persists).</summary>
public record TraktShow(
    string Title,
    int? Year,
    TraktIds Ids,
    string? Tagline,
    string? Overview,
    DateTime? FirstAired,
    int? Runtime,
    string? Country,
    string? Status,
    decimal? Rating,
    int Votes,
    string? Trailer,
    string? Language,
    string[]? Genres,
    string[]? Subgenres,
    string? Certification,
    string? Network,
    int? AiredEpisodes);

public record TraktWatchedMovie(int Plays, DateTime? LastWatchedAt, DateTime? LastUpdatedAt, TraktMovie Movie);

public record TraktWatchedEpisode(int Number, int Plays, DateTime? LastWatchedAt);

public record TraktWatchedSeason(int Number, TraktWatchedEpisode[] Episodes);

public record TraktWatchedShow(
    int Plays,
    DateTime? LastWatchedAt,
    DateTime? LastUpdatedAt,
    DateTime? ResetAt,
    TraktShow Show,
    TraktWatchedSeason[]? Seasons);

/// <summary>One /users/{slug}/ratings item — exactly one of movie/show is set; season/episode carry numbers.</summary>
public record TraktRatingItem(
    DateTime RatedAt,
    short Rating,
    string Type,
    TraktMovie? Movie,
    TraktShow? Show,
    TraktSeasonRef? Season,
    TraktEpisodeRef? Episode);

public record TraktSeasonRef(int Number);

public record TraktEpisodeRef(int? Season, int Number, string? Title);

/// <summary>
/// One /sync/history event. Id is the event id /sync/history/remove deletes by; exactly one of
/// movie/show is set (episode events carry both episode and show).
/// </summary>
public record TraktHistoryItem(
    long Id,
    DateTime WatchedAt,
    string Action,
    string Type,
    TraktMovie? Movie,
    TraktShow? Show,
    TraktEpisodeRef? Episode);

/// <summary>GET shows/{id}/progress/watched essentials.</summary>
public record TraktShowProgress(int Aired, int Completed, TraktNextEpisode? NextEpisode);

/// <summary>
/// The next episode to watch. <paramref name="FirstAired"/> is Trakt's <c>first_aired</c>, present
/// when the progress call asks for <c>extended=full</c>; null when Trakt has no air date for it.
/// </summary>
public record TraktNextEpisode(int Season, int Number, string? Title, DateTime? FirstAired = null);

/// <summary>
/// One /sync/watchlist item. <paramref name="ListedAt"/> is when it was put on the watchlist — by
/// the user on Trakt, or by Reel's own write-back. Exactly one of movie/show is set for the two types
/// Reel reads ("movie", "show").
/// </summary>
public record TraktWatchlistItem(
    long Id,
    DateTime ListedAt,
    string Type,
    TraktMovie? Movie,
    TraktShow? Show);

/// <summary>
/// One /users/hidden/{section} item. <paramref name="Section"/> is filled by the adapter (Trakt
/// does not echo it); a season or user entry carries neither movie nor show.
/// </summary>
public record TraktHiddenItem(
    DateTime HiddenAt,
    string Type,
    TraktMovie? Movie,
    TraktShow? Show,
    string? Section = null);

/// <summary>
/// Trakt's community rating for one catalogue title, found by its TMDB id. Null
/// <paramref name="Rating"/> means Trakt knows the title but has no rating for it yet.
/// </summary>
public record TraktCatalogRating(long TraktId, string? Slug, string? ImdbId, decimal? Rating, int Votes);

/// <summary>
/// One entry of Trakt's own personalised recommendations (<c>/recommendations/movies|shows</c>).
/// Exactly one of movie/show is set. <paramref name="Rank"/> is the item's 1-based position in
/// Trakt's answer for its media type — Trakt's order is the rail's order, so it is kept explicitly
/// rather than trusted to survive a dictionary or a query.
/// </summary>
public record TraktRecommendation(int Rank, TraktMovie? Movie, TraktShow? Show);
