using FrostAura.Reel.Domain.Tenancy;

namespace FrostAura.Reel.Domain.Feed;

/// <summary>
/// One account's weekly digest — the "new and high-for-you this week" picks, frozen at build
/// time. Upserted by the BuildDigest job on (AccountId, WeekStart) where <see cref="WeekStart"/>
/// is the Monday of the current ISO week (UTC). <see cref="ItemsJson"/> is a jsonb array of
/// <c>{titleId, predictedRating, reason}</c> with reason "new-release" | "new-on-provider".
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
/// </summary>
public sealed record DigestItem(
    [property: System.Text.Json.Serialization.JsonPropertyName("titleId")] Guid TitleId,
    [property: System.Text.Json.Serialization.JsonPropertyName("predictedRating")] decimal PredictedRating,
    [property: System.Text.Json.Serialization.JsonPropertyName("reason")] string Reason,
    [property: System.Text.Json.Serialization.JsonPropertyName("degradedReason")] string? DegradedReason = null);

/// <summary>Digest item reasons — stable strings, persisted inside ItemsJson.</summary>
public static class DigestReasons
{
    public const string NewRelease = "new-release";
    public const string NewOnProvider = "new-on-provider";
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
