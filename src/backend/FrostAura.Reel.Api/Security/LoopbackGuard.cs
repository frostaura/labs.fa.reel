using System.Net;
using FrostAura.Reel.Api.Endpoints;
using Microsoft.Extensions.Primitives;

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
/// <para><b>What counts as local.</b> The connection's peer must be a loopback address, AND every
/// hop any proxy recorded must be loopback too. A request with no forwarding header at all is a
/// direct caller. A request that did pass through a proxy is accepted only when that proxy wrote
/// down a loopback client (<c>X-Forwarded-For</c>, <c>X-Original-For</c>, <c>X-Real-IP</c>,
/// <c>Forwarded: for=</c>) and a loopback host (<c>X-Forwarded-Host</c>): that is the Vite dev
/// server on this machine with <c>xfwd</c> on, serving a browser on this machine. Anything else is
/// refused: a LAN client through Vite appears as its own address in the list Vite appends to; the
/// compose gateway's hop is a Docker bridge address, never loopback; and <c>CF-Connecting-IP</c>
/// means the request came through Cloudflare, so it is refused whatever it says. A value that does
/// not parse as an address (<c>unknown</c>, an obfuscated <c>_token</c>, an empty list entry) is
/// refused rather than skipped.</para>
///
/// <para><b>What this cannot see.</b> A proxy that forwards WITHOUT recording the client looks
/// exactly like a direct caller, because its own hop to this process is loopback. Vite does that
/// unless <c>xfwd</c> is set (WS-8 review, 2026-10-24: a LAN host minted a Founder session through
/// the Vite proxy that way). That hole is closed where it opens, in <c>src/frontend/vite.config.ts</c>
/// (<c>xfwd: true</c>, loopback listen by default), and <c>make ci-vite-proxy</c> fails while either
/// is missing. Any other local proxy in front of this process must record its client the same way.</para>
///
/// <para>A <see langword="null"/> peer (an in-process test server, a Unix socket) is not loopback.
/// The refusal is a 403 with the house error envelope, code <see cref="RefusalCode"/>.</para>
/// </summary>
public static class LoopbackGuard
{
    /// <summary>The error code a refused caller receives.</summary>
    public const string RefusalCode = "loopback_only";

    /// <summary>Headers that name the client a proxy forwarded, as one address or a comma-separated hop list.</summary>
    public static readonly IReadOnlyList<string> AddressListHeaders = ["X-Forwarded-For", "X-Original-For", "X-Real-IP"];

    /// <summary>Every header the guard reads; each one is checked, none is ignored.</summary>
    public static readonly IReadOnlyList<string> ForwardingHeaders =
    [
        .. AddressListHeaders,
        "X-Forwarded-Host",
        "Forwarded",
        "CF-Connecting-IP",
    ];

    /// <summary>
    /// True only when the peer is loopback and every hop a proxy recorded is loopback (see the type
    /// remarks for what that does and does not prove).
    /// </summary>
    public static bool IsDirectLoopback(IPAddress? remoteAddress, IHeaderDictionary headers)
    {
        if (!IsLoopback(remoteAddress))
        {
            return false;
        }

        // Cloudflare's edge is the internet by definition, whatever address it reports.
        if (headers.ContainsKey("CF-Connecting-IP"))
        {
            return false;
        }

        foreach (var header in AddressListHeaders)
        {
            if (headers.TryGetValue(header, out var values) && !Hops(values).All(IsLoopbackHop))
            {
                return false;
            }
        }

        if (headers.TryGetValue("Forwarded", out var forwarded) && !ForwardedClients(forwarded).All(IsLoopbackHop))
        {
            return false;
        }

        if (headers.TryGetValue("X-Forwarded-Host", out var hosts) && !Hops(hosts).All(IsLoopbackHost))
        {
            return false;
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

    private static bool IsLoopback(IPAddress? address)
    {
        if (address is null)
        {
            return false;
        }

        return IPAddress.IsLoopback(address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address);
    }

    /// <summary>Every comma-separated entry across every value of a header, trimmed; empty entries kept so they fail.</summary>
    private static List<string> Hops(StringValues values)
    {
        var hops = new List<string>();
        foreach (var value in values)
        {
            foreach (var entry in (value ?? string.Empty).Split(','))
            {
                hops.Add(entry.Trim());
            }
        }

        return hops;
    }

    /// <summary>The <c>for=</c> node of every element of an RFC 7239 <c>Forwarded</c> header; an element without one yields "" (refused).</summary>
    private static List<string> ForwardedClients(StringValues values)
    {
        var clients = new List<string>();
        foreach (var element in Hops(values))
        {
            var node = string.Empty;
            foreach (var pair in element.Split(';'))
            {
                var separator = pair.IndexOf('=', StringComparison.Ordinal);
                if (separator > 0 && pair[..separator].Trim().Equals("for", StringComparison.OrdinalIgnoreCase))
                {
                    node = pair[(separator + 1)..].Trim().Trim('"');
                }
            }

            clients.Add(node);
        }

        return clients;
    }

    /// <summary>A hop is "127.0.0.1", "::1", "[::1]:5173", "127.0.0.1:51234" or "::ffff:127.0.0.1"; anything that does not parse is not loopback.</summary>
    private static bool IsLoopbackHop(string hop)
    {
        if (IPAddress.TryParse(hop, out var address))
        {
            return IsLoopback(address);
        }

        if (hop.StartsWith('['))
        {
            var close = hop.IndexOf(']', StringComparison.Ordinal);
            return close > 1 && IPAddress.TryParse(hop[1..close], out var bracketed) && IsLoopback(bracketed);
        }

        var colon = hop.LastIndexOf(':');
        return colon > 0 && hop.IndexOf(':', StringComparison.Ordinal) == colon
            && IPAddress.TryParse(hop[..colon], out var withPort) && IsLoopback(withPort);
    }

    /// <summary>A forwarded host is <c>localhost</c> or a loopback address, with or without a port.</summary>
    private static bool IsLoopbackHost(string host)
    {
        var name = host;
        if (!name.StartsWith('[') && name.Count(c => c == ':') == 1)
        {
            name = name[..name.IndexOf(':', StringComparison.Ordinal)];
        }

        return name.Equals("localhost", StringComparison.OrdinalIgnoreCase) || IsLoopbackHop(name);
    }

    private static async ValueTask<object?> Filter(EndpointFilterInvocationContext context, EndpointFilterDelegate next) =>
        IsDirectLoopback(context.HttpContext)
            ? await next(context)
            : EndpointHelpers.Forbidden(RefusalCode);
}
