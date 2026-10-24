using FrostAura.Reel.Application.Auth;
using FrostAura.Reel.Application.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace FrostAura.Reel.Tests.Auth;

/// <summary>
/// The typed-options pattern (MOD-2), pinned through its first user. What matters is the split:
/// an ABSENT key takes the documented default, a PRESENT-but-wrong key fails the host at start
/// with the key named — never silently becomes the default.
/// </summary>
public class SessionOptionsTests
{
    private const string Secret = "unit-test-secret-at-least-32-chars!!";

    private static IConfiguration Config(params (string Key, string? Value)[] settings) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build();

    [Fact]
    public void Absent_optional_keys_take_their_defaults()
    {
        var options = ValidatedOptions.From<SessionOptions>(Config(("JWT_SECRET", Secret)));

        Assert.Empty(options.Problems);
        Assert.Equal("frostaura-reel", options.Issuer);
        Assert.Equal("frostaura-reel-app", options.Audience);
        Assert.Equal(15, options.AccessMinutes);
        Assert.Equal(30, options.RefreshDays);
    }

    [Fact]
    public void Present_keys_are_read()
    {
        var options = ValidatedOptions.From<SessionOptions>(Config(
            ("JWT_SECRET", Secret),
            ("JWT_ISSUER", "issuer-x"),
            ("JWT_AUDIENCE", "audience-y"),
            ("JWT_ACCESS_TOKEN_EXPIRY_MINUTES", "60"),
            ("JWT_REFRESH_TOKEN_EXPIRY_DAYS", " 7 ")));

        Assert.Empty(options.Problems);
        Assert.Equal(("issuer-x", "audience-y", 60, 7), (options.Issuer, options.Audience, options.AccessMinutes, options.RefreshDays));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_missing_secret_is_a_problem(string? secret)
    {
        var options = ValidatedOptions.From<SessionOptions>(Config(("JWT_SECRET", secret)));

        Assert.Equal(["JWT_SECRET is required."], options.Problems);
    }

    [Theory]
    [InlineData("JWT_ACCESS_TOKEN_EXPIRY_MINUTES", "fifteen")]
    [InlineData("JWT_ACCESS_TOKEN_EXPIRY_MINUTES", "15.5")]
    [InlineData("JWT_ACCESS_TOKEN_EXPIRY_MINUTES", "0")]
    [InlineData("JWT_ACCESS_TOKEN_EXPIRY_MINUTES", "1441")]
    [InlineData("JWT_REFRESH_TOKEN_EXPIRY_DAYS", "-1")]
    [InlineData("JWT_REFRESH_TOKEN_EXPIRY_DAYS", "366")]
    [InlineData("JWT_REFRESH_TOKEN_EXPIRY_DAYS", "30d")]
    public void A_present_but_invalid_number_is_a_problem_naming_the_key(string key, string value)
    {
        var options = ValidatedOptions.From<SessionOptions>(Config(("JWT_SECRET", Secret), (key, value)));

        var problem = Assert.Single(options.Problems);
        Assert.StartsWith(key + " must be", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void A_problem_never_echoes_an_unparseable_value()
    {
        // A mis-pasted secret in the wrong key must not end up in a startup log.
        var options = ValidatedOptions.From<SessionOptions>(Config(
            ("JWT_SECRET", Secret),
            ("JWT_ACCESS_TOKEN_EXPIRY_MINUTES", "sk-or-v1-looks-like-a-key")));

        Assert.DoesNotContain(options.Problems, p => p.Contains("sk-or-v1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_invalid_value_stops_the_host_at_start_with_every_problem_named()
    {
        using var host = Host.CreateDefaultBuilder()
            .ConfigureAppConfiguration(c => c.Sources.Clear())
            .ConfigureAppConfiguration(c => c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["JWT_ACCESS_TOKEN_EXPIRY_MINUTES"] = "never",
            }))
            .ConfigureServices(s => s.AddValidatedOptions<SessionOptions>())
            .Build();

        var failure = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());

        Assert.Contains(failure.Failures, f => f == "SessionOptions: JWT_SECRET is required.");
        Assert.Contains(failure.Failures, f => f.StartsWith("SessionOptions: JWT_ACCESS_TOKEN_EXPIRY_MINUTES must be", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_valid_configuration_starts_and_binds_through_di()
    {
        using var host = Host.CreateDefaultBuilder()
            .ConfigureAppConfiguration(c => c.Sources.Clear())
            .ConfigureAppConfiguration(c => c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["JWT_SECRET"] = Secret,
                ["JWT_ACCESS_TOKEN_EXPIRY_MINUTES"] = "20",
            }))
            .ConfigureServices(s => s.AddValidatedOptions<SessionOptions>())
            .Build();

        await host.StartAsync();
        try
        {
            Assert.Equal(20, host.Services.GetRequiredService<IOptions<SessionOptions>>().Value.AccessMinutes);
        }
        finally
        {
            await host.StopAsync();
        }
    }
}
