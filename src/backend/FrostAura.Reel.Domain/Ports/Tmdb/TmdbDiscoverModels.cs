namespace FrostAura.Reel.Domain.Ports.Tmdb;

/// <summary>One provider entry from TMDB watch/providers for a region.</summary>
public record TmdbWatchProvider(int ProviderId, string Name, string? LogoPath, int DisplayPriority, Providers.AvailabilityKind Kind);

/// <summary>A TMDB keyword (concept tag) — resolved from free text to drive concept discovery.</summary>
public record TmdbKeyword(int Id, string Name);

/// <summary>A TMDB person (search/person) — resolves a name to an id for credit lookups.</summary>
public record TmdbPerson(long Id, string Name, string? KnownForDepartment);

/// <summary>Which slice of a person's filmography to pull from combined_credits.</summary>
public enum PersonCreditRole
{
    Director,
    Cast,
    Any,
}

/// <summary>One entry of a TMDB list payload (discover/trending) — enough to seed a candidate.</summary>
public record TmdbListItem(
    long Id,
    bool IsMovie,
    string Name,
    string? PosterPath,
    string? BackdropPath,
    string? Overview,
    decimal Popularity,
    decimal VoteAverage,
    int VoteCount,
    DateTime? ReleasedAt,
    int[] GenreIds,
    /// <summary>TMDB original_language (ISO-639-1, e.g. "en") — carried through discovery/search so
    /// freshly-upserted candidates get a real language and aren't dropped by the English-only
    /// eligibility default before they're hydrated. Null when the list row didn't carry it.</summary>
    string? OriginalLanguage = null);

/// <summary>
/// Everything a concept-discover call needs beyond genres, keywords and dates: the account's own
/// content rules projected into TMDB parameters, the optional person constraint the credit-affinity
/// arm uses, and which rate-gate lane the call belongs in.
///
/// <para>
/// It exists because the feed build used to ask TMDB for whatever was popular and let the
/// eligibility layer throw most of it away. On the founder's account ~421 of 5,319 catalogue titles
/// are eligible, so ~92% of every fetched page was waste by construction — and no amount of deeper
/// paging fixes that, because deeper pages of an unfiltered query are simply more ineligible titles.
/// A rule applied AFTER the fetch can only shrink the pool; the same rule sent WITH the fetch
/// changes what is in it.
/// </para>
///
/// <para>
/// The defaults reproduce the pre-existing Ask Reel behaviour exactly — no language constraint, no
/// vote-average floor, the endpoint's own vote-count floor, and the Interactive lane — so an
/// existing caller that passes nothing is unchanged.
/// </para>
/// </summary>
public sealed record TmdbDiscoverOptions(
    /// <summary><c>with_original_language</c> (ISO-639-1). Null leaves language unconstrained —
    /// which is also what a "*" or multi-language allow-list must project to, since TMDB takes a
    /// single code here and silently ignores the rest of an OR-list.</summary>
    string? OriginalLanguage = null,
    /// <summary><c>vote_average.gte</c> — the account's MinCrowdRating floor. TMDB-only, whereas
    /// eligibility resolves the crowd rating TMDB-then-Trakt; the gap is titles TMDB has not rated,
    /// which fail the eligibility floor anyway.</summary>
    decimal? MinVoteAverage = null,
    /// <summary><c>vote_count.gte</c> override. Null keeps the endpoint's own default (10 for the
    /// concept firehose, which wants recall); the feed build passes 50 to match its old lane.</summary>
    int? MinVoteCount = null,
    /// <summary><c>with_people</c>, OR-joined. TMDB movie discover only — <c>/discover/tv</c> has no
    /// person parameter, so this is ignored for TV rather than silently returning an unfiltered page.</summary>
    IReadOnlyList<long>? PeopleIds = null,
    /// <summary>Which shared rate-gate lane the call drains. Interactive by default (Ask Reel);
    /// background supply passes Reconcile so a feed build cannot starve a live search.</summary>
    RatePriority Priority = RatePriority.Interactive,
    /// <summary>The order TMDB pages in. Popularity by default, which is what every existing caller
    /// was built against.</summary>
    TmdbDiscoverSort Sort = TmdbDiscoverSort.Popularity);

/// <summary>The <c>sort_by</c> orders a concept discover can page in (always descending).</summary>
public enum TmdbDiscoverSort
{
    Popularity,
    VoteCount,
    VoteAverage,
    Newest,
}

/// <summary>
/// One discover page WITH its pagination envelope — what a bulk walk needs and what
/// <c>DiscoverAsync</c> throws away.
///
/// <para>
/// <paramref name="TotalPages"/> is TMDB's own count for the query and is the only honest way to
/// know a partition is exhausted: paging until an empty page costs one wasted call per partition,
/// and paging to a fixed depth either stops short or hammers the 500-page wall. TMDB reports
/// <c>total_pages</c> for the whole result set even when it will only SERVE 500 of them, so a
/// caller must clamp it itself — see <c>CatalogSeedPlan.MaxDiscoverPage</c>.
/// </para>
///
/// <para>
/// <paramref name="TotalResults"/> is what makes the plan measurable before it is run: one call per
/// partition reports exactly how many titles that partition holds, which is how the seed's size is
/// estimated from TMDB's numbers rather than guessed.
/// </para>
/// </summary>
public sealed record TmdbDiscoverPage(
    IReadOnlyList<TmdbListItem> Items, int Page, int TotalPages, int TotalResults);

