using System.Text.RegularExpressions;

namespace FrostAura.Reel.Tests.Harness;

/// <summary>
/// A source-level guard: a class in the <c>postgres</c> collection that seeds
/// <c>TitleEmbeddings</c> must inherit <c>IntegrationTests.EmbeddedCatalogTestBase</c>, which
/// removes those rows again when the test ends.
///
/// <para><b>Why this needs a guard rather than a convention.</b> Leaving embedding rows behind is
/// harmless to the class that leaves them and harmful only to some other class, on some other run,
/// through a mechanism neither file mentions: the vectors share one HNSW index, an HNSW scan
/// filters a bounded window of graph candidates, and so a sibling's rows change which titles an
/// unrelated account's search ever gets to consider. The author of the offending fixture sees a
/// green test; the bill lands on whoever owns the flakiest file in the suite. Three fixtures had
/// each independently diagnosed and patched their own half of this before it was written down —
/// <c>CandidateRetrieverTests</c>, <c>CrossReferenceBacktestTests</c>,
/// <c>CrossReferenceJobTests</c> — and <c>LiveSearchExpansionTests</c> broke again the moment a
/// fourth embedded fixture landed. Per-fixture care demonstrably does not compose.</para>
///
/// <para><b>The guard matches any MENTION of the table, not only the two seeding shapes.</b> It
/// used to match the seeding shapes alone, and the two classes that slipped through cost
/// <c>LiveSearchExpansionTests</c> its determinism for a second time: <c>EnrichCatalogJobHandler</c>
/// and the catalogue follow-through embed what they hydrate, through PRODUCTION code, over the
/// whole shared catalogue — a thousand vectors on a measured run, written by a fixture whose own
/// source contains no <c>TitleEmbeddings.Add</c> for a regex to find. Both files named the table
/// (they assert on what the sweep wrote), so the wider rule catches them, and it costs nothing:
/// every other class in the collection that mentions the table already inherits the base.</para>
///
/// <para>The blind spot that remains is a class that leaves vectors behind and never names the
/// table at all. Nothing source-level can see that, which is why several such classes carry an
/// explicit <c>&lt;remarks&gt;</c> saying they inherit the base without seeding — and why
/// <see cref="IntegrationTests.EmbeddedCatalogTestBase"/> repairs the index on EVERY teardown
/// rather than only its own.</para>
///
/// <para>It is a ratchet, not a verdict: <see cref="Exempt"/> holds files that seed embeddings and
/// legitimately do not inherit the base, each with the reason. A row without a reason is a
/// suppression, and the reason is the point.</para>
/// </summary>
public class EmbeddedFixtureIsolationTests
{
    /// <summary>
    /// Any mention of the embeddings table. Deliberately wider than the two literal seeding shapes:
    /// a fixture that drives a production sweep leaves vectors behind without ever writing one, and
    /// asserting on what the sweep wrote is the trace it does leave. See the class remark.
    /// </summary>
    private static readonly Regex Seeds = new(
        @"\bTitleEmbeddings?\b", RegexOptions.Compiled);

    private static readonly Regex InPostgresCollection = new(
        @"\[Collection\(""postgres""\)\]", RegexOptions.Compiled);

    private static readonly Regex InheritsBase = new(
        @":\s*EmbeddedCatalogTestBase\b", RegexOptions.Compiled);

    /// <summary>
    /// Files that seed embeddings in the postgres collection and are deliberately not on the base,
    /// keyed by path relative to the test project. Empty is the intended state.
    /// </summary>
    private static readonly Dictionary<string, string> Exempt = new(StringComparer.Ordinal)
    {
        ["IntegrationTests/SyntheticWeek/DatabaseClockTests.cs"] =
            "Seeds its one embedding on a database of its own (FreshDatabaseAsync), never on the shared catalogue, and drops that database afterwards.",
    };

    [Fact]
    public void Every_embedded_fixture_cleans_up_after_itself()
    {
        var root = TestProjectRoot();
        var findings = new List<string>();

        foreach (var file in Directory
            .EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .OrderBy(f => f, StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/');
            if (relative is "IntegrationTests/EmbeddedCatalogTestBase.cs" or "Harness/EmbeddedFixtureIsolationTests.cs")
            {
                continue;
            }

            var source = File.ReadAllText(file);
            if (!Seeds.IsMatch(source)
                || !InPostgresCollection.IsMatch(source)
                || InheritsBase.IsMatch(source)
                || Exempt.ContainsKey(relative))
            {
                continue;
            }

            findings.Add(relative);
        }

        Assert.True(
            findings.Count == 0,
            "These classes seed or otherwise "
            + "touch TitleEmbeddings in the shared postgres catalogue without "
            + "inheriting EmbeddedCatalogTestBase, so their vectors stay in the HNSW index for every "
            + "test that runs afterwards and change what an unrelated account's semantic search "
            + "finds. Inherit the base — it removes exactly the rows the test added — or add the "
            + "file to EmbeddedFixtureIsolationTests.Exempt WITH THE REASON.\n\n"
            + string.Join("\n", findings));
    }

    /// <summary>Stale exemptions are worse than none: they read as a considered decision about a
    /// file that no longer exists or no longer names the table.</summary>
    [Fact]
    public void No_exemption_outlives_the_file_it_names()
    {
        var root = TestProjectRoot();
        var stale = Exempt.Keys
            .Where(k =>
            {
                var path = Path.Combine(root, k.Replace('/', Path.DirectorySeparatorChar));
                return !File.Exists(path) || !Seeds.IsMatch(File.ReadAllText(path));
            })
            .ToList();

        Assert.True(
            stale.Count == 0,
            "EmbeddedFixtureIsolationTests.Exempt names files that no longer exist or no longer "
            + "mention TitleEmbeddings. Delete the rows:\n" + string.Join("\n", stale));
    }

    private static string TestProjectRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "FrostAura.Reel.Tests.csproj")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Could not locate the test project root.");
    }
}
