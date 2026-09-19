using FrostAura.Reel.Application.Jobs;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Library;
using FrostAura.Reel.Domain.Tenancy;

namespace FrostAura.Reel.Tests.IntegrationTests;

/// <summary>
/// The bound on how many passive watches one delta sync may teach the online residual head, on
/// real Postgres.
///
/// <para>
/// This had no test of any kind until 2026-09-10, which is uncomfortable given what it guards: the
/// head's bias ran away on the founder's account, and a large part of the cause was that the old
/// bound — "the 20 most recently first-synced unrated watches", with no window — re-taught the
/// SAME rows on every poll. 224 updates, dominated by repeats of a handful of titles. The rule was
/// fixed at the time; the assertion that would have caught it coming back was never written.
/// </para>
///
/// <para>
/// Every clause is asserted separately, because each one alone is sufficient to reintroduce the
/// defect: the force guard, the changed-category guard, the null-cursor guard, the window, and the
/// per-delta cap.
/// </para>
/// </summary>
[Collection("postgres")]
public class PassiveWatchBoundTests(PostgresFixture fixture)
{
    private static readonly DateTime PreviousDelta = new(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Only_watches_first_seen_since_the_previous_delta_are_observed()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        var stale = await SeedWatchAsync(accountId, PreviousDelta.AddDays(-3));
        var fresh = await SeedWatchAsync(accountId, PreviousDelta.AddMinutes(5));

        var observed = await ObserveAsync(accountId, force: false, changed: true, previous: PreviousDelta);

        // The whole defect in one assertion: a poll every few minutes against a library that moves
        // far more slowly must not re-teach the backlog on every pass.
        Assert.Contains(fresh, observed);
        Assert.DoesNotContain(stale, observed);
    }

    [Fact]
    public async Task A_rated_watch_is_never_a_passive_signal()
    {
        if (!fixture.Available)
        {
            return;
        }

        // A watch is weak "I chose this" evidence worth a soft 6.8 nudge. A RATED watch is a real
        // label the head learns properly elsewhere; teaching it 6.8 as well would fight that.
        var accountId = await SeedAccountAsync();
        var unrated = await SeedWatchAsync(accountId, PreviousDelta.AddMinutes(5));
        var rated = await SeedWatchAsync(accountId, PreviousDelta.AddMinutes(5));
        await RateAsync(accountId, rated, 9);

        var observed = await ObserveAsync(accountId, force: false, changed: true, previous: PreviousDelta);

        Assert.Contains(unrated, observed);
        Assert.DoesNotContain(rated, observed);
    }

    [Fact]
    public async Task A_forced_reconcile_observes_nothing()
    {
        if (!fixture.Available)
        {
            return;
        }

        // The nightly pass enqueues {"force":true}, which refetches every category — so every row
        // in the library looks freshly synced. Observing there drowns the head in one pass.
        var accountId = await SeedAccountAsync();
        await SeedWatchAsync(accountId, PreviousDelta.AddMinutes(5));

        Assert.Empty(await ObserveAsync(accountId, force: true, changed: true, previous: PreviousDelta));
    }

    [Fact]
    public async Task The_first_delta_after_a_full_ingest_observes_nothing()
    {
        if (!fixture.Available)
        {
            return;
        }

        // A null cursor means the full ingest first-synced everything a moment ago. Every row would
        // qualify, and none of it is new information.
        var accountId = await SeedAccountAsync();
        await SeedWatchAsync(accountId, PreviousDelta.AddMinutes(5));

        Assert.Empty(await ObserveAsync(accountId, force: false, changed: true, previous: null));
    }

    [Fact]
    public async Task Nothing_is_observed_when_neither_watch_category_moved()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        await SeedWatchAsync(accountId, PreviousDelta.AddMinutes(5));

        Assert.Empty(await ObserveAsync(accountId, force: false, changed: false, previous: PreviousDelta));
    }

    [Fact]
    public async Task One_unusually_large_delta_still_cannot_exceed_the_budget()
    {
        if (!fixture.Available)
        {
            return;
        }

        // The window is the real defence; the cap is what stops a single catch-up delta (a laptop
        // offline for a fortnight, a Trakt backfill) from landing hundreds of updates at once.
        var accountId = await SeedAccountAsync();
        for (var i = 0; i < 30; i++)
        {
            await SeedWatchAsync(accountId, PreviousDelta.AddMinutes(5 + i));
        }

        var observed = await ObserveAsync(accountId, force: false, changed: true, previous: PreviousDelta);

        Assert.Equal(20, observed.Count);
    }

    [Fact]
    public async Task Another_accounts_new_watches_are_never_observed_for_this_one()
    {
        if (!fixture.Available)
        {
            return;
        }

        var mine = await SeedAccountAsync();
        var theirs = await SeedAccountAsync();
        var theirWatch = await SeedWatchAsync(theirs, PreviousDelta.AddMinutes(5));

        var observed = await ObserveAsync(mine, force: false, changed: true, previous: PreviousDelta);

        Assert.DoesNotContain(theirWatch, observed);
    }

    // ── harness ─────────────────────────────────────────────────────────────────────────────

    private async Task<IReadOnlyList<Guid>> ObserveAsync(Guid accountId, bool force, bool changed, DateTime? previous)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        return await DeltaSyncJobHandler.PassiveWatchesToObserveAsync(
            db, accountId, force, changed, previous, CancellationToken.None);
    }

    private async Task<Guid> SeedAccountAsync()
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var account = new Account
        {
            Id = Guid.NewGuid(),
            TraktUserSlug = $"passive-{Guid.NewGuid():N}",
            CreatedAt = DateTime.UtcNow,
            LastSeenAt = DateTime.UtcNow,
            PipelineStageChangedAt = DateTime.UtcNow,
        };
        db.Add(account);
        await db.SaveChangesAsync();
        return account.Id;
    }

    /// <summary>A watched, unrated title first synced at the given instant.</summary>
    private async Task<Guid> SeedWatchAsync(Guid accountId, DateTime firstSyncedAt)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var title = new Title
        {
            Id = Guid.NewGuid(),
            MediaType = MediaType.Movie,
            TmdbId = Random.Shared.NextInt64(1, long.MaxValue),
            TraktId = Random.Shared.NextInt64(1, long.MaxValue),
            TraktSlug = string.Empty,
            Name = $"title {Guid.NewGuid():N}",
            Genres = ["thriller"],
            Language = "en",
            CreatedAt = DateTime.UtcNow,
        };
        db.Add(title);
        db.WatchedTitles.Add(new WatchedTitle
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            TitleId = title.Id,
            Plays = 1,
            IsFullyWatched = true,
            LastWatchedAt = firstSyncedAt,
            FirstSyncedAt = firstSyncedAt,
            UpdatedAt = firstSyncedAt,
        });
        await db.SaveChangesAsync();
        return title.Id;
    }

    private async Task RateAsync(Guid accountId, Guid titleId, short rating)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        db.UserRatings.Add(new UserRating
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            TitleId = titleId,
            SubjectType = RatingSubjectType.Movie,
            Rating = rating,
            RatedAt = PreviousDelta,
            FirstRatedAt = PreviousDelta,
            SyncedAt = PreviousDelta,
        });
        await db.SaveChangesAsync();
    }
}
