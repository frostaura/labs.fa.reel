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

    [Theory]
    [InlineData("JWT_ACCESS_TOKEN_EXPIRY_MINUTES", "2000")]
    [InlineData("JWT_ACCESS_TOKEN_EXPIRY_MINUTES", "1234567")]
    [InlineData("JWT_REFRESH_TOKEN_EXPIRY_DAYS", "987654")]
    public void A_problem_never_echoes_an_out_of_range_value(string key, string value)
    {
        // A number is still a value: a PIN or an account number pasted into the wrong key parses.
        var options = ValidatedOptions.From<SessionOptions>(Config(("JWT_SECRET", Secret), (key, value)));

        var problem = Assert.Single(options.Problems);
        Assert.DoesNotContain(value, problem, StringComparison.Ordinal);
    }

    [Fact]
    public void A_secret_is_kept_exactly_as_configured()
    {
        // Trimming would derive a different signing key from the same configured secret.
        var padded = "  " + Secret + "\t";

        var options = ValidatedOptions.From<SessionOptions>(Config(("JWT_SECRET", padded)));

        Assert.Empty(options.Problems);
        Assert.Equal(padded, options.Secret);
    }

    [Theory]
    [InlineData(" 60 ", 60)]
    [InlineData("\t20\n", 20)]
    public void Numbers_tolerate_surrounding_whitespace(string value, int expected)
    {
        var options = ValidatedOptions.From<SessionOptions>(Config(("JWT_SECRET", Secret), ("JWT_ACCESS_TOKEN_EXPIRY_MINUTES", value)));

        Assert.Empty(options.Problems);
        Assert.Equal(expected, options.AccessMinutes);
    }

    [Fact]
    public void The_startup_validator_fails_before_any_host_starts()
    {
        // What Program calls before the pre-migration dump and the migrations: the same validation
        // app.Run() would do, but early enough that an invalid setting changes nothing (MOD-2).
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(Config(("JWT_SECRET", Secret), ("JWT_ACCESS_TOKEN_EXPIRY_MINUTES", "2000")));
        services.AddValidatedOptions<SessionOptions>();
        using var provider = services.BuildServiceProvider();

        var failure = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IStartupValidator>().Validate());

        Assert.Equal(["SessionOptions: JWT_ACCESS_TOKEN_EXPIRY_MINUTES must be between 1 and 1440."], failure.Failures);
    }

    [Fact]
    public void Program_validates_options_before_it_dumps_or_migrates()
    {
        // The call above only helps if Program makes it first. Source order is the contract here:
        // Program's startup block is top-level statements, not a method a test can invoke alone.
        var program = File.ReadAllText(Path.Combine(
            FrostAura.Reel.Tests.Harness.ComposeDescriptor.RepositoryRoot(), "src", "backend", "FrostAura.Reel.Api", "Program.cs"));

        var validate = program.IndexOf("GetService<IStartupValidator>()?.Validate();", StringComparison.Ordinal);
        var dump = program.IndexOf("await PreMigrationBackup.EnsureAsync(", StringComparison.Ordinal);
        var migrate = program.IndexOf("await DatabaseInitializer.InitializeAsync(", StringComparison.Ordinal);

        Assert.True(validate > 0, "Program no longer runs the startup validator explicitly.");
        Assert.True(dump > validate && migrate > validate, "Program must validate options before the pre-migration dump and the migrations.");
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
