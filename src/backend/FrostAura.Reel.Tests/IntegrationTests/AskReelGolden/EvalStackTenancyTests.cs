using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Tests.Harness;
using Microsoft.Extensions.Configuration;

namespace FrostAura.Reel.Tests.IntegrationTests.AskReelGolden;

/// <summary>
/// HREV4-PLAT-02: the eval stack measures the app as live runs it, and live fails closed on an unscoped
/// read (<see cref="UnscopedReadPolicy.DefaultMode"/> is Throw; the live descriptors leave
/// <c>Tenancy__UnscopedReads</c> blank). The compose eval stack pinned it to Warn before Throw became the
/// default, so a path that throws on live only logged there, and nothing noticed: the parity tests check
/// that a key is present, never its value. This holds both eval launchers to the live default — the compose
/// file by its literal value, read the way the API reads it, and <c>scripts/eval/run-api.sh</c> by never
/// setting the key — and holds the eval value to a literal, so an operator's shell carrying Warn cannot
/// reach the eval API through interpolation either.
/// </summary>
public class EvalStackTenancyTests
{
    private const string Key = "Tenancy__UnscopedReads";

    [Fact]
    public void The_compose_eval_stack_runs_the_live_tenancy_default()
    {
        var eval = ComposeDescriptor.Load("docker-compose.eval.yml");

        Assert.Equal(UnscopedReadPolicy.DefaultMode, EvalTenancyMode(eval));
        Assert.Equal(UnscopedReadMode.Throw, EvalTenancyMode(eval));
    }

    [Fact]
    public void The_live_descriptors_leave_the_mode_to_the_code_default()
    {
        // The reference the eval is held to: blank on every live descriptor, so the code default (Throw) runs.
        foreach (var descriptor in new[] { "docker-compose.yml", "docker-compose.prod.yml" })
        {
            var compose = ComposeDescriptor.Load(descriptor);
            Assert.Equal("${Tenancy__UnscopedReads:-}", compose.Environment(ComposeEnvParityTests.ApiService[descriptor])[Key]);
        }
    }

    [Fact]
    public void The_slot_launcher_never_sets_the_tenancy_mode()
    {
        var runApi = File.ReadAllText(Path.Combine(ComposeDescriptor.RepositoryRoot(), "scripts", "eval", "run-api.sh"));

        Assert.DoesNotContain(Key, runApi, StringComparison.Ordinal);
        Assert.DoesNotContain("Tenancy:UnscopedReads", runApi, StringComparison.Ordinal);
    }

    [Theory]
    // Every way back to a permissive mode the check must catch, not just the one the file had.
    [InlineData("\"Warn\"", UnscopedReadMode.Warn)]
    [InlineData("Warn", UnscopedReadMode.Warn)]
    [InlineData("\"allow\"", UnscopedReadMode.Allow)]
    [InlineData("\"Throw\"", UnscopedReadMode.Throw)]
    [InlineData("\"\"", UnscopedReadMode.Throw)]
    [InlineData("''", UnscopedReadMode.Throw)]
    public void The_check_reads_a_literal_value_as_the_api_would(string value, UnscopedReadMode expected)
    {
        var compose = ComposeDescriptor.Parse("docker-compose.eval.yml", $"services:\n  eval-api:\n    environment:\n      {Key}: {value}\n");

        Assert.Equal(expected, EvalTenancyMode(compose));
    }

    [Theory]
    [InlineData("${Tenancy__UnscopedReads:-}")]
    [InlineData("${Tenancy__UnscopedReads:-Throw}")]
    [InlineData("\"${EVAL_TENANCY:-Throw}\"")]
    public void An_interpolated_value_is_refused_because_a_shell_carrying_Warn_would_reach_the_eval_api(string value)
    {
        var compose = ComposeDescriptor.Parse("docker-compose.eval.yml", $"services:\n  eval-api:\n    environment:\n      {Key}: {value}\n");

        var failure = Assert.Throws<InvalidOperationException>(() => EvalTenancyMode(compose));
        Assert.Contains("literal", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_missing_key_is_refused_rather_than_read_as_the_default()
    {
        // The parity rule (K-5) wants every key the code reads plumbed; absence is a different defect, named.
        var compose = ComposeDescriptor.Parse("docker-compose.eval.yml", "services:\n  eval-api:\n    environment:\n      OTHER: x\n");

        var failure = Assert.Throws<InvalidOperationException>(() => EvalTenancyMode(compose));
        Assert.Contains(Key, failure.Message, StringComparison.Ordinal);
    }

    /// <summary>The mode the eval API would run in, through the API's own parser of the value.</summary>
    private static UnscopedReadMode EvalTenancyMode(ComposeDescriptor compose)
    {
        if (!compose.Environment("eval-api").TryGetValue(Key, out var raw))
        {
            throw new InvalidOperationException($"{compose.RelativePath} does not plumb {Key} into eval-api");
        }

        if (raw.Contains("${", StringComparison.Ordinal) || raw.Contains('$', StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"{compose.RelativePath} must set {Key} as a literal, never interpolated: '{raw}'");
        }

        var value = raw.Length >= 2 && (raw[0] == '"' || raw[0] == '\'') && raw[^1] == raw[0] ? raw[1..^1] : raw;
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [UnscopedReadPolicy.ConfigurationKey] = value })
            .Build();
        return UnscopedReadPolicy.FromConfiguration(configuration, logger: null).Mode;
    }
}
