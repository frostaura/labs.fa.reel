using FrostAura.Reel.Domain.Tenancy;

namespace FrostAura.Reel.Domain.Feed;

/// <summary>
/// One account's weekly digest — the "new and high-for-you this week" picks, frozen at build
/// time. Upserted by the BuildDigest job on (AccountId, WeekStart) where <see cref="WeekStart"/>
/// is the Monday of the current ISO week (UTC). <see cref="ItemsJson"/> is a jsonb array of
/// <c>{titleId, predictedRating, reason, degradedReason?, provider?}</c> with reason
/// "new-release" | "new-on-provider".
/// </summary>
public class AccountDigest : IAccountScoped
{
    public Guid Id { get; set; }

    public Guid AccountId { get; set; }

    /// <summary>Monday of the ISO week this digest covers (UTC date).</summary>
    public DateOnly WeekStart { get; set; }

    public DateTime GeneratedAt { get; set; }

    /// <summary>jsonb: [{"titleId":…,"predictedRating":…,"reason":"new-release"}].</summary>
    public string ItemsJson { get; set; } = "[]";
}

/// <summary>
/// One entry inside <see cref="AccountDigest.ItemsJson"/>.
/// <para>
/// <paramref name="DegradedReason"/> is a DIGEST-level fact carried per item because
/// <see cref="AccountDigest"/> has no column for it yet and this branch ships no migration. It is
/// null on a digest that met the "high for you" bar honestly, and identical across every item of a
/// relaxed one. Optional and last, so digests written before it deserialise unchanged.
/// </para>
/// <para>
/// <paramref name="Provider"/> is, for a <see cref="DigestReasons.NewOnProvider"/> item, the name of
/// the provider the title ARRIVED on this week (its offer's <c>FirstSeenAt</c> fell in the window).
/// Null on every other item, and on every new-on-provider item built before IR2S-02 — those were
/// picked off a cache refresh, not an arrival, and the read side treats them as unsupported.
/// </para>
/// </summary>
public sealed record DigestItem(
    [property: System.Text.Json.Serialization.JsonPropertyName("titleId")] Guid TitleId,
    [property: System.Text.Json.Serialization.JsonPropertyName("predictedRating")] decimal PredictedRating,
    [property: System.Text.Json.Serialization.JsonPropertyName("reason")] string Reason,
    [property: System.Text.Json.Serialization.JsonPropertyName("degradedReason")] string? DegradedReason = null,
    [property: System.Text.Json.Serialization.JsonPropertyName("provider")] string? Provider = null);

/// <summary>Digest item reasons — stable strings, persisted inside ItemsJson.</summary>
public static class DigestReasons
{
    public const string NewRelease = "new-release";
    public const string NewOnProvider = "new-on-provider";

    /// <summary>
    /// The user-facing why for a digest item. Single-sourced because the digest rail and the push
    /// notification are two renderings of the same pick and must not describe it differently.
    /// <para>
    /// An arrival names its provider: "Streaming on Netflix". It used to read "Just landed on your
    /// services", which was false twice over — "just landed" was a cache-refresh timestamp, and
    /// Reel has no notion of which services are "yours" (IR2S-02, program §1). Without a provider
    /// the line still says only what is true of every arrival: it streams now.
    /// </para>
    /// </summary>
    public static string CopyFor(string reason, string? provider = null) =>
        reason == NewOnProvider
            ? string.IsNullOrWhiteSpace(provider) ? "Streaming now" : $"Streaming on {provider.Trim()}"
            : "New this week";
}

/// <summary>Week arithmetic shared by the digest job, its scheduler and the API.</summary>
public static class DigestWeek
{
    /// <summary>Monday of the ISO week containing <paramref name="utcNow"/> (UTC date).</summary>
    public static DateOnly MondayOf(DateTime utcNow)
    {
        var date = DateOnly.FromDateTime(utcNow);
        var daysSinceMonday = ((int)date.DayOfWeek + 6) % 7; // Monday=0 … Sunday=6
        return date.AddDays(-daysSinceMonday);
    }
}
