using FrostAura.Reel.Application.Ml.ClosedLoop;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Feed;
using FrostAura.Reel.Domain.Library;
using FrostAura.Reel.Domain.Ml;
using FrostAura.Reel.Domain.Sync;
using FrostAura.Reel.Domain.Tenancy;

namespace FrostAura.Reel.Tests.IntegrationTests;

/// <summary>
/// The retro closed loop on real Postgres. Two things are proved here that an in-memory fake
/// cannot: that the joins translate to SQL at all, and that the answer is per-tenant — the query
/// never names an account id, so isolation rests entirely on the global query filters. If those
/// filters ever stop applying, this test reports one account's track record to the other.
/// </summary>
[Collection("postgres")]
public class RetroLoopQueryTests(PostgresFixture fixture)
{
    private static readonly DateTime Now = new(2026, 9, 9, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Retro_loop_is_computed_per_tenant_and_never_leaks_across_accounts()
    {
        if (!fixture.Available)
        {
            return; // no Postgres available — covered in CI's backend-integration job
        }

        var seed = await SeedAsync();

        // ── Account A ────────────────────────────────────────────────────────────────────
        var reportA = await ComputeAsync(seed.AccountA);

        // Two conversions, both rated, both scored before their removal.
        Assert.Equal(2, reportA.Conversions.Count);
        Assert.Equal(2, reportA.Conversions.RatedCount);
        Assert.Equal(2, reportA.Conversions.ScoredCount);

        // Predicted 8 and 6 against actual 4 and 7: means 7.0 / 5.5, MAE (4+1)/2, bias (4−1)/2,
        // within-one on exactly one of the two. The 3.0 score written AFTER the removal is
        // hindsight and must not be the one picked.
        Assert.Equal(7.0d, reportA.Conversions.Accuracy.MeanPredicted, 6);
        Assert.Equal(5.5d, reportA.Conversions.Accuracy.MeanActual, 6);
        Assert.Equal(2.5d, reportA.Conversions.Accuracy.MeanAbsoluteError, 6);
        Assert.Equal(1.5d, reportA.Conversions.Accuracy.Bias, 6);
        Assert.Equal(0.5d, reportA.Conversions.Accuracy.WithinOnePointRate, 6);

        var worst = reportA.Conversions.WorstMisses.First();
        Assert.Equal("A-converted-badly", worst.Name);
        Assert.Equal(4d, worst.Gap, 6);

        Assert.Equal(
            [new KeyValuePair<int, int>(4, 1), new KeyValuePair<int, int>(7, 1)],
            reportA.Conversions.RatingDistribution);

        // Wider pairs: every whole-title rating with a score that predates it — the two conversions
        // plus the self-found title. Predicted 8,6,5 vs actual 4,7,8.
        Assert.Equal(3, reportA.PredictedVsActualPairs);
        Assert.Equal(3, reportA.PredictedVsActual.Count);
        Assert.Equal((8d + 6d + 5d) / 3d, reportA.PredictedVsActual.MeanPredicted, 6);
        Assert.Equal((4d + 7d + 8d) / 3d, reportA.PredictedVsActual.MeanActual, 6);
        Assert.Equal((4d + 1d + 3d) / 3d, reportA.PredictedVsActual.MeanAbsoluteError, 6);
        Assert.Equal((4d - 1d - 3d) / 3d, reportA.PredictedVsActual.Bias, 6);

        // Counterfactual: managed-list titles (4, 7) plus the feed-surfaced title (6), each against
        // what the user found alone within two weeks of it — the self-found 8, a day to eight days
        // away from all three.
        var recent = reportA.Counterfactual.Single(w => w.Days == RetroLoopQuery.RecentWindowDays);
        Assert.Equal(3, recent.ViaReelCount);
        Assert.Equal((4d + 7d + 6d) / 3d, recent.ViaReelMeanRating!.Value, 6);
        Assert.Equal(1, recent.OtherCount);
        Assert.Equal(8d, recent.OtherMeanRating!.Value, 6);
        Assert.Equal(((4d + 7d + 6d) / 3d) - 8d, recent.Delta!.Value, 6);
        Assert.Equal(0, recent.ViaReelUnmatchedCount);

        // All-time is matched the same way, so the 400-day-old 10 is nobody's same-period baseline
        // and cannot drag the comparison (HREV4-DESK-02): the same answer as the year.
        var allTime = reportA.Counterfactual.Single(w => w.Days is null);
        Assert.Equal(3, allTime.ViaReelCount);
        Assert.Equal(1, allTime.OtherCount);
        Assert.Equal(8d, allTime.OtherMeanRating!.Value, 6);
        Assert.Equal(recent.Delta, allTime.Delta);

        // ── Account B — the same query, a different answer ───────────────────────────────
        var reportB = await ComputeAsync(seed.AccountB);

        Assert.Equal(1, reportB.Conversions.Count);
        Assert.Equal(9d, reportB.Conversions.Accuracy.MeanPredicted, 6);
        Assert.Equal(9d, reportB.Conversions.Accuracy.MeanActual, 6);
        Assert.Equal(0d, reportB.Conversions.Accuracy.MeanAbsoluteError, 6);
        Assert.DoesNotContain(reportB.Conversions.WorstMisses, m => m.Name.StartsWith("A-", StringComparison.Ordinal));
        Assert.Equal(1, reportB.PredictedVsActualPairs);

        // Nothing of A's is visible from B and vice versa.
        Assert.DoesNotContain(reportA.Conversions.WorstMisses, m => m.Name.StartsWith("B-", StringComparison.Ordinal));
        Assert.DoesNotContain(reportA.PredictedVsActualWorstMisses, m => m.Name.StartsWith("B-", StringComparison.Ordinal));
    }

    [Theory]
    // Reel's picks rated exactly like the user's own finds of the same weeks: no gap, however far
    // the user's average has fallen since 2020.
    [InlineData((short)6, 0d)]
    // A genuinely better pick in the second period: the gap is that pick's lead over its own weeks,
    // averaged over the three matched picks — (0 + 0 + 3) / 3.
    [InlineData((short)9, 1d)]
    public async Task The_counterfactual_compares_reel_picks_with_the_users_own_finds_from_the_same_weeks(
        short secondPeriodPick, double expectedDelta)
    {
        if (!fixture.Available)
        {
            return;
        }

        // HREV4-DESK-02. Every via-Reel rating is from the months Reel existed; the user's own finds
        // go back years, and here their average falls from 8 (2020) to 5–6 (this year). Pooling every
        // other rating as the baseline reported that drift as "Reel steered you toward films you
        // liked less". Matched within the band, the drift drops out and only the picks' own lead stays.
        var accountId = await SeedDriftingAccountAsync(secondPeriodPick);

        var report = await ComputeAsync(accountId);

        foreach (var window in report.Counterfactual)
        {
            Assert.Equal(3, window.ViaReelCount);
            // The pick rated 2 sixty days back has nothing of the user's own within two weeks of it.
            Assert.Equal(1, window.ViaReelUnmatchedCount);
            // The five same-period finds; none of the ten 2020 ratings of 8.
            Assert.Equal(5, window.OtherCount);
            Assert.Equal(expectedDelta, window.Delta!.Value, 6);
            Assert.Equal(window.ViaReelMeanRating!.Value - window.OtherMeanRating!.Value, window.Delta.Value, 6);
            Assert.Equal(RetroLoopQuery.MatchBandDays, window.MatchDays);
        }
    }

    [Fact]
    public void A_find_exactly_on_the_band_edge_is_a_baseline_and_one_a_day_beyond_is_not()
    {
        var pick = Now.AddDays(-50);
        var band = RetroLoopQuery.MatchBandDays;
        RetroLoopQuery.RatedTitleRow[] rows =
        [
            new(7, pick, ViaReel: true),
            new(5, pick.AddDays(-band), ViaReel: false),
            new(9, pick.AddDays(band), ViaReel: false),
            new(1, pick.AddDays(band + 1), ViaReel: false),
        ];

        var window = RetroLoopQuery.MatchedWindow("w", null, rows);

        Assert.Equal(1, window.ViaReelCount);
        Assert.Equal(2, window.OtherCount);
        Assert.Equal(7d, window.OtherMeanRating!.Value, 6);
        Assert.Equal(0d, window.Delta!.Value, 6);
    }

    [Fact]
    public void A_window_with_no_reel_picks_reports_no_gap_rather_than_a_zero()
    {
        var window = RetroLoopQuery.MatchedWindow("w", null, [new(8, Now, ViaReel: false)]);

        Assert.Equal((0, 0, 0), (window.ViaReelCount, window.OtherCount, window.ViaReelUnmatchedCount));
        Assert.Null(window.ViaReelMeanRating);
        Assert.Null(window.OtherMeanRating);
        Assert.Null(window.Delta);
    }

    [Fact]
    public async Task An_unscoped_context_is_refused_rather_than_aggregating_every_tenant()
    {
        if (!fixture.Available)
        {
            return;
        }

        await using var db = fixture.CreateContext(new AccountContext());
        var query = new RetroLoopQuery(db, new AccountContext());

        await Assert.ThrowsAsync<InvalidOperationException>(() => query.ComputeAsync(Now, CancellationToken.None));
    }

    private async Task<RetroLoopReport> ComputeAsync(Guid accountId)
    {
        var context = new AccountContext();
        context.SetAccount(accountId);
        await using var db = fixture.CreateContext(context);
        return await new RetroLoopQuery(db, context).ComputeAsync(Now, CancellationToken.None);
    }

    private async Task<(Guid AccountA, Guid AccountB)> SeedAsync()
    {
        await using var db = fixture.CreateContext(new AccountContext());

        var accountA = NewAccount("retro-a");
        var accountB = NewAccount("retro-b");
        db.Accounts.AddRange(accountA, accountB);

        var artifactOld = Guid.NewGuid();
        var artifactNew = Guid.NewGuid();

        // A: a conversion the model badly over-promised, and one it nearly got right.
        var badly = NewTitle("A-converted-badly", 2019);
        var nearly = NewTitle("A-converted-nearly", 2021);
        // A: rated on the user's own initiative, but scored long before — a wider pair, not a conversion.
        var selfFound = NewTitle("A-self-found", 2015);
        // A: surfaced on the feed only (no managed-list row) — the second arm of "came via Reel".
        var feedOnly = NewTitle("A-feed-only", 2023);
        // A: an old rating outside the rolling window, so all-time and last-365 differ.
        var ancient = NewTitle("A-ancient", 2004);
        var bTitle = NewTitle("B-converted", 2020);
        db.Titles.AddRange(badly, nearly, selfFound, feedOnly, ancient, bTitle);

        db.ManagedListItems.AddRange(
            Removed(accountA.Id, badly.Id, Now.AddDays(-10)),
            Removed(accountA.Id, nearly.Id, Now.AddDays(-3)),
            Removed(accountB.Id, bTitle.Id, Now.AddDays(-6)));

        db.TitleScores.AddRange(
            Score(accountA.Id, badly.Id, artifactOld, 8.00m, Now.AddDays(-20)),
            // Written after the removal: the loop must ignore it, or every conversion gets graded by
            // a model that already knew the answer.
            Score(accountA.Id, badly.Id, artifactNew, 3.00m, Now.AddDays(-5)),
            Score(accountA.Id, nearly.Id, artifactOld, 6.00m, Now.AddDays(-4)),
            Score(accountA.Id, selfFound.Id, artifactOld, 5.00m, Now.AddDays(-30)),
            Score(accountB.Id, bTitle.Id, artifactOld, 9.00m, Now.AddDays(-8)));

        db.UserRatings.AddRange(
            Rating(accountA.Id, badly.Id, 4, Now.AddDays(-9)),
            Rating(accountA.Id, nearly.Id, 7, Now.AddDays(-2)),
            Rating(accountA.Id, selfFound.Id, 8, Now.AddDays(-1)),
            Rating(accountA.Id, feedOnly.Id, 6, Now.AddDays(-7)),
            Rating(accountA.Id, ancient.Id, 10, Now.AddDays(-400)),
            Rating(accountB.Id, bTitle.Id, 9, Now.AddDays(-5)));

        var snapshot = new FeedSnapshot
        {
            Id = Guid.NewGuid(),
            AccountId = accountA.Id,
            ModelArtifactId = artifactOld,
            GeneratedAt = Now.AddDays(-8),
            Status = SnapshotStatus.Superseded,
        };
        db.FeedSnapshots.Add(snapshot);
        db.FeedItems.Add(new FeedItem
        {
            Id = Guid.NewGuid(),
            AccountId = accountA.Id,
            FeedSnapshotId = snapshot.Id,
            Row = FeedRowKind.Hero,
            Rank = 0,
            TitleId = feedOnly.Id,
            PredictedRating = 7.0m,
            FinalScore = 7.0m,
        });

        await db.SaveChangesAsync(CancellationToken.None);
        return (accountA.Id, accountB.Id);
    }

    /// <summary>
    /// An account whose own ratings drift down over the years, with Reel picks in two recent periods
    /// and one isolated pick. Self-found: ten 8s in 2020; 5s at −100, −98 and −102 days; 6s at −30 and
    /// −32. Via Reel: 5s at −99 and −101, <paramref name="secondPeriodPick"/> at −31, and a 2 at −60.
    /// </summary>
    private async Task<Guid> SeedDriftingAccountAsync(short secondPeriodPick)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var account = NewAccount("retro-drift");
        db.Accounts.Add(account);

        void Rate(string name, short rating, DateTime at, bool viaReel)
        {
            var title = NewTitle(name, 2010);
            db.Titles.Add(title);
            db.UserRatings.Add(Rating(account.Id, title.Id, rating, at));
            if (viaReel)
            {
                db.ManagedListItems.Add(Removed(account.Id, title.Id, at.AddDays(-1)));
            }
        }

        for (var i = 0; i < 10; i++)
        {
            Rate($"drift-2020-{i}", 8, new DateTime(2020, 3, 1 + i, 0, 0, 0, DateTimeKind.Utc), viaReel: false);
        }

        Rate("drift-own-100", 5, Now.AddDays(-100), viaReel: false);
        Rate("drift-own-98", 5, Now.AddDays(-98), viaReel: false);
        Rate("drift-own-102", 5, Now.AddDays(-102), viaReel: false);
        Rate("drift-own-30", 6, Now.AddDays(-30), viaReel: false);
        Rate("drift-own-32", 6, Now.AddDays(-32), viaReel: false);

        Rate("drift-reel-99", 5, Now.AddDays(-99), viaReel: true);
        Rate("drift-reel-101", 5, Now.AddDays(-101), viaReel: true);
        Rate("drift-reel-31", secondPeriodPick, Now.AddDays(-31), viaReel: true);
        Rate("drift-reel-lonely", 2, Now.AddDays(-60), viaReel: true);

        await db.SaveChangesAsync(CancellationToken.None);
        return account.Id;
    }

