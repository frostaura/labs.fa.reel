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
/// <item><b>1</b> — the original vector: 30 scalars, 25 genres, 32 subgenre buckets. Prose across
/// the codebase called it "86" and then went on saying "~86" after v2 and v3 appended five more
/// scalars. Never type the width into prose again — it is <see cref="VectorLength"/>, and the
/// user-visible caveat in <c>EvalHarness</c> interpolates it rather than repeating a number that
/// has already been wrong twice.</item>
/// <item><b>2</b> — missing crowd data stopped being imputed as a real zero. Added the
/// <c>hasRuntime</c> / <c>hasTraktRating</c> / <c>hasPopularity</c> presence flags and neutral
/// imputation for the three fields behind them. The flags are APPENDED (never interleaved next to
/// the fields they describe) so every pre-existing scalar index is untouched by the bump.</item>
/// <item><b>3</b> — dislike reasons became learnable. Added <c>simNotInterestedCentroid</c>
/// (cosine of the title's plot embedding to the recency-weighted centroid of the user's live
/// not-interested titles, as-of aware) and <c>dislikedGenreOverlap</c> (the recency-weighted
/// disliked-genre weight matched by this title's genres — reason=Genre reactions only; math
/// shared with the serving-time <c>DislikeSignal</c> demotion). Both APPENDED, per the same
/// rule as v2; neutral value 0 when the account has no dislikes or the title no embedding.</item>
/// <item><b>4</b> — the train/serve-skew bump, and the ONE deliberate reorder in this file's
/// history. Four things at once, bundled precisely because a bump costs every account a retrain:
/// <list type="number">
/// <item><b><c>showEngagement</c> DELETED.</b> It was pinned to <c>0f</c> on both paths and kept
/// only so v2 indices would not renumber. A bump renumbers them anyway, so the dead slot goes
/// here or it never goes. This is the reorder — every scalar after index 16 shifts down one, and
/// that is exactly why it may only happen inside a version bump.</item>
/// <item><b>Presence flags <c>hasCertification</c> / <c>hasSubgenres</c>.</b> Certification and
/// subgenres were written only by Trakt ingest, so a TMDB-discovered candidate scored
/// <c>certificationOrdinal = 0</c> ("unknown") and 32 identically-zero subgenre buckets while its
/// training-row counterparts were live — the same silent train/serve skew v2 fixed for the crowd
/// fields, at a larger scale. v4 sources certification from TMDB (see <c>TitleHydrator</c>) AND
/// flags absence, so "unrated" is no longer spelled the same way as "rated G".</item>
/// <item><b><c>Tone</c> and <c>Era</c> one-hots</b> over the closed
/// <see cref="AttributeVocabulary"/> vocabularies (16 + 13), with
/// <c>hasAttributesTone</c> / <c>hasAttributesEra</c> beside them. Both fields were extracted,
/// validated and stored since v1 and <c>Era</c> had no reader at all.</item>
/// <item><b>Two categorical BLOCKS appended</b> after the subgenre buckets, which is the block-level
/// form of the same append rule: genre and subgenre indices are unchanged relative to the start of
/// their own blocks, and nothing already in the vector moves because of them.</item>
/// </list>
/// </item>
/// <item><b>5</b> — the CREDIT-KNOWLEDGE and UNSCRIPTED bump, and a pure append (no reorder).
/// Three scalars and five genre slots, all at the end of their own blocks:
/// <list type="number">
/// <item><b><c>knownPeopleFraction</c> / <c>knownDirectorOrWriter</c>.</b> <c>hasCredits</c> answers
/// "does this title have credit rows", which is nearly always yes and was being read as "we know
/// something about these people" — it is not the same question. A reality show with twelve unknown
/// presenters and no director or writer row scored <c>hasCredits = 1</c> alongside
/// <c>directorAffinity</c> / <c>writerAffinity</c> imputed at the user's own mean, and the model
/// could not tell "known and average" from "we know nothing", so an all-unknown title read as a
/// solidly-average one. These two scalars carry the KNOWLEDGE signal that <c>hasCredits</c> was
/// being asked to carry and cannot; see <see cref="NeutralLowPersonAffinityOffset"/> for the second
/// half of the same fix.</item>
/// <item><b><c>isUnscripted</c>.</b> The aggregate of the five genre slots below, so a tree can
/// split "any unscripted format" once instead of five times. Deliberately a SCALAR rather than a
/// <c>genre:unscripted</c> slot: <c>Genres</c> is also read by <c>LexicalSearchService</c> as the
/// searchable genre vocabulary, so a pseudo-slug there would become a search facet matching no
/// title's actual genre list.</item>
/// <item><b>Five unscripted genre slots APPENDED</b> — <c>reality</c>, <c>soap</c>,
/// <c>talk-show</c>, <c>news</c>, <c>game-show</c>. They are real Trakt slugs that <c>TmdbGenres</c>
/// has mapped all along (bar <c>game-show</c>, which TMDB's TV taxonomy has no id for and which
/// therefore reaches the vector only through Trakt ingest), and which had NO slot here: a reality
/// show lit zero of the 25 genre one-hots, so the whole unscripted half of television was
/// indistinguishable from a title with no genres at all. Appended after <c>western</c>, so every
/// pre-existing genre index is unmoved relative to the start of the block.</item>
/// </list>
/// </item>
/// <item><b>6</b> — the WATCHED-HISTORY bump. <b>Built, measured and REVERTED. Version 6 is burned:
/// the next bump is 7.</b> It appended five scalars (132 → 137) — <c>simWatchedCentroid</c> /
/// <c>hasWatchedCentroid</c> (cosine to the centroid of titles WATCHED but not RATED as-of),
/// <c>simRewatchCentroid</c> / <c>hasRewatchCentroid</c> (<c>min(plays, 3)</c>-weighted centroid of
/// <c>Plays &gt;= 2</c> titles, rated or not) and <c>watchedGenreShare</c> — on the argument that
/// every user-side feature up to v5 reads RATED titles alone, so on the founder's account 1,363
/// watched-and-never-scored titles fed nothing.
/// <para><b>Why it is not here.</b> Iteration 17 was run under a rule pre-registered before the
/// numbers existed (recorded in <c>memory/decisions.md</c>): ship only if the mean relative NDCG
/// lift improves on iteration 16 <i>and</i> <c>NdcgLiftCiLow</c> does not move further negative.
/// Iteration 17 at v6 measured NDCG@10 <b>0.053</b> against a popularity baseline of 0.198, CI
/// <b>[−0.356, +0.026]</b>, Spearman 0.116, holdout 209. Iteration 16 at v5 measured <b>0.056</b>
/// against the same 0.198 baseline, CI [−0.43, +0.08]. No lift; the bump was reverted per the rule
/// rather than renegotiated once the number was on screen.</para>
/// <para><b>What the revert does NOT establish.</b> It does not say these features carry no signal.
/// The instrument is noise-dominated at this holdout size — two v5 runs over identical data returned
/// 0.000 and 0.056 — so an effect the size of the one being looked for is not resolvable here. The
/// honest reading is "unmeasurable with a 209-row holdout", not "absent". Re-proposing this idea is
/// legitimate; re-proposing it against <i>this</i> instrument is not, and it would land as v7 with a
/// wider vector, never by reusing 6.</para>
/// <para>Two further scalars scoped into the same bump — <c>nearestRatedNeighbourRating</c> and
/// <c>nearestRatedNeighbourSim</c>, a k = 3 distance-weighted kNN over the account's own rated
/// titles — were written in full and dropped BEFORE the iteration ran, on a separate pre-registered
/// cost gate of 2×. A warm <c>EvalHarness</c> run over a copy of the live database: <b>2.8–2.9 s at
/// v5</b>, <b>3.8–4.2 s with the five scalars above</b> (1.3–1.5×), <b>12.6–12.8 s with the kNN pair
/// added</b> (4.4–5.5×). The cost was not the scan — building the search index alone moved
/// <c>BuildTasteStateAsync</c> from ~100 ms to ~1,036 ms, and the harness pays that five times a
/// run. If they ever return it is behind a materialised, incrementally-maintained neighbour index,
/// on their own bump with their own measurement, never inside the per-build recompute.</para>
/// </item>
/// </list>
/// A version bump makes every stored artifact unreadable by construction — the vector width and
/// column meanings move. <c>ModelArtifact.FeatureSchemaJson</c> records the version an artifact was
/// trained against and <c>ReelDbContext</c> refuses to surface an Active artifact whose recorded
/// version is not this one, so a bump degrades to "no model, retrain" rather than silent nonsense.
/// </remarks>
public static class FeatureSchema
{
    /// <summary>
    /// The live schema version. <b>The next bump is 7, not 6</b> — 6 was built, measured against the
    /// iteration-17 pre-registered rule and reverted; the version history above records what it held
    /// and why it went. Re-using a burned number would make two different 137-wide vectors answer to
    /// the same integer in stored <c>ModelArtifact.FeatureSchemaJson</c> rows.
    /// </summary>
    public const int Version = 5;

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

