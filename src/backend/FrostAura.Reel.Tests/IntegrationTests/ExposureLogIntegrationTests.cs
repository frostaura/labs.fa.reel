using FrostAura.Reel.Application.Ml;
using FrostAura.Reel.Application.Ml.ClosedLoop;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Feed;
using FrostAura.Reel.Domain.Library;
using FrostAura.Reel.Domain.Ml;
using FrostAura.Reel.Domain.Sync;
using FrostAura.Reel.Domain.Tenancy;
using FrostAura.Reel.Infrastructure.Persistence;
using FrostAura.Reel.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrostAura.Reel.Tests.IntegrationTests;

/// <summary>
/// The closed loop on real Postgres. Three things are proved here that no in-memory fake can:
/// that the generated minute-bucket column and its NULLS-NOT-DISTINCT unique index behave as
/// designed, that the outcome view's causal guard actually holds in SQL, and that the whole thing
/// is per-tenant — the queries never name an account id, so isolation rests entirely on the global
/// query filters plus the view's own AccountId predicates.
/// </summary>
[Collection("postgres")]
public class ExposureLogIntegrationTests(PostgresFixture fixture)
{
    private static readonly DateTime Now = new(2026, 9, 10, 19, 17, 30, DateTimeKind.Utc);

    /// <summary>
    /// The only thing <see cref="AccuracyQuery"/> asks a judge: which prompt generation is being
    /// served, so the cross-reference block can mark the backtest row that governs today's feed.
    /// Pinned here rather than taken from the adapter so a version bump does not silently rewrite
    /// what these tests assert.
    /// </summary>
    private static readonly FixedVersionCrossReferenceJudge CurrentJudge = new(2);

    [Fact]
    public async Task A_refetch_inside_the_same_minute_is_one_exposure_and_the_next_minute_is_another()
    {
        if (!fixture.Available)
        {
            return; // no Postgres available — covered in CI's backend-integration job
        }

        var (accountId, titleId, artifactId, snapshotId) = await SeedAsync("idempotency");

        var log = LogFor(accountId, out var db1);
        await using (db1)
        {
            // Three reads of the same page, seconds apart — exactly what RTK Query does on mount
            // plus a tag invalidation plus a manual refresh.
            for (var i = 0; i < 3; i++)
            {
                await log.RecordAsync(
                    accountId, ExposureSurface.FeedRefill, snapshotId, artifactId, Now.AddSeconds(i * 7),
                    [new ExposureCard(titleId, 0, 8.42m)], CancellationToken.None);
            }
        }

        var later = LogFor(accountId, out var db2);
        await using (db2)
        {
            await later.RecordAsync(
                accountId, ExposureSurface.FeedRefill, snapshotId, artifactId, Now.AddMinutes(1),
                [new ExposureCard(titleId, 0, 8.42m)], CancellationToken.None);
        }

        await using var verify = fixture.CreateContext(Scoped(accountId));
        var rows = await verify.ExposureEvents.OrderBy(e => e.OccurredAt).ToListAsync();

        Assert.Equal(2, rows.Count);
        Assert.Equal(Now.AddSeconds(0), rows[0].OccurredAt);
        // Postgres computed the bucket; the application never wrote it.
        Assert.Equal(new DateTime(2026, 9, 10, 19, 17, 0, DateTimeKind.Utc), rows[0].MinuteBucket);
        Assert.Equal(new DateTime(2026, 9, 10, 19, 18, 0, DateTimeKind.Utc), rows[1].MinuteBucket);
    }

    [Fact]
    public async Task A_snapshotless_surface_still_de_duplicates_because_the_index_treats_nulls_as_equal()
    {
        if (!fixture.Available)
        {
            return;
        }

        // The digest, Ask Reel and the similar rail all carry a null FeedSnapshotId. Under
        // Postgres's DEFAULT nulls-distinct behaviour those rows would never collide and every
        // refetch would insert a duplicate — silently inflating the denominator on exactly the
        // surfaces with the least other evidence behind them.
        var (accountId, titleId, artifactId, _) = await SeedAsync("nulls");

        for (var i = 0; i < 3; i++)
        {
            var log = LogFor(accountId, out var db);
            await using (db)
            {
                await log.RecordAsync(
                    accountId, ExposureSurface.Digest, feedSnapshotId: null, artifactId, Now.AddSeconds(i * 5),
                    [new ExposureCard(titleId, 0, 7.10m)], CancellationToken.None);
            }
        }

        await using var verify = fixture.CreateContext(Scoped(accountId));
        Assert.Equal(1, await verify.ExposureEvents.CountAsync());
    }

