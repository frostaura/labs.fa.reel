using System.Net;
using FrostAura.Reel.Application.Configuration;
using Microsoft.AspNetCore.Builder;

namespace FrostAura.Reel.Api.Security;

/// <summary>
/// The networks whose <c>X-Forwarded-For</c> / <c>X-Forwarded-Proto</c> the API believes (J-7).
///
/// <para>Production is cloudflared → nginx gateway → API, so the API's peer is always the gateway
/// container on a Docker bridge network, and the client's scheme and address arrive only as headers.
/// Believing those headers from ANY peer would let anyone who can reach the API port claim to be
/// loopback or HTTPS; believing them from no peer leaves every request looking like plain HTTP from
/// the gateway, which is what kept production cookies from ever being <c>Secure</c>.</para>
///
/// <para>The code default trusts loopback (a local Vite proxy or slot API) and <c>172.16.0.0/12</c>,
/// the range Docker allocates bridge networks from — right for the dev and eval stacks. The production
/// stack narrows it to its own pinned bridge (<c>docker-compose.prod.yml</c>), where cloudflared and
/// the gateway are the only two hops and no service publishes a host port. Comma-separated CIDRs.</para>
/// </summary>
public sealed class TrustedProxyOptions : ValidatedOptions
{
    public const string NetworksKey = "TRUSTED_PROXY_NETWORKS";

    public const string DefaultNetworks = "127.0.0.0/8,::1/128,172.16.0.0/12";

    public IReadOnlyList<IPNetwork> Networks { get; private set; } = [];

    protected override void Read(SettingReader reader)
    {
        var raw = reader.String(NetworksKey, DefaultNetworks);
        if (string.IsNullOrWhiteSpace(raw))
        {
            raw = DefaultNetworks;
        }

        var networks = new List<IPNetwork>();
        var bad = new List<string>();
        foreach (var entry in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (IPNetwork.TryParse(entry, out var network))
            {
                networks.Add(network);
            }
            else
            {
                bad.Add(entry);
            }
        }

        Networks = networks;
        BadEntries = bad.Count;
    }

    private int BadEntries { get; set; }

    /// <summary>
    /// Points the forwarded-headers middleware at these networks and nothing else.
    ///
    /// <para><see cref="ForwardedHeadersOptions.ForwardLimit"/> is lifted (null) rather than left at
    /// the framework's 1. With one hop believed, production's cloudflared → gateway → API chain resolves
    /// the client to the cloudflared CONTAINER — the last entry the gateway appended — not the visitor.
    /// Unbounded is still safe because the walk is bounded by trust instead of a count: entries are
    /// consumed right to left only while the address just revealed is itself in a trusted network, so
    /// it stops at the first address outside them (the real visitor) and anything a client wrote
    /// further left is never believed.</para>
    /// </summary>
    public void ApplyTo(ForwardedHeadersOptions forwarded)
    {
        forwarded.ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto;
        forwarded.ForwardLimit = null;
        forwarded.KnownProxies.Clear();
        forwarded.KnownIPNetworks.Clear();
        foreach (var network in Networks)
        {
            forwarded.KnownIPNetworks.Add(network);
        }
    }

    protected override void Check(Action<string> problem)
    {
        if (BadEntries > 0)
        {
            // The entries are not echoed: the house rule is that a validation message names the key,
            // never the value.
            problem($"{NetworksKey}: {BadEntries} entr{(BadEntries == 1 ? "y is" : "ies are")} not a CIDR network (e.g. 172.16.0.0/12).");
        }
        else if (Networks.Count == 0)
        {
            problem($"{NetworksKey}: no networks configured.");
        }
    }
}