/// <summary>
/// TMDB genre-id ↔ Trakt-style slug mapping (movie and TV id spaces differ). Slugs align with
/// the ingest vocabulary so genre features and filters treat discovery candidates identically.
/// </summary>
public static class TmdbGenres
{
    public static readonly IReadOnlyDictionary<int, string> MovieIdToSlug = new Dictionary<int, string>
    {
        [28] = "action",
        [12] = "adventure",
        [16] = "animation",
        [35] = "comedy",
        [80] = "crime",
        [99] = "documentary",
        [18] = "drama",
        [10751] = "family",
        [14] = "fantasy",
        [36] = "history",
        [27] = "horror",
        [10402] = "music",
        [9648] = "mystery",
        [10749] = "romance",
        [878] = "science-fiction",
        [53] = "thriller",
        [10752] = "war",
        [37] = "western",
    };

    /// <summary>
    /// EVERY TMDB TV genre id, mapped to the Trakt-style slugs it stands for. Complete as of
    /// 2026-09-10 — <c>TvGenreIdsGoldenTests</c> pins the id set so a TMDB addition fails a test
    /// rather than silently vanishing.
    ///
    /// <para>One id maps to MANY slugs because TMDB's TV taxonomy fuses pairs the movie taxonomy
    /// keeps apart: <c>Action &amp; Adventure</c>, <c>Sci-Fi &amp; Fantasy</c>, <c>War &amp;
    /// Politics</c>. Before this it was a 1:1 map covering 11 of the 25 ingest slugs, so a
    /// TMDB-discovered SHOW lit <c>action</c> but never <c>adventure</c>, <c>science-fiction</c> but
    /// never <c>fantasy</c> — while the same show ingested from the user's Trakt library lit both.
    /// Fourteen of the 25 genre one-hots were train-only for TV, which is train/serve skew
    /// indistinguishable from a signal.</para>
    ///
    /// <para><c>reality</c>, <c>news</c>, <c>talk-show</c> and <c>soap</c> were mapped here before
    /// they had any slot in <c>FeatureSchema.Genres</c>, because <c>Title.Genres</c> is also the
    /// substrate for the account's ExcludeGenre rules. Since schema v5 they DO have slots, so a
    /// TMDB-discovered unscripted show now lights its genre one-hot on both paths — which is the
    /// whole point of mapping them here rather than only at ingest. <c>game-show</c> is the one
    /// unscripted slot with no TMDB TV id to map (their taxonomy has none), so it reaches the vector
    /// through Trakt ingest only; that is a gap in TMDB, not an omission here, and it is pinned by
    /// <c>TmdbTvGenreMapTests</c> so it fails loudly if TMDB ever adds one.</para>
    /// </summary>
    public static readonly IReadOnlyDictionary<int, string[]> TvIdToSlugs = new Dictionary<int, string[]>
    {
        [10759] = ["action", "adventure"],   // Action & Adventure
        [16] = ["animation"],                // Animation
        [35] = ["comedy"],                   // Comedy
        [80] = ["crime"],                    // Crime
        [99] = ["documentary"],              // Documentary
        [18] = ["drama"],                    // Drama
        [10751] = ["family"],                // Family
        [10762] = ["family"],                // Kids — no "children" slug in the ingest vocabulary
        [9648] = ["mystery"],                // Mystery
        [10763] = ["news"],                  // News
        [10764] = ["reality"],               // Reality
        [10765] = ["science-fiction", "fantasy"], // Sci-Fi & Fantasy
        [10766] = ["soap"],                  // Soap
        [10767] = ["talk-show"],             // Talk
        [10768] = ["war"],                   // War & Politics
        [37] = ["western"],                  // Western
    };

    /// <summary>Slug → (movie genre id, tv genre id); null when that medium has no equivalent.</summary>
    public static readonly IReadOnlyDictionary<string, (int? Movie, int? Tv)> SlugToIds =
        BuildReverse();

    public static string[] MapToSlugs(bool isMovie, int[] genreIds)
    {
        if (isMovie)
        {
            return genreIds.Where(MovieIdToSlug.ContainsKey).Select(id => MovieIdToSlug[id]).Distinct().ToArray();
        }

        return genreIds
            .Where(TvIdToSlugs.ContainsKey)
            .SelectMany(id => TvIdToSlugs[id])
            .Distinct()
            .ToArray();
    }

    /// <summary>
    /// The reverse map used to ASK TMDB for a slug. A fused TV id is the answer for both of its
    /// slugs — discovering "fantasy" on TV means asking for 10765, which is the closest thing TMDB
    /// has and is exactly what the forward map says a 10765 show is. First writer wins per medium,
    /// which only matters for <c>family</c> (10751 Family, ahead of 10762 Kids).
    /// </summary>
    private static Dictionary<string, (int?, int?)> BuildReverse()
    {
        var result = new Dictionary<string, (int?, int?)>(StringComparer.Ordinal);
        foreach (var (id, slug) in MovieIdToSlug)
        {
            result[slug] = (id, result.TryGetValue(slug, out var existing) ? existing.Item2 : null);
        }

        // Ordered by id so the "first writer wins" tie-break is deterministic rather than a
        // property of dictionary insertion order. The only tie is `family`, which resolves to
        // 10751 (Family) ahead of 10762 (Kids).
        foreach (var (id, slugs) in TvIdToSlugs.OrderBy(kv => kv.Key))
        {
            foreach (var slug in slugs)
            {
                var existing = result.TryGetValue(slug, out var found) ? found : (null, null);
                result[slug] = (existing.Item1, existing.Item2 ?? id);
            }
        }

        return result;
    }
}