    [Fact]
    public async Task A_concurrent_race_on_the_idempotency_index_never_reaches_the_caller()
    {
        if (!fixture.Available)
        {
            return;
        }

        // The in-memory dedupe handles the ordinary case, but two requests can pass the read at the
        // same instant. The loser then hits a 23505 — which must be swallowed, because a rendered
        // page is worth more than the row that records it.
        var (accountId, titleId, artifactId, snapshotId) = await SeedAsync("race");

        await using var first = fixture.CreateContext(Scoped(accountId));
        await using var second = fixture.CreateContext(Scoped(accountId));
        var logA = new ExposureLog(first, NullLogger<ExposureLog>.Instance);
        var logB = new ExposureLog(second, NullLogger<ExposureLog>.Instance);

        // Both stage before either saves — the read-then-insert race, made deterministic.
        await logA.Stage(accountId, ExposureSurface.FeedPage, snapshotId, artifactId, Now, [new ExposureCard(titleId, 0, 6m)], CancellationToken.None);
        await logB.Stage(accountId, ExposureSurface.FeedPage, snapshotId, artifactId, Now, [new ExposureCard(titleId, 0, 6m)], CancellationToken.None);
        await first.SaveChangesAsync();

        var written = await logB.RecordAsync(
            accountId, ExposureSurface.FeedPage, snapshotId, artifactId, Now,
            [new ExposureCard(titleId, 0, 6m)], CancellationToken.None);

        Assert.Equal(0, written); // absorbed, not thrown
        await using var verify = fixture.CreateContext(Scoped(accountId));
        Assert.Equal(1, await verify.ExposureEvents.CountAsync());
    }

    [Fact]
    public async Task The_outcome_view_refuses_to_count_an_action_that_predates_its_exposure()
    {
        if (!fixture.Available)
        {
            return;
        }

        // The whole point of the temporal join. A title the user rated last year, then saw on the
        // feed today, is not a conversion — and a view that counted it would flatter every number
        // on the track record with outcomes the product had nothing to do with.
        var (accountId, _, artifactId, snapshotId) = await SeedAsync("causality");

        await using (var seed = fixture.CreateContext(Scoped(accountId)))
        {
            var before = NewTitle("rated-before-shown");
            var after = NewTitle("rated-after-shown");
            var pending = NewTitle("never-acted-on");
            seed.Titles.AddRange(before, after, pending);

            seed.UserRatings.AddRange(
                Rating(accountId, before.Id, 9, Now.AddDays(-30)),
                Rating(accountId, after.Id, 4, Now.AddDays(2)));

            var log = new ExposureLog(seed, NullLogger<ExposureLog>.Instance);
            await log.Stage(
                accountId, ExposureSurface.FeedHero, snapshotId, artifactId, Now,
                [
                    new ExposureCard(before.Id, 0, 9.5m),
                    new ExposureCard(after.Id, 1, 8.8m),
                    new ExposureCard(pending.Id, 2, 7.2m),
                ],
                CancellationToken.None);
            await seed.SaveChangesAsync();
        }

        await using var verify = fixture.CreateContext(Scoped(accountId));
        var outcomes = await verify.ExposureOutcomes.ToListAsync();
        var byName = await NamesAsync(verify, outcomes.Select(o => o.TitleId).ToList());

        Assert.Equal(3, outcomes.Count);

        var beforeRow = outcomes.Single(o => byName[o.TitleId] == "rated-before-shown");
        Assert.Null(beforeRow.ActualRating);
        Assert.Null(beforeRow.RatedAt);
        Assert.Equal(ExposureOutcomeKind.Pending, beforeRow.Outcome);

        var afterRow = outcomes.Single(o => byName[o.TitleId] == "rated-after-shown");
        Assert.Equal((short)4, afterRow.ActualRating);
        Assert.Equal(ExposureOutcomeKind.Converted, afterRow.Outcome);

        Assert.Equal(ExposureOutcomeKind.Pending, outcomes.Single(o => byName[o.TitleId] == "never-acted-on").Outcome);
    }

