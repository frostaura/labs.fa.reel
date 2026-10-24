using System.Text.Json;
using FrostAura.Reel.Api.Endpoints;
using FrostAura.Reel.Application.Jobs;
using FrostAura.Reel.Application.Ml;
using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Application.Ranking;
using FrostAura.Reel.Application.Saved;
using FrostAura.Reel.Application.Sync;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Library;
using FrostAura.Reel.Domain.Ml;
using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Domain.Providers;
using FrostAura.Reel.Domain.Sync;
using FrostAura.Reel.Domain.Tenancy;
using FrostAura.Reel.Infrastructure.Background;
using FrostAura.Reel.Infrastructure.Ml;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrostAura.Reel.Tests.IntegrationTests;

/// <summary>
/// The Saved queue on real Postgres. Two things are proved here that no unit test can:
///
/// <list type="bullet">
/// <item><b>The on-demand backfill actually closes the null-prediction gap.</b> Saved titles are
/// excluded from eligibility, so they are never rescored by a feed build; after a retrain, a save
/// made before it has no score under the Active artifact and used to render with no number at all.
/// On the founder's database that was the MODAL state, not an edge case. This asserts that a queue
/// larger than one request's budget still reaches zero nulls — and that it does so because scores
/// were PERSISTED, not because the second request re-did the first one's work.</item>
/// <item><b>The loop-closing prompt never fires on a title the user has already rated.</b> The
/// guard is the entire design: the same delta sync that detects a watch ingests the Trakt rating,
/// so on the founder's history all 73 conversions were already rated before Reel could ask.</item>
/// </list>
/// </summary>
[Collection("postgres")]
public class SavedQueueEndpointTests(PostgresFixture fixture)
{
    private static readonly DateTime Now = new(2026, 9, 9, 20, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task On_demand_backfill_respects_its_budget_persists_scores_and_converges_to_zero_nulls()
    {
        if (!fixture.Available)
        {
            return; // no Postgres available — covered in CI's backend-integration job
        }

        var accountId = await SeedAccountAsync();
        var titleIds = new List<Guid>();
        for (var i = 0; i < 40; i++)
        {
            titleIds.Add(await SeedTitleAsync($"saved-{i}", runtimeMinutes: 90 + i));
        }

        // Five already carry a score under the Active artifact — the state a fresh retrain leaves
        // behind: a handful of saves that happened to be in the last shortlist, and 35 that were not.
        var artifactId = await SeedActiveArtifactAsync(accountId);
        await SeedScoresAsync(accountId, artifactId, titleIds.Take(5).ToDictionary(id => id, _ => 7.0m), Now.AddDays(-1));
        await SaveAllAsync(accountId, titleIds);

        var first = await QueueAsync(accountId);

        // 35 needed a score; the budget bought 30 of them, so exactly 5 are still missing and the
        // response SAYS 5 rather than quietly rendering blanks.
        Assert.Equal(40, first.Items.Count);
        Assert.Equal(5, first.UnscoredRemaining);
        Assert.Equal(5, first.Items.Count(i => i.PredictedRating is null));

        // The budget bought real, persisted rows — this is what makes the convergence below cheap
        // rather than the same 30 titles being rescored on every page view forever.
        await using (var verify = fixture.CreateContext(Pinned(accountId)))
        {
            Assert.Equal(35, await verify.TitleScores.CountAsync(
                s => s.AccountId == accountId && s.ModelArtifactId == artifactId));
        }

        var second = await QueueAsync(accountId);

        Assert.Equal(0, second.UnscoredRemaining);
        Assert.All(second.Items, i => Assert.NotNull(i.PredictedRating));
        // Predictions are served at 1dp, on the account's own 0-10 scale.
        Assert.All(second.Items, i => Assert.InRange(i.PredictedRating!.Value, 0m, 10m));
        Assert.All(second.Items, i => Assert.Equal(Math.Round(i.PredictedRating!.Value, 1), i.PredictedRating!.Value));

        await using var final = fixture.CreateContext(Pinned(accountId));
        Assert.Equal(40, await final.TitleScores.CountAsync(
            s => s.AccountId == accountId && s.ModelArtifactId == artifactId));
    }

    [Fact]
    public async Task Tonight_pins_the_best_available_title_inside_the_evening_budget()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync(eveningBudgetMinutes: 120);
        var artifactId = await SeedActiveArtifactAsync(accountId);
        var providerId = await SeedProviderAsync("Netflix");

        // pinned      — 8.5, streams, 100 min → the only honest answer to "what do I put on"
        // tooLong     — 9.5, streams, 175 min → better, does not fit the 2h budget
        // notStreaming— 9.9, 95 min, nowhere  → the best card in the queue and unwatchable tonight
        var pinned = await SeedTitleAsync("pinned", runtimeMinutes: 100);
        var tooLong = await SeedTitleAsync("too-long", runtimeMinutes: 175);
        var notStreaming = await SeedTitleAsync("not-streaming", runtimeMinutes: 95);
        await SeedAvailabilityAsync(pinned, providerId, "US", Now.AddDays(-1));
        await SeedAvailabilityAsync(tooLong, providerId, "US", Now.AddDays(-1));
        await SeedScoresAsync(accountId, artifactId, new()
        {
            [pinned] = 8.5m,
            [tooLong] = 9.5m,
            [notStreaming] = 9.9m,
        }, Now.AddDays(-1));
        await SaveAllAsync(accountId, [pinned, tooLong, notStreaming]);

        var tonight = await QueueAsync(accountId);

        Assert.Equal("tonight", tonight.Sort);
        Assert.Equal(120, tonight.EveningBudgetMinutes);
        var pin = Assert.Single(tonight.Items, i => i.WatchTonight);
        Assert.Equal(pinned, pin.TitleId);
        Assert.Contains("1h 40m against your 2h", pin.WatchTonightReason!, StringComparison.Ordinal);
        Assert.Contains("on Netflix", pin.WatchTonightReason!, StringComparison.Ordinal);
        // Availability leads the order; the unwatchable 9.9 is last despite being the best number.
        Assert.Equal([tooLong, pinned, notStreaming], tonight.Items.Select(i => i.TitleId).ToArray());
        Assert.True(tonight.Items.Single(i => i.TitleId == pinned).AvailableNow);
        Assert.False(tonight.Items.Single(i => i.TitleId == notStreaming).AvailableNow);
        Assert.False(tonight.Items.Single(i => i.TitleId == tooLong).FitsEveningBudget);
        Assert.Equal("Netflix", tonight.Items.Single(i => i.TitleId == pinned).Providers.Single().Name);

        // A different sort re-orders the SAME set and keeps the pin: the pinned pick is a property
        // of the queue, not of the ordering the user happens to be looking at.
        var byPredicted = await QueueAsync(accountId, sort: "predicted");
        Assert.Equal("predicted", byPredicted.Sort);
        Assert.Equal([notStreaming, tooLong, pinned], byPredicted.Items.Select(i => i.TitleId).ToArray());
        Assert.Equal(pinned, Assert.Single(byPredicted.Items, i => i.WatchTonight).TitleId);
    }

