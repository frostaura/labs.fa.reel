using System.Diagnostics;
using System.Reflection;
using FrostAura.Reel.Tests.IntegrationTests;

namespace FrostAura.Reel.Tests.Harness;

/// <summary>
/// CI runs every database-backed test against a database, and runs each test exactly once (CR-11,
/// R1-34).
///
/// <para><b>What happened.</b> <c>make ci-integration</c> selected the <c>IntegrationTests</c>
/// namespace and <c>make ci-test</c> everything else. Six Postgres classes lived outside that
/// namespace, so CI ran them only in the unit pass — no connection string, every test early-returned
/// on <c>fixture.Available</c>, and the job reported them green having executed nothing.</para>
///
/// <para><b>How this holds.</b> Database-backed is decided from the compiled assembly — a class
/// consuming a database fixture by collection, class fixture or constructor — not from a list. The
/// two filters are produced by the same script <c>make</c> runs, and evaluated here against every
/// test class.</para>
/// </summary>
public class CiTestSelectionTests
{
    /// <summary>Fixtures that open the test database. A class consuming one needs Postgres.</summary>
    private static readonly Type[] DatabaseFixtures = [typeof(PostgresFixture), typeof(JobRunnerDatabaseFixture), typeof(ReelApiFactory)];

    /// <summary>Fixtures known to need no database. Every fixture in the assembly is one or the other.</summary>
    private static readonly Type[] NonDatabaseFixtures = [];

    private const string IntegrationNamespace = "FrostAura.Reel.Tests.IntegrationTests";

    [Fact]
    public void Every_fixture_the_suite_uses_is_classified()
    {
        // A new fixture type would otherwise be treated as database-free without anyone deciding so.
        var unclassified = FixtureTypes()
            .Where(t => !DatabaseFixtures.Contains(t) && !NonDatabaseFixtures.Contains(t))
            .Select(t => t.FullName)
            .ToList();

        Assert.True(unclassified.Count == 0, "Classify each fixture as database or not in CiTestSelectionTests: " + string.Join(", ", unclassified));
    }

    [Fact]
    public void The_list_names_exactly_the_database_classes_outside_the_integration_namespace()
    {
        var expected = TestClasses()
            .Where(NeedsDatabase)
            .Where(t => !InIntegrationNamespace(t))
            .Select(t => t.FullName!)
            .Order(StringComparer.Ordinal)
            .ToList();

        // Non-vacuous: the six CR-11 found.
        Assert.Contains("FrostAura.Reel.Tests.CrossReference.LeakageTests", expected);
        Assert.Contains("FrostAura.Reel.Tests.Ingestion.ShowProgressReconcileTests", expected);
        Assert.Contains("FrostAura.Reel.Tests.Sync.OutboxDispatcherHistoryTests", expected);

        Assert.Equal(expected, ListedClasses().Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Every_test_class_is_selected_by_exactly_one_ci_pass_and_every_database_class_by_the_integration_pass()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var unit = Filter("unit");
        var integration = Filter("integration");
        var wrong = new List<string>();

        var classes = TestClasses().ToList();
        Assert.True(classes.Count > 200, $"Expected the whole suite; found {classes.Count} test classes.");

        foreach (var type in classes)
        {
            var test = TestName(type);
            var inUnit = unit.Matches(test);
            var inIntegration = integration.Matches(test);

            if (inUnit == inIntegration)
            {
                wrong.Add($"{type.FullName}: {(inUnit ? "selected by both passes" : "selected by neither pass")}");
            }
            else if (NeedsDatabase(type) && !inIntegration)
            {
                wrong.Add($"{type.FullName}: needs Postgres but runs only in the unit pass, where it executes nothing");
            }
        }

        Assert.True(wrong.Count == 0, string.Join(Environment.NewLine, wrong));
    }

    [Theory]
    [InlineData("ci-test", "unit")]
    [InlineData("ci-integration", "integration")]
    public void The_makefile_selects_each_half_through_the_script_and_nothing_else(string target, string half)
    {
        // The proof above is about the script's filters; it is worth nothing if a target filters by hand.
        var makefile = File.ReadAllText(Path.Combine(ComposeDescriptor.RepositoryRoot(), "Makefile")).Replace("\r\n", "\n", StringComparison.Ordinal);
        var recipe = makefile.Split("\n" + target + ":\n", 2)[1].Split("\n\n", 2)[0];

        Assert.Contains($"filter=$$(sh deploy/ci/test-filter.sh {half}) || exit 1;", recipe, StringComparison.Ordinal);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(recipe, "--filter "));
        Assert.Contains("--filter \"$$filter\"", recipe, StringComparison.Ordinal);
    }

    [Fact]
    public void A_listed_class_is_matched_at_its_own_boundary_only()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var integration = Filter("integration");

