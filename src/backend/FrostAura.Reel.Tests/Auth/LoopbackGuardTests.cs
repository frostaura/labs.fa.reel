using System.Net;
using FrostAura.Reel.Api.Endpoints;
using FrostAura.Reel.Api.Security;
using Microsoft.AspNetCore.Http;

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
    [InlineData("X-Forwarded-For", "192.168.0.148")]
    [InlineData("X-Forwarded-For", "127.0.0.1")]
    [InlineData("X-Forwarded-Host", "reel.frostaura.net")]
    [InlineData("X-Original-For", "192.168.0.148:51234")]
    [InlineData("X-Real-IP", "192.168.0.148")]
    [InlineData("Forwarded", "for=192.168.0.148")]
    [InlineData("CF-Connecting-IP", "203.0.113.9")]
    public void A_loopback_peer_that_forwarded_someone_else_is_refused(string header, string value)
    {
        // A proxy on loopback says nothing about who is behind it — even a forwarded "127.0.0.1",
        // which any client can write into its own request before the proxy appends to it.
        var headers = new HeaderDictionary { [header] = value };

        Assert.False(LoopbackGuard.IsDirectLoopback(IPAddress.Loopback, headers));
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
    public void A_dev_link_session_is_marked_by_its_user_agent(string? userAgent, string expected)
    {
        Assert.Equal(expected, DevEndpoints.MarkDevLinkUserAgent(userAgent));
    }

    [Fact]
    public void A_marked_user_agent_still_fits_the_column()
    {
        var marked = DevEndpoints.MarkDevLinkUserAgent(new string('x', 400));

        Assert.Equal(256, marked.Length);
        Assert.StartsWith(DevEndpoints.DevLinkSessionMarker + " ", marked, StringComparison.Ordinal);
    }
}
