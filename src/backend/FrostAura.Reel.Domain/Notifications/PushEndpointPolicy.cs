namespace FrostAura.Reel.Domain.Notifications;

/// <summary>
/// Which push-subscription endpoints Reel will store and POST to (J-9).
///
/// <para><b>Why.</b> A subscription's endpoint is a URL the browser hands the client, and the server
/// later POSTs an encrypted payload to it. Taken on trust, it is a server-side request forgery
/// primitive: subscribing <c>http://postgres:5432</c> or <c>http://169.254.169.254/</c> makes the
/// API send requests into its own network on every digest. Real endpoints only ever come from a
/// handful of browser push services, all HTTPS on port 443, so the rule is an allow-list of those
/// services' hosts rather than a deny-list of internal ones.</para>
/// </summary>
public static class PushEndpointPolicy
{
    /// <summary>Subscriptions one account may hold — one per browser or device it uses.</summary>
    public const int MaxSubscriptionsPerAccount = 10;

    /// <summary>The longest endpoint accepted. Real ones are a few hundred characters.</summary>
    public const int MaxEndpointLength = 2048;

    /// <summary>
    /// Push-service hosts, matched exactly or as a dot-suffix: Chrome/Edge-on-Android/Opera (FCM),
    /// Firefox (Mozilla autopush), Edge/Windows (WNS) and Safari (Apple Push).
    /// </summary>
    public static readonly IReadOnlyList<string> AllowedHosts =
    [
        "fcm.googleapis.com",
        "android.googleapis.com",
        "push.services.mozilla.com",
        "notify.windows.com",
        "push.apple.com",
    ];

    /// <summary>True when <paramref name="endpoint"/> is an HTTPS URL on a known push service.</summary>
    public static bool IsAllowed(string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint)
            || endpoint.Length > MaxEndpointLength
            || !Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !uri.IsDefaultPort
            || uri.UserInfo.Length > 0
            || uri.HostNameType != UriHostNameType.Dns)
        {
            return false;
        }

        var host = uri.IdnHost.TrimEnd('.').ToLowerInvariant();
        return AllowedHosts.Any(allowed => host == allowed || host.EndsWith("." + allowed, StringComparison.Ordinal));
    }
}
