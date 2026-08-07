using FrostAura.Reel.Domain.Catalog;

namespace FrostAura.Reel.Application.Ml;

/// <summary>
/// The ordered, versioned feature contract. The trainer, scorer, and explanation layer all
/// index into this — order changes are a schema version bump, never an in-place edit, or
/// stored artifacts would silently misread their inputs.
/// </summary>
/// <remarks>
/// Version history:
/// <list type="bullet">
/// <item><b>1</b> — original 86-column vector.</item>
/// <item><b>2</b> — missing crowd data stopped being imputed as a real zero. Added the
/// <c>hasRuntime</c> / <c>hasTraktRating</c> / <c>hasPopularity</c> presence flags and neutral
/// imputation for the three fields behind them. The flags are APPENDED (never interleaved next to
/// the fields they describe) so every pre-existing scalar index is untouched by the bump.</item>
/// </list>
/// A version bump makes every stored artifact unreadable by construction — the vector width and
/// column meanings move. <c>ModelArtifact.FeatureSchemaJson</c> records the version an artifact was
/// trained against and <c>ReelDbContext</c> refuses to surface an Active artifact whose recorded
/// version is not this one, so a bump degrades to "no model, retrain" rather than silent nonsense.
/// </remarks>
public static class FeatureSchema
{
    public const int Version = 2;

    /// <summary>
    /// Neutral fill-ins for crowd data the catalogue simply does not have. Half the catalogue has no
    /// Trakt rating and a third has no runtime; telling the trainer "0 minutes, rated 0/10, zero
    /// popularity" invents a strong (and false) negative signal, so each missing field is filled with
    /// a mid-distribution value and flagged as absent instead. The constants are the observed
    /// catalogue medians (2026-08: movie runtime 107 min / popularity 5.5; show 45 min / 32) rounded,
    /// and are deliberately CONSTANT — a live median would make the feature non-deterministic and
    /// reintroduce train/serve skew.
    /// </summary>
    public const float NeutralMovieRuntimeMinutes = 105f;

    /// <inheritdoc cref="NeutralMovieRuntimeMinutes"/>
    public const float NeutralShowRuntimeMinutes = 45f;

    /// <inheritdoc cref="NeutralMovieRuntimeMinutes"/>
    public const decimal NeutralMoviePopularity = 5.5m;

    /// <inheritdoc cref="NeutralMovieRuntimeMinutes"/>
    public const decimal NeutralShowPopularity = 32m;

    /// <summary>Neutral runtime for a title whose runtime is unknown (see the constants above).</summary>
    public static float NeutralRuntimeMinutes(MediaType mediaType) =>
        mediaType == MediaType.Show ? NeutralShowRuntimeMinutes : NeutralMovieRuntimeMinutes;

    /// <summary>Neutral TMDB popularity for a title with no popularity figure (see the constants above).</summary>
    public static decimal NeutralPopularity(MediaType mediaType) =>
        mediaType == MediaType.Show ? NeutralShowPopularity : NeutralMoviePopularity;

    /// <summary>Canonical Trakt genre vocabulary (static — title genres are not user state).</summary>
    public static readonly string[] Genres =
    [
        "action", "adventure", "animation", "anime", "comedy", "crime", "documentary",
        "drama", "family", "fantasy", "history", "holiday", "horror", "music", "musical",
        "mystery", "romance", "science-fiction", "short", "sports", "superhero",
        "suspense", "thriller", "war", "western",
    ];

    public const int SubgenreBuckets = 32;

    /// <summary>Scalar feature names, in vector order (genre multi-hot and hash buckets follow).</summary>
    public static readonly string[] Scalars =
    [
        // Title
        "releaseAgeLog",          // log1p(years since release/first air, asOf)
        "runtimeMinutes",
        "isShow",
        "traktRating",            // global crowd rating 0-10
        "traktVotesLog",
        "tmdbPopularityLog",
        "certificationOrdinal",
        // User-genre interaction
        "userGenreAffinity",      // shrunken mean of user's ratings over this title's genres
        "genreDrift",             // recency-weighted minus all-time genre affinity
        "decadeAffinity",         // shrunken mean over this title's release decade
        // Cast & crew
        "directorAffinity",
        "castAffinity",           // mean over top-billed cast affinities
        "writerAffinity",
        "lovedCastCount",         // cast members the user rates ≥ 8
        "hasCredits",
        // Crowd-vs-user calibration
        "contrarianAdjustedRating", // traktRating + user's genre-level contrarian offset
        // Show engagement (0 for movies)
        "showEngagement",         // mean of user's episode/season ratings for this title
        // LLM attributes (0 + flag until extracted)
        "attrDarkness",
        "attrPacing",
        "attrComplexity",
        "attrEmotionalIntensity",
        "attrHumor",
        "attrOptimism",
        "attrEnsembleVsSolo",
        "hasAttributes",
        // Embedding similarities (0 + flag until embedded)
        "simLovedCentroid",
        "simRecentCentroid",
        "simTopLovedMax",
        "simTopLovedMean",
        "hasEmbedding",
        // Crowd-data presence (schema v2). Appended rather than placed next to the fields they
        // describe so the bump does not renumber any scalar that already existed in v1.
        "hasRuntime",             // 0 when runtimeMinutes is the neutral fill-in
        "hasTraktRating",         // 0 when traktRating is the neutral fill-in
        "hasPopularity",          // 0 when tmdbPopularityLog is the neutral fill-in
    ];

    public static int VectorLength => Scalars.Length + Genres.Length + SubgenreBuckets;

    /// <summary>Human-readable name per vector index (explanations + persisted schema json).</summary>
    public static string[] AllNames()
    {
        var names = new string[VectorLength];
        Scalars.CopyTo(names, 0);
        for (var i = 0; i < Genres.Length; i++)
        {
            names[Scalars.Length + i] = $"genre:{Genres[i]}";
        }

        for (var i = 0; i < SubgenreBuckets; i++)
        {
            names[Scalars.Length + Genres.Length + i] = $"subgenreBucket:{i}";
        }

        return names;
    }
}