    private static Account NewAccount(string prefix) => new()
    {
        Id = Guid.NewGuid(),
        TraktUserSlug = $"{prefix}-{Guid.NewGuid():N}",
        CreatedAt = Now,
        LastSeenAt = Now,
        PipelineStageChangedAt = Now,
    };

    private static Title NewTitle(string name, int year) => new()
    {
        Id = Guid.NewGuid(),
        MediaType = MediaType.Movie,
        TraktId = Random.Shared.NextInt64(1, long.MaxValue),
        TraktSlug = $"{name.ToLowerInvariant()}-{Guid.NewGuid():N}",
        Name = name,
        Year = year,
        CreatedAt = Now,
    };

    private static ManagedListItem Removed(Guid accountId, Guid titleId, DateTime removedAt) => new()
    {
        Id = Guid.NewGuid(),
        AccountId = accountId,
        TitleId = titleId,
        AddedAt = removedAt.AddDays(-20),
        RemovedAt = removedAt,
        RemovalReason = ListRemovalReason.Watched,
    };

    private static TitleScore Score(Guid accountId, Guid titleId, Guid artifactId, decimal predicted, DateTime scoredAt) => new()
    {
        Id = Guid.NewGuid(),
        AccountId = accountId,
        TitleId = titleId,
        ModelArtifactId = artifactId,
        PredictedRating = predicted,
        ScoredAt = scoredAt,
    };

    private static UserRating Rating(Guid accountId, Guid titleId, short rating, DateTime ratedAt) => new()
    {
        Id = Guid.NewGuid(),
        AccountId = accountId,
        TitleId = titleId,
        SubjectType = RatingSubjectType.Movie,
        Rating = rating,
        RatedAt = ratedAt,
        SyncedAt = ratedAt,
        Source = RatingSource.Trakt,
    };
}
