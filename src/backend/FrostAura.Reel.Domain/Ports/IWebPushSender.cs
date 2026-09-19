namespace FrostAura.Reel.Domain.Ports;

/// <summary>
/// Outcome of a single Web Push delivery attempt. <see cref="Gone"/> means the push service
/// answered 404/410 — the subscription is dead and its row should be deleted, not retried.
/// </summary>
public enum WebPushOutcome
{
    Success,
    Gone,
    Failure,
}

public sealed record WebPushResult(WebPushOutcome Outcome, string? Error = null);

/// <summary>
/// Sends a Web Push notification to one browser subscription (RFC 8291 encryption + RFC 8292
/// VAPID auth are the adapter's concern). Callers own subscription lifecycle: delete on
/// <see cref="WebPushOutcome.Gone"/>, count failures and prune on repeated
/// <see cref="WebPushOutcome.Failure"/>.
/// </summary>
public interface IWebPushSender
{
    Task<WebPushResult> SendAsync(string endpoint, string p256dh, string auth, string payloadJson, CancellationToken ct);
}
