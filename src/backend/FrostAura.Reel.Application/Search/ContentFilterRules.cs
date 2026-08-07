using System.Globalization;

namespace FrostAura.Reel.Application.Search;

/// <summary>
/// Validation and canonicalisation for the numeric content filters, extracted from the settings
/// endpoint so the rules are testable on their own and so every writer stores the same string shape.
/// <c>ContentFilter.Value</c> is a free-text column shared by every filter kind, and
/// <see cref="EligibilityQueryBuilder"/> casts these particular rows to a number inside SQL — a
/// value written in any other shape would be a runtime cast error, not a wrong answer. Write through
/// here or not at all.
/// </summary>
public static class ContentFilterRules
{
    /// <summary>Earliest plausible release year — the Roundhay Garden Scene (1888) with headroom.</summary>
    public const int MinAllowedYear = 1870;

    /// <summary>Latest accepted release year; far enough out to cover any announced slate.</summary>
    public const int MaxAllowedYear = 2100;

    /// <summary>An inclusive release-year window after validation, swapping and canonicalisation.</summary>
    public readonly record struct YearWindow(int? MinYear, int? MaxYear);

    /// <summary>
    /// Validates a requested year window. Out-of-range bounds are REJECTED (false + an error) rather
    /// than clamped: 19990 is a typo, and silently reading it as 2100 would hand the user a window
    /// they never asked for. Inverted bounds are SWAPPED rather than rejected — "1990 to 1980" is an
    /// obvious slider drag-past, and the only other honest reading of it is an empty feed.
    /// </summary>
    public static bool TryNormalizeYears(int? minYear, int? maxYear, out YearWindow window, out string? error)
    {
        window = default;
        error = null;

        if (minYear is { } lo && (lo < MinAllowedYear || lo > MaxAllowedYear))
        {
            error = $"minYear must be between {MinAllowedYear} and {MaxAllowedYear}";
            return false;
        }

        if (maxYear is { } hi && (hi < MinAllowedYear || hi > MaxAllowedYear))
        {
            error = $"maxYear must be between {MinAllowedYear} and {MaxAllowedYear}";
            return false;
        }

        if (minYear is { } a && maxYear is { } b && a > b)
        {
            (minYear, maxYear) = (b, a);
        }

        window = new YearWindow(minYear, maxYear);
        return true;
    }

    /// <summary>
    /// The crowd-rating floor clamped to the 0–10 scale both TMDB and Trakt publish on. Zero (or
    /// below) is "no floor set" and stores no row at all, mirroring how MinPredictedRating behaves.
    /// </summary>
    public static decimal? NormalizeCrowdFloor(decimal? value) =>
        value is { } raw && raw > 0m ? Math.Clamp(raw, 0m, 10m) : null;

    /// <summary>The canonical stored form of a rating floor — one decimal, invariant culture.</summary>
    public static string FormatRating(decimal value) => value.ToString("0.0", CultureInfo.InvariantCulture);

    /// <summary>The canonical stored form of a year bound — plain digits, invariant culture.</summary>
    public static string FormatYear(int value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>Reads a stored rating row back, clamped; null when absent or malformed.</summary>
    public static decimal? ParseRating(string? stored) =>
        stored is not null && decimal.TryParse(stored, NumberStyles.Number, CultureInfo.InvariantCulture, out var value)
            ? Math.Clamp(value, 0m, 10m)
            : null;

    /// <summary>Reads a stored year row back; null when absent or malformed.</summary>
    public static int? ParseYear(string? stored) =>
        stored is not null && int.TryParse(stored, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
}