    [Fact]
    public async Task The_outcome_view_ranks_saved_and_dismissed_beneath_converted()
    {
        if (!fixture.Available)
        {
            return;
        }

        var (accountId, _, artifactId, snapshotId) = await SeedAsync("ladder");

        await using (var seed = fixture.CreateContext(Scoped(accountId)))
        {
            var saved = NewTitle("saved-not-yet-watched");
            var dismissed = NewTitle("said-no");
            var revoked = NewTitle("saved-then-unsaved");
            seed.Titles.AddRange(saved, dismissed, revoked);

            seed.UserTitleReactions.AddRange(
                new UserTitleReaction { Id = Guid.NewGuid(), AccountId = accountId, TitleId = saved.Id, Kind = ReactionKind.SaveForLater, CreatedAt = Now.AddHours(1) },
                new UserTitleReaction { Id = Guid.NewGuid(), AccountId = accountId, TitleId = dismissed.Id, Kind = ReactionKind.NotInterested, CreatedAt = Now.AddHours(1) },
                // Revoked reactions must NOT count — and, being several rows on one title, they are
                // also the case that would multiply an exposure into duplicates under a plain join.
                new UserTitleReaction { Id = Guid.NewGuid(), AccountId = accountId, TitleId = revoked.Id, Kind = ReactionKind.SaveForLater, CreatedAt = Now.AddHours(1), RevokedAt = Now.AddHours(2) },
                new UserTitleReaction { Id = Guid.NewGuid(), AccountId = accountId, TitleId = revoked.Id, Kind = ReactionKind.NotInterested, CreatedAt = Now.AddHours(3), RevokedAt = Now.AddHours(4) });

            var log = new ExposureLog(seed, NullLogger<ExposureLog>.Instance);
            await log.Stage(
                accountId, ExposureSurface.FeedRow, snapshotId, artifactId, Now,
                [
                    new ExposureCard(saved.Id, 0, 8m),
                    new ExposureCard(dismissed.Id, 1, 7m),
                    new ExposureCard(revoked.Id, 2, 6m),
                ],
                CancellationToken.None);
            await seed.SaveChangesAsync();
        }

        await using var verify = fixture.CreateContext(Scoped(accountId));
        var outcomes = await verify.ExposureOutcomes.ToListAsync();
        var byName = await NamesAsync(verify, outcomes.Select(o => o.TitleId).ToList());

        // One row per exposure, never more: the revoked title has two reactions behind it.
        Assert.Equal(3, outcomes.Count);
        Assert.Equal(ExposureOutcomeKind.Saved, outcomes.Single(o => byName[o.TitleId] == "saved-not-yet-watched").Outcome);
        Assert.Equal(ExposureOutcomeKind.Dismissed, outcomes.Single(o => byName[o.TitleId] == "said-no").Outcome);
        Assert.Equal(ExposureOutcomeKind.Pending, outcomes.Single(o => byName[o.TitleId] == "saved-then-unsaved").Outcome);
    }

    [Fact]
    public async Task The_rollup_counts_minutes_rather_than_rows_and_keeps_the_best_rank()
    {
        if (!fixture.Available)
        {
            return;
        }

        var (accountId, titleId, artifactId, snapshotId) = await SeedAsync("rollup");

        // Three genuinely separate serves, at falling ranks and drifting predictions — the exact
        // shape the "it keeps sliding down the page" complaint describes and that a de-duplicated
        // counter would have destroyed.
        var serves = new[] { (0, 8.4m), (3, 7.9m), (6, 6.6m) };
        for (var i = 0; i < serves.Length; i++)
        {
            var log = LogFor(accountId, out var db);
            await using (db)
            {
                await log.RecordAsync(
                    accountId, ExposureSurface.FeedHero, snapshotId, artifactId, Now.AddMinutes(i),
                    [new ExposureCard(titleId, serves[i].Item1, serves[i].Item2)], CancellationToken.None);
            }
        }

        await using var verify = fixture.CreateContext(Scoped(accountId));
        var rollup = await verify.TitleExposureRollups.SingleAsync();

        Assert.Equal(3, rollup.ShowCount);
        Assert.Equal(0, rollup.MinRank);
        Assert.Equal(Now, rollup.FirstShownAt);
        Assert.Equal(Now.AddMinutes(2), rollup.LastShownAt);
        Assert.Equal(6.60m, rollup.MinPredicted);
        Assert.Equal(8.40m, rollup.MaxPredicted);
    }

    [Fact]
    public async Task The_seven_snapshot_prune_no_longer_destroys_the_record_of_what_was_served()
    {
        if (!fixture.Available)
        {
            return;
        }

        // This is the guard that makes keeping the prune at 7 defensible. Before the exposure log,
        // nine minutes of queueing evicted the entire history; now the snapshot can go and the
        // evidence stays, because ExposureEvent.FeedSnapshotId carries no foreign key.
        var (accountId, titleId, artifactId, snapshotId) = await SeedAsync("prune");

        var log = LogFor(accountId, out var db);
        await using (db)
        {
            await log.RecordAsync(
                accountId, ExposureSurface.FeedHero, snapshotId, artifactId, Now,
                [new ExposureCard(titleId, 0, 8m)], CancellationToken.None);
        }

        await using (var prune = fixture.CreateContext(Scoped(accountId)))
        {
            prune.FeedSnapshots.RemoveRange(await prune.FeedSnapshots.ToListAsync());
            await prune.SaveChangesAsync();
        }

        await using var verify = fixture.CreateContext(Scoped(accountId));
        Assert.Empty(await verify.FeedSnapshots.ToListAsync());
        var survivor = await verify.ExposureEvents.SingleAsync();
        Assert.Equal(snapshotId, survivor.FeedSnapshotId);
    }