        Assert.True(integration.Matches("FrostAura.Reel.Tests.CrossReference.LeakageTests.Some_test"));
        Assert.False(integration.Matches("FrostAura.Reel.Tests.CrossReference.LeakageTestsExtra.Some_test"));
        Assert.True(integration.Matches(IntegrationNamespace + ".AnyTests.Some_test"));
        Assert.False(Filter("unit").Matches(IntegrationNamespace + ".AnyTests.Some_test"));
    }

    [Fact]
    public void The_database_rule_sees_each_way_a_class_can_reach_postgres()
    {
        // By collection, by class fixture, and a pure class that reaches nothing.
        Assert.True(NeedsDatabase(typeof(FrostAura.Reel.Tests.CrossReference.LeakageTests)));
        Assert.True(NeedsDatabase(typeof(DevSurfaceLockdownTests)));
        Assert.False(NeedsDatabase(typeof(CiGateScriptTests)));
        Assert.False(NeedsDatabase(typeof(CiTestSelectionTests)));
    }

    private static bool InIntegrationNamespace(Type type) =>
        type.Namespace is { } ns && (ns == IntegrationNamespace || ns.StartsWith(IntegrationNamespace + ".", StringComparison.Ordinal));

    private static IEnumerable<Type> TestClasses() =>
        typeof(CiTestSelectionTests).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && !t.IsNested)
            .Where(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Any(m => m.GetCustomAttributes<FactAttribute>(inherit: true).Any()));

    private static bool NeedsDatabase(Type type)
    {
        var fixtures = ClassFixtures(type)
            .Concat(CollectionFixtures(type))
            .Concat(type.GetConstructors().SelectMany(c => c.GetParameters()).Select(p => p.ParameterType));
        return fixtures.Any(f => DatabaseFixtures.Any(d => d.IsAssignableFrom(f)));
    }

    private static IEnumerable<Type> ClassFixtures(Type type) =>
        type.GetInterfaces()
            .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IClassFixture<>))
            .Select(i => i.GetGenericArguments()[0]);

    private static IEnumerable<Type> CollectionFixtures(Type type)
    {
        var name = type.GetCustomAttributesData()
            .Where(a => a.AttributeType == typeof(CollectionAttribute))
            .Select(a => a.ConstructorArguments[0].Value as string)
            .FirstOrDefault();
        if (name is null)
        {
            return [];
        }

        var definition = typeof(CiTestSelectionTests).Assembly.GetTypes().SingleOrDefault(t => t.GetCustomAttributesData()
            .Any(a => a.AttributeType == typeof(CollectionDefinitionAttribute) && a.ConstructorArguments.Count > 0 && (string?)a.ConstructorArguments[0].Value == name));
        Assert.True(definition is not null, $"{type.FullName} names collection '{name}', which has no [CollectionDefinition].");

        return definition!.GetInterfaces()
            .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(ICollectionFixture<>))
            .Select(i => i.GetGenericArguments()[0]);
    }

    private static IEnumerable<Type> FixtureTypes() =>
        typeof(CiTestSelectionTests).Assembly.GetTypes()
            .SelectMany(t => t.GetInterfaces())
            .Where(i => i.IsGenericType && (i.GetGenericTypeDefinition() == typeof(IClassFixture<>) || i.GetGenericTypeDefinition() == typeof(ICollectionFixture<>)))
            .Select(i => i.GetGenericArguments()[0])
            .Distinct();

    /// <summary>A name the filter sees for one of the class's tests: VSTest's FullyQualifiedName is class + "." + method.</summary>
    private static string TestName(Type type) => type.FullName + ".Any_test";

    private static IEnumerable<string> ListedClasses() =>
        File.ReadAllLines(Path.Combine(ComposeDescriptor.RepositoryRoot(), "deploy", "ci", "database-test-classes.txt"))
            .Select(line => line.Split('#', 2)[0].Trim())
            .Where(line => line.Length > 0);

    private static VsTestFilter Filter(string half)
    {
        var start = new ProcessStartInfo("sh") { RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Path.Combine(ComposeDescriptor.RepositoryRoot(), "deploy", "ci", "test-filter.sh"));
        start.ArgumentList.Add(half);

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();

        Assert.True(process.ExitCode == 0, $"test-filter.sh {half} failed: {error}");
        return VsTestFilter.Parse(output.Trim());
    }

    /// <summary>
    /// The subset of VSTest's filter grammar the script emits: <c>FullyQualifiedName~X</c> or
    /// <c>FullyQualifiedName!~X</c> terms, all joined by <c>|</c> or all by <c>&amp;</c>. Anything
    /// else fails the parse rather than being guessed at.
    /// </summary>
    private sealed record VsTestFilter(bool All, IReadOnlyList<(bool Negated, string Value)> Terms)
    {
        public static VsTestFilter Parse(string text)
        {
            var any = text.Contains('|', StringComparison.Ordinal);
            var all = text.Contains('&', StringComparison.Ordinal);
            Assert.False(any && all, $"Mixed | and & in one filter: {text}");

            var terms = text.Split(any ? '|' : '&').Select(term =>
            {
                if (term.StartsWith("FullyQualifiedName!~", StringComparison.Ordinal))
                {
                    return (true, term["FullyQualifiedName!~".Length..]);
                }

                Assert.StartsWith("FullyQualifiedName~", term, StringComparison.Ordinal);
                return (false, term["FullyQualifiedName~".Length..]);
            }).ToList();

            return new VsTestFilter(!any, terms);
        }

        public bool Matches(string fullyQualifiedName)
        {
            bool Term((bool Negated, string Value) t) => fullyQualifiedName.Contains(t.Value, StringComparison.Ordinal) != t.Negated;
            return All ? Terms.All(Term) : Terms.Any(Term);
        }
    }
}
