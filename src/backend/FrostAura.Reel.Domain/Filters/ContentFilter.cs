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
