using System.Net;
using FrostAura.Reel.Api.Endpoints;

namespace FrostAura.Reel.Api.Security;

/// <summary>
/// "Only this machine may call this route" — the control that actually closes the QA helpers to the
/// LAN, where an account or tier check cannot.
///
/// <para><b>Why a tier check is not enough.</b> <c>POST /api/auth/dev/link</c> MINTS a Founder
/// session for whoever calls it, so every founder-only route behind it was exactly as open as
/// dev/link itself. On 2026-10-22 the live stack (Development, QA helpers on, gateway on 0.0.0.0,
/// host firewall off) answered an unauthenticated <c>GET /api/dev/residual-replay</c> from another
/// LAN host with the founder's slug and 2,142 replay events (finding J-1). The fix has two halves:
/// the compose files publish on <c>127.0.0.1</c> unless <c>GATEWAY_BIND</c> opts in, and this guard
/// refuses any caller that is not the local machine whatever the publish address.</para>
///
/// <para><b>What counts as local.</b> The connection's peer must be a loopback address, AND the
/// request must carry no forwarding header. A request that arrived through a proxy — the compose
/// gateway, cloudflared, anything that adds <c>X-Forwarded-For</c> — is refused even when the proxy
/// itself sits on loopback, because the proxy's address says nothing about who is behind it. That
/// is deliberate: inside the compose stack every request reaches Kestrel from the gateway container,
/// so the helpers are reachable only from a host-side <c>dotnet run</c>, which is the only place the
/// build program uses them. The check also refuses after <c>UseForwardedHeaders</c> has rewritten
/// the peer, because that middleware leaves <c>X-Original-For</c> behind.</para>
///
/// <para>A <see langword="null"/> peer (an in-process test server, a Unix socket) is not loopback.
/// The refusal is a 403 with the house error envelope, code <see cref="RefusalCode"/>.</para>
/// </summary>
public static class LoopbackGuard
{
    /// <summary>The error code a refused caller receives.</summary>
    public const string RefusalCode = "loopback_only";

    /// <summary>Headers whose presence means a proxy stood between the caller and this process.</summary>
    public static readonly IReadOnlyList<string> ForwardingHeaders =
    [
        "X-Forwarded-For",
        "X-Forwarded-Host",
        "X-Original-For",
        "X-Real-IP",
        "Forwarded",
        "CF-Connecting-IP",
    ];

    /// <summary>True only when the peer is a loopback address and no proxy forwarded the request.</summary>
    public static bool IsDirectLoopback(IPAddress? remoteAddress, IHeaderDictionary headers)
    {
        if (remoteAddress is null)
        {
            return false;
        }

        var address = remoteAddress.IsIPv4MappedToIPv6 ? remoteAddress.MapToIPv4() : remoteAddress;
        if (!IPAddress.IsLoopback(address))
        {
            return false;
        }

        foreach (var header in ForwardingHeaders)
        {
            if (headers.ContainsKey(header))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>True when the current request comes straight from this machine.</summary>
    public static bool IsDirectLoopback(HttpContext context) =>
        IsDirectLoopback(context.Connection.RemoteIpAddress, context.Request.Headers);

    /// <summary>Refuses every route in the group unless the caller is this machine.</summary>
    public static RouteGroupBuilder RequireLoopback(this RouteGroupBuilder group)
    {
        group.AddEndpointFilter(Filter);
        return group;
    }

    /// <summary>Refuses this route unless the caller is this machine.</summary>
    public static RouteHandlerBuilder RequireLoopback(this RouteHandlerBuilder route)
    {
        route.AddEndpointFilter(Filter);
        return route;
    }

    private static async ValueTask<object?> Filter(EndpointFilterInvocationContext context, EndpointFilterDelegate next) =>
        IsDirectLoopback(context.HttpContext)
            ? await next(context)
            : EndpointHelpers.Forbidden(RefusalCode);
}
