using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using FrostAura.Reel.Api;
using FrostAura.Reel.Api.Endpoints;
using FrostAura.Reel.Api.Security;
using FrostAura.Reel.Application.Configuration;
using FrostAura.Reel.Domain.Notifications;
using FrostAura.Reel.Domain.Tenancy;
using FrostAura.Reel.Infrastructure.Security;
using FrostAura.Reel.Tests.Harness;
using Microsoft.Extensions.Configuration;

namespace FrostAura.Reel.Tests.Auth;

/// <summary>
/// The pure validators behind the P1 security fixes: push endpoints (J-9), settings input (H-4,
/// H-11), trusted proxies (J-7), the key-ring certificate (J-12) and the /health commit (A-17).
/// </summary>
public class EdgeValidationTests
{
    [Theory]
    [InlineData("https://fcm.googleapis.com/fcm/send/abc:APA91b")]
    [InlineData("https://updates.push.services.mozilla.com/wpush/v2/gAAAAAB")]
    [InlineData("https://web.push.apple.com/QGuQ")]
    [InlineData("https://wns2-par02p.notify.windows.com/w/?token=BQYAAA")]
    [InlineData("https://FCM.googleapis.com./fcm/send/x")]
    public void Real_push_services_are_allowed(string endpoint) => Assert.True(PushEndpointPolicy.IsAllowed(endpoint));

    [Theory]
    [InlineData("http://postgres:5432")]
    [InlineData("https://postgres:5432/")]
    [InlineData("http://fcm.googleapis.com/fcm/send/x")]
    [InlineData("https://fcm.googleapis.com:8443/fcm/send/x")]
    [InlineData("https://169.254.169.254/latest/meta-data")]
    [InlineData("https://127.0.0.1/")]
    [InlineData("https://fcm.googleapis.com.evil.example/x")]
    [InlineData("https://evilfcm.googleapis.com.attacker.net/")]
    [InlineData("https://user:pass@fcm.googleapis.com/x")]
    [InlineData("https://notfcm.googleapis.co/x")]
    [InlineData("file:///etc/passwd")]
    [InlineData("")]
    [InlineData(null)]
    public void Anything_else_is_refused(string? endpoint) => Assert.False(PushEndpointPolicy.IsAllowed(endpoint));

    [Fact]
    public void An_overlong_endpoint_is_refused() =>
        Assert.False(PushEndpointPolicy.IsAllowed("https://fcm.googleapis.com/" + new string('a', PushEndpointPolicy.MaxEndpointLength)));

    [Theory]
    [InlineData("za", "ZA")]
    [InlineData(" US ", "US")]
    [InlineData("GB", "GB")]
    [InlineData("ZZ", null)]
    [InlineData("UK", null)]
    [InlineData("USA", null)]
    [InlineData("1A", null)]
    [InlineData("", null)]
    public void Regions_are_assigned_iso_3166_codes(string input, string? expected) =>
        Assert.Equal(expected, RegionCodes.Normalise(input));

    [Fact]
    public void The_region_list_is_the_full_assigned_set() => Assert.Equal(249, RegionCodes.Assigned.Count);

    [Theory]
    [InlineData("dean@frostaura.net", true)]
    [InlineData("first.last+tag@sub.example.co.za", true)]
    [InlineData("not-an-email", false)]
    [InlineData("a@b", false)]
    [InlineData("a@b.", false)]
    [InlineData("Dean <dean@frostaura.net>", false)]
    [InlineData("a@b.com, c@d.com", false)]
    [InlineData("a b@c.com", false)]
    [InlineData("@example.com", false)]
    public void Email_shape_is_one_plain_address_with_a_dotted_domain(string email, bool valid) =>
        Assert.Equal(valid, SettingsEndpoints.IsPlausibleEmail(email));

    [Fact]
    public void An_address_over_254_characters_is_refused() =>
        Assert.False(SettingsEndpoints.IsPlausibleEmail(new string('a', 250) + "@x.io"));

    [Fact]
    public void Trusted_proxies_default_to_loopback_and_the_docker_bridge_range()
    {
        var options = ValidatedOptions.From<TrustedProxyOptions>(new ConfigurationBuilder().Build());

        Assert.Empty(options.Problems);
        Assert.Contains(options.Networks, n => n.Contains(System.Net.IPAddress.Parse("172.18.0.5")));
        Assert.Contains(options.Networks, n => n.Contains(System.Net.IPAddress.Loopback));
        Assert.DoesNotContain(options.Networks, n => n.Contains(System.Net.IPAddress.Parse("192.168.0.148")));
    }