    [Fact]
    public async Task One_tenants_exposures_and_outcomes_are_invisible_to_another()
    {
        if (!fixture.Available)
        {
            return;
        }

        var a = await SeedAsync("tenant-a");
        var b = await SeedAsync("tenant-b");

        foreach (var (accountId, titleId, artifactId, snapshotId) in new[] { a, b })
        {
            var log = LogFor(accountId, out var db);
            await using (db)
            {
                await log.RecordAsync(
                    accountId, ExposureSurface.FeedHero, snapshotId, artifactId, Now,
                    [new ExposureCard(titleId, 0, 8m)], CancellationToken.None);
            }
        }

        await using var asA = fixture.CreateContext(Scoped(a.AccountId));
        var exposuresA = await asA.ExposureEvents.ToListAsync();
        var outcomesA = await asA.ExposureOutcomes.ToListAsync();
        var rollupA = await asA.TitleExposureRollups.ToListAsync();

        Assert.Single(exposuresA);
        Assert.Single(outcomesA);
        Assert.Single(rollupA);
        Assert.All(exposuresA, e => Assert.Equal(a.AccountId, e.AccountId));
        Assert.All(outcomesA, o => Assert.Equal(a.AccountId, o.AccountId));
        Assert.DoesNotContain(exposuresA, e => e.TitleId == b.TitleId);
        Assert.DoesNotContain(outcomesA, o => o.TitleId == b.TitleId);
        Assert.DoesNotContain(rollupA, r => r.TitleId == b.TitleId);
    }

    [Fact]
    public async Task The_accuracy_surface_falls_back_to_the_retro_join_before_the_exposure_log_has_history()
    {
        if (!fixture.Available)
        {
            return;
        }

        // Day one. The exposure table is empty, and a track record reading "no data" on the day it
        // ships is the opposite of honest from day one — so the same scores that already exist are
        // joined to the ratings that followed them, labelled `retro` so the weaker evidence is
        // visible rather than smoothed over.
        var (accountId, _, artifactId, _) = await SeedAsync("retro-fallback");

        await using (var seed = fixture.CreateContext(Scoped(accountId)))
        {
            var title = NewTitle("scored-then-rated");
            seed.Titles.Add(title);
            seed.TitleScores.Add(new TitleScore
            {
                Id = Guid.NewGuid(),
                AccountId = accountId,
                TitleId = title.Id,
                ModelArtifactId = artifactId,
                PredictedRating = 8.40m,
                ScoredAt = Now.AddDays(-5),
            });
            seed.UserRatings.Add(Rating(accountId, title.Id, 5, Now.AddDays(-1)));
            await seed.SaveChangesAsync();
        }

        var report = await ComputeAccuracyAsync(accountId, "90d");

        Assert.Equal(AccuracySource.Retro, report.Source);
        Assert.Equal(1, report.Headline.Count);
        Assert.Equal(3.4d, report.Headline.Bias, 6);      // over-promised by 3.4 points
        Assert.Equal(0d, report.Headline.WithinOnePointRate, 6);
        // The retro arm cannot prove the user saw the number, so it must not claim a surface.
        var moment = Assert.Single(report.Moments);
        Assert.Null(moment.Surface);
        Assert.Null(moment.ShownAt);
        Assert.Null(moment.DaysToOutcome);
    }

    [Fact]
    public async Task The_accuracy_surface_prefers_the_exposure_log_once_it_has_history()
    {
        if (!fixture.Available)
        {
            return;
        }

        var (accountId, _, artifactId, snapshotId) = await SeedAsync("exposure-preferred");

        await using (var seed = fixture.CreateContext(Scoped(accountId)))
        {
            var title = NewTitle("shown-then-rated");
            seed.Titles.Add(title);
            seed.UserRatings.Add(Rating(accountId, title.Id, 5, Now.AddDays(-1)));

            var log = new ExposureLog(seed, NullLogger<ExposureLog>.Instance);
            // Two serves of the same card. The EARLIEST is the one scored, because "predicted 8.4
            // twelve days before you rated it 5" is the honest framing — taking the latest would
            // flatter the number by grading the most recently recalibrated promise.
            await log.Stage(accountId, ExposureSurface.FeedHero, snapshotId, artifactId, Now.AddDays(-13), [new ExposureCard(title.Id, 0, 8.40m)], CancellationToken.None);
            await log.Stage(accountId, ExposureSurface.FeedPage, feedSnapshotId: null, artifactId, Now.AddDays(-2), [new ExposureCard(title.Id, 4, 5.20m)], CancellationToken.None);
            await seed.SaveChangesAsync();
        }

        var report = await ComputeAccuracyAsync(accountId, "90d");

        Assert.Equal(AccuracySource.Exposures, report.Source);
        Assert.Equal(1, report.Headline.Count);
        var moment = Assert.Single(report.Moments);
        Assert.Equal(8.40d, moment.Predicted, 6);
        Assert.Equal(nameof(ExposureSurface.FeedHero), moment.Surface);
        Assert.Equal(12, moment.DaysToOutcome);
    }

