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

        // Counterfactual: managed-list titles (4, 7) plus the feed-surfaced title (6) against what
        // the user found alone — 8 inside the year, 8 and 10 all-time.
        var recent = reportA.Counterfactual.Single(w => w.Days == RetroLoopQuery.RecentWindowDays);
        Assert.Equal(3, recent.ViaReelCount);
        Assert.Equal((4d + 7d + 6d) / 3d, recent.ViaReelMeanRating!.Value, 6);
        Assert.Equal(1, recent.OtherCount);
        Assert.Equal(8d, recent.OtherMeanRating!.Value, 6);
        Assert.Equal(((4d + 7d + 6d) / 3d) - 8d, recent.Delta!.Value, 6);

        var allTime = reportA.Counterfactual.Single(w => w.Days is null);
        Assert.Equal(3, allTime.ViaReelCount);
        Assert.Equal(2, allTime.OtherCount);
        Assert.Equal(9d, allTime.OtherMeanRating!.Value, 6);

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
