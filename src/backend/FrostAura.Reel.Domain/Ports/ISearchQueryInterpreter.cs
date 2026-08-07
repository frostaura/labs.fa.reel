using FrostAura.Reel.Domain.Catalog;

namespace FrostAura.Reel.Domain.Ports;

/// <summary>
/// A natural-language search query resolved into a structured TMDB discovery spec — the bridge
/// between "shark horrors" and the keyword/genre/text calls that pull the real titles. Genres are
/// slugs aligned with the ingest vocabulary (<c>TmdbGenres.SlugToIds</c>); keywords are free
/// phrases resolved to TMDB keyword ids; FreeText is the residual for literal title search.
/// </summary>
/// <summary>The role a named person plays in a query — "movies Nolan DIRECTS" vs "movies WITH Tom Hardy".</summary>
public enum PersonRole
{
    Director,
    Cast,
    Any,
}

/// <summary>A named person constraint extracted from the query, with the role the user meant.</summary>
public sealed record PersonQuery(string Name, PersonRole Role);

public sealed record SearchIntent(
    IReadOnlyList<string> Genres,
    IReadOnlyList<string> Keywords,
    IReadOnlyList<MediaType> MediaTypes,
    string FreeText,
    int? MinYear,
    // Upper release-year bound, inclusive of the whole year. Without it a decade query cannot be
    // expressed at all: "80s sci-fi" resolves to MinYear=1980 and then returns 1980-to-today
    // sorted by popularity, so modern blockbusters dominate a request explicitly asking for old
    // films. Nullable and positioned after MinYear so a bound is always optional.
    int? MaxYear,
    // A person/filmography query ("all movies Christopher Nolan directs") — resolved to TMDB
    // person credits instead of keyword/title search, which can't represent a name. Optional so
    // every existing construction site stays valid.
    IReadOnlyList<PersonQuery>? People = null,
    // What the user said they do NOT want — "less horror", "no ghosts", "not a musical".
    // These MUST stay out of Genres/Keywords: those lists are pull signals, so a negated term
    // landing there makes discovery actively chase the thing the user just rejected (the original
    // defect — "I don't want ghosts" fetched ghost stories). Applied instead as a discovery
    // exclusion plus a ranking penalty — never as a hard cull, so a negation can narrow the
    // ordering but can never empty the result set.
    IReadOnlyList<string>? ExcludeGenres = null,
    IReadOnlyList<string>? ExcludeKeywords = null,
    // Titles the user names as COMPARISONS ("like Blade Runner but more emotional", "similar to
    // Severance") — not things to return, but anchors to search NEAR. Resolved to catalogue titles
    // with embeddings so their semantic neighbours dominate discovery; the named title itself is
    // excluded from the results (the user has plainly seen or at least knows it). Nullable with a
    // null default so every existing construction site stays valid; treat null as empty.
    IReadOnlyList<string>? ReferenceTitles = null,
    // An upper runtime bound in minutes ("under 90 minutes" → 90). Applied to TMDB discovery
    // (with_runtime.lte) AND as a post-filter on the final pool, because the semantic arm knows
    // nothing about runtimes.
    int? MaxRuntimeMinutes = null);

public interface ISearchQueryInterpreter
{
    /// <summary>True when a live model OR the deterministic stub is available (the stub always answers).</summary>
    bool IsAvailable { get; }

    string ModelId { get; }

    /// <summary>Parse a free-text query into a discovery spec. Never throws — degrades to a keyword-only intent.</summary>
    Task<SearchIntent> InterpretAsync(string query, CancellationToken ct = default);
}