    [Fact]
    public async Task The_after_rating_verdict_reads_the_exposure_first_and_the_score_as_a_fallback()
    {
        if (!fixture.Available)
        {
            return;
        }

        var (accountId, _, artifactId, snapshotId) = await SeedAsync("verdict");
        var ratedAt = Now.AddDays(1);

        Guid exposedId, scoredOnlyId, unknownId;
        await using (var seed = fixture.CreateContext(Scoped(accountId)))
        {
            var exposed = NewTitle("exposed");
            var scoredOnly = NewTitle("scored-only");
            var unknown = NewTitle("never-mentioned");
            seed.Titles.AddRange(exposed, scoredOnly, unknown);
            (exposedId, scoredOnlyId, unknownId) = (exposed.Id, scoredOnly.Id, unknown.Id);

            seed.TitleScores.AddRange(
                // A score also exists for the exposed title; the exposure must win, because only it
                // proves the number was on screen.
                new TitleScore { Id = Guid.NewGuid(), AccountId = accountId, TitleId = exposed.Id, ModelArtifactId = artifactId, PredictedRating = 3.00m, ScoredAt = Now.AddDays(-1) },
                new TitleScore { Id = Guid.NewGuid(), AccountId = accountId, TitleId = scoredOnly.Id, ModelArtifactId = artifactId, PredictedRating = 7.25m, ScoredAt = Now.AddDays(-1) });

            var log = new ExposureLog(seed, NullLogger<ExposureLog>.Instance);
            await log.Stage(accountId, ExposureSurface.FeedHero, snapshotId, artifactId, Now, [new ExposureCard(exposedId, 0, 8.40m)], CancellationToken.None);
            await seed.SaveChangesAsync();
        }

        await using var db = fixture.CreateContext(Scoped(accountId));
        var verdicts = new RatingVerdictQuery(db);

        var strong = await verdicts.ForAsync(accountId, exposedId, 5, ratedAt, CancellationToken.None);
        Assert.NotNull(strong);
        Assert.Equal(8.40m, strong!.Value.PredictedBefore);
        Assert.Equal(nameof(ExposureSurface.FeedHero), strong.Value.Surface);
        Assert.Equal(3.40m, strong.Value.Delta);

        var weak = await verdicts.ForAsync(accountId, scoredOnlyId, 7, ratedAt, CancellationToken.None);
        Assert.NotNull(weak);
        Assert.Equal(7.25m, weak!.Value.PredictedBefore);
        Assert.Null(weak.Value.Surface); // a score is not proof the user saw it
        Assert.Null(weak.Value.ShownAt);

        // Nothing to say → nothing said. A toast reading "Reel said — · you said 7" is worse than
        // no toast at all.
        Assert.Null(await verdicts.ForAsync(accountId, unknownId, 7, ratedAt, CancellationToken.None));
    }