    /// <summary>
    /// How far BELOW the user's own mean a person-affinity is imputed when nothing is known about a
    /// title's credited people (schema v5). Half a rating point, and deliberately not zero.
    ///
    /// <para>Until v5 an unknown person's affinity was imputed at the user's mean, which is the
    /// correct prior for a person the user has rated too few times to estimate — and quite wrong for
    /// one they have never encountered at all. The two cases were spelled identically, so a title
    /// whose entire cast was unknown scored <c>directorAffinity</c> and <c>writerAffinity</c> at the
    /// mean and those slots then turned up as the TOP POSITIVE contributors on exactly the titles
    /// with nobody known in them. "We know nothing" was being sold to the model, and to the
    /// explanation layer, as "solidly average for you".</para>
    ///
    /// <para>Half a point is a nudge, not a verdict: it is smaller than the spread between the
    /// user's best and worst genres, so a genuinely well-matched unknown title still outranks a
    /// poorly-matched known one. It is also not the whole fix — <c>knownPeopleFraction</c> and
    /// <c>knownDirectorOrWriter</c> carry the presence signal, so the model can learn its own
    /// discount rather than inheriting this one. This constant only stops the imputed value from
    /// being an outright lie in the meantime.</para>
    /// </summary>
    public const decimal NeutralLowPersonAffinityOffset = 0.5m;

