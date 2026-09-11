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
    public DbSet<StreamingProvider> StreamingProviders => Set<StreamingProvider>();
    public DbSet<TitleAvailability> TitleAvailabilities => Set<TitleAvailability>();

    // Account library
    public DbSet<WatchedTitle> WatchedTitles => Set<WatchedTitle>();
    public DbSet<ShowWatchProgress> ShowWatchProgresses => Set<ShowWatchProgress>();
    public DbSet<UserRating> UserRatings => Set<UserRating>();
    public DbSet<UserRatingEvent> UserRatingEvents => Set<UserRatingEvent>();
    public DbSet<UserPersonRating> UserPersonRatings => Set<UserPersonRating>();

    public DbSet<UserPreferenceTag> UserPreferenceTags => Set<UserPreferenceTag>();

    public DbSet<Domain.Search.SearchConversation> SearchConversations => Set<Domain.Search.SearchConversation>();

    public DbSet<Domain.Search.SearchTurn> SearchTurns => Set<Domain.Search.SearchTurn>();

    public DbSet<Domain.Search.SearchTurnCard> SearchTurnCards => Set<Domain.Search.SearchTurnCard>();

    public DbSet<UserTitleReaction> UserTitleReactions => Set<UserTitleReaction>();
    public DbSet<ContentFilter> ContentFilters => Set<ContentFilter>();

    // ML
    public DbSet<PersonAffinity> PersonAffinities => Set<PersonAffinity>();
    public DbSet<TrainingRun> TrainingRuns => Set<TrainingRun>();
    public DbSet<ModelArtifact> ModelArtifacts => Set<ModelArtifact>();
    public DbSet<EvaluationResult> EvaluationResults => Set<EvaluationResult>();
    public DbSet<TitleScore> TitleScores => Set<TitleScore>();
    public DbSet<OnlineModelState> OnlineModelStates => Set<OnlineModelState>();

    // The day's taste partition. Account-owned and therefore filtered; see TasteCluster for why it
    // is persisted at all and why nothing but the feed build writes it.
    public DbSet<TasteCluster> TasteClusters => Set<TasteCluster>();

    // Feed
    public DbSet<FeedSnapshot> FeedSnapshots => Set<FeedSnapshot>();
    public DbSet<FeedItem> FeedItems => Set<FeedItem>();
    public DbSet<FeedBuildStat> FeedBuildStats => Set<FeedBuildStat>();
    public DbSet<AccountDigest> AccountDigests => Set<AccountDigest>();

    // ── "Through your lens" ────────────────────────────────────────────────────────────
    // Both are account-owned and therefore filtered; TitleLensCopy is deliberately NOT shared
    // across tenants the way TitleAttributes is — being per-account is the whole point of it.
    public DbSet<Domain.Lens.AccountTasteNarrative> AccountTasteNarratives => Set<Domain.Lens.AccountTasteNarrative>();
    public DbSet<Domain.Lens.TitleLensCopy> TitleLensCopies => Set<Domain.Lens.TitleLensCopy>();

    // The cross-reference rounds — see TitleCrossReferenceVerdict for why Round is in the key and
    // ModelArtifactId is not.
    public DbSet<Domain.CrossReference.TitleCrossReferenceVerdict> TitleCrossReferenceVerdicts =>
        Set<Domain.CrossReference.TitleCrossReferenceVerdict>();

    public DbSet<Domain.CrossReference.CrossReferenceEvaluation> CrossReferenceEvaluations =>
        Set<Domain.CrossReference.CrossReferenceEvaluation>();

    public DbSet<Domain.CrossReference.CrossReferenceEvaluationItem> CrossReferenceEvaluationItems =>
        Set<Domain.CrossReference.CrossReferenceEvaluationItem>();

    // ── The closed loop ────────────────────────────────────────────────────────────────
    // ExposureEvents is append-only and is never touched by the feed builder's prune.
    // The other two are read-only SQL views created in the Phase2ExposureLog migration.
    public DbSet<ExposureEvent> ExposureEvents => Set<ExposureEvent>();
    public DbSet<ExposureOutcome> ExposureOutcomes => Set<ExposureOutcome>();
    public DbSet<TitleExposureRollup> TitleExposureRollups => Set<TitleExposureRollup>();

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

    /// <summary>
    /// Attaches <see cref="HnswFilteredSearchInterceptor"/> here rather than at DI registration, so
    /// that EVERY context — the API's, a job's, and the one the integration fixture constructs by
    /// hand — opens its connections with the same filtered-ANN contract. Read that interceptor's
    /// remarks before removing this: without it, filtered vector searches silently return a
    /// fraction of the rows they ask for.
    /// </summary>
    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        optionsBuilder.AddInterceptors(HnswFilteredSearchInterceptor.Instance);
        base.OnConfiguring(optionsBuilder);
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

        modelBuilder.Entity<StreamingProvider>(e => e.HasIndex(p => p.TmdbProviderId).IsUnique());

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
            // The eval split axis and the nightly retrain gate both order/count on this.
            e.HasIndex(r => new { r.AccountId, r.FirstRatedAt });
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

        modelBuilder.Entity<Domain.Search.SearchConversation>(e =>
        {
            e.HasIndex(c => new { c.AccountId, c.LastTurnAt });
            e.Property(c => c.Title).HasMaxLength(200);
        });

        modelBuilder.Entity<Domain.Search.SearchTurn>(e =>
        {
            // The transcript read, oldest first.
            e.HasIndex(t => new { t.ConversationId, t.CreatedAt });
            // The ordering key, and the concurrency backstop: two turns racing for one conversation
            // compute the same next sequence and one of them loses here rather than both landing.
            e.HasIndex(t => new { t.ConversationId, t.Sequence }).IsUnique();
            // The idempotency key. FILTERED, because ClientTurnId is null on every user row and on
            // every row that predates the turn record — under a plain unique index the first two
            // nulls in a conversation would collide, and under NULLS DISTINCT it would admit
            // duplicates it is meant to stop. Same shape as UserTitleReaction's RevokedAt filter.
            e.HasIndex(t => new { t.ConversationId, t.ClientTurnId })
                .IsUnique()
                .HasFilter("\"ClientTurnId\" IS NOT NULL");
            e.HasIndex(t => t.AccountId);

            e.Property(t => t.Role).HasMaxLength(16);
            e.Property(t => t.Kind).HasMaxLength(16);
            e.Property(t => t.Status).HasMaxLength(16);
            e.Property(t => t.FailureCode).HasMaxLength(64);
            e.Property(t => t.MediaFilter).HasMaxLength(64);
            e.Property(t => t.Reason).HasMaxLength(1024);
            e.Property(t => t.PromptVersion).HasMaxLength(32);
            e.Property(t => t.ModelId).HasMaxLength(128);
            e.Property(t => t.IntentJson).HasColumnType("jsonb");
            e.Property(t => t.StageTimingsJson).HasColumnType("jsonb");
        });

        modelBuilder.Entity<Domain.Search.SearchTurnCard>(e =>
        {
            // The replay read, in the order the user saw. Unique so a turn cannot store two cards
            // at one position — the rank IS the position on screen.
            e.HasIndex(c => new { c.TurnId, c.Rank }).IsUnique();
            // One title appears at most once in a turn.
            e.HasIndex(c => new { c.TurnId, c.TitleId }).IsUnique();
            // The no-repeat set: SELECT DISTINCT "TmdbId" WHERE "ConversationId" = @c, on the hot
            // path of every turn before discovery runs.
            e.HasIndex(c => new { c.ConversationId, c.TmdbId });
            e.HasIndex(c => c.AccountId);

            // Mirrors ExposureEvent's precisions exactly, so a card and the exposure row that
            // describes it can never round to two different numbers.
            e.Property(c => c.PredictedRating).HasPrecision(4, 2);
            e.Property(c => c.LlmFit).HasPrecision(4, 3);
            e.Property(c => c.Blend).HasPrecision(6, 4);
            e.Property(c => c.Why).HasMaxLength(1024);
            e.Property(c => c.WhySource).HasMaxLength(32);
        });

        // ── ML ─────────────────────────────────────────────────────────────────────────────
        modelBuilder.Entity<PersonAffinity>(e => e.HasIndex(a => new { a.AccountId, a.PersonId, a.Role }).IsUnique());

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
        // The online residual head is read-modify-write (load weights → SGD step → save), so two
        // concurrent observes for one account would otherwise have the loser's step silently
        // overwritten — the event lost, with nothing to see. The natural fix is a row version on
        // Postgres's xmin system column (e.Property<uint>("xmin").IsRowVersion()), which needs no
        // DDL; it is NOT here because it is still a MODEL change, and `ef migrations
        // has-pending-model-changes` goes dirty the moment it is declared — this branch is not the
        // one that owns the Phase 0 migration. OnlineResidualModel enforces the same guarantee in
        // application code instead: it writes through a guarded UPDATE predicated on the UpdateCount
        // it read, and treats "no rows affected" as the conflict. Move it here when a migration lands.
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

        // ── Taste clusters ─────────────────────────────────────────────────────────────────
        modelBuilder.Entity<TasteCluster>(e =>
        {
            // One row per (account, day, ordinal). The unique index is what makes the day's
            // partition replaceable rather than append-only: a second build on the same day rewrites
            // it, and two concurrent builds cannot leave a half-partition of interleaved ordinals.
            e.HasIndex(c => new { c.AccountId, c.ComputedForDate, c.Ordinal }).IsUnique();
            // The nightly prune's scan, and the "what did the partition look like then" read.
            e.HasIndex(c => new { c.AccountId, c.ComputedForDate }).IsDescending(false, true);
            // A real pgvector column, configured exactly like TitleEmbedding.Embedding — never JSON
            // floats. No ANN index: this table is read by account and day, never searched by vector.
            e.Property(c => c.Centroid).HasColumnType("vector(1536)");
            e.Property(c => c.Weight).HasPrecision(12, 4);
            e.Property(c => c.ExemplarTitleIdsJson).HasColumnType("jsonb");
            e.Property(c => c.Label).HasMaxLength(128);
        });

        // ── Build instrumentation ──────────────────────────────────────────────────────────
        // No FK on FeedSnapshotId: snapshots are pruned to the last seven and these rows outlive them.
        modelBuilder.Entity<FeedBuildStat>(e =>
        {
            e.HasIndex(s => new { s.AccountId, s.BuiltAt }).IsDescending(false, true);
            e.Property(s => s.ShortlistP50).HasPrecision(4, 2);
            e.Property(s => s.ShortlistP90).HasPrecision(4, 2);
            e.Property(s => s.AnnPerClusterJson).HasColumnType("jsonb");
            e.Property(s => s.NewlyEligiblePerArmJson).HasColumnType("jsonb");
        });

        // ── "Through your lens" ────────────────────────────────────────────────────────────
        modelBuilder.Entity<Domain.Lens.AccountTasteNarrative>(e =>
        {
            // One live narrative per account per version; regeneration writes a new version rather
            // than mutating the old one, so the history of what the product believed about someone
            // stays readable.
            e.HasIndex(n => new { n.AccountId, n.Version }).IsUnique();
            e.Property(n => n.ProfileHash).HasMaxLength(16);
            e.Property(n => n.ProfileFingerprint).HasMaxLength(2048);
            e.Property(n => n.ModelId).HasMaxLength(128);
            e.Property(n => n.FactsJson).HasColumnType("jsonb");
        });

        modelBuilder.Entity<Domain.Lens.TitleLensCopy>(e =>
        {
            // The cache key. ProfileHash carries the taste fingerprint (including the dislike
            // version) and PromptVersion the manual bump lever; the model artifact is deliberately
            // absent — see TasteProfileHash for why, and why the prose never states the number.
            e.HasIndex(c => new { c.AccountId, c.TitleId, c.ProfileHash, c.PromptVersion }).IsUnique();
            // The lens job's "what is already warm" read, and the stale-note fallback's read.
            e.HasIndex(c => new { c.AccountId, c.TitleId, c.GeneratedAt }).IsDescending(false, false, true);
            e.Property(c => c.ProfileHash).HasMaxLength(16);
            e.Property(c => c.ModelId).HasMaxLength(128);
            e.Property(c => c.RejectionReason).HasMaxLength(128);
            e.Property(c => c.LensJson).HasColumnType("jsonb");
        });

        // ── Cross-reference rounds ─────────────────────────────────────────────────────────
        modelBuilder.Entity<Domain.CrossReference.TitleCrossReferenceVerdict>(e =>
        {
            // The verdict key. Round is IN it: the aggregation rule counts observations ("two keeps
            // and no out"), so a round-agnostic key would make round 2 an overwrite of round 1 and
            // leave nothing to count. ModelArtifactId is deliberately absent for the same reason it
            // is absent from the lens key — see TasteProfileHash: an artifact in the key empties the
            // whole store every retrain night.
            e.HasIndex(v => new { v.AccountId, v.TitleId, v.ProfileHash, v.PromptVersion, v.Round })
                .IsUnique()
                .HasDatabaseName("IX_TitleCrossReferenceVerdicts_Key");

            // THE JOB'S RESUME STATE. There is no cursor: "what is still unjudged at the current
            // key" is an anti-join over this index, which is correct after a crash, after a budget
            // stop, after a partial batch and after a concurrent run — none of which a stored cursor
            // survives. Do not add a cursor column; add work to this index instead.
            e.HasIndex(v => new { v.AccountId, v.ProfileHash, v.PromptVersion, v.Round, v.Verdict })
                .HasDatabaseName("IX_TitleCrossReferenceVerdicts_Sweep");

            // The stale ladder's read: the newest verdict per title regardless of fingerprint.
            e.HasIndex(v => new { v.AccountId, v.TitleId, v.GeneratedAt }).IsDescending(false, false, true);

            e.Property(v => v.ProfileHash).HasMaxLength(16);
            e.Property(v => v.ReasonCode).HasMaxLength(32);
            e.Property(v => v.ModelId).HasMaxLength(128);
            // Three decimals: the Out threshold is 0.70 and a coarser column puts a quantisation
            // step across the decision boundary.
            e.Property(v => v.Confidence).HasPrecision(4, 3);
            e.Property(v => v.NeighbourIdsJson).HasColumnType("jsonb");
        });

        modelBuilder.Entity<Domain.CrossReference.CrossReferenceEvaluation>(e =>
        {
            // "The latest run, newest first" — the dev endpoint's only read.
            e.HasIndex(x => new { x.AccountId, x.EvaluatedAt }).IsDescending(false, true);
            // One run's five origins plus its mean row, joined.
            e.HasIndex(x => new { x.AccountId, x.RunId });
            e.Property(x => x.GatingDecision).HasMaxLength(32);
            e.Property(x => x.ModelId).HasMaxLength(128);
            e.Property(x => x.Caveat).HasMaxLength(2048);
            e.Property(x => x.FalseRuleOutRate).HasPrecision(6, 4);
            e.Property(x => x.TrueRuleOutRate).HasPrecision(6, 4);
            e.Property(x => x.WilsonLow).HasPrecision(6, 4);
            e.Property(x => x.WilsonHigh).HasPrecision(6, 4);
            e.Property(x => x.UnsureRate).HasPrecision(6, 4);

            // The discrimination block. All nullable — a row written before these existed measured
            // no AUC, which is a different fact from an AUC of zero.
            e.Property(x => x.MeanLaterRatingKeep).HasPrecision(6, 4);
            e.Property(x => x.MeanLaterRatingOut).HasPrecision(6, 4);
            e.Property(x => x.MeanLaterRatingUnsure).HasPrecision(6, 4);
            e.Property(x => x.KeepLovedRate).HasPrecision(6, 4);
            e.Property(x => x.KeepDislikedRate).HasPrecision(6, 4);
            e.Property(x => x.OutLovedRate).HasPrecision(6, 4);
            e.Property(x => x.OutDislikedRate).HasPrecision(6, 4);
            // Signed, and the only column here that can legitimately be negative: a Spearman of
            // −0.2 says the judge orders titles backwards, which is a finding rather than an error.
            e.Property(x => x.SpearmanConfidenceVsRating).HasPrecision(7, 4);
            e.Property(x => x.AucKeepVsOut).HasPrecision(6, 4);
        });

        modelBuilder.Entity<Domain.CrossReference.CrossReferenceEvaluationItem>(e =>
        {
            // The dev surface's only read: one origin's items, paged, in a stable order. TitleId is
            // in the index so the page boundary cannot move between two requests for the same page.
            e.HasIndex(x => new { x.AccountId, x.EvaluationId, x.TitleId });
            e.Property(x => x.ReasonCode).HasMaxLength(32);
            e.Property(x => x.Confidence).HasPrecision(4, 3);
            e.Property(x => x.NeighbourIdsJson).HasColumnType("jsonb");
        });

        // ── The closed loop ────────────────────────────────────────────────────────────────
        modelBuilder.Entity<ExposureEvent>(e =>
        {
            // Postgres computes the minute bucket from the timestamp it truncates, so the two can
            // never disagree. It exists ONLY to carry the idempotency index below.
            e.Property(x => x.MinuteBucket)
                .HasComputedColumnSql("date_trunc('minute', \"OccurredAt\", 'UTC')", stored: true);

            // The idempotency key. AreNullsDistinct(false) is load-bearing: FeedSnapshotId is
            // legitimately null on the digest, Ask Reel and similar surfaces, and under the default
            // NULLS DISTINCT those rows would never collide — every RTK refetch inside one minute
            // would insert a fresh duplicate and the calibration denominator would inflate.
            e.HasIndex(x => new { x.AccountId, x.TitleId, x.Surface, x.FeedSnapshotId, x.MinuteBucket })
                .IsUnique()
                .AreNullsDistinct(false);

            // "What did you say about this title, and when" — the toast's and the moments list's read.
            e.HasIndex(x => new { x.AccountId, x.TitleId, x.OccurredAt }).IsDescending(false, false, true);
            // The window scan behind every /api/accuracy query.
            e.HasIndex(x => new { x.AccountId, x.OccurredAt }).IsDescending(false, true);
            // Per-artifact-era slicing: which model made which promises.
            e.HasIndex(x => new { x.AccountId, x.ModelArtifactId });

            e.Property(x => x.PredictedRating).HasPrecision(4, 2);
            e.Property(x => x.BaseScore).HasPrecision(6, 3);
            e.Property(x => x.CalibrationDelta).HasPrecision(6, 3);
            e.Property(x => x.ResidualApplied).HasPrecision(6, 3);
            e.Property(x => x.DemotionApplied).HasPrecision(6, 3);
            e.Property(x => x.LlmFit).HasPrecision(4, 3);
            e.Property(x => x.RowKey).HasMaxLength(128);
            e.Property(x => x.WhySource).HasMaxLength(32);
            e.Property(x => x.PolicyId).HasMaxLength(64);
            // Nullable on purpose: NULL is "no verdict existed when this card was served", which is
            // a different fact from Unsure. Never back-filled — see the property doc-comments.
            e.Property(x => x.XrefVerdict).HasMaxLength(32);
            e.Property(x => x.XrefConfidence).HasPrecision(4, 3);

            // The interleaving read: every drafted card, grouped by slate, in time order. Filtered
            // so the index costs nothing on the overwhelming majority of rows, which carry no
            // policy at all — the experiment is off by default and most surfaces never draft.
            e.HasIndex(x => new { x.AccountId, x.InterleaveGroup })
                .HasFilter("\"InterleaveGroup\" IS NOT NULL");
        });

        modelBuilder.Entity<UserRatingEvent>(e =>
        {
            e.HasIndex(x => new { x.AccountId, x.ObservedAt }).IsDescending(false, true);
            e.HasIndex(x => new { x.AccountId, x.TitleId, x.ObservedAt }).IsDescending(false, false, true);
        });

        // Read-only views, created by raw SQL in the Phase2ExposureLog migration. ToView keeps the
        // migration scaffolder from trying to own them as tables while still letting the tenancy
        // loop below hang a query filter on each — so a caller cannot read another tenant's loop
        // even though the view's own SQL already names AccountId on every join.
        modelBuilder.Entity<ExposureOutcome>(e =>
        {
            e.HasNoKey();
            e.ToView("ExposureOutcomes");
            e.Property(x => x.PredictedRating).HasPrecision(4, 2);
        });

        modelBuilder.Entity<TitleExposureRollup>(e =>
        {
            e.HasNoKey();
            e.ToView("TitleExposureRollups");
            e.Property(x => x.MinPredicted).HasPrecision(4, 2);
            e.Property(x => x.MaxPredicted).HasPrecision(4, 2);
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
