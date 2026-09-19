using FrostAura.Reel.Domain.Tenancy;

namespace FrostAura.Reel.Domain.Notifications;

/// <summary>
/// One browser's Web Push subscription for an account (an account can hold several — one per
/// browser/device). Unique on (AccountId, Endpoint). Lifecycle: deleted when the push service
/// reports the endpoint gone (404/410); pruned when <see cref="FailCount"/> reaches 5.
/// </summary>
public class PushSubscription : IAccountScoped
{
    public Guid Id { get; set; }

    public Guid AccountId { get; set; }

    /// <summary>Push-service URL — opaque, browser-issued, can exceed 500 chars (text).</summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>Client public key (base64url) for RFC 8291 payload encryption.</summary>
    public string P256dh { get; set; } = string.Empty;

    /// <summary>Client auth secret (base64url) for RFC 8291 payload encryption.</summary>
    public string Auth { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }

    public DateTime? LastSuccessAt { get; set; }

    /// <summary>Consecutive delivery failures; reset on success, row pruned at ≥5.</summary>
    public int FailCount { get; set; }
}