    /// <summary>Canonical Trakt genre vocabulary (static — title genres are not user state).</summary>
    public static readonly string[] Genres =
    [
        "action", "adventure", "animation", "anime", "comedy", "crime", "documentary",
        "drama", "family", "fantasy", "history", "holiday", "horror", "music", "musical",
        "mystery", "romance", "science-fiction", "short", "sports", "superhero",
        "suspense", "thriller", "war", "western",
        // Unscripted formats (schema v5), APPENDED so every slug above keeps its block index.
        // Before this the entire unscripted half of television lit no genre one-hot at all — a
        // reality show and a title with an empty genre list were the same vector to the model,
        // which is how "no genre demotion" and "no genre evidence" came to mean the same thing.
        "reality", "soap", "talk-show", "news", "game-show",
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
        // NOTE: v1-v3 carried "showEngagement" here, pinned to 0 on both paths. DELETED in v4 —
        // see the version history above. Do not re-add a slot in the middle of this array.
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
        // Dislike signals (schema v3). Appended, never interleaved — same rule as v2.
        "simNotInterestedCentroid", // cosine to the recency-weighted not-interested centroid
        "dislikedGenreOverlap",     // recency-weighted disliked-genre weight matched by this title
        // Attribute/metadata presence (schema v4). Appended, same rule again. Each one answers
        // "was this signal SOURCED for this title", which is a different question from its value:
        // certificationOrdinal 0 means "unknown", and 32 zero subgenre buckets mean "we have no
        // subgenres" — neither is distinguishable from a real reading without these.
        "hasCertification",       // 0 when Certification is absent (certificationOrdinal is then 0)
        "hasSubgenres",           // 0 when neither Title.Subgenres nor TitleAttributes.Themes had a value
        "hasAttributesTone",      // 0 when Tone is absent, out-of-vocabulary, or from a stale extractor
        "hasAttributesEra",       // 0 when Era is absent, out-of-vocabulary, or from a stale extractor
        // Credit KNOWLEDGE and the unscripted aggregate (schema v5). Appended, same rule again.
        // hasCredits (index 14) answers "do credit rows exist"; these answer "do we know anything
        // about the people in them", which is the question the affinity scalars are actually
        // conditioned on and which hasCredits was silently being read as.
        "knownPeopleFraction",    // distinct credited people the user has rated / distinct credited people; 0 with no credits
        "knownDirectorOrWriter",  // 1 when at least one director or writer credit resolves to a rated person
        "isUnscripted",           // aggregate of the five unscripted genre slots (reality/soap/talk-show/news/game-show)
    ];

