using System.Net;
using FrostAura.Reel.Api.Endpoints;
using FrostAura.Reel.Api.Security;
using FrostAura.Reel.Application.Auth;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

namespace FrostAura.Reel.Tests.Auth;

/// <summary>
/// The predicate behind "only this machine may call the QA helpers" (J-1). Pure: no host, no
/// database. The end-to-end answers each route gives are pinned in
/// <c>IntegrationTests/DevSurfaceLockdownTests.cs</c>.
/// </summary>
public class LoopbackGuardTests
{
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("127.0.0.53")]
    [InlineData("::1")]
    [InlineData("::ffff:127.0.0.1")]
    public void A_direct_loopback_peer_is_local(string address)
    {
        Assert.True(LoopbackGuard.IsDirectLoopback(IPAddress.Parse(address), new HeaderDictionary()));
    }

    [Theory]
    // The LAN host the 2026-10-22 probe came from, a Docker bridge peer (what Kestrel sees behind
    // the compose gateway), a private /8, a public address, and the all-zeros bind address.
    [InlineData("192.168.0.148")]
    [InlineData("172.18.0.5")]
    [InlineData("10.0.0.7")]
    [InlineData("8.8.8.8")]
    [InlineData("0.0.0.0")]
    [InlineData("::ffff:192.168.0.148")]
    [InlineData("fe80::1")]
    public void Any_other_peer_is_refused(string address)
    {
        Assert.False(LoopbackGuard.IsDirectLoopback(IPAddress.Parse(address), new HeaderDictionary()));
    }

    [Fact]
    public void An_unknown_peer_is_refused()
    {
        // An in-process test server or a Unix socket reports no address; absence is not loopback.
        Assert.False(LoopbackGuard.IsDirectLoopback(null, new HeaderDictionary()));
    }

    [Theory]
    // Someone else, as each proxy records it.
    [InlineData("X-Forwarded-For", "192.168.0.148")]
    [InlineData("X-Forwarded-Host", "reel.frostaura.net")]
    [InlineData("X-Forwarded-Host", "192.168.0.149:5173")]
    [InlineData("X-Original-For", "192.168.0.148:51234")]
    [InlineData("X-Real-IP", "192.168.0.148")]
    [InlineData("Forwarded", "for=192.168.0.148")]
    // Cloudflare's edge is refused even when it claims loopback.
    [InlineData("CF-Connecting-IP", "203.0.113.9")]
    [InlineData("CF-Connecting-IP", "127.0.0.1")]
    // The 2026-10-24 review case: a client writes 127.0.0.1 itself, and Vite (xfwd) appends the
    // LAN address it really saw. The real last hop decides.
    [InlineData("X-Forwarded-For", "127.0.0.1, 192.168.0.148")]
    [InlineData("X-Forwarded-For", "127.0.0.1,::ffff:192.168.0.148")]
    // The compose gateway: nginx's hop to the backend is a Docker bridge address.
    [InlineData("X-Forwarded-For", "172.18.0.1")]
    [InlineData("X-Real-IP", "192.168.65.1")]
    // Values that do not parse are refused, never skipped.
    [InlineData("X-Forwarded-For", "")]
    [InlineData("X-Forwarded-For", "127.0.0.1,")]
    [InlineData("X-Forwarded-For", "unknown")]
    [InlineData("X-Forwarded-For", "localhost")]
    [InlineData("Forwarded", "for=unknown")]
    [InlineData("Forwarded", "for=_hidden")]
    [InlineData("Forwarded", "proto=http")]
    [InlineData("Forwarded", "for=127.0.0.1, for=192.168.0.148")]
    [InlineData("Forwarded", "for=\"[::1]:5173\", for=\"[fe80::1]\"")]
    public void A_loopback_peer_that_forwarded_someone_else_is_refused(string header, string value)
    {
        // A proxy on loopback says nothing about who is behind it; only the hops it recorded do.
        var headers = new HeaderDictionary { [header] = value };

        Assert.False(LoopbackGuard.IsDirectLoopback(IPAddress.Loopback, headers));
    }

    [Theory]
    // Vite on this machine with xfwd, serving a browser on this machine (both address families).
    [InlineData("X-Forwarded-For", "127.0.0.1")]
    [InlineData("X-Forwarded-For", "::1")]
    [InlineData("X-Forwarded-For", "::ffff:127.0.0.1")]
    [InlineData("X-Forwarded-For", "127.0.0.1, ::1")]
    [InlineData("X-Forwarded-Host", "localhost:5208")]
    [InlineData("X-Forwarded-Host", "127.0.0.1:5173")]
    [InlineData("X-Forwarded-Host", "[::1]:5173")]
    [InlineData("X-Original-For", "127.0.0.1:51234")]
    [InlineData("X-Real-IP", "127.0.0.1")]
    [InlineData("Forwarded", "for=127.0.0.1;proto=http")]
    [InlineData("Forwarded", "for=\"[::1]:5173\"")]
    public void A_loopback_peer_that_forwarded_this_machine_is_local(string header, string value)
    {
        var headers = new HeaderDictionary { [header] = value };

        Assert.True(LoopbackGuard.IsDirectLoopback(IPAddress.Loopback, headers));
    }

    [Fact]
    public void What_vite_with_xfwd_sends_for_a_local_browser_is_local()
    {
        // Byte for byte what Vite 8's proxy adds with xfwd on (node.js chunk XHeaders): the socket's
        // remote address, port and scheme, and the Host the browser used.
        var headers = new HeaderDictionary
        {
            ["X-Forwarded-For"] = "::ffff:127.0.0.1",
            ["X-Forwarded-Port"] = "5208",
            ["X-Forwarded-Proto"] = "http",
            ["X-Forwarded-Host"] = "localhost:5208",
        };

        Assert.True(LoopbackGuard.IsDirectLoopback(IPAddress.Loopback, headers));
    }

    [Fact]
    public void What_vite_with_xfwd_sends_for_a_lan_host_is_refused()
    {
        var headers = new HeaderDictionary
        {
            ["X-Forwarded-For"] = "::ffff:192.168.0.148",
            ["X-Forwarded-Port"] = "5173",
            ["X-Forwarded-Proto"] = "http",
            ["X-Forwarded-Host"] = "192.168.0.149:5173",
        };

        Assert.False(LoopbackGuard.IsDirectLoopback(IPAddress.Loopback, headers));
    }

    [Fact]
    public void One_bad_hop_among_several_header_values_is_refused()
    {
        var headers = new HeaderDictionary { ["X-Forwarded-For"] = new StringValues(["127.0.0.1", "192.168.0.148"]) };

        Assert.False(LoopbackGuard.IsDirectLoopback(IPAddress.Loopback, headers));
    }

    [Fact]
    public void Loopback_hops_do_not_rescue_a_lan_peer()
    {
        var headers = new HeaderDictionary { ["X-Forwarded-For"] = "127.0.0.1" };

        Assert.False(LoopbackGuard.IsDirectLoopback(IPAddress.Parse("192.168.0.148"), headers));
    }

    [Fact]
    public void Every_forwarding_header_named_in_the_guard_is_exercised_above()
    {
        // Adding a header to the guard without a case above would leave it unpinned.
        string[] covered = ["X-Forwarded-For", "X-Forwarded-Host", "X-Original-For", "X-Real-IP", "Forwarded", "CF-Connecting-IP"];

        Assert.Equal(covered.Order(StringComparer.Ordinal), LoopbackGuard.ForwardingHeaders.Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData(null, "reel-dev-link")]
    [InlineData("", "reel-dev-link")]
    [InlineData("   ", "reel-dev-link")]
    [InlineData("curl/8.7.1", "reel-dev-link curl/8.7.1")]
    [InlineData("  curl/8.7.1  ", "reel-dev-link curl/8.7.1")]
    // A refresh from a client whose own agent already carries the marker is not marked twice.
    [InlineData("reel-dev-link curl/8.7.1", "reel-dev-link curl/8.7.1")]
    [InlineData("reel-dev-link", "reel-dev-link")]
    public void A_dev_link_session_is_marked_by_its_user_agent(string? userAgent, string expected)
    {
        Assert.Equal(expected, SessionService.MarkQaUserAgent(userAgent));
        Assert.True(SessionService.IsQaSession(SessionService.MarkQaUserAgent(userAgent)));
    }

    [Fact]
    public void A_marked_user_agent_still_fits_the_column()
    {
        var marked = SessionService.MarkQaUserAgent(new string('x', 400));

        Assert.Equal(256, marked.Length);
        Assert.StartsWith(DevEndpoints.DevLinkSessionMarker + " ", marked, StringComparison.Ordinal);
    }
}
