namespace FrostAura.Reel.Domain.Tenancy;

/// <summary>Per-account settings, persisted as a jsonb document on the account row.</summary>
public class AccountSettings
{
    /// <summary>PBKDF2 hash of the optional settings PIN; null = no PIN configured.</summary>
    public string? SettingsPinHash { get; set; }

    /// <summary>Set when the user completes the onboarding build-up and enters the app.</summary>
    public bool OnboardingCompleted { get; set; }

    /// <summary>Optional maturity ceiling (certification system value, e.g. "PG-13", "TV-MA").</summary>
    public string? MaturityCeiling { get; set; }

    /// <summary>Captured in-app ahead of M5 billing; not collected at signup.</summary>
    public string? EmailForBilling { get; set; }

    /// <summary>
    /// How long an evening is, in minutes — the ceiling the Saved shelf's pinned "watch tonight"
    /// pick must fit inside. Null means the user has never said, and the queue applies its own
    /// default rather than storing one here: a value written on their behalf reads back as a
    /// preference they expressed. Lives in the owned jsonb document, so adding it is NOT a schema
    /// change and must not produce a migration.
    /// </summary>
    public int? EveningBudgetMinutes { get; set; }

    /// <summary>
    /// The account's IANA time zone (<c>Africa/Johannesburg</c>) — the day "tonight", a daily
    /// allowance and stable-within-day picks are counted in (ADD-22). Null means never set, and every
    /// consumer counts in UTC through <see cref="AccountTimeZone"/>. Lives in the owned jsonb
    /// document, so adding it is NOT a schema change (M7 needs no migration).
    /// </summary>
    public string? TimeZone { get; set; }
}