    /// <summary>
    /// Closed tone vocabulary, as a one-hot block (schema v4). Deliberately a reference to
    /// <see cref="AttributeVocabulary.Tones"/> rather than a copy: the extractor enforces that exact
    /// list on the provider through a JSON-schema enum, so a second copy here would be a second
    /// vocabulary the moment either moved, and the block width would drift from what is extractable.
    /// </summary>
    public static readonly string[] Tones = AttributeVocabulary.Tones;

    /// <inheritdoc cref="Tones"/>
    public static readonly string[] Eras = AttributeVocabulary.Eras;

    /// <summary>
    /// The oldest <c>TitleAttributes.ExtractorVersion</c> whose <c>Tone</c>/<c>Era</c> this schema
    /// will read. Version 1 predates closed-vocabulary enforcement and left 2,312 rows carrying
    /// free text — 215 distinct tones against a 16-value vocabulary, 97 eras against 13, including
    /// entries like <c>period-victorian</c>, <c>1980s</c> and <c>fantasy</c> (not an era). A row
    /// below this reads as ABSENT: its flag is 0 and every one-hot in its block is 0, exactly as
    /// for a title with no attributes at all. That is the honest reading — the value is not
    /// missing, it is untrustworthy, and the two are the same thing to a model.
    ///
    /// <para>The scalars are unaffected and were never version-gated: they were numeric and
    /// range-validated from the start, so v1 rows keep feeding <c>attrDarkness</c> and friends.</para>
    ///
    /// <para>Raising this is a schema decision, not an extractor decision. It moves ONLY when a
    /// later extractor version invalidates the tone/era vocabulary itself; a prompt revision that
    /// leaves the enum intact must NOT raise it, or every bump blacks out both blocks until the
    /// re-extraction drain finishes.</para>
    /// </summary>
    public const int MinTrustedExtractorVersion = 2;

    public static int VectorLength =>
        Scalars.Length + Genres.Length + SubgenreBuckets + Tones.Length + Eras.Length;

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

        var toneOffset = Scalars.Length + Genres.Length + SubgenreBuckets;
        for (var i = 0; i < Tones.Length; i++)
        {
            names[toneOffset + i] = $"tone:{Tones[i]}";
        }

        var eraOffset = toneOffset + Tones.Length;
        for (var i = 0; i < Eras.Length; i++)
        {
            names[eraOffset + i] = $"era:{Eras[i]}";
        }

        return names;
    }
}
