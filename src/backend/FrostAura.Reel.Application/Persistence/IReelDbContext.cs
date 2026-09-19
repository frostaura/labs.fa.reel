using FrostAura.Reel.Domain.Auth;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Feed;
using FrostAura.Reel.Domain.Filters;
using FrostAura.Reel.Domain.Library;
using FrostAura.Reel.Domain.Ml;
using FrostAura.Reel.Domain.Providers;
using FrostAura.Reel.Domain.Sync;
using FrostAura.Reel.Domain.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace FrostAura.Reel.Application.Persistence;

/// <summary>
/// Persistence port for application services — EF's DbSet/LINQ surface IS the abstraction
/// (pragmatic hexagonal: swapping ORMs is not a real risk; swapping databases is handled by
/// the provider). Implemented by ReelDbContext in Infrastructure.
/// </summary>
public interface IReelDbContext
{
    DbSet<Account> Accounts { get; }
    DbSet<TraktConnection> TraktConnections { get; }
    DbSet<RefreshSession> RefreshSessions { get; }

    DbSet<Title> Titles { get; }
    DbSet<TitleEmbedding> TitleEmbeddings { get; }
    DbSet<TitleAttributes> TitleAttributes { get; }
    DbSet<Person> Persons { get; }
    DbSet<TitleCredit> TitleCredits { get; }
    DbSet<StreamingProvider> StreamingProviders { get; }
    DbSet<TitleAvailability> TitleAvailabilities { get; }

    DbSet<WatchedTitle> WatchedTitles { get; }
    DbSet<ShowWatchProgress> ShowWatchProgresses { get; }
    DbSet<UserRating> UserRatings { get; }
    DbSet<UserRatingEvent> UserRatingEvents { get; }
    DbSet<UserPersonRating> UserPersonRatings { get; }
    DbSet<UserTitleReaction> UserTitleReactions { get; }
    DbSet<ContentFilter> ContentFilters { get; }
    DbSet<UserPreferenceTag> UserPreferenceTags { get; }
    DbSet<Domain.Search.SearchConversation> SearchConversations { get; }
    DbSet<Domain.Search.SearchTurn> SearchTurns { get; }

    DbSet<Domain.Search.SearchTurnCard> SearchTurnCards { get; }

    DbSet<PersonAffinity> PersonAffinities { get; }
    DbSet<TrainingRun> TrainingRuns { get; }
    DbSet<ModelArtifact> ModelArtifacts { get; }
    DbSet<EvaluationResult> EvaluationResults { get; }
    DbSet<TitleScore> TitleScores { get; }
    DbSet<OnlineModelState> OnlineModelStates { get; }

    /// <summary>
    /// The day's taste partition per account — a RETRIEVAL budget, never a feature. Written by the
    /// feed build only and pruned to a fortnight; nothing in the feature builder reads it.
    /// </summary>
    DbSet<TasteCluster> TasteClusters { get; }

    DbSet<FeedSnapshot> FeedSnapshots { get; }
    DbSet<FeedItem> FeedItems { get; }
    DbSet<FeedBuildStat> FeedBuildStats { get; }
    DbSet<AccountDigest> AccountDigests { get; }

    // "Through your lens": the account's own taste, written out, and titles retold through it.
    DbSet<Domain.Lens.AccountTasteNarrative> AccountTasteNarratives { get; }
    DbSet<Domain.Lens.TitleLensCopy> TitleLensCopies { get; }

    // The cross-reference rounds. Verdicts are additive evidence keyed on (account, title, profile
    // hash, prompt version, round); the evaluations table is the offline backtest's own ledger and
    // is deliberately NOT EvaluationResult, which is the M2 modelling record.
    DbSet<Domain.CrossReference.TitleCrossReferenceVerdict> TitleCrossReferenceVerdicts { get; }

    DbSet<Domain.CrossReference.CrossReferenceEvaluation> CrossReferenceEvaluations { get; }

    // One row per judged holdout title inside a backtest origin — the verdict beside the outcome.
    // The aggregate rows above cannot be re-interrogated; these can, which is what makes a new
    // discrimination metric a recomputation rather than another paid run.
    DbSet<Domain.CrossReference.CrossReferenceEvaluationItem> CrossReferenceEvaluationItems { get; }

    // The closed loop. ExposureEvents is append-only; the other two are read-only SQL views.
    DbSet<ExposureEvent> ExposureEvents { get; }
    DbSet<ExposureOutcome> ExposureOutcomes { get; }
    DbSet<TitleExposureRollup> TitleExposureRollups { get; }

    DbSet<Domain.Notifications.PushSubscription> PushSubscriptions { get; }
    DbSet<Domain.Notifications.AppSecret> AppSecrets { get; }

    DbSet<SyncJob> SyncJobs { get; }
    DbSet<TraktOutboxEntry> TraktOutbox { get; }
    DbSet<ManagedListItem> ManagedListItems { get; }
    DbSet<ExternalApiUsage> ExternalApiUsages { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// The raw-SQL escape hatch, for the one query shape EF cannot express: a
    /// <c>CROSS JOIN LATERAL</c> nearest-neighbour scan (see
    /// <c>Application.CrossReference.NeighbourFinder</c>). Deliberately the LAST thing on this
    /// port, and deliberately narrow in practice — the DbSet/LINQ surface above IS the abstraction.
    ///
    /// <para><b>Raw SQL bypasses the global query filters.</b> Every tenancy guarantee this codebase
    /// holds by construction is, inside a string handed to this property, held only by whoever wrote
    /// the string. Any query reaching an account-owned table through here MUST write its own
    /// <c>"AccountId" = {accountId}</c> predicate, and must interpolate rather than concatenate so
    /// the value is a parameter.</para>
    /// </summary>
    Microsoft.EntityFrameworkCore.Infrastructure.DatabaseFacade Database { get; }
}
