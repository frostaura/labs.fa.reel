using System.Reflection;
using FrostAura.Reel.Application.Ml;
using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Auth;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Feed;
using FrostAura.Reel.Domain.Filters;
using FrostAura.Reel.Domain.Library;
using FrostAura.Reel.Domain.Ml;
using FrostAura.Reel.Domain.Providers;
using FrostAura.Reel.Domain.Sync;
using FrostAura.Reel.Domain.Tenancy;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace FrostAura.Reel.Infrastructure.Persistence;

/// <summary>
/// Single DbContext for catalog (global) + account-scoped data. Multi-tenancy is enforced
/// here, once: every <see cref="IAccountScoped"/> entity carries a global query filter bound
/// to the scoped <see cref="IAccountContext"/>. Unscoped contexts (background/global work)
/// see everything; HTTP requests are always pinned to the session's account.
/// </summary>
public class ReelDbContext(DbContextOptions<ReelDbContext> options, IAccountContext accountContext)
    : DbContext(options), IDataProtectionKeyContext, IReelDbContext
{
    /// <summary>
    /// Shadow property on <see cref="ModelArtifact"/>: the feature-schema version the artifact was
    /// trained against, computed by Postgres from <c>FeatureSchemaJson</c>. Named here so the
    /// reconcile sweep can query it.
    /// </summary>
    public const string FeatureSchemaVersionProperty = "FeatureSchemaVersion";

    private readonly IAccountContext _accountContext = accountContext;

    // Tenancy & auth
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<TraktConnection> TraktConnections => Set<TraktConnection>();
    public DbSet<RefreshSession> RefreshSessions => Set<RefreshSession>();

    // Global catalog
    public DbSet<Title> Titles => Set<Title>();
    public DbSet<TitleEmbedding> TitleEmbeddings => Set<TitleEmbedding>();
    public DbSet<TitleAttributes> TitleAttributes => Set<TitleAttributes>();
    public DbSet<Person> Persons => Set<Person>();
    public DbSet<TitleCredit> TitleCredits => Set<TitleCredit>();
    public DbSet<Episode> Episodes => Set<Episode>();
    public DbSet<StreamingProvider> StreamingProviders => Set<StreamingProvider>();
    public DbSet<ProviderLinkPattern> ProviderLinkPatterns => Set<ProviderLinkPattern>();
    public DbSet<TitleAvailability> TitleAvailabilities => Set<TitleAvailability>();

    // Account library
    public DbSet<WatchedTitle> WatchedTitles => Set<WatchedTitle>();
    public DbSet<ShowWatchProgress> ShowWatchProgresses => Set<ShowWatchProgress>();
    public DbSet<UserRating> UserRatings => Set<UserRating>();
    public DbSet<UserPersonRating> UserPersonRatings => Set<UserPersonRating>();

    public DbSet<UserPreferenceTag> UserPreferenceTags => Set<UserPreferenceTag>();

    public DbSet<Domain.Search.SearchConversation> SearchConversations => Set<Domain.Search.SearchConversation>();

    public DbSet<Domain.Search.SearchTurn> SearchTurns => Set<Domain.Search.SearchTurn>();
    public DbSet<UserTitleReaction> UserTitleReactions => Set<UserTitleReaction>();
    public DbSet<ContentFilter> ContentFilters => Set<ContentFilter>();

    // ML
    public DbSet<PersonAffinity> PersonAffinities => Set<PersonAffinity>();
    public DbSet<AccountTasteProfile> AccountTasteProfiles => Set<AccountTasteProfile>();
    public DbSet<TrainingRun> TrainingRuns => Set<TrainingRun>();
    public DbSet<ModelArtifact> ModelArtifacts => Set<ModelArtifact>();
    public DbSet<EvaluationResult> EvaluationResults => Set<EvaluationResult>();
    public DbSet<TitleScore> TitleScores => Set<TitleScore>();
    public DbSet<OnlineModelState> OnlineModelStates => Set<OnlineModelState>();

    // Feed
    public DbSet<FeedSnapshot> FeedSnapshots => Set<FeedSnapshot>();
    public DbSet<FeedItem> FeedItems => Set<FeedItem>();
    public DbSet<FeedBuildStat> FeedBuildStats => Set<FeedBuildStat>();
    public DbSet<AccountDigest> AccountDigests => Set<AccountDigest>();

    // Notifications
    public DbSet<Domain.Notifications.PushSubscription> PushSubscriptions => Set<Domain.Notifications.PushSubscription>();
    public DbSet<Domain.Notifications.AppSecret> AppSecrets => Set<Domain.Notifications.AppSecret>();

    // Sync
    public DbSet<SyncJob> SyncJobs => Set<SyncJob>();
    public DbSet<TraktOutboxEntry> TraktOutbox => Set<TraktOutboxEntry>();
    public DbSet<ManagedListItem> ManagedListItems => Set<ManagedListItem>();
    public DbSet<ExternalApiUsage> ExternalApiUsages => Set<ExternalApiUsage>();

    // Data Protection key ring (encrypted Trakt tokens survive restarts)
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // Enums as strings everywhere: ops-friendly (readable rows, usable in index filters).
        foreach (var enumType in typeof(Account).Assembly.GetTypes().Where(t => t.IsEnum))
        {
            configurationBuilder.Properties(enumType).HaveConversion<string>().HaveMaxLength(32);
        }

        // Npgsql rejects non-UTC DateTimes for timestamptz; external date-only payloads arrive
        // Kind=Unspecified, so normalize model-wide instead of policing every assignment.
        configurationBuilder.Properties<DateTime>().HaveConversion<UtcDateTimeConverter>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasPostgresExtension("vector");
        modelBuilder.HasPostgresExtension("pg_trgm");

        // ── Tenancy ────────────────────────────────────────────────────────────────────────
        modelBuilder.Entity<Account>(e =>
        {
            e.HasIndex(a => a.TraktUserSlug).IsUnique();
            // Blend codes are shareable lookups — unique only where set, so accounts without
            // one don't collide on NULL and regeneration frees the old code atomically.
            e.HasIndex(a => a.BlendCode).IsUnique().HasFilter("\"BlendCode\" IS NOT NULL");
            e.Property(a => a.BlendCode).HasMaxLength(8);
            e.OwnsOne(a => a.Settings, s => s.ToJson());
        });

        modelBuilder.Entity<TraktConnection>(e =>
        {
            e.HasIndex(c => c.AccountId).IsUnique();
            e.Property(c => c.LastActivitiesJson).HasColumnType("jsonb");
        });

        modelBuilder.Entity<RefreshSession>(e =>
        {
            e.HasIndex(s => s.TokenHash).IsUnique();
            e.HasIndex(s => s.AccountId);
        });

        // ── Catalog ────────────────────────────────────────────────────────────────────────
        modelBuilder.Entity<Title>(e =>
        {
            e.HasIndex(t => new { t.MediaType, t.TraktId }).IsUnique().HasFilter("\"TraktId\" IS NOT NULL");
            e.HasIndex(t => new { t.MediaType, t.TmdbId }).IsUnique().HasFilter("\"TmdbId\" IS NOT NULL");
            e.HasIndex(t => t.Name).HasMethod("gin").HasOperators("gin_trgm_ops");
            e.HasIndex(t => t.TmdbPopularity).IsDescending();
            e.HasIndex(t => t.Genres).HasMethod("gin");
        });

        modelBuilder.Entity<TitleEmbedding>(e =>
        {
            e.HasKey(x => x.TitleId);
            e.Property(x => x.Embedding).HasColumnType("vector(1536)");
            // HNSW over ivfflat: the catalog grows incrementally from zero rows, and HNSW
            // needs no representative data at index-build time.
            e.HasIndex(x => x.Embedding).HasMethod("hnsw").HasOperators("vector_cosine_ops");
        });

        modelBuilder.Entity<TitleAttributes>(e =>
        {
            e.HasKey(x => x.TitleId);
            e.Property(x => x.RawJson).HasColumnType("jsonb");
            // Extraction queue scan: only Pending rows are ever fetched by the worker.
            e.HasIndex(x => x.Status).HasFilter("\"Status\" = 'Pending'");
        });

        modelBuilder.Entity<Person>(e =>
        {
            e.HasIndex(p => p.TmdbId).IsUnique();
            e.HasIndex(p => p.Name).HasMethod("gin").HasOperators("gin_trgm_ops");
        });

        modelBuilder.Entity<TitleCredit>(e =>
        {
            e.HasIndex(c => new { c.TitleId, c.PersonId, c.Role }).IsUnique();
            e.HasIndex(c => c.PersonId);
        });

        modelBuilder.Entity<Episode>(e =>
        {
            e.HasIndex(x => new { x.TitleId, x.SeasonNumber, x.EpisodeNumber }).IsUnique();
        });

        modelBuilder.Entity<StreamingProvider>(e => e.HasIndex(p => p.TmdbProviderId).IsUnique());

        modelBuilder.Entity<ProviderLinkPattern>(e => e.HasIndex(p => new { p.ProviderId, p.Region }));

        modelBuilder.Entity<TitleAvailability>(e =>
        {
            e.HasIndex(a => new { a.TitleId, a.Region, a.ProviderId, a.Kind }).IsUnique();
            e.HasIndex(a => new { a.TitleId, a.Region });
        });

        // ── Library ────────────────────────────────────────────────────────────────────────
        modelBuilder.Entity<WatchedTitle>(e =>
        {
            e.HasIndex(w => new { w.AccountId, w.TitleId }).IsUnique();
            e.HasIndex(w => new { w.AccountId, w.IsFullyWatched });
        });

        modelBuilder.Entity<ShowWatchProgress>(e => e.HasIndex(p => new { p.AccountId, p.TitleId }).IsUnique());

        modelBuilder.Entity<UserRating>(e =>
        {
            e.HasIndex(r => new { r.AccountId, r.SubjectType, r.TitleId, r.SeasonNumber, r.EpisodeNumber }).IsUnique();
            e.HasIndex(r => new { r.AccountId, r.RatedAt });
        });

        modelBuilder.Entity<UserPersonRating>(e =>
        {
            e.HasIndex(r => new { r.AccountId, r.PersonId }).IsUnique();
            e.HasIndex(r => new { r.AccountId, r.RatedAt });
        });

        modelBuilder.Entity<UserTitleReaction>(e =>
        {
            e.HasIndex(r => new { r.AccountId, r.TitleId, r.Kind }).IsUnique().HasFilter("\"RevokedAt\" IS NULL");
        });

        modelBuilder.Entity<ContentFilter>(e => e.HasIndex(f => new { f.AccountId, f.Kind, f.Value }).IsUnique());

        modelBuilder.Entity<UserPreferenceTag>(e =>
        {
            e.HasIndex(t => new { t.AccountId, t.Text }).IsUnique();
            e.Property(t => t.Embedding).HasColumnType("vector(1536)");
        });

        modelBuilder.Entity<Domain.Search.SearchConversation>(e => e.HasIndex(c => new { c.AccountId, c.LastTurnAt }));
        modelBuilder.Entity<Domain.Search.SearchTurn>(e => e.HasIndex(t => new { t.ConversationId, t.CreatedAt }));

        // ── ML ─────────────────────────────────────────────────────────────────────────────
        modelBuilder.Entity<PersonAffinity>(e => e.HasIndex(a => new { a.AccountId, a.PersonId, a.Role }).IsUnique());

        modelBuilder.Entity<AccountTasteProfile>(e =>
        {
            e.HasIndex(p => p.AccountId).IsUnique();
            e.Property(p => p.LovedCentroid).HasColumnType("vector(1536)");
            e.Property(p => p.RecentCentroid).HasColumnType("vector(1536)");
            e.Property(p => p.ProfileJson).HasColumnType("jsonb");
        });

        modelBuilder.Entity<TrainingRun>(e =>
        {
            e.HasIndex(r => new { r.AccountId, r.StartedAt });
            e.Property(r => r.HyperparamsJson).HasColumnType("jsonb");
        });

        modelBuilder.Entity<ModelArtifact>(e =>
        {
            e.HasIndex(a => new { a.AccountId, a.Version }).IsUnique();
            // At most one Active artifact per account — enforced by the database, not by the
            // check-then-insert in TrainJobHandler, so a raced retrain fails loudly instead of
            // leaving two Active artifacts for an unordered FirstOrDefault to pick between.
            e.HasIndex(a => a.AccountId).IsUnique().HasFilter("\"Status\" = 'Active'");
            e.Property(a => a.FeatureSchemaJson).HasColumnType("jsonb");
            // The feature-schema version the artifact was trained against, projected out of the jsonb
            // by Postgres itself so the column can never disagree with the document it came from.
            // Shadow rather than a domain property: it is a persistence-level projection, not state
            // the domain owns.
            e.Property<int?>(FeatureSchemaVersionProperty)
                .HasComputedColumnSql("((\"FeatureSchemaJson\" ->> 'version'))::integer", stored: true);

            // Tenancy AND schema compatibility, in ONE filter (this entity is deliberately excluded
            // from the generic tenancy loop below so the two predicates compose instead of one
            // replacing the other). An artifact whose recorded schema version is not the running
            // FeatureSchema.Version cannot be read back as Active: the vector's width and column
            // meanings moved under it, and Score()/ScoreCached() validate width only — the exact
            // silent failure FeatureSchema's own doc-comment warns about. Every consumer already
            // treats "no Active artifact" as "no model yet", which is the correct degradation;
            // NightlyReconcileService logs the mismatch loudly and forces a retrain.
            e.HasQueryFilter(a =>
                (_accountContext.AccountId == null || a.AccountId == _accountContext.AccountId)
                && (a.Status != ArtifactStatus.Active
                    || EF.Property<int?>(a, FeatureSchemaVersionProperty) == FeatureSchema.Version));
        });

        modelBuilder.Entity<EvaluationResult>(e =>
        {
            e.HasIndex(r => r.TrainingRunId).IsUnique();
            e.Property(r => r.DetailJson).HasColumnType("jsonb");
        });

        modelBuilder.Entity<TitleScore>(e =>
        {
            e.HasIndex(s => new { s.AccountId, s.TitleId, s.ModelArtifactId }).IsUnique();
            e.HasIndex(s => new { s.AccountId, s.ModelArtifactId, s.PredictedRating }).IsDescending(false, false, true);
            e.Property(s => s.ContributionsJson).HasColumnType("jsonb");
        });

        // One residual head per account (upserted on every event); unique so the online update
        // has a single row to load-modify-write against.
        modelBuilder.Entity<OnlineModelState>(e => e.HasIndex(s => s.AccountId).IsUnique());

        // ── Feed ───────────────────────────────────────────────────────────────────────────
        modelBuilder.Entity<FeedSnapshot>(e =>
        {
            e.HasIndex(s => s.AccountId).IsUnique().HasFilter("\"Status\" = 'Active'");
        });

        modelBuilder.Entity<FeedItem>(e =>
        {
            e.HasIndex(i => new { i.FeedSnapshotId, i.Row, i.Rank });
            e.Property(i => i.ExplanationJson).HasColumnType("jsonb");
        });

        modelBuilder.Entity<AccountDigest>(e =>
        {
            // One digest per account per ISO week — the BuildDigest job upserts against this.
            e.HasIndex(d => new { d.AccountId, d.WeekStart }).IsUnique();
            e.Property(d => d.ItemsJson).HasColumnType("jsonb");
        });

        // ── Notifications ──────────────────────────────────────────────────────────────────
        modelBuilder.Entity<Domain.Notifications.PushSubscription>(e =>
        {
            // One row per (account, browser endpoint); re-subscribing the same browser upserts.
            e.HasIndex(s => new { s.AccountId, s.Endpoint }).IsUnique();
        });

        modelBuilder.Entity<Domain.Notifications.AppSecret>(e =>
        {
            e.HasKey(s => s.Key);
            e.Property(s => s.Key).HasMaxLength(64);
        });

        // ── Sync ───────────────────────────────────────────────────────────────────────────
        modelBuilder.Entity<SyncJob>(e =>
        {
            // No duplicate in-flight job per (account, kind). Postgres treats NULL account ids
            // as distinct — global-job dedup is the scheduler's responsibility.
            e.HasIndex(j => new { j.AccountId, j.Kind }).IsUnique().HasFilter("\"Status\" IN ('Pending', 'Running')");
            e.HasIndex(j => new { j.Status, j.Priority, j.EnqueuedAt });
            e.Property(j => j.CursorJson).HasColumnType("jsonb");

            // SyncJob is the one account-bearing entity that is NOT IAccountScoped — its AccountId
            // is nullable because a global job (catalog refresh) belongs to no tenant — so the
            // reflective loop below cannot cover it and every reader hand-writes the predicate.
            // One missed predicate is a cross-tenant leak that no structural test would catch, so
            // the filter is declared explicitly here instead.
            //
            // It mirrors the generic filter's null-context behaviour (unpinned sees everything —
            // JobRunnerService reads the job BEFORE pinning, and the background schedulers run in
            // unscoped scopes) and additionally always admits global rows, which is the difference
            // from the generic predicate and the reason SyncJob cannot simply implement the
            // interface. The explicit AccountId filters in SyncEndpoints/MetricsEndpoints stay:
            // they narrow further, to this account's own jobs, excluding global ones.
            e.HasQueryFilter(j =>
                _accountContext.AccountId == null
                || j.AccountId == null
                || j.AccountId == _accountContext.AccountId);
        });

        modelBuilder.Entity<TraktOutboxEntry>(e =>
        {
            e.HasIndex(o => new { o.Status, o.NextAttemptAt });
            e.Property(o => o.PayloadJson).HasColumnType("jsonb");
        });

        modelBuilder.Entity<ManagedListItem>(e =>
        {
            e.HasIndex(m => new { m.AccountId, m.TitleId }).IsUnique().HasFilter("\"RemovedAt\" IS NULL");
        });

        modelBuilder.Entity<ExternalApiUsage>(e =>
        {
            // NULLS NOT DISTINCT is load-bearing, not a nicety: AccountId/Model/Purpose are legitimately
            // null for Trakt, TMDB and catalogue-wide work, and under the default NULLS DISTINCT the
            // recorder's ON CONFLICT upsert would never fire for those rows — the 15s flush would
            // insert a fresh duplicate every tick, forever. See ExternalApiUsage's doc-comment.
            e.HasIndex(u => new { u.Provider, u.Day, u.AccountId, u.Model, u.Purpose })
                .IsUnique()
                .AreNullsDistinct(false);
            e.Property(u => u.Day).HasColumnType("date");
            e.Property(u => u.Model).HasMaxLength(128);
            e.Property(u => u.Purpose).HasMaxLength(64);
        });

        // ── Build instrumentation ──────────────────────────────────────────────────────────
        // No FK on FeedSnapshotId: snapshots are pruned to the last seven and these rows outlive them.
        modelBuilder.Entity<FeedBuildStat>(e =>
        {
            e.HasIndex(s => new { s.AccountId, s.BuiltAt }).IsDescending(false, true);
            e.Property(s => s.ShortlistP50).HasPrecision(4, 2);
            e.Property(s => s.ShortlistP90).HasPrecision(4, 2);
        });

        // ── Row-level tenancy, enforced once ───────────────────────────────────────────────
        // ModelArtifact is the one exception: it declares its own filter above, which carries this
        // same tenancy predicate plus the feature-schema compatibility check.
        foreach (var entityType in modelBuilder.Model.GetEntityTypes()
                     .Where(t => typeof(IAccountScoped).IsAssignableFrom(t.ClrType) && !t.IsOwned())
                     .Where(t => t.ClrType != typeof(ModelArtifact)))
        {
            typeof(ReelDbContext)
                .GetMethod(nameof(ApplyAccountFilter), BindingFlags.NonPublic | BindingFlags.Instance)!
                .MakeGenericMethod(entityType.ClrType)
                .Invoke(this, [modelBuilder]);
        }
    }

    private void ApplyAccountFilter<TEntity>(ModelBuilder modelBuilder)
        where TEntity : class, IAccountScoped
    {
        modelBuilder.Entity<TEntity>()
            .HasQueryFilter(e => _accountContext.AccountId == null || e.AccountId == _accountContext.AccountId);
    }
}