    [Fact]
    public async Task The_outcome_view_reproduces_the_founders_own_seventy_three_conversion_numbers()
    {
        if (!fixture.Available)
        {
            return;
        }

        // The regression guard on the number the whole programme turns on. Read off the live
        // database on 2026-09-09: 73 titles reached the founder through Reel, were watched and were
        // rated; Reel predicted a mean of 6.81 and he gave them a mean of 5.78 — so the product
        // steered him toward films he liked LESS than his own lifetime average, over-promising by
        // just over a point, with 37% rated at 5 or below.
        //
        // Fed the same shape, the outcome view and the accuracy headline must report exactly that.
        // If a later change to the join, the guard or the rounding quietly improves these numbers,
        // this test is what says the improvement is in the measurement, not the model.
        var (accountId, _, artifactId, snapshotId) = await SeedAsync("seventy-three");

        // 27 fours (37% at ≤5), 8 sixes, 38 sevens → 422 / 73 = 5.7808 → 5.78.
        var actuals = Enumerable.Repeat((short)4, 27)
            .Concat(Enumerable.Repeat((short)6, 8))
            .Concat(Enumerable.Repeat((short)7, 38))
            .ToList();
        // 71 at 6.81 plus one either side, so the mean is exactly 6.81 and the predictions still
        // carry variance — a zero-variance side would make the correlation undefined and hide the
        // most damning number of the three.
        var predictions = Enumerable.Repeat(6.81m, 71).Append(6.31m).Append(7.31m).ToList();

        Assert.Equal(73, actuals.Count);
        Assert.Equal(73, predictions.Count);

        await using (var seed = fixture.CreateContext(Scoped(accountId)))
        {
            var cards = new List<ExposureCard>(73);
            for (var i = 0; i < 73; i++)
            {
                var title = NewTitle($"conversion-{i:D2}");
                seed.Titles.Add(title);
                seed.UserRatings.Add(Rating(accountId, title.Id, actuals[i], Now.AddDays(1)));
                cards.Add(new ExposureCard(title.Id, i, predictions[i]));
            }

            var log = new ExposureLog(seed, NullLogger<ExposureLog>.Instance);
            await log.Stage(accountId, ExposureSurface.FeedHero, snapshotId, artifactId, Now, cards, CancellationToken.None);
            await seed.SaveChangesAsync();
        }

        await using (var verify = fixture.CreateContext(Scoped(accountId)))
        {
            var outcomes = await verify.ExposureOutcomes.ToListAsync();

            Assert.Equal(73, outcomes.Count);
            Assert.All(outcomes, o => Assert.Equal(ExposureOutcomeKind.Converted, o.Outcome));
            Assert.Equal(5.78d, outcomes.Average(o => (double)o.ActualRating!.Value), 2);
            Assert.Equal(6.81d, outcomes.Average(o => (double)o.PredictedRating), 2);
            Assert.Equal(27, outcomes.Count(o => o.ActualRating <= 5));
        }

        var report = await ComputeAccuracyAsync(accountId, "90d");

        Assert.Equal(AccuracySource.Exposures, report.Source);
        Assert.Equal(73, report.Headline.Count);
        Assert.Equal(6.81d, report.Headline.MeanPredicted, 2);
        Assert.Equal(5.78d, report.Headline.MeanActual, 2);
        // Over-promising by just over a point, exactly as the live join says.
        Assert.Equal(1.03d, report.Headline.Bias, 2);
        // 46 of 73 land within a point (the sixes and sevens); the 27 fours do not.
        Assert.Equal(46d / 73d, report.Headline.WithinOnePointRate, 4);
        // The moments strip is capped, and the cap is what keeps the page renderable on an account
        // with a real history rather than a fixture.
        Assert.Equal(AccuracyQuery.MomentLimit, report.Moments.Count);
    }

