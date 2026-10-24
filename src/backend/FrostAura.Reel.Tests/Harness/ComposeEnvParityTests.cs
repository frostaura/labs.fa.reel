using System.Text.RegularExpressions;

namespace FrostAura.Reel.Tests.Harness;

/// <summary>
/// Every configuration key the code reads reaches the backend container in every descriptor (K-5).
///
/// <para><b>What happened.</b> Compose passes the backend an explicit <c>environment:</c>
/// allow-list, while a host-side <c>dotnet run</c> reads <c>.env</c> directly through
/// <c>DotEnv.Load</c>. The two paths diverged: <c>.env</c> set <c>LLM_XREF_DAILY_CALLS=40</c>, the
/// descriptors never mentioned it, and the live container ran the code default of 80 — along with
/// every other LLM ceiling, calibrator switch and interleaving knob the founder believed he had set.
/// Nothing failed and nothing logged; the setting simply never arrived.</para>
///
/// <para><b>How the list is built.</b> Not by hand. The production sources are scanned for the two
/// shapes every key in this codebase takes — an <c>UPPER_SNAKE</c> string literal, or a
/// <c>Section:Key</c> literal read through configuration — so a key added next month is held to the
/// rule without anyone remembering to list it. The only hand-written lists are the exceptions below,
/// each with its reason.</para>
///
/// <para><b>The four descriptors</b> are the dev stack, the prod file, the Portainer stack and WS-7's
/// eval stack. The eval file is checked as soon as it exists in the tree.</para>
/// </summary>
public partial class ComposeEnvParityTests
{
    /// <summary>Descriptors whose backend runs Development (QA helpers allowed).</summary>
    private static readonly string[] DevDescriptors = ["docker-compose.yml", "docker-compose.eval.yml"];

    /// <summary>Descriptors whose backend runs Production.</summary>
    private static readonly string[] ProdDescriptors = ["docker-compose.prod.yml", "deploy/portainer-stack.yml"];