    [Fact]
    public void A_malformed_trusted_proxy_entry_fails_startup_without_echoing_it()
    {
        var options = ValidatedOptions.From<TrustedProxyOptions>(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [TrustedProxyOptions.NetworksKey] = "10.0.0.0/8,secret-looking-value" }).Build());

        var problem = Assert.Single(options.Problems);
        Assert.Contains(TrustedProxyOptions.NetworksKey, problem, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-looking-value", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void The_key_ring_certificate_loads_and_a_missing_one_fails_naming_the_key()
    {
        var dir = Directory.CreateTempSubdirectory("reel-keyring-");
        try
        {
            var path = Path.Combine(dir.FullName, "keyring.pfx");
            using (var rsa = RSA.Create(2048))
            {
                var request = new CertificateRequest("CN=reel-keyring-test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
                File.WriteAllBytes(path, certificate.Export(X509ContentType.Pkcs12, "test-password"));
            }

            var loaded = ValidatedOptions.From<KeyRingOptions>(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                [KeyRingOptions.CertPathKey] = path,
                [KeyRingOptions.CertPasswordKey] = "test-password",
            }).Build());
            Assert.Empty(loaded.Problems);
            Assert.True(loaded.Certificate!.HasPrivateKey);

            var wrongPassword = ValidatedOptions.From<KeyRingOptions>(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                [KeyRingOptions.CertPathKey] = path,
                [KeyRingOptions.CertPasswordKey] = "not-it",
            }).Build());
            Assert.Contains(KeyRingOptions.CertPathKey, Assert.Single(wrongPassword.Problems), StringComparison.Ordinal);
            Assert.DoesNotContain("not-it", wrongPassword.Problems[0], StringComparison.Ordinal);

            var missing = ValidatedOptions.From<KeyRingOptions>(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                [KeyRingOptions.OldCertPathsKey] = Path.Combine(dir.FullName, "gone.pfx"),
            }).Build());
            Assert.Contains(KeyRingOptions.OldCertPathsKey, Assert.Single(missing.Problems), StringComparison.Ordinal);

            Assert.Null(ValidatedOptions.From<KeyRingOptions>(new ConfigurationBuilder().Build()).Certificate);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData("1.0.0+0a1b2c3d", "0a1b2c3d")]
    [InlineData("1.0.0", "unknown")]
    [InlineData("1.0.0+", "unknown")]
    [InlineData(null, "unknown")]
    public void The_health_commit_is_the_sha_after_the_plus(string? informational, string expected) =>
        Assert.Equal(expected, ReleaseInfo.Parse(informational));

    /// <summary>
    /// The SDK stamps SourceRevisionId from git on a local build; the image gets it from GIT_SHA. A
    /// <c>git archive</c> build outside any checkout is given neither and says "unknown" rather than
    /// failing the suite (HREV1-PLAT-6) — but whatever this build was stamped with, the API reports it.
    /// </summary>
    [Fact]
    public void A_build_reports_the_commit_it_was_stamped_with()
    {
        Assert.Equal(BuildProvenance.StampedCommit, ReleaseInfo.Commit);
        if (BuildProvenance.MustBeStamped)
        {
            Assert.Matches(BuildProvenance.FullSha, ReleaseInfo.Commit);
        }
        else
        {
            Assert.Equal("unknown", ReleaseInfo.Commit);
        }
    }

    [Fact]
    public void A_checkout_is_found_by_its_git_directory_or_a_worktrees_git_file()
    {
        var root = Directory.CreateTempSubdirectory("reel-provenance-");
        try
        {
            var deep = Directory.CreateDirectory(Path.Combine(root.FullName, "a", "bin", "Release"));
            Assert.False(BuildProvenance.FindCheckout(deep.FullName));

            File.WriteAllText(Path.Combine(root.FullName, "a", ".git"), "gitdir: /elsewhere/worktrees/a");
            Assert.True(BuildProvenance.FindCheckout(deep.FullName));

            File.Delete(Path.Combine(root.FullName, "a", ".git"));
            Directory.CreateDirectory(Path.Combine(root.FullName, ".git"));
            Assert.True(BuildProvenance.FindCheckout(deep.FullName));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }
}
