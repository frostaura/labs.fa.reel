using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Sync;
using FrostAura.Reel.Domain.Tenancy;
using FrostAura.Reel.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FrostAura.Reel.Tests.Persistence;

/// <summary>
/// Offline model checks — the multi-tenancy contract is structural, so these tests fail the
/// build if someone adds an account-scoped entity without isolation.
/// </summary>
public class TenancyModelTests
{
    private static ReelDbContext CreateContext() =>
        new(
            new DbContextOptionsBuilder<ReelDbContext>()
                .UseNpgsql("Host=localhost;Database=model_only", npgsql => npgsql.UseVector())
                .Options,
            new AccountContext());

    [Fact]
    public void Every_account_scoped_entity_has_a_query_filter()
    {
        using var db = CreateContext();

        var scopedWithoutFilter = db.Model.GetEntityTypes()
            .Where(t => typeof(IAccountScoped).IsAssignableFrom(t.ClrType) && !t.IsOwned())
            .Where(t => t.GetDeclaredQueryFilters().Count == 0)
            .Select(t => t.ClrType.Name)
            .ToList();

        Assert.Empty(scopedWithoutFilter);
    }

    [Fact]
    public void Catalog_entities_are_not_account_scoped()
    {
        // The shared catalog is the unit-economics lever (one LLM extraction serves every
        // tenant) — accidentally scoping it would silently fork the catalog per account.
        var catalogTypes = new[]
        {
            typeof(Domain.Catalog.Title),
            typeof(Domain.Catalog.TitleEmbedding),
            typeof(Domain.Catalog.KeywordLabel),
            typeof(Domain.Catalog.TitleAttributes),
            typeof(Domain.Catalog.Person),
            typeof(Domain.Catalog.TitleCredit),
            typeof(Domain.Providers.StreamingProvider),
            typeof(Domain.Providers.TitleAvailability),
        };

        foreach (var type in catalogTypes)
        {
            Assert.False(typeof(IAccountScoped).IsAssignableFrom(type), $"{type.Name} must not be account-scoped");
        }
    }

    [Fact]
    public void SyncJob_carries_an_explicit_query_filter_that_admits_global_rows()
    {
        // SyncJob is the one account-bearing entity that cannot implement IAccountScoped: its
        // AccountId is NULLABLE because a global job (catalog refresh) belongs to no tenant, and
        // the interface declares a non-nullable Guid. So the reflective loop above cannot cover
        // it, every reader hand-writes the predicate, and a single missed one is a cross-tenant
        // leak that the test above would never see. The filter is therefore declared explicitly
        // in ReelDbContext, and this test is what keeps it declared.
        Assert.False(typeof(IAccountScoped).IsAssignableFrom(typeof(SyncJob)));

        using var db = CreateContext();
        var syncJob = db.Model.FindEntityType(typeof(SyncJob));

        Assert.NotNull(syncJob);
        Assert.NotEmpty(syncJob.GetDeclaredQueryFilters());
    }

    [Fact]
    public void ExternalApiUsage_is_deliberately_unscoped_and_filtered_by_hand_where_it_is_read()
    {
        // ExternalApiUsage carries an AccountId (nullable: system rows belong to no tenant) but is
        // the operations ledger global monitoring reads, so it is deliberately NOT IAccountScoped and
        // has no query filter. Every reader that serves it to an account filters by hand: today that is
        // MetricsEndpoints' apiUsage block, which shows a non-founder only its own rows (HREV1-PLAT-3,
        // pinned by MetricsPipelineUsageTenancyTests). A new reader must do the same (WS-5 CR-WS6).
        Assert.False(typeof(IAccountScoped).IsAssignableFrom(typeof(ExternalApiUsage)));

        using var db = CreateContext();
        var usage = db.Model.FindEntityType(typeof(ExternalApiUsage));

        Assert.NotNull(usage);
        Assert.Empty(usage.GetDeclaredQueryFilters());
        Assert.True(usage.FindProperty(nameof(ExternalApiUsage.AccountId))!.IsNullable);
    }

    [Fact]
    public void Model_builds_with_all_expected_tables()
    {
        using var db = CreateContext();
        // Tables only. Keyless view mappings are pinned separately below, because "how many tables
        // does this product have" and "how many derived views does it read" are different questions
        // and a single number that answers both hides a table being swapped for a view.
        var tableCount = db.Model.GetEntityTypes().Count(t => !t.IsOwned() && t.FindPrimaryKey() is not null);
        Assert.Equal(46, tableCount); // +KeywordLabel (2026-10-26, WS-3 M2); +UserPersonRating, +UserPreferenceTag, +SearchConversation, +SearchTurn (2026-06-17); +OnlineModelState (2026-07-05); +AccountDigest, +PushSubscription, +AppSecret (2026-08-07); +FeedBuildStat (2026-09-09); −Episode, −ProviderLinkPattern, −AccountTasteProfile, +ExposureEvent, +UserRatingEvent (2026-09-10); +AccountTasteNarrative, +TitleLensCopy (2026-09-10, Phase3ProfileLens); +TasteCluster (2026-09-10, Phase5TasteClusters); +TitleCrossReferenceVerdict, +CrossReferenceEvaluation (2026-09-10, Phase5CrossReference); +CrossReferenceEvaluationItem (2026-09-10, Phase5CrossReferenceItems); +SearchTurnCard (AskReelTurnModel)
    }

    [Fact]
    public void The_closed_loop_views_are_mapped_keyless_and_never_as_tables()
    {
        // A view that quietly becomes a table would be scaffolded into a migration and then start
        // drifting from the SQL it was supposed to derive itself from — the exact staleness class
        // the view was chosen to avoid. Pin the mapping, not just the count.
        using var db = CreateContext();

        var views = db.Model.GetEntityTypes()
            .Where(t => !t.IsOwned() && t.FindPrimaryKey() is null)
            .Select(t => t.ClrType.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(["ExposureOutcome", "TitleExposureRollup"], views);
        Assert.All(
            db.Model.GetEntityTypes().Where(t => t.FindPrimaryKey() is null),
            t => Assert.NotNull(t.GetViewName()));
    }
}