    /// <summary>
    /// The API service in each descriptor. WS-7's eval stack names its API <c>eval-api</c>; a lookup
    /// that assumed <c>backend</c> there would find no environment block at all and report every key
    /// missing, so the service name is part of the descriptor's identity, not a constant.
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, string> ApiService = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["docker-compose.yml"] = "backend",
        ["docker-compose.prod.yml"] = "backend",
        ["deploy/portainer-stack.yml"] = "backend",
        ["docker-compose.eval.yml"] = "eval-api",
    };

    private static IReadOnlyDictionary<string, string> ApiEnvironment(string descriptor) =>
        ApiEnvironment(ComposeDescriptor.Load(descriptor), descriptor);

    private static IReadOnlyDictionary<string, string> ApiEnvironment(ComposeDescriptor compose, string descriptor)
    {
        var service = ApiService[descriptor];
        Assert.True(compose.Services.Contains(service), $"{descriptor} has no '{service}' service; the parity rules check that service's environment.");
        var environment = compose.Environment(service);
        Assert.True(environment.Count > 0, $"{descriptor}: the '{service}' service has no environment: block.");
        return environment;
    }

    /// <summary>Read by the code, deliberately NOT plumbed into any container.</summary>
    private static readonly Dictionary<string, string> NeverPlumbed = new(StringComparer.Ordinal)
    {
        ["OPENAI_API_KEY"] = "legacy fallback in the embeddings adapter; EMBEDDINGS_API_KEY is the plumbed key",
        ["REEL_DESIGN_CONNECTION"] = "design-time EF factory for `dotnet ef`; never read inside a container",
    };

    /// <summary>
    /// QA-only keys: required in the Development descriptors, FORBIDDEN in the production ones. A
    /// production container that carries them is one environment flip from minting Founder sessions.
    /// </summary>
    private static readonly string[] DevelopmentOnly =
    [
        "QA_HELPERS_ENABLED",
        "TRAKT_ACCESS_TOKEN",
        "TRAKT_REFRESH_TOKEN",
        "TRAKT_DEV_PROFILE",
        "TestHelpers__ModelStubMode",
    ];

    /// <summary>Keys a descriptor may carry that no Reel code reads (the framework reads them).</summary>
    private static readonly string[] FrameworkKeys = ["ASPNETCORE_ENVIRONMENT", "ASPNETCORE_URLS"];

    /// <summary>Section prefixes of literals that look like config keys but are EF/Npgsql annotations.</summary>
    private static readonly string[] NotConfigurationPrefixes = ["Npgsql", "Relational"];

    public static TheoryData<string> AllDescriptors()
    {
        var data = new TheoryData<string>();
        foreach (var descriptor in DevDescriptors.Concat(ProdDescriptors).Where(ComposeDescriptor.Exists))
        {
            data.Add(descriptor);
        }

        return data;
    }

    [Fact]
    public void The_scan_finds_the_keys_this_guard_exists_for()
    {
        // Non-vacuous: if the scan silently found nothing, every parity check would pass.
        var keys = CodeReadKeys();

        Assert.Contains("LLM_XREF_DAILY_CALLS", keys);
        Assert.Contains("REEL_SEARCH_MODEL", keys);
        Assert.Contains("CALIBRATOR_MODE", keys);
        Assert.Contains("Background__JobRunner", keys);
        Assert.Contains("ConnectionStrings__Postgres", keys);
        Assert.Contains("BACKUP_REQUEST_DIR", keys);
        Assert.True(keys.Count >= 70, $"Expected at least 70 configuration keys in the code; the scan found {keys.Count}.");
    }

    [Fact]
    public void The_three_owned_descriptors_exist()
    {
        foreach (var descriptor in new[] { "docker-compose.yml", "docker-compose.prod.yml", "deploy/portainer-stack.yml" })
        {
            Assert.True(ComposeDescriptor.Exists(descriptor), $"{descriptor} is missing.");
        }
    }

    [Theory]
    [MemberData(nameof(AllDescriptors))]
    public void Every_key_the_code_reads_reaches_the_backend(string descriptor)
    {
        var environment = ApiEnvironment(descriptor);
        var production = ProdDescriptors.Contains(descriptor);

        var missing = CodeReadKeys()
            .Where(key => !NeverPlumbed.ContainsKey(key))
            .Where(key => !(production && DevelopmentOnly.Contains(key)))
            .Where(key => !environment.ContainsKey(key))
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(
            missing.Count == 0,
            $"{descriptor}: the backend's environment: block is missing {missing.Count} key(s) the code reads. A key absent here never "
            + "reaches the container — it silently runs the code default (K-5). Add each with its default (blank means \"code default\" only "
            + "for keys read through FeatureFlags), to all four descriptors and .env.example:" + System.Environment.NewLine
            + string.Join(System.Environment.NewLine, missing));
    }

    [Theory]
    [MemberData(nameof(AllDescriptors))]
    public void Every_key_the_backend_is_given_is_read_by_something(string descriptor)
    {
        // The other direction: a key nothing reads is a setting the founder can change to no effect.
        var codeKeys = CodeReadKeys();
        var dead = ApiEnvironment(descriptor).Keys
            .Where(key => !codeKeys.Contains(key) && !FrameworkKeys.Contains(key))
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(dead.Count == 0, $"{descriptor}: the backend is given key(s) no code reads: {string.Join(", ", dead)}");
    }

    [Theory]
    [MemberData(nameof(AllDescriptors))]
    public void Production_descriptors_carry_no_qa_key(string descriptor)
    {
        if (!ProdDescriptors.Contains(descriptor))
        {
            return;
        }

        var environment = ApiEnvironment(descriptor);
        var present = DevelopmentOnly.Where(environment.ContainsKey).ToList();

        Assert.True(present.Count == 0, $"{descriptor} passes QA-only key(s) to a production backend: {string.Join(", ", present)}");
        Assert.Equal("Production", environment["ASPNETCORE_ENVIRONMENT"]);
    }

    [Theory]
    [MemberData(nameof(AllDescriptors))]
    public void A_key_read_with_typed_parsing_never_defaults_to_blank(string descriptor)
    {
        // IConfiguration.GetValue<bool|int>("KEY", default) THROWS on an empty string rather than
        // falling back — so `KEY: ${KEY:-}` in a descriptor is a startup crash (or a 500 on the one
        // request that reads it) the moment the variable is unset.
        var environment = ApiEnvironment(descriptor);

        var blank = TypedReadKeys()
            .Where(environment.ContainsKey)
            .Where(key => IsBlankDefault(key, environment[key]))
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(blank.Count == 0, $"{descriptor}: typed key(s) default to blank, which GetValue cannot parse: {string.Join(", ", blank)}");
    }

    [Fact]
    public void Env_example_documents_every_key()
    {
        var example = File.ReadAllText(Path.Combine(ComposeDescriptor.RepositoryRoot(), ".env.example"));
        var documented = EnvExampleKey().Matches(example).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);

        var missing = CodeReadKeys()
            .Where(key => !NeverPlumbed.ContainsKey(key) && !documented.Contains(key))
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(missing.Count == 0, ".env.example does not document: " + string.Join(", ", missing));
    }

    [Fact]
    public void The_parity_check_fails_when_a_key_is_removed()
    {
        // Proves the check above can fail: drop one key from a real descriptor and re-run the rule.
        var environment = new Dictionary<string, string>(ApiEnvironment("docker-compose.prod.yml"), StringComparer.Ordinal);
        Assert.True(environment.Remove("LLM_XREF_DAILY_CALLS"));

        var missing = CodeReadKeys()
            .Where(key => !NeverPlumbed.ContainsKey(key) && !DevelopmentOnly.Contains(key) && !environment.ContainsKey(key))
            .ToList();

        Assert.Equal(["LLM_XREF_DAILY_CALLS"], missing);
    }

    [Fact]
    public void The_api_service_is_found_by_its_own_name_in_a_descriptor_that_does_not_call_it_backend()
    {
        // WS-7's eval file, in the shape it has on its branch: the API is `eval-api`, and its
        // database is published on a literal loopback address.
        var eval = ComposeDescriptor.Parse(
            "docker-compose.eval.yml",
            "services:\n  eval-postgres:\n    image: pgvector/pgvector:pg16\n  eval-api:\n    environment:\n      LLM_XREF_DAILY_CALLS: ${LLM_XREF_DAILY_CALLS:-}\n      ASPNETCORE_ENVIRONMENT: Development\n");

        var environment = ApiEnvironment(eval, "docker-compose.eval.yml");

        Assert.Equal(["ASPNETCORE_ENVIRONMENT", "LLM_XREF_DAILY_CALLS"], environment.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void A_descriptor_without_its_api_service_fails_loudly_rather_than_reporting_every_key_missing()
    {
        var renamed = ComposeDescriptor.Parse("docker-compose.eval.yml", "services:\n  backend:\n    environment:\n      A_KEY: x\n");

        var failure = Assert.ThrowsAny<Exception>(() => ApiEnvironment(renamed, "docker-compose.eval.yml"));

        Assert.Contains("has no 'eval-api' service", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_descriptor_the_rules_know_has_an_api_service_named()
    {
        Assert.Equal(
            DevDescriptors.Concat(ProdDescriptors).Order(StringComparer.Ordinal),
            ApiService.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void A_typed_key_with_digits_in_its_name_is_held_to_the_blank_default_rule()
    {
        // The key class once stopped at [A-Za-z_:], so TRAKT_RATE_BUDGET_PER_5MIN was never checked.
        var match = TypedGetValue().Match("configuration.GetValue(\"TRAKT_RATE_BUDGET_PER_5MIN\", 1000)");

        Assert.True(match.Success);
        Assert.Equal("TRAKT_RATE_BUDGET_PER_5MIN", match.Groups[1].Value);
        // And the real scan sees the real read in DependencyInjection.Security.cs.
        Assert.Contains("TRAKT_RATE_BUDGET_PER_5MIN", TypedReadKeys());
    }

    // ── the scan ────────────────────────────────────────────────────────────────────────────

    /// <summary>Every configuration key the production code reads, in its environment-variable form.</summary>
    internal static IReadOnlySet<string> CodeReadKeys()
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var text in ProductionSources())
        {
            foreach (Match m in UpperSnakeLiteral().Matches(text))
            {
                keys.Add(m.Groups[1].Value);
            }

            foreach (Match m in SectionKeyLiteral().Matches(text))
            {
                if (!NotConfigurationPrefixes.Contains(m.Groups[1].Value))
                {
                    keys.Add(m.Groups[1].Value + "__" + m.Groups[2].Value);
                }
            }
        }

        return keys;
    }

    /// <summary>Keys read through <c>GetValue(…, non-string default)</c>, which cannot parse a blank.</summary>
    internal static IReadOnlySet<string> TypedReadKeys()
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var text in ProductionSources())
        {
            foreach (Match m in TypedGetValue().Matches(text))
            {
                if (!m.Groups[2].Value.TrimStart().StartsWith('"'))
                {
                    keys.Add(m.Groups[1].Value.Replace(":", "__", StringComparison.Ordinal));
                }
            }
        }

        return keys;
    }

    private static bool IsBlankDefault(string key, string value) =>
        value.Length == 0
        || value is "\"\"" or "''"
        || value == "${" + key + ":-}"
        || value == "${" + key + "}";

    private static IEnumerable<string> ProductionSources()
    {
        var backend = Path.Combine(ComposeDescriptor.RepositoryRoot(), "src", "backend");
        foreach (var project in new[] { "FrostAura.Reel.Api", "FrostAura.Reel.Application", "FrostAura.Reel.Domain", "FrostAura.Reel.Infrastructure" })
        {
            foreach (var file in Directory.EnumerateFiles(Path.Combine(backend, project), "*.cs", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(backend, file).Replace('\\', '/');
                if (relative.Contains("/bin/", StringComparison.Ordinal)
                    || relative.Contains("/obj/", StringComparison.Ordinal)
                    || relative.Contains("/Migrations/", StringComparison.Ordinal))
                {
                    continue;
                }

                yield return File.ReadAllText(file);
            }
        }
    }

    [GeneratedRegex(@"""([A-Z][A-Z0-9]*(?:_[A-Z0-9]+)+)""")]
    private static partial Regex UpperSnakeLiteral();

    [GeneratedRegex(@"""([A-Z][A-Za-z]+):([A-Z][A-Za-z]+)""")]
    private static partial Regex SectionKeyLiteral();

    [GeneratedRegex(@"GetValue(?:<[^>]+>)?\(\s*""([A-Za-z0-9_:]+)""\s*,([^)]*)\)")]
    private static partial Regex TypedGetValue();

    [GeneratedRegex(@"^#?\s*([A-Za-z_][A-Za-z0-9_]*)=", RegexOptions.Multiline)]
    private static partial Regex EnvExampleKey();
}
