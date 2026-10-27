using System.Reflection;
using FrostAura.Reel.Tests.IntegrationTests;

namespace FrostAura.Reel.Tests.Harness;

/// <summary>
/// The quarantine is a claim about a named set of tests, so it is pinned like any other
/// contract: the set may only change deliberately, and the marker may never skip by default.
/// Offline — pure reflection, no database.
/// <para>
/// <b>The set is now EMPTY.</b> Every row left on its merits, none by relaxing an assertion:
/// </para>
/// <list type="bullet">
/// <item><description>
/// 2026-09-09 — <c>EnrichCatalogJobHandlerTests.An_unextractable_title_…</c>: the assertion
/// counted a GLOBAL sweep as a total, so other classes' seeded titles inflated it. Handler
/// ordering and assertion grain both fixed.
/// </description></item>
/// <item><description>
/// 2026-09-10 — both <c>CandidateRetrieverTests</c> rows: the catalogue is GLOBAL, so a sibling
/// class's popular title displaced the popularity head and a sibling's embedding became a nearer
/// ANN neighbour. Fixed at the cause — that class stamps a synthetic genre on every title it
/// seeds and gives its account a matching IncludeGenre allowlist. Not one assertion changed.
/// </description></item>
/// <item><description>
/// 2026-09-10 — both <c>LiveSearchExpansionTests</c> rows: never local and never flaky. Their
/// fake TMDB client returned list rows with <c>OriginalLanguage = null</c>, so every discovered
/// candidate was upserted with a null <c>Language</c> and then dropped by the English-only
/// eligibility default introduced in e790bb2. The fake now carries "en", as TMDB's real rows do.
/// </description></item>
/// <item><description>
/// 2026-09-10 — <c>LexicalSearchIntegrationTests.Keyword_exclusion_filter_removes_titles_by_tmdb_keyword</c>:
/// also neither local nor flaky. It asserted the reverse (filter-contains-keyword) exclusion arm
/// that was deliberately deleted in 2767cf9 for over-excluding ("warehouse" swallowed "ware").
/// The test now asserts the surviving forward contract, and a new sibling test pins the
/// counterexample so the retired arm cannot return unnoticed. (That class was deleted with
/// <c>LexicalSearchService</c> in trust-v1 wave B; <c>KeylessTextSearchIntegrationTests</c> covers it.)
/// </description></item>
/// </list>
/// <para>
/// The infrastructure stays. An empty set is the state to defend, not a reason to delete the
/// mechanism: the next genuine local-only divergence should be recorded here rather than in a
/// memory file, and the pin is what makes adding a row a decision instead of a shrug.
/// </para>
/// </summary>
public class LocalFlakyQuarantineTests
{
    /// <summary>
    /// The exact quarantined set, as `Class.Method`. EMPTY, and meant to stay that way. Adding a
    /// row is a decision — it says "CI is authoritative for this test and local red is expected",
    /// which is a claim that has been wrong every time it was checked. Establish the cause first.
    /// </summary>
    private static readonly string[] Quarantined = [];

    [Fact]
    public void The_marker_never_skips_unless_the_opt_out_is_exactly_one()
    {
        // CI sets nothing, so every one of these must run there. Only "1" quarantines.
        Assert.False(LocalFlakyFactAttribute.ShouldSkip(null));
        Assert.False(LocalFlakyFactAttribute.ShouldSkip(string.Empty));
        Assert.False(LocalFlakyFactAttribute.ShouldSkip("0"));
        Assert.False(LocalFlakyFactAttribute.ShouldSkip("true"));
        Assert.True(LocalFlakyFactAttribute.ShouldSkip("1"));
    }

    [Fact]
    public void Nothing_is_quarantined()
    {
        var marked = typeof(PostgresFixture).Assembly.GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Where(m => m.GetCustomAttribute<LocalFlakyFactAttribute>() is not null)
            .Select(m => $"{m.DeclaringType!.Name}.{m.Name}")
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(Quarantined.OrderBy(n => n, StringComparer.Ordinal).ToList(), marked);
    }

    [Fact]
    public void Every_quarantined_test_is_also_trait_filterable()
    {
        // The trait is what makes the set selectable from the runner; the attribute is what
        // makes it skippable. They are separate mechanisms, so assert they stay in step.
        var missingTrait = typeof(PostgresFixture).Assembly.GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Where(m => m.GetCustomAttribute<LocalFlakyFactAttribute>() is not null)
            // xUnit 2.x's TraitAttribute exposes nothing at runtime, so read the ctor args.
            .Where(m => !m.GetCustomAttributesData()
                .Where(d => d.AttributeType == typeof(TraitAttribute))
                .Any(d => d.ConstructorArguments.Select(a => a.Value as string).SequenceEqual(["Flaky", "local"])))
            .Select(m => $"{m.DeclaringType!.Name}.{m.Name}")
            .ToList();

        Assert.Empty(missingTrait);
    }
}
