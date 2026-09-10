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

    DbSet<PersonAffinity> PersonAffinities { get; }
    DbSet<TrainingRun> TrainingRuns { get; }
    DbSet<ModelArtifact> ModelArtifacts { get; }
    DbSet<EvaluationResult> EvaluationResults { get; }
    DbSet<TitleScore> TitleScores { get; }
    DbSet<OnlineModelState> OnlineModelStates { get; }

    DbSet<FeedSnapshot> FeedSnapshots { get; }
    DbSet<FeedItem> FeedItems { get; }
    DbSet<FeedBuildStat> FeedBuildStats { get; }
    DbSet<AccountDigest> AccountDigests { get; }

    // "Through your lens": the account's own taste, written out, and titles retold through it.
    DbSet<Domain.Lens.AccountTasteNarrative> AccountTasteNarratives { get; }
    DbSet<Domain.Lens.TitleLensCopy> TitleLensCopies { get; }

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
}