    [Fact]
    public async Task Recently_watched_offers_only_unrated_in_window_conversions_that_carry_a_prediction()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        var artifactId = await SeedActiveArtifactAsync(accountId);

        var unrated = await SeedTitleAsync("unrated-conversion");
        var alreadyRated = await SeedTitleAsync("already-rated");
        var outOfWindow = await SeedTitleAsync("out-of-window");
        var noPrediction = await SeedTitleAsync("no-prediction");
        var userRemoved = await SeedTitleAsync("user-removed");

        await SeedRemovedAsync(accountId, unrated, Now.AddDays(-3), ListRemovalReason.Watched);
        await SeedRemovedAsync(accountId, alreadyRated, Now.AddDays(-3), ListRemovalReason.Watched);
        await SeedRemovedAsync(accountId, outOfWindow, Now.AddDays(-30), ListRemovalReason.Watched);
        await SeedRemovedAsync(accountId, noPrediction, Now.AddDays(-2), ListRemovalReason.Watched);
        await SeedRemovedAsync(accountId, userRemoved, Now.AddDays(-1), ListRemovalReason.UserRemoved);

        await SeedScoresAsync(accountId, artifactId, new()
        {
            [alreadyRated] = 8.0m,
            [outOfWindow] = 8.0m,
            [userRemoved] = 8.0m,
        }, Now.AddDays(-40));
        // The prediction Reel committed to before the watch...
        await SeedScoresAsync(accountId, artifactId, new() { [unrated] = 8.4m }, Now.AddDays(-10));
        // ...and a rescore AFTER it, which is hindsight and must not be the number the prompt shows.
        // Under a different artifact id, because (account, title, artifact) is unique — which is
        // also how it happens in life: the second number comes from the next retrain.
        await SeedScoresAsync(accountId, Guid.NewGuid(), new() { [unrated] = 5.1m }, Now.AddDays(-1));
        // `noPrediction` was only ever scored after its removal: no promise was made, so there is
        // nothing to settle and the strip stays silent rather than inventing a guess.
        await SeedScoresAsync(accountId, artifactId, new() { [noPrediction] = 9.0m }, Now.AddDays(-1));