    [Fact]
    public async Task An_unscoped_context_is_refused_rather_than_aggregating_every_tenant()
    {
        if (!fixture.Available)
        {
            return;
        }

        await using var db = fixture.CreateContext(new AccountContext());
        var unscoped = new AccountContext();
        var query = new AccuracyQuery(db, unscoped, new RetroLoopQuery(db, unscoped), new ModelLearnedQuery(db), CurrentJudge);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => query.ComputeAsync("90d", Now, CancellationToken.None));
    }

    [Fact]
    public async Task An_unknown_window_is_refused_rather_than_silently_defaulted()
    {
        if (!fixture.Available)
        {
            return;
        }

        var (accountId, _, _, _) = await SeedAsync("window");
        var context = Scoped(accountId);
        await using var db = fixture.CreateContext(context);
        var query = new AccuracyQuery(db, context, new RetroLoopQuery(db, context), new ModelLearnedQuery(db), CurrentJudge);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => query.ComputeAsync("1y", Now, CancellationToken.None));
    }

    // ── The cross-reference block ───────────────────────────────────────────────────────────

    [Fact]
    public async Task The_cross_reference_standing_is_written_only_when_both_halves_are_present()
    {
        if (!fixture.Available)
        {
            return;
        }

        // The verdict and its confidence travel as a pair, exactly as the interleaving attribution
        // does: a confidence with no verdict is a number with no claim attached, and a verdict with
        // no confidence cannot be read back against the backtest that judges it.
        var (accountId, titleId, artifactId, snapshotId) = await SeedAsync("xrefpair");
        var other = NewTitle("xrefpair-half");
        var third = NewTitle("xrefpair-none");
        await using (var seed = fixture.CreateContext(new AccountContext()))
        {
            seed.Titles.AddRange(other, third);
            await seed.SaveChangesAsync();
        }

        var log = LogFor(accountId, out var db);
        await using (db)
        {
            await log.RecordAsync(
                accountId, ExposureSurface.FeedHero, snapshotId, artifactId, Now,
                [
                    new ExposureCard(titleId, 0, 8.42m, XrefVerdict: "ProvisionalKeep", XrefConfidence: 0.8123m),
                    // Half a pair, from either side. Both record nothing rather than half a fact.
                    new ExposureCard(other.Id, 1, 7.10m, XrefVerdict: "Out"),
                    new ExposureCard(third.Id, 2, 6.40m, XrefConfidence: 0.9m),
                ],
                CancellationToken.None);
        }

        await using var verify = fixture.CreateContext(Scoped(accountId));
        var rows = await verify.ExposureEvents.OrderBy(e => e.Rank).ToListAsync();

        Assert.Equal("ProvisionalKeep", rows[0].XrefVerdict);
        // Rounded to the column's three decimals, here and nowhere else.
        Assert.Equal(0.812m, rows[0].XrefConfidence);
        Assert.Null(rows[1].XrefVerdict);
        Assert.Null(rows[1].XrefConfidence);
        Assert.Null(rows[2].XrefVerdict);
        Assert.Null(rows[2].XrefConfidence);
    }

    [Fact]
    public async Task The_cross_reference_block_answers_per_standing_and_refuses_below_the_threshold()
    {
        if (!fixture.Available)
        {
            return;
        }

        // Two standings with deliberately different sample sizes: one over the threshold, one far
        // under it. The under-sampled one must still report its count — the page says "not yet
        // answerable — N outcomes so far" off exactly this — rather than disappearing or, worse,
        // rendering a mean over three titles beside a mean over thirty.
        var (accountId, _, artifactId, snapshotId) = await SeedAsync("xrefblock");
        var keepRatings = new short[] { 9, 9, 8, 8, 7, 7, 6, 6, 5, 4, 9, 8, 8, 7, 7, 6, 6, 5, 9, 8 };
        var outRatings = new short[] { 3, 4, 9 };

        await SeedJudgedExposuresAsync(accountId, artifactId, snapshotId, "ProvisionalKeep", 0.8m, keepRatings);
        await SeedJudgedExposuresAsync(accountId, artifactId, snapshotId, "Out", 0.9m, outRatings);
        // A card served before anything had judged it. NULL is not a standing and must not become
        // one — it is by far the larger set mid-pass, and folding it in would put thousands of
        // unjudged serves behind a number describing a judgement.
        await SeedJudgedExposuresAsync(accountId, artifactId, snapshotId, verdict: null, null, [10, 10, 2]);

        var report = await ComputeAccuracyAsync(accountId, "all");
        var online = report.CrossReference.Online.ToDictionary(o => o.Verdict, o => o);

        Assert.Equal(2, online.Count);
        Assert.Equal(20, report.CrossReference.MinimumOutcomes);

        var keep = online["ProvisionalKeep"];
        Assert.Equal(keepRatings.Length, keep.Count);
        Assert.True(keep.Answerable);
        Assert.Equal(keepRatings.Average(r => (double)r), keep.MeanLaterRating!.Value, 6);
        Assert.Equal(keepRatings.Count(r => r >= 8) / (double)keepRatings.Length, keep.LovedRate!.Value, 6);
        Assert.Equal(keepRatings.Count(r => r <= 4) / (double)keepRatings.Length, keep.DislikedRate!.Value, 6);

        var ruledOut = online["Out"];
        Assert.Equal(outRatings.Length, ruledOut.Count);
        Assert.False(ruledOut.Answerable);
        // The numbers are still computed — the page decides what to say about them, and it needs
        // the count to say it.
        Assert.Equal(outRatings.Average(r => (double)r), ruledOut.MeanLaterRating!.Value, 6);
    }

    [Fact]
    public async Task A_rating_that_predates_the_serve_is_not_an_outcome_of_it()
    {
        if (!fixture.Available)
        {
            return;
        }

        // The causal guard, on the cross-reference join specifically. FirstRatedAt is used rather
        // than RatedAt because RatedAt is rewritten by every Trakt ratings walk, which would drift
        // an old opinion forward past a serve that came after it.
        var (accountId, titleId, artifactId, snapshotId) = await SeedAsync("xrefcausal");
        await using (var seed = fixture.CreateContext(new AccountContext()))
        {
            seed.UserRatings.Add(Rating(accountId, titleId, 10, Now.AddDays(-30)));
            await seed.SaveChangesAsync();
        }

        var log = LogFor(accountId, out var db);
        await using (db)
        {
            await log.RecordAsync(
                accountId, ExposureSurface.FeedHero, snapshotId, artifactId, Now,
                [new ExposureCard(titleId, 0, 8.0m, XrefVerdict: "ProvisionalKeep", XrefConfidence: 0.9m)],
                CancellationToken.None);
        }

        var report = await ComputeAccuracyAsync(accountId, "all");
        Assert.Empty(report.CrossReference.Online);
    }

    /// <summary>
    /// One exposure per rating, each on its own title, all under one standing — the shape the online
    /// half aggregates. A null <paramref name="verdict"/> seeds serves nothing had judged.
    /// </summary>
    private async Task SeedJudgedExposuresAsync(
        Guid accountId, Guid artifactId, Guid snapshotId, string? verdict, decimal? confidence,
        IReadOnlyList<short> laterRatings)
    {
        var titles = laterRatings.Select((_, i) => NewTitle($"xref-{verdict ?? "none"}-{i}")).ToList();
        await using (var seed = fixture.CreateContext(new AccountContext()))
        {
            seed.Titles.AddRange(titles);
            for (var i = 0; i < titles.Count; i++)
            {
                // Rated AFTER the serve, which is what makes it an outcome of it.
                seed.UserRatings.Add(Rating(accountId, titles[i].Id, laterRatings[i], Now.AddDays(2)));
            }

            await seed.SaveChangesAsync();
        }

        var log = LogFor(accountId, out var db);
        await using (db)
        {
            await log.RecordAsync(
                accountId, ExposureSurface.FeedRow, snapshotId, artifactId, Now,
                titles
                    .Select((t, i) => new ExposureCard(
                        t.Id, i, 7.0m, XrefVerdict: verdict, XrefConfidence: verdict is null ? null : confidence))
                    .ToList(),
                CancellationToken.None);
        }
    }

    private async Task<AccuracyReport> ComputeAccuracyAsync(Guid accountId, string window)
    {
        var context = Scoped(accountId);
        await using var db = fixture.CreateContext(context);
        return await new AccuracyQuery(db, context, new RetroLoopQuery(db, context), new ModelLearnedQuery(db), CurrentJudge)
            .ComputeAsync(window, Now.AddDays(3), CancellationToken.None);
    }

    private ExposureLog LogFor(Guid accountId, out ReelDbContext db)
    {
        db = fixture.CreateContext(Scoped(accountId));
        return new ExposureLog(db, NullLogger<ExposureLog>.Instance);
    }

    private static AccountContext Scoped(Guid accountId)
    {
        var context = new AccountContext();
        context.SetAccount(accountId);
        return context;
    }

    private static async Task<Dictionary<Guid, string>> NamesAsync(ReelDbContext db, IReadOnlyList<Guid> titleIds) =>
        await db.Titles.Where(t => titleIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, t => t.Name);

    private async Task<(Guid AccountId, Guid TitleId, Guid ArtifactId, Guid SnapshotId)> SeedAsync(string prefix)
    {
        await using var db = fixture.CreateContext(new AccountContext());

        var account = new Account
        {
            Id = Guid.NewGuid(),
            TraktUserSlug = $"exposure-{prefix}-{Guid.NewGuid():N}",
            CreatedAt = Now,
            LastSeenAt = Now,
            PipelineStageChangedAt = Now,
        };
        db.Accounts.Add(account);

        var title = NewTitle($"{prefix}-seed");
        db.Titles.Add(title);

        // The artifact is a bare id, not a row: the exposure log carries a soft reference to it and
        // never joins, so a full ModelArtifact (with its schema-version filter) would only make the
        // fixture heavier without making it more truthful.
        var artifactId = Guid.NewGuid();
        var snapshot = new FeedSnapshot
        {
            Id = Guid.NewGuid(),
            AccountId = account.Id,
            ModelArtifactId = artifactId,
            GeneratedAt = Now,
            Status = SnapshotStatus.Active,
        };
        db.FeedSnapshots.Add(snapshot);

        await db.SaveChangesAsync();
        return (account.Id, title.Id, artifactId, snapshot.Id);
    }

    private static Title NewTitle(string name) => new()
    {
        Id = Guid.NewGuid(),
        MediaType = MediaType.Movie,
        TraktId = Random.Shared.NextInt64(1, long.MaxValue),
        TraktSlug = $"{name}-{Guid.NewGuid():N}",
        Name = name,
        Year = 2020,
        CreatedAt = Now,
    };

    private static UserRating Rating(Guid accountId, Guid titleId, short rating, DateTime ratedAt) => new()
    {
        Id = Guid.NewGuid(),
        AccountId = accountId,
        TitleId = titleId,
        SubjectType = RatingSubjectType.Movie,
        Rating = rating,
        RatedAt = ratedAt,
        // The outcome view and the retro fallback both order on FirstRatedAt, never RatedAt — a
        // fixture that left it default would silently test neither.
        FirstRatedAt = ratedAt,
        SyncedAt = ratedAt,
        Source = RatingSource.Trakt,
    };
}
