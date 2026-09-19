namespace FrostAura.Reel.Domain.Ports.Tmdb;

/// <summary>Unified movie/TV hydration payload (the union Reel persists from either endpoint).</summary>
public record TmdbTitleDetails(
    long Id,
    string? PosterPath,
    string? BackdropPath,
    decimal Popularity,
    decimal VoteAverage,
    int VoteCount,
    int? Runtime,
    string? Overview,
    string? Tagline,
    int? NumberOfEpisodes,
    TmdbCastMember[] Cast,
    TmdbCrewMember[] Crew,
    string? TrailerYouTubeKey,
    string[] Keywords,
    /// <summary>TMDB original_language (ISO-639-1, e.g. "en"). Untrustworthy on its own — see <see cref="TmdbLanguage.Resolve"/>.</summary>
    string? OriginalLanguage = null,
    /// <summary>
    /// ISO-639-1 codes from TMDB <c>spoken_languages[].iso_639_1</c>. Present on BOTH the movie and
    /// the TV details endpoint (verified live 2026-08-07).
    /// </summary>
    string[]? SpokenLanguages = null,
    /// <summary>
    /// ISO-639-1 codes from TMDB <c>languages</c>. TV only — the movie details endpoint does not
    /// return this field. Used as the fallback corroborator when spoken_languages is absent.
    /// </summary>
    string[]? Languages = null,
    /// <summary>
    /// The display title (<c>title</c> for movies, <c>name</c> for TV). Nullable because hydration
    /// of an already-known row never needed it; the on-demand detail path does — it synthesizes a
    /// <see cref="TmdbListItem"/> for a title Reel has never seen, and a candidate row without a
    /// name is unrenderable.
    /// </summary>
    string? Name = null,
    /// <summary>Release date (<c>release_date</c> for movies, <c>first_air_date</c> for TV).</summary>
    DateTime? ReleasedAt = null,
    /// <summary>
    /// Genre ids from the details payload's <c>genres[].id</c> — same id space as a list row's
    /// <c>genre_ids</c>, so <see cref="TmdbGenres.MapToSlugs"/> applies unchanged.
    /// </summary>
    int[]? GenreIds = null,
    /// <summary>
    /// Age certification per ISO-3166-1 region, upper-cased keys ("US", "GB", "ZA"), from
    /// <c>release_dates</c> (movies) or <c>content_ratings</c> (TV) — both folded into the SAME
    /// hydration call via <c>append_to_response</c>, so certification costs zero extra requests.
    ///
    /// <para>It exists because <c>Title.Certification</c> was written only by Trakt ingest, i.e.
    /// only for titles already in the user's library, so every TMDB-discovered candidate scored
    /// <c>certificationOrdinal = 0</c> against training rows that had a real value. Empty when TMDB
    /// has no certification for the title at all; regions whose certification string is blank are
    /// dropped rather than stored as "".</para>
    /// </summary>
    IReadOnlyDictionary<string, string>? CertificationsByRegion = null);

