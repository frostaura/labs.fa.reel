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
    RatePriority Priority = RatePriority.Interactive);

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

    public static readonly IReadOnlyDictionary<int, string> TvIdToSlug = new Dictionary<int, string>
    {
        [10759] = "action",
        [16] = "animation",
        [35] = "comedy",
        [80] = "crime",
        [99] = "documentary",
        [18] = "drama",
        [10751] = "family",
        [10762] = "family",
        [9648] = "mystery",
        [10765] = "science-fiction",
        [10768] = "war",
        [37] = "western",
    };

    /// <summary>Slug → (movie genre id, tv genre id); null when that medium has no equivalent.</summary>
    public static readonly IReadOnlyDictionary<string, (int? Movie, int? Tv)> SlugToIds =
        BuildReverse();

    public static string[] MapToSlugs(bool isMovie, int[] genreIds)
    {
        var map = isMovie ? MovieIdToSlug : TvIdToSlug;
        return genreIds.Where(map.ContainsKey).Select(id => map[id]).Distinct().ToArray();
    }

    private static Dictionary<string, (int?, int?)> BuildReverse()
    {
        var result = new Dictionary<string, (int?, int?)>();
        foreach (var (id, slug) in MovieIdToSlug)
        {
            result[slug] = (id, result.TryGetValue(slug, out var existing) ? existing.Item2 : null);
        }

        foreach (var (id, slug) in TvIdToSlug)
        {
            result[slug] = (result.TryGetValue(slug, out var existing) ? existing.Item1 : null, id);
        }

        return result;
    }
}
