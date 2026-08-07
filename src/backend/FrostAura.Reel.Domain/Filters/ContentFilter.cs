using FrostAura.Reel.Domain.Tenancy;

namespace FrostAura.Reel.Domain.Filters;

public enum FilterKind
{
    ExcludeGenre,
    IncludeGenre,
    ExcludeKeyword,
    MaxMaturity,
    /// <summary>Floor on the predicted rating for recommendation surfaces (value = "6.5").</summary>
    MinPredictedRating,
    /// <summary>
    /// Allowed original language (value = ISO-639-1 code, e.g. "en"). Allow-list: when any rows
    /// exist a title's language must match one. The sentinel "*" allows every language. With no
    /// rows at all the account defaults to English-only (founder ask 2026-06-30).
    /// </summary>
    Language,

    /// <summary>
    /// Floor on the COMMUNITY rating (value = "7.0"), resolved TMDB-then-Trakt exactly as
    /// <see cref="Catalog.CrowdRating"/> resolves the badge — so the number you filter on is the
    /// number the card shows. A title with no crowd rating from either source FAILS the floor, the
    /// same way an unrated certification fails a maturity ceiling: a restriction the catalogue has
    /// no data for is not a licence to ignore the restriction.
    /// </summary>
    MinCrowdRating,

    /// <summary>
    /// Inclusive lower bound on the release year (value = "1990"). Year resolves as
    /// <c>Year ?? ReleasedAt.Year ?? FirstAiredAt.Year</c>; a title with none of those PASSES —
    /// absence of a date is not evidence of the wrong date, matching how the Ask Reel year window
    /// already treats undated titles.
    /// </summary>
    MinYear,

    /// <summary>Inclusive upper bound on the release year (value = "2010"). Same resolution and
    /// same undated-passes rule as <see cref="MinYear"/>.</summary>
    MaxYear,
}

/// <summary>
/// One content-preference rule. None active by default; opt-in; airtight — the
/// EligibilityQueryBuilder applies these to every surface (hero, rows, search, continue-watching).
/// </summary>
public class ContentFilter : IAccountScoped
{
    public Guid Id { get; set; }

    public Guid AccountId { get; set; }

    public FilterKind Kind { get; set; }

    public string Value { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }
}
