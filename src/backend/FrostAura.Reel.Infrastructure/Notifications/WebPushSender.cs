using System.Net;
using FrostAura.Reel.Domain.Notifications;
using FrostAura.Reel.Domain.Ports;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using WebPush;

namespace FrostAura.Reel.Infrastructure.Notifications;

/// <summary>
/// <see cref="IWebPushSender"/> adapter over the WebPush library (RFC 8291 payload encryption
/// + RFC 8292 VAPID auth). Maps push-service 404/410 to <see cref="WebPushOutcome.Gone"/> so
/// callers can retire dead subscriptions; every other error is a plain
/// <see cref="WebPushOutcome.Failure"/>. Never throws for delivery problems.
/// </summary>
public sealed class WebPushSender(
    VapidKeyProvider vapidKeys,
    IConfiguration configuration,
    ILogger<WebPushSender> logger) : IWebPushSender
{
    public async Task<WebPushResult> SendAsync(
        string endpoint, string p256dh, string auth, string payloadJson, CancellationToken ct)
    {
        // Checked again at send time, not only at subscribe: rows stored before the allow-list
        // existed are retired (Gone) instead of POSTed to (J-9).
        if (!PushEndpointPolicy.IsAllowed(endpoint))
        {
            logger.LogWarning("Web push subscription refused at send: its endpoint is not a known push service; retiring it.");
            return new WebPushResult(WebPushOutcome.Gone, "endpoint is not a known push service");
        }

        try
        {
            var (publicKey, privateKey) = await vapidKeys.GetKeysAsync(ct);
            // The VAPID subject is a contact URL push services may use to reach the operator.
            var subject = configuration.GetValue("Push:Subject", "https://reel.frostaura.net");
            var vapidDetails = new VapidDetails(subject, publicKey, privateKey);

            using var client = new WebPushClient();
            await client.SendNotificationAsync(
                new global::WebPush.PushSubscription(endpoint, p256dh, auth), payloadJson, vapidDetails, ct);
            return new WebPushResult(WebPushOutcome.Success);
        }
        catch (WebPushException ex) when (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
        {
            return new WebPushResult(WebPushOutcome.Gone, ex.Message);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Web push delivery to {Endpoint} failed.", endpoint);
            return new WebPushResult(WebPushOutcome.Failure, ex.Message);
        }
    }
}