        await SeedRatingAsync(accountId, alreadyRated, 6, Now.AddDays(-2));

        var recent = await RecentlyWatchedAsync(accountId);

        var item = Assert.Single(recent.Items);
        Assert.Equal(unrated, item.TitleId);
        Assert.Equal(8.4m, item.PredictedRating);

        // And once the user answers, the prompt stops offering it — the loop is closed, not looped.
        await SeedRatingAsync(accountId, unrated, 6, Now.AddHours(-1));
        Assert.Empty((await RecentlyWatchedAsync(accountId)).Items);
    }

    /// <summary>
    /// G-1: 116 of the founder's 150 live saves were titles he had already watched and rated. The
    /// queue now drops every consumed save — fully watched, or carrying ANY rating — on the read
    /// itself, before anything has revoked it. Each fixture differs from a kept card by one fact.
    /// </summary>
    [Fact]
    public async Task Consumed_saves_never_render_and_everything_else_does()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        var otherAccount = await SeedAccountAsync();
        var plain = await SeedTitleAsync("plain");
        var watchedMovie = await SeedTitleAsync("watched-movie");
        var ratedNotWatched = await SeedTitleAsync("rated-not-watched");
        var showInProgress = await SeedTitleAsync("show-in-progress");
        var episodeRated = await SeedTitleAsync("episode-rated");
        var watchedByOther = await SeedTitleAsync("watched-by-someone-else");
        await SaveAllAsync(accountId, [plain, watchedMovie, ratedNotWatched, showInProgress, episodeRated, watchedByOther]);

        await SeedWatchedAsync(accountId, watchedMovie, fullyWatched: true);
        await SeedRatingAsync(accountId, ratedNotWatched, 7, Now.AddDays(-1));
        // Partially watched and unrated: still a live intention, not a consumed one.
        await SeedWatchedAsync(accountId, showInProgress, fullyWatched: false);
        // An episode rating is a rating — the same footprint eligibility already honours.
        await SeedRatingAsync(accountId, episodeRated, 9, Now.AddDays(-1), RatingSubjectType.Episode);
        // Another tenant's watch says nothing about this account's queue.
        await SeedWatchedAsync(otherAccount, watchedByOther, fullyWatched: true);
        await SeedRatingAsync(otherAccount, watchedByOther, 8, Now.AddDays(-1));

        var queue = await QueueAsync(accountId, sort: "added");

        Assert.Equal(
            new[] { plain, showInProgress, watchedByOther }.OrderBy(id => id),
            queue.Items.Select(i => i.TitleId).OrderBy(id => id));
    }

    /// <summary>
    /// G-3: "syncing…" was rendered for every save not on the managed list, so 116 cards said
    /// "syncing" with zero outbox rows pending. <c>listSync</c> now says "pending" only for a
    /// ListAdd write-back that is genuinely still waiting, so the number of pending cards equals the
    /// number of pending ListAdd rows.
    /// </summary>
    [Fact]
    public async Task List_sync_is_pending_only_while_a_list_add_is_actually_waiting()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        var onList = await SeedTitleAsync("on-list");
        var pendingAdd = await SeedTitleAsync("pending-add");
        var retryingAdd = await SeedTitleAsync("retrying-add");
        var deadLettered = await SeedTitleAsync("dead-lettered");
        var removedOnTrakt = await SeedTitleAsync("removed-on-trakt");
        await SaveAllAsync(accountId, [onList, pendingAdd, retryingAdd, deadLettered, removedOnTrakt]);

        await SeedListItemAsync(accountId, onList);
        await SeedListItemAsync(accountId, pendingAdd);
        await SeedListItemAsync(accountId, retryingAdd);
        await SeedOutboxAsync(accountId, onList, OutboxKind.ListAdd, OutboxStatus.Sent);
        await SeedOutboxAsync(accountId, pendingAdd, OutboxKind.ListAdd, OutboxStatus.Pending);
        await SeedOutboxAsync(accountId, retryingAdd, OutboxKind.ListAdd, OutboxStatus.Failed);
        await SeedOutboxAsync(accountId, deadLettered, OutboxKind.ListAdd, OutboxStatus.DeadLetter);
        await SeedOutboxAsync(accountId, removedOnTrakt, OutboxKind.ListAdd, OutboxStatus.Sent);
        // A pending write of another kind is not a list add and must not read as "syncing".
        await SeedOutboxAsync(accountId, removedOnTrakt, OutboxKind.AddToWatchlist, OutboxStatus.Pending);

        var queue = await QueueAsync(accountId);
        var byTitle = queue.Items.ToDictionary(i => i.TitleId);

        Assert.Equal(SavedListSync.OnList, byTitle[onList].ListSync);
        Assert.Equal(SavedListSync.Pending, byTitle[pendingAdd].ListSync);
        Assert.Equal(SavedListSync.Pending, byTitle[retryingAdd].ListSync);
        Assert.Equal(SavedListSync.OffList, byTitle[deadLettered].ListSync);
        Assert.Equal(SavedListSync.OffList, byTitle[removedOnTrakt].ListSync);
        // OnManagedList (what an older client reads) is true only when the title is confirmed on the
        // list: a pending card is not on it yet, so an older client says "syncing…" for it.
        Assert.True(byTitle[onList].OnManagedList);
        Assert.False(byTitle[pendingAdd].OnManagedList);
        Assert.False(byTitle[retryingAdd].OnManagedList);
        Assert.False(byTitle[deadLettered].OnManagedList);
        Assert.False(byTitle[removedOnTrakt].OnManagedList);

        await using var db = fixture.CreateContext(new AccountContext());
        var pendingListAdds = await db.TraktOutbox.CountAsync(o => o.AccountId == accountId
            && o.Kind == OutboxKind.ListAdd
            && (o.Status == OutboxStatus.Pending || o.Status == OutboxStatus.Failed));
        Assert.Equal(pendingListAdds, queue.Items.Count(i => i.ListSync == SavedListSync.Pending));
    }

    /// <summary>
    /// The reconciler (DeltaSync and the nightly backstop) revokes a consumed save in the same
    /// transaction it closes the list row — a watch pulled from Trakt, and a rating pulled from
    /// Trakt alike. Revoked, never deleted.
    /// </summary>
    [Fact]
    public async Task The_reconciler_revokes_watched_and_rated_saves_with_their_list_rows()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        var watchedOnTrakt = await SeedTitleAsync("watched-on-trakt");
        var ratedOnTrakt = await SeedTitleAsync("rated-on-trakt");
        var untouched = await SeedTitleAsync("untouched");
        await SaveAllAsync(accountId, [watchedOnTrakt, ratedOnTrakt, untouched]);
        foreach (var id in new[] { watchedOnTrakt, ratedOnTrakt, untouched })
        {
            await SeedListItemAsync(accountId, id);
        }

        await SeedWatchedAsync(accountId, watchedOnTrakt, fullyWatched: true);
        await SeedRatingAsync(accountId, ratedOnTrakt, 8, Now.AddDays(-1));

        IReadOnlyList<Guid> removed;
        await using (var db = fixture.CreateContext(Pinned(accountId)))
        {
            removed = await new ManagedListReconciler(db).RemoveWatchedAsync(accountId, Now, CancellationToken.None);
            await db.SaveChangesAsync();
        }

        Assert.Equal(new[] { watchedOnTrakt, ratedOnTrakt }.OrderBy(id => id), removed.OrderBy(id => id));

        await using var verify = fixture.CreateContext(new AccountContext());
        var saves = await verify.UserTitleReactions
            .Where(r => r.AccountId == accountId && r.Kind == ReactionKind.SaveForLater)
            .ToDictionaryAsync(r => r.TitleId);
        Assert.Equal(3, saves.Count); // revoked, not deleted
        Assert.Equal(Now, saves[watchedOnTrakt].RevokedAt);
        Assert.Equal(Now, saves[ratedOnTrakt].RevokedAt);
        Assert.Null(saves[untouched].RevokedAt);

        var list = await verify.ManagedListItems.Where(m => m.AccountId == accountId).ToDictionaryAsync(m => m.TitleId);
        Assert.Equal(ListRemovalReason.Watched, list[watchedOnTrakt].RemovalReason);
        Assert.Equal(ListRemovalReason.Watched, list[ratedOnTrakt].RemovalReason);
        Assert.Null(list[untouched].RemovedAt);
        Assert.Equal(2, await verify.TraktOutbox.CountAsync(o => o.AccountId == accountId && o.Kind == OutboxKind.ListRemove));
        // The save put the title on the Trakt watchlist; consuming it takes it off, as un-saving does
        // — the rated-but-unwatched title is the case Trakt would never clear by itself.
        var watchlistRemovals = (await verify.TraktOutbox
                .Where(o => o.AccountId == accountId && o.Kind == OutboxKind.RemoveFromWatchlist)
                .Select(o => o.PayloadJson)
                .ToListAsync())
            .Select(json => JsonSerializer.Deserialize<OutboxDispatcher.OutboxPayload>(json)!.TitleId)
            .OrderBy(id => id);
        Assert.Equal(new[] { watchedOnTrakt, ratedOnTrakt }.OrderBy(id => id), watchlistRemovals);
    }

    /// <summary>
    /// Review D1: a save made AFTER the title was watched or rated is a deliberate rewatch, and it is
    /// live. Consumption is time-aware — only a watch or rating after the save consumes it — so the
    /// card shows, the reconciler leaves both the save and its list row alone, and the boot repair
    /// does too. Each fixture differs from a consumed one by one timestamp.
    /// </summary>
    [Fact]
    public async Task A_save_made_after_the_title_was_seen_is_a_live_rewatch_save()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        var ratedBefore = await SeedTitleAsync("rated-years-ago-then-saved");
        var watchedBefore = await SeedTitleAsync("watched-years-ago-then-saved");
        var ratedAfter = await SeedTitleAsync("saved-then-rated");
        var reRatedAfter = await SeedTitleAsync("rated-then-saved-then-re-rated");
        await SaveAllAsync(accountId, [ratedBefore, watchedBefore, ratedAfter, reRatedAfter]);
        foreach (var id in new[] { ratedBefore, watchedBefore, ratedAfter, reRatedAfter })
        {
            await SeedListItemAsync(accountId, id);
        }

        var yearsAgo = Now.AddYears(-3);
        await SeedRatingAsync(accountId, ratedBefore, 8, yearsAgo);
        await SeedWatchedAsync(accountId, watchedBefore, fullyWatched: true, watchedAt: yearsAgo);
        await SeedRatingAsync(accountId, ratedAfter, 8, Now.AddDays(-1));
        // A re-rate moves RatedAt forward, past the save: rewatched and re-scored, so consumed.
        await SeedRatingAsync(accountId, reRatedAfter, 9, Now.AddDays(-1));
        await using (var db = fixture.CreateContext(new AccountContext()))
        {
            await db.UserRatings.Where(r => r.AccountId == accountId && r.TitleId == reRatedAfter)
                .ExecuteUpdateAsync(set => set.SetProperty(r => r.FirstRatedAt, yearsAgo));
        }

        var queue = await QueueAsync(accountId, sort: "added");
        Assert.Equal(
            new[] { ratedBefore, watchedBefore }.OrderBy(id => id),
            queue.Items.Select(i => i.TitleId).OrderBy(id => id));

        IReadOnlyList<Guid> removed;
        await using (var db = fixture.CreateContext(Pinned(accountId)))
        {
            removed = await new ManagedListReconciler(db).RemoveWatchedAsync(accountId, Now, CancellationToken.None);
            await db.SaveChangesAsync();
        }

        Assert.Equal(new[] { ratedAfter, reRatedAfter }.OrderBy(id => id), removed.OrderBy(id => id));
        // The boot repair applies the same rule, so it leaves both rewatch saves live.
        await using (var db = fixture.CreateContext(new AccountContext()))
        {
            await DataRepairs.RevokeConsumedSavesAsync(db, Now, CancellationToken.None);
        }

        await using var verify = fixture.CreateContext(new AccountContext());
        var live = await SaveConsumption.LiveSaves(verify, accountId).Select(r => r.TitleId).ToListAsync();
        Assert.Equal(new[] { ratedBefore, watchedBefore }.OrderBy(id => id), live.OrderBy(id => id));
        var openList = await verify.ManagedListItems
            .Where(m => m.AccountId == accountId && m.RemovedAt == null)
            .Select(m => m.TitleId)
            .ToListAsync();
        Assert.Equal(new[] { ratedBefore, watchedBefore }.OrderBy(id => id), openList.OrderBy(id => id));
        Assert.Equal(2, (await QueueAsync(accountId)).Items.Count);
    }

    /// <summary>
    /// The one-off repair that revokes the historical backlog of consumed saves: it runs on every
    /// boot, so a second pass must change nothing, and it must never touch a save that is still live
    /// in intent.
    /// </summary>
    [Fact]
    public async Task The_consumed_save_repair_is_idempotent_and_leaves_live_saves_alone()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        var consumed = await SeedTitleAsync("consumed");
        var live = await SeedTitleAsync("live");
        var rewatch = await SeedTitleAsync("rewatch");
        await SaveAllAsync(accountId, [consumed, live, rewatch]);
        await SeedWatchedAsync(accountId, consumed, fullyWatched: true);
        await SeedRatingAsync(accountId, consumed, 9, Now.AddDays(-2));
        // Watched and rated BEFORE it was saved: a rewatch save, live in intent.
        await SeedWatchedAsync(accountId, rewatch, fullyWatched: true, watchedAt: Now.AddYears(-2));
        await SeedRatingAsync(accountId, rewatch, 9, Now.AddYears(-2));

        await using (var db = fixture.CreateContext(new AccountContext()))
        {
            Assert.True(await DataRepairs.RevokeConsumedSavesAsync(db, Now, CancellationToken.None) >= 1);
        }

        DateTime? firstRevokedAt;
        await using (var db = fixture.CreateContext(new AccountContext()))
        {
            firstRevokedAt = (await db.UserTitleReactions.SingleAsync(r => r.AccountId == accountId && r.TitleId == consumed)).RevokedAt;
            Assert.Equal(Now, firstRevokedAt);
            Assert.Null((await db.UserTitleReactions.SingleAsync(r => r.AccountId == accountId && r.TitleId == live)).RevokedAt);
            Assert.Null((await db.UserTitleReactions.SingleAsync(r => r.AccountId == accountId && r.TitleId == rewatch)).RevokedAt);
        }

        await using (var db = fixture.CreateContext(new AccountContext()))
        {
            Assert.Equal(0, await DataRepairs.RevokeConsumedSavesAsync(db, Now.AddHours(1), CancellationToken.None));
            var pending = await DataRepairs.PendingAsync(db, CancellationToken.None);
            Assert.Equal(0, pending.Single(p => p.Name == DataRepairs.RevokeConsumedSavesName).RowsChanged);
        }

        await using var verify = fixture.CreateContext(new AccountContext());
        // The second pass did not move the first pass's timestamp: same end state, not a re-write.
        Assert.Equal(firstRevokedAt, (await verify.UserTitleReactions.SingleAsync(r => r.AccountId == accountId && r.TitleId == consumed)).RevokedAt);
        Assert.Equal(
            new[] { live, rewatch }.OrderBy(id => id),
            (await QueueAsync(accountId)).Items.Select(i => i.TitleId).OrderBy(id => id));
        // A boot repair never writes to Trakt.
        Assert.Equal(0, await verify.TraktOutbox.CountAsync(o => o.AccountId == accountId));
    }

    /// <summary>
    /// The route to live: <see cref="DataRepairStartupService"/> runs the repairs in the host's
    /// STARTING phase — before any hosted service's StartAsync, so before the job runner — and its
    /// gate turns it off. Gate off: nothing changes. Gate on: the consumed save is revoked and the
    /// pass reports it. A second boot changes nothing.
    /// </summary>
    [Fact]
    public async Task The_boot_repair_runs_before_the_host_starts_and_honours_its_gate()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        var consumed = await SeedTitleAsync("consumed-at-boot");
        await SaveAllAsync(accountId, [consumed]);
        await SeedRatingAsync(accountId, consumed, 7, Now.AddDays(-1));

        var services = new ServiceCollection()
            .AddScoped(_ => fixture.CreateContext(new AccountContext()))
            .BuildServiceProvider();

        DataRepairStartupService Boot(bool enabled) => new(
            services.GetRequiredService<IServiceScopeFactory>(),
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [DataRepairStartupService.GateKey] = enabled ? "true" : "false",
                })
                .Build(),
            NullLogger<DataRepairStartupService>.Instance);

        async Task<DateTime?> RevokedAtAsync()
        {
            await using var db = fixture.CreateContext(new AccountContext());
            return (await db.UserTitleReactions.SingleAsync(r => r.AccountId == accountId && r.TitleId == consumed)).RevokedAt;
        }

        var off = Boot(enabled: false);
        await off.StartingAsync(CancellationToken.None);
        Assert.Empty(off.LastResults);
        Assert.Null(await RevokedAtAsync());

        var on = Boot(enabled: true);
        await on.StartingAsync(CancellationToken.None);
        Assert.True(on.LastResults.Single(r => r.Name == DataRepairs.RevokeConsumedSavesName).RowsChanged >= 1);
        var revokedAt = await RevokedAtAsync();
        Assert.NotNull(revokedAt);

        var again = Boot(enabled: true);
        await again.StartingAsync(CancellationToken.None);
        Assert.All(again.LastResults, r => Assert.Equal(0, r.RowsChanged));
        Assert.Equal(revokedAt, await RevokedAtAsync());
    }

    // ── Invocation ──────────────────────────────────────────────────────────────────────────

    private async Task<SavedEndpoints.SavedQueueResponse> QueueAsync(Guid accountId, string? sort = null)
    {
        await using var db = fixture.CreateContext(Pinned(accountId));
        var account = await db.Accounts.SingleAsync(a => a.Id == accountId);
        return await SavedEndpoints.QueueAsync(
            sort, account, db, Scorer(db), Calibration(db), Now, CancellationToken.None);
    }

    private async Task<SavedEndpoints.RecentlyWatchedResponse> RecentlyWatchedAsync(Guid accountId)
    {
        await using var db = fixture.CreateContext(Pinned(accountId));
        return await SavedEndpoints.RecentlyWatchedAsync(
            accountId, db, Calibration(db), Now, CancellationToken.None);
    }

    private static OnDemandScorer Scorer(IReelDbContext db)
    {
        var featureBuilder = new FeatureVectorBuilder(db);
        var engine = new FastTreeModelEngine();
        return new OnDemandScorer(
            db,
            featureBuilder,
            engine,
            new OnlineResidualModel(
                db, featureBuilder, engine,
                new MemoryCache(new MemoryCacheOptions()), NullLogger<OnlineResidualModel>.Instance),
            new DislikeSignal(db, new MemoryCache(new MemoryCacheOptions())),
            Calibration(db));
    }

    private static RatingCalibrationService Calibration(IReelDbContext db) =>
        new(db, new MemoryCache(new MemoryCacheOptions()));

    private static AccountContext Pinned(Guid accountId)
    {
        var context = new AccountContext();
        context.SetAccount(accountId);
        return context;
    }

    // ── Seeding ─────────────────────────────────────────────────────────────────────────────

    private async Task<Guid> SeedAccountAsync(int? eveningBudgetMinutes = null)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var account = new Account
        {
            Id = Guid.NewGuid(),
            TraktUserSlug = $"saved-queue-{Guid.NewGuid():N}",
            DisplayName = "Saved Queue Fixture",
            Region = "US",
            CreatedAt = Now,
            LastSeenAt = Now,
            PipelineStageChangedAt = Now,
            Settings = new AccountSettings { EveningBudgetMinutes = eveningBudgetMinutes },
        };
        db.Add(account);
        await db.SaveChangesAsync();
        return account.Id;
    }

    private async Task<Guid> SeedTitleAsync(string name, int? runtimeMinutes = null)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var title = new Title
        {
            Id = Guid.NewGuid(),
            MediaType = MediaType.Movie,
            TmdbId = Random.Shared.NextInt64(1, long.MaxValue),
            TraktId = Random.Shared.NextInt64(1, long.MaxValue),
            TraktSlug = $"{name}-{Guid.NewGuid():N}",
            Name = $"{name} {Guid.NewGuid():N}",
            Language = "en",
            PosterPath = "/p.jpg",
            Genres = ["drama"],
            Year = 2020,
            ReleasedAt = Now.AddYears(-5),
            RuntimeMinutes = runtimeMinutes,
            // Bottom of the popularity order so these fixtures never displace another suite's
            // top-N assertions on the shared catalogue.
            TmdbPopularity = 0m,
            CreatedAt = Now,
        };
        db.Add(title);
        await db.SaveChangesAsync();
        return title.Id;
    }

    private async Task SaveAllAsync(Guid accountId, IEnumerable<Guid> titleIds)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var offset = 0;
        foreach (var titleId in titleIds)
        {
            db.UserTitleReactions.Add(new UserTitleReaction
            {
                Id = Guid.NewGuid(),
                AccountId = accountId,
                TitleId = titleId,
                Kind = ReactionKind.SaveForLater,
                // Distinct save times, so "newest first" is a real order rather than a tie — and all
                // a month back, BEFORE any watch or rating the fixtures seed (a day back), because
                // consumption is time-aware: only a watch or rating after the save consumes it.
                CreatedAt = Now.AddDays(-30 - offset++),
            });
        }

        await db.SaveChangesAsync();
    }

    private async Task<Guid> SeedProviderAsync(string name)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var provider = new StreamingProvider
        {
            Id = Guid.NewGuid(),
            TmdbProviderId = Random.Shared.Next(1, int.MaxValue),
            Name = name,
            LogoPath = "/logo.jpg",
            DisplayPriority = 1,
        };
        db.Add(provider);
        await db.SaveChangesAsync();
        return provider.Id;
    }

    private async Task SeedAvailabilityAsync(Guid titleId, Guid providerId, string region, DateTime fetchedAt)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        db.TitleAvailabilities.Add(new TitleAvailability
        {
            Id = Guid.NewGuid(),
            TitleId = titleId,
            Region = region,
            ProviderId = providerId,
            Kind = AvailabilityKind.Flatrate,
            FetchedAt = fetchedAt,
        });
        await db.SaveChangesAsync();
    }

    /// <summary>A small FastTree over the live schema, Active — the same shape the other suites use.</summary>
    private async Task<Guid> SeedActiveArtifactAsync(Guid accountId)
    {
        var rng = new Random(13);
        var rows = new List<float[]>();
        var labels = new List<float>();
        for (var i = 0; i < 200; i++)
        {
            var v = new float[FeatureSchema.VectorLength];
            for (var j = 0; j < v.Length; j++)
            {
                v[j] = (float)rng.NextDouble();
            }

            rows.Add(v);
            labels.Add(Math.Clamp(5f + (v[0] - 0.5f), 1f, 10f));
        }

        var trained = new FastTreeModelEngine().Train(
            new TrainingMatrix(rows.ToArray(), labels.ToArray(), FeatureSchema.AllNames()),
            TrainJobHandler.DefaultHyperparams);

        await using var db = fixture.CreateContext(new AccountContext());
        var artifact = new ModelArtifact
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            Version = 1,
            Algo = "FastTree",
            ArtifactBytes = trained.ArtifactBytes,
            TrainingRunId = Guid.NewGuid(),
            Status = ArtifactStatus.Active,
            TrainedAt = Now.AddDays(-2),
            // The schema-compat shadow column projects 'version' out of this jsonb; without a
            // current version the artifact is invisible to every Active-artifact read.
            FeatureSchemaJson = JsonSerializer.Serialize(
                new { version = FeatureSchema.Version, names = FeatureSchema.AllNames() }),
        };
        db.Add(artifact);
        await db.SaveChangesAsync();
        return artifact.Id;
    }

    private async Task SeedScoresAsync(
        Guid accountId, Guid artifactId, Dictionary<Guid, decimal> scores, DateTime scoredAt)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        foreach (var (titleId, predicted) in scores)
        {
            db.TitleScores.Add(new TitleScore
            {
                Id = Guid.NewGuid(),
                AccountId = accountId,
                TitleId = titleId,
                ModelArtifactId = artifactId,
                PredictedRating = predicted,
                ScoredAt = scoredAt,
            });
        }

        await db.SaveChangesAsync();
    }

    private async Task SeedRemovedAsync(Guid accountId, Guid titleId, DateTime removedAt, ListRemovalReason reason)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        db.ManagedListItems.Add(new ManagedListItem
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            TitleId = titleId,
            AddedAt = removedAt.AddDays(-30),
            RemovedAt = removedAt,
            RemovalReason = reason,
        });
        await db.SaveChangesAsync();
    }

    private async Task SeedWatchedAsync(Guid accountId, Guid titleId, bool fullyWatched, DateTime? watchedAt = null)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var at = watchedAt ?? Now.AddDays(-1);
        db.WatchedTitles.Add(new WatchedTitle
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            TitleId = titleId,
            Plays = 1,
            IsFullyWatched = fullyWatched,
            LastWatchedAt = at,
            FirstSyncedAt = at,
            UpdatedAt = at,
        });
        await db.SaveChangesAsync();
    }

    private async Task SeedListItemAsync(Guid accountId, Guid titleId, DateTime? addedAt = null)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        db.ManagedListItems.Add(new ManagedListItem
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            TitleId = titleId,
            AddedAt = addedAt ?? Now.AddDays(-30),
        });
        await db.SaveChangesAsync();
    }

    private async Task SeedOutboxAsync(Guid accountId, Guid titleId, OutboxKind kind, OutboxStatus status)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        db.TraktOutbox.Add(new TraktOutboxEntry
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            Kind = kind,
            Status = status,
            PayloadJson = JsonSerializer.Serialize(new OutboxDispatcher.OutboxPayload(
                titleId, MediaType.Movie, 1, 1, null, null)),
            EnqueuedAt = Now.AddMinutes(-5),
            NextAttemptAt = Now.AddMinutes(-5),
            SentAt = status == OutboxStatus.Sent ? Now.AddMinutes(-4) : null,
        });
        await db.SaveChangesAsync();
    }

    private async Task SeedRatingAsync(
        Guid accountId, Guid titleId, short rating, DateTime ratedAt, RatingSubjectType subject = RatingSubjectType.Movie)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        db.UserRatings.Add(new UserRating
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            TitleId = titleId,
            SubjectType = subject,
            SeasonNumber = subject == RatingSubjectType.Episode ? 1 : 0,
            EpisodeNumber = subject == RatingSubjectType.Episode ? 1 : 0,
            Rating = rating,
            RatedAt = ratedAt,
            FirstRatedAt = ratedAt,
        });
        await db.SaveChangesAsync();
    }
}
