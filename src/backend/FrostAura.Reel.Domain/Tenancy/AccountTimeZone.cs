using System.Text.RegularExpressions;

namespace FrostAura.Reel.Domain.Tenancy;

/// <summary>
/// The account's own day (ADD-22, M7): which IANA time zone "tonight", "today's picks" and a daily
/// allowance are counted in.
///
/// <para><b>Why it exists.</b> Every day boundary in Reel is a UTC day. For the founder at +0200
/// that moves "today" two hours early — a daily Ask allowance resets at 02:00, and a pick that is
/// meant to be stable within the day can change at 02:00 in the middle of an evening. For an
/// external user in the Americas the boundary falls in the evening itself.</para>
///
/// <para><b>Contract for consumers</b> (WS-1 budget days, WS-5 "tonight" and stable-within-day):
/// read <see cref="AccountSettings.TimeZone"/>, then use <see cref="LocalDate"/> for "which day is
/// it for this account" and <see cref="DayStartUtc"/> for the UTC instant that day began (a
/// window's lower bound against UTC timestamps). An account that never set a zone — and one whose
/// stored zone this host cannot resolve — gets UTC, which is exactly today's behaviour, so adopting
/// the helper changes nothing until a zone is set.</para>
///
/// <para>Stored as the IANA id (<c>Africa/Johannesburg</c>), never an offset: offsets change with
/// daylight saving and an account's evening does not.</para>
/// </summary>
public static partial class AccountTimeZone
{
    /// <summary>The zone every account without a usable setting is counted in.</summary>
    public const string Default = "UTC";

    /// <summary>
    /// The canonical IANA id for <paramref name="input"/>, or null when it is not an IANA zone this
    /// host knows. Windows ids (<c>South Africa Standard Time</c>), offsets (<c>+02:00</c>) and
    /// paths are refused: the browser's <c>Intl</c> API reports IANA ids, and the stored value must
    /// resolve on every host the image runs on.
    /// </summary>
    public static string? Normalise(string? input)
    {
        var candidate = input?.Trim();
        if (candidate is null || !IanaShape().IsMatch(candidate))
        {
            return null;
        }

        if (!TimeZoneInfo.TryFindSystemTimeZoneById(candidate, out var zone) || !zone.HasIanaId)
        {
            return null;
        }

        // A case-insensitive file system resolves "africa/johannesburg"; store the zone's own
        // spelling so the value means the same thing on a case-sensitive host.
        var listed = TimeZoneInfo.GetSystemTimeZones()
            .FirstOrDefault(z => string.Equals(z.Id, candidate, StringComparison.OrdinalIgnoreCase));
        return listed?.Id ?? (string.Equals(zone.Id, candidate, StringComparison.Ordinal) ? zone.Id : null);
    }

    /// <summary>The zone for a stored id; UTC when it is unset or this host cannot resolve it.</summary>
    public static TimeZoneInfo Resolve(string? timeZoneId) =>
        timeZoneId is { Length: > 0 } id && TimeZoneInfo.TryFindSystemTimeZoneById(id, out var zone)
            ? zone
            : TimeZoneInfo.Utc;

    /// <summary>The account's calendar date at <paramref name="utcNow"/>.</summary>
    public static DateOnly LocalDate(string? timeZoneId, DateTime utcNow) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(AsUtc(utcNow), Resolve(timeZoneId)));

    /// <summary>
    /// The UTC instant the account's current day began: local midnight of <see cref="LocalDate"/>.
    /// On a day that skips midnight (a daylight-saving jump at 00:00) it is the first local instant
    /// that exists.
    /// </summary>
    public static DateTime DayStartUtc(string? timeZoneId, DateTime utcNow) =>
        MidnightUtc(Resolve(timeZoneId), LocalDate(timeZoneId, utcNow));

    /// <summary>
    /// The UTC instant the account's NEXT day begins — when a daily allowance counted from
    /// <see cref="DayStartUtc"/> comes back. Same daylight-saving rule as <see cref="DayStartUtc"/>.
    /// </summary>
    public static DateTime NextDayStartUtc(string? timeZoneId, DateTime utcNow) =>
        MidnightUtc(Resolve(timeZoneId), LocalDate(timeZoneId, utcNow).AddDays(1));

    private static DateTime MidnightUtc(TimeZoneInfo zone, DateOnly date)
    {
        var midnight = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        while (zone.IsInvalidTime(midnight))
        {
            midnight = midnight.AddMinutes(15);
        }

        return TimeZoneInfo.ConvertTimeToUtc(midnight, zone);
    }

    private static DateTime AsUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };

    /// <summary><c>Area/Location</c> (up to three segments) or a bare id such as <c>UTC</c>; no dots, so no paths.</summary>
    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_+-]{0,40}(/[A-Za-z0-9_+-]{1,40}){0,2}$", RegexOptions.CultureInvariant)]
    private static partial Regex IanaShape();
}
