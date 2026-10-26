namespace FrostAura.Reel.Application.Ingestion;

/// <summary>
/// TMDB's "TV Movie" genre, kept on the catalogue row so it can be filtered (ADD-18, for WS-3's
/// eligibility and Ask Reel's "no TV movies").
///
/// <para><b>Why it needs its own home.</b> It is a TMDB-only genre: Trakt has no such slug, and the
/// TMDB id map the discovery paths share leaves it out, so a made-for-TV film was indistinguishable
/// from a theatrical one everywhere in Reel. Hydration reads the details payload's genre ids and
/// carries the <see cref="Slug"/> on <c>Title.Genres</c>; a Trakt walk, which rewrites
/// <c>Genres</c> from Trakt's own list, keeps it (<see cref="Preserve"/>), so the flag survives
/// every re-ingest and is only ever cleared by TMDB itself on a refresh.</para>
///
/// <para>The slug sits outside <c>FeatureSchema.Genres</c>, so it lights no model feature: it is a
/// filter fact, not a taste signal, and adding a one-hot is a schema version the model owner
/// decides.</para>
/// </summary>
public static class TvMovieGenre
{
    /// <summary>The slug stored in <c>Title.Genres</c>, in the ingest vocabulary's kebab-case.</summary>
    public const string Slug = "tv-movie";

    /// <summary>TMDB's movie genre id for "TV Movie" (GET /3/genre/movie/list).</summary>
    public const int TmdbMovieGenreId = 10770;

    /// <summary>
    /// <paramref name="genres"/> with <see cref="Slug"/> present exactly when TMDB's
    /// <paramref name="tmdbGenreIds"/> say it is a TV movie. Null ids mean the payload carried no
    /// genre list, which says nothing either way, so the genres come back unchanged.
    /// </summary>
    public static string[] Apply(string[] genres, IReadOnlyCollection<int>? tmdbGenreIds)
    {
        if (tmdbGenreIds is null)
        {
            return genres;
        }

        var isTvMovie = tmdbGenreIds.Contains(TmdbMovieGenreId);
        var has = genres.Contains(Slug, StringComparer.Ordinal);
        return (isTvMovie, has) switch
        {
            (true, false) => [.. genres, Slug],
            (false, true) => genres.Where(g => !string.Equals(g, Slug, StringComparison.Ordinal)).ToArray(),
            _ => genres,
        };
    }

    /// <summary>
    /// Trakt's <paramref name="incoming"/> genre list, keeping the TMDB-only <see cref="Slug"/> when
    /// <paramref name="current"/> carries it — Trakt cannot know it, so its silence is not a removal.
    /// </summary>
    public static string[] Preserve(string[] current, string[] incoming) =>
        current.Contains(Slug, StringComparer.Ordinal) && !incoming.Contains(Slug, StringComparer.Ordinal)
            ? [.. incoming, Slug]
            : incoming;
}