/// <summary>
/// Resolves the language Reel stores for a title, because TMDB's <c>original_language</c> is not
/// reliable on its own.
///
/// The defect that forced this: TV 95439 "El final del paraíso" — a Telemundo telenovela — reports
/// <c>original_language: "en"</c> with <c>languages: ["es"]</c> and <c>spoken_languages: ["es"]</c>.
/// Reel mirrored the "en" faithfully, and the title then passed the English-only language
/// allow-list straight into the feed.
///
/// The rule: corroborate original_language against the spoken-language list and only override it
/// when the list actively contradicts it.
///
/// It is deliberately NOT "use the first spoken language". Sampled live, TMDB orders that array
/// arbitrarily — Parasite is <c>original_language: "ko"</c> with <c>spoken_languages:
/// ["en","de","ko"]</c>, so "first" would relabel a Korean film as English. Membership is the test;
/// order is only consulted once membership has already failed.
///
/// Measured against Reel's whole live catalogue on 2026-08-07 (a full census, not a sample), the
/// rule moves 14 of 3,913 hydrated titles and every move was checked by hand:
///   * 5 of the 3,729 marked "en" are genuinely not English — "El final del paraíso" (es),
///     "¿Quieres ser mi hijo?" (es), Cyberpunk: Edgerunners (ja), Ninja Kamui (ja), City of Ghosts
///     (ar). These are the founder-visible half of the bug.
///   * 9 of the 184 marked non-English are English films whose original_language encodes the
///     PRODUCTION country instead — The Fifth Element ("fr"), Colonia ("de"), The Best Offer ("it"),
///     The Pagan King ("lv") and five others. They were being wrongly HIDDEN from an English-only
///     feed, which is the same defect with the sign flipped.
/// The residual risk is a foreign-language title whose spoken_languages lists only its English dub;
/// no such case exists in this catalogue, and it would relabel toward the language actually heard,
/// which is what the allow-list is asking about.
/// </summary>
public static class TmdbLanguage
{
    /// <summary>
    /// TMDB's "No Language" sentinel. It is a real row in <c>spoken_languages</c> for silent and
    /// near-dialogue-free films (Metropolis, The Artist, The General — all verified live 2026-08-07)
    /// and it corroborates nothing. Left unfiltered it would relabel every such title as language
    /// "xx" and drop it out of the English allow-list. That is not hypothetical: three titles in
    /// Reel's own catalogue (The Ring, Begotten, Silent Night) carry a sentinel-only spoken list, so
    /// the unfiltered rule would have corrected 5 mislabels and caused 3 fresh ones.
    /// </summary>
    private const string NoLanguageSentinel = "xx";

    /// <summary>
    /// The stored language, lowercased, or null when TMDB gives us nothing usable (callers keep
    /// whatever they already had rather than overwriting it with a null).
    /// </summary>
    /// <param name="originalLanguage">TMDB <c>original_language</c>.</param>
    /// <param name="spokenLanguages">TMDB <c>spoken_languages[].iso_639_1</c>, if any.</param>
    /// <param name="languages">TMDB <c>languages</c> (TV only), used when spoken_languages is empty.</param>
    public static string? Resolve(string? originalLanguage, string[]? spokenLanguages, string[]? languages)
    {
        var original = Normalize(originalLanguage);

        // spoken_languages first (both endpoints carry it), then TV's `languages` as a fallback.
        // Across a full live census of the 3,729 titles Reel had marked "en" on 2026-08-07 the two
        // fields never once disagreed, so preferring either is safe; spoken_languages leads because
        // movies only ever have that one.
        var corroborators = Clean(spokenLanguages);
        if (corroborators.Count == 0)
        {
            corroborators = Clean(languages);
        }

        // Nothing to corroborate against (no data, or "No Language" only) — original stands.
        if (corroborators.Count == 0)
        {
            return original;
        }

        // The list agrees with original_language, or there is no original_language to defend.
        // Either way the list is not evidence against it.
        if (original is null)
        {
            return corroborators[0];
        }

        return corroborators.Contains(original) ? original : corroborators[0];
    }

    private static List<string> Clean(string[]? codes)
    {
        if (codes is null || codes.Length == 0)
        {
            return [];
        }

        var cleaned = new List<string>(codes.Length);
        foreach (var code in codes)
        {
            var normalized = Normalize(code);
            if (normalized is not null && normalized != NoLanguageSentinel && !cleaned.Contains(normalized))
            {
                cleaned.Add(normalized);
            }
        }

        return cleaned;
    }

    private static string? Normalize(string? code) =>
        string.IsNullOrWhiteSpace(code) ? null : code.Trim().ToLowerInvariant();
}

public record TmdbCastMember(long Id, string Name, string? Character, int Order, string? ProfilePath, string? KnownForDepartment);

public record TmdbCrewMember(long Id, string Name, string? Job, string? Department, string? ProfilePath);
