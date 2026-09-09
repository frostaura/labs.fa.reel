using System.Reflection;
using FrostAura.Reel.Tests.IntegrationTests;

namespace FrostAura.Reel.Tests.Harness;

/// <summary>
/// The quarantine is a claim about six named tests, so it is pinned like any other contract:
/// the set may only change deliberately, and the marker may never skip by default. Offline —
/// pure reflection, no database.
/// </summary>
public class LocalFlakyQuarantineTests
{
    /// <summary>
    /// The exact quarantined set, as `Class.Method`. Adding a row here is a decision — it says
    /// "CI is authoritative for this test and local red is expected". Removing one is the goal.
    /// </summary>
    private static readonly string[] Quarantined =
    [
        "CandidateRetrieverTests.Cold_start_with_no_loved_centroid_falls_back_to_pure_popularity",
        "CandidateRetrieverTests.RetrieveSimilar_surfaces_unseen_neighbours_and_excludes_the_anchor_and_seen_titles",
        "EnrichCatalogJobHandlerTests.An_unextractable_title_is_never_marked_Done_and_stops_billing_after_the_attempt_budget",
        "LexicalSearchIntegrationTests.Keyword_exclusion_filter_removes_titles_by_tmdb_keyword",
        "LiveSearchExpansionTests.A_director_query_returns_the_filmography_including_watched_titles",
        "LiveSearchExpansionTests.Streams_unseen_matches_excludes_seen_and_caches_the_fetch",
    ];

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
    public void Exactly_the_six_known_local_failures_are_quarantined()
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
