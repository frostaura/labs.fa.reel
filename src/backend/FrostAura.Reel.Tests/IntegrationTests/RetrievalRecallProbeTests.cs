using FrostAura.Reel.Application.Ml;
using FrostAura.Reel.Application.Ranking;
using FrostAura.Reel.Application.Search;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Filters;
using FrostAura.Reel.Domain.Library;
using FrostAura.Reel.Domain.Tenancy;
using Microsoft.EntityFrameworkCore;
using Pgvector;

namespace FrostAura.Reel.Tests.IntegrationTests;

/// <summary>
/// The offline retrieval-recall test, on real Postgres because the ANN arms are pgvector kNN and
/// there is no meaningful way to fake that.
///
/// <para>The single assertion that decides whether this tool is worth anything is
/// <see cref="A_target_is_eligible_on_its_own_as_of_day_even_though_it_is_rated_today"/>. Every
/// target is BY CONSTRUCTION a title the account has since rated, so under the live eligibility
/// predicate every one of them is ineligible and the probe would report exactly 0% recall, for
/// every account, for ever — a number that looks like a devastating finding and is in fact a bug.
/// The as-of rewind is the whole tool.</para>
///
/// <para>The rest pin the two failure modes apart (supply vs retrieval), the false-positive band,
/// and that the probe writes nothing.</para>
/// </summary>
[Collection("postgres")]
public class RetrievalRecallProbeTests(PostgresFixture fixture)
{
    private const int EmbeddingDims = 1536;

    /// <summary>Every title in this suite carries it, and the account allow-lists it — see <see cref="SeedAccountAsync"/>.</summary>
    private const string FixtureGenre = "retrieval-recall-fixture";

    private static readonly DateTime Now = new(2026, 9, 10, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task A_target_is_eligible_on_its_own_as_of_day_even_though_it_is_rated_today()
    {
        if (!fixture.Available)
        {
            return;
        }

        // One loved title, in the catalogue well before it was rated, and nothing else competing.
        // The live eligibility predicate would exclude it — the rating is right there in the
        // database — so a probe that forgot to rewind reports 0/1. It must report 1/1.
        var accountId = await SeedAccountAsync();
        var ratedAt = Now.AddDays(-30);
        var loved = await SeedTitleAsync("loved and later rated", popularity: 50m, createdAt: ratedAt.AddDays(-90));
        await SeedRatingAsync(accountId, loved, rating: 9, firstRatedAt: ratedAt);

        var report = await RunAsync(accountId);

        Assert.Equal(1, report.Loved.Targets);
        Assert.Equal(1, report.Loved.InCatalogue);
        Assert.Equal(1, report.Loved.EligibleAsOf);
        Assert.Equal(1, report.Loved.Hits.Union);
        Assert.Equal(1, report.Loved.Hits.Popularity);
        Assert.Equal(1d, report.Loved.Rates.Union);
        Assert.Equal(1d, report.Loved.RecallGivenReachable);
        Assert.Empty(report.NotReachable);

        // …and the as-of instant is the day BEFORE the rating, not the rating's own timestamp: a
        // pool built on the day itself may already have seen the rating land.
        Assert.Contains("day before", report.AsOfRule, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_title_the_catalogue_did_not_hold_that_day_is_reported_as_supply_not_retrieval()
    {
        if (!fixture.Available)
        {
            return;
        }

        // The founder's real pattern: he watches something Reel has never heard of, and the title
        // row is created by the very sync that brings the rating in. No retrieval budget can fix
        // that, and the report must not let it be read as a retrieval miss.
        var accountId = await SeedAccountAsync();
        var ratedAt = Now.AddDays(-10);
        var arrivedWithTheRating = await SeedTitleAsync(
            "never in the catalogue", popularity: 90m, createdAt: ratedAt);
        await SeedRatingAsync(accountId, arrivedWithTheRating, rating: 10, firstRatedAt: ratedAt);

        var report = await RunAsync(accountId);

        Assert.Equal(1, report.Loved.Targets);
        Assert.Equal(0, report.Loved.InCatalogue);
        Assert.Equal(0, report.Loved.Hits.Union);
        Assert.Equal(0d, report.Loved.Rates.InCatalogue);

        // The denominator that answers "could a bigger pool have helped" is EMPTY here, so the
        // retrieval rate is null rather than 0 — "0% of nothing" is exactly the number that gets
        // misquoted into a licensing decision later.
        Assert.Null(report.Loved.RecallGivenReachable);

        var missed = Assert.Single(report.NotReachable);
        Assert.Contains("not in the catalogue", missed.Reason, StringComparison.Ordinal);
        Assert.Contains("discovery, not retrieval", missed.Reason, StringComparison.Ordinal);
        Assert.Equal(arrivedWithTheRating, missed.TitleId);
        Assert.Equal((short)10, missed.Rating);
    }

    [Fact]
    public async Task An_eligible_title_no_arm_returned_is_reported_as_a_genuine_retrieval_miss()
    {
        if (!fixture.Available)
        {
            return;
        }

        // Budgets of one: the popular decoy takes the single popularity slot and the ANN arms are
        // switched off, so the loved title is eligible, in the catalogue, and simply not returned.
        // That is the only failure mode a bigger pool or a new arm could actually fix.
        var accountId = await SeedAccountAsync();
        var ratedAt = Now.AddDays(-20);
        var loved = await SeedTitleAsync("unpopular but loved", popularity: 1m, createdAt: ratedAt.AddDays(-200));
        await SeedTitleAsync("popular decoy", popularity: 999m, createdAt: ratedAt.AddDays(-200));
        await SeedRatingAsync(accountId, loved, rating: 9, firstRatedAt: ratedAt);

        var report = await RunAsync(accountId, popCount: 1, annCount: 0);

        Assert.Equal(1, report.Loved.InCatalogue);
        Assert.Equal(1, report.Loved.EligibleAsOf);
        Assert.Equal(0, report.Loved.Hits.Union);
        Assert.Equal(0d, report.Loved.RecallGivenReachable);

        var missed = Assert.Single(report.NotReachable);
        Assert.Contains("genuine retrieval miss", missed.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_ann_arm_is_credited_even_when_popularity_claimed_the_title_first()
    {
        if (!fixture.Available)
        {
            return;
        }

        // Attribution in the combined pool is first-claim, and popularity runs first — so without
        // the popularity-free second pass the ANN arms read as zero on every title the popularity
        // slice happened to cover, which is most of them. That would say "ANN contributes nothing"
        // about an arm doing real work.
        var accountId = await SeedAccountAsync();
        var ratedAt = Now.AddDays(-40);
        var created = ratedAt.AddDays(-300);

        // A loved history with embeddings on axis 0 gives the account a loved centroid there.
        for (var i = 0; i < 3; i++)
        {
            var history = await SeedTitleAsync($"loved history {i}", popularity: 5m, createdAt: created);
            await SeedEmbeddingAsync(history, axis: 0);
            await SeedRatingAsync(accountId, history, rating: 9, firstRatedAt: created.AddDays(1 + i));
        }

        // The target sits on the same axis AND is the most popular thing in the account's world.
        var target = await SeedTitleAsync("popular and on-centroid", popularity: 500m, createdAt: created);
        await SeedEmbeddingAsync(target, axis: 0);
        await SeedRatingAsync(accountId, target, rating: 9, firstRatedAt: ratedAt);

        var report = await RunAsync(accountId);

        // Popularity claimed it in the combined pool…
        Assert.Equal(1, report.Loved.Hits.Popularity);

        // …and the ANN pass still shows it was reachable without popularity's help. The two
        // therefore do not sum to the union, which is the documented contract.
        Assert.Equal(1, report.Loved.Hits.AnnLoved);
        Assert.True(report.Loved.Hits.Union < report.Loved.Hits.Popularity + report.Loved.Hits.AnnLoved);
    }

    [Fact]
    public async Task The_disliked_band_is_measured_separately_as_the_false_positive_side()
    {
        if (!fixture.Available)
        {
            return;
        }

        // The middle of the scale is not evidence either way and must be in neither band: a 6 is
        // "tolerated", and counting it as a miss would understate recall while counting it as a
        // false positive would overstate the harm.
        var accountId = await SeedAccountAsync();
        var ratedAt = Now.AddDays(-15);
        var created = ratedAt.AddDays(-100);

        var loved = await SeedTitleAsync("loved", popularity: 60m, createdAt: created);
        var hated = await SeedTitleAsync("hated", popularity: 61m, createdAt: created);
        var tolerated = await SeedTitleAsync("tolerated", popularity: 62m, createdAt: created);
        await SeedRatingAsync(accountId, loved, rating: 8, firstRatedAt: ratedAt);
        await SeedRatingAsync(accountId, hated, rating: 3, firstRatedAt: ratedAt);
        await SeedRatingAsync(accountId, tolerated, rating: 6, firstRatedAt: ratedAt);

        var report = await RunAsync(accountId);

        Assert.Equal(1, report.Loved.Targets);
        Assert.Equal(1, report.Disliked.Targets);
        Assert.Equal(1, report.Loved.Hits.Union);

        // The pool would have shown him the title he went on to rate 3 — that is the false positive,
        // and it is a RANKING failure, not a supply one.
        Assert.Equal(1, report.Disliked.Hits.Union);
        Assert.Equal(1d, report.Disliked.Rates.Union);
        Assert.Equal(">= 8", report.Loved.Threshold);
        Assert.Equal("<= 4", report.Disliked.Threshold);

        // The tolerated 6 appears in neither band, and in neither denominator.
        Assert.Equal(2, report.Loved.Targets + report.Disliked.Targets);
    }

    [Fact]
    public async Task The_probe_persists_nothing()
    {
        if (!fixture.Available)
        {
            return;
        }

        // It is a diagnostic run by hand against the founder's live database. A stray write here is
        // a write to production data with nobody watching.
        var accountId = await SeedAccountAsync();
        var ratedAt = Now.AddDays(-5);
        var loved = await SeedTitleAsync("anything", popularity: 10m, createdAt: ratedAt.AddDays(-50));
        await SeedRatingAsync(accountId, loved, rating: 9, firstRatedAt: ratedAt);

        var before = await CountRowsAsync(accountId);
        await RunAsync(accountId);
        var after = await CountRowsAsync(accountId);

        Assert.Equal(before, after);
    }

    [Fact]
    public async Task Ratings_outside_the_window_are_not_measured()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        var old = await SeedTitleAsync("rated long ago", popularity: 10m, createdAt: Now.AddYears(-3));
        await SeedRatingAsync(accountId, old, rating: 9, firstRatedAt: Now.AddDays(-400));

        var report = await RunAsync(accountId, windowDays: 180);

        Assert.Equal(0, report.Loved.Targets);
        Assert.Null(report.Loved.Rates.Union);
        Assert.Equal(0, report.Budget.DaysTotal);
        Assert.False(report.Budget.Exhausted);
    }

    /// <summary>
    /// The <c>perCluster</c> block — <b>the table that decides whether B1 worked</b> — is populated,
    /// partitions the loved targets, and reports the multi-centroid arm rather than the single one.
    ///
    /// <para>Two properties are asserted and both are what make the block readable. The
    /// <c>targetsNear</c> counts must SUM to the embedded loved targets, because each target is
    /// assigned to exactly one nearest centroid — without that the per-ordinal recalls would be
    /// rates over overlapping denominators and could not be compared. And <c>annLoved</c> must be
    /// zero while <c>annCluster</c> carries the reach: the single-centroid arm only fires when there
    /// is no partition, so a non-zero <c>annLoved</c> beside a populated <c>perCluster</c> would mean
    /// the two arms were both running and the recall was being double-counted.</para>
    /// </summary>
    [Fact]
    public async Task The_per_cluster_block_partitions_the_loved_targets_and_reports_the_cluster_arm()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        var planted = new List<Guid>();

        // History OUTSIDE the window: it is what the partition is built from on every as-of day, and
        // being outside the window keeps it out of the target set. Two directions, 60 titles, so the
        // k rule (min(3, 60/20) = 3) has something real to find.
        for (var i = 0; i < 60; i++)
        {
            var id = await SeedTitleAsync($"history {i}", popularity: 2m, createdAt: Now.AddYears(-3));
            await SeedRatingAsync(accountId, id, rating: 9, firstRatedAt: Now.AddDays(-400 - i));
            await SeedEmbeddingAsync(id, i % 4 == 3 ? 7 : 0);
            planted.Add(id);
        }

        // Targets INSIDE the window, spread over both pockets.
        for (var i = 0; i < 6; i++)
        {
            var id = await SeedTitleAsync($"target {i}", popularity: 3m, createdAt: Now.AddDays(-90));
            await SeedRatingAsync(accountId, id, rating: 9, firstRatedAt: Now.AddDays(-10 - i));
            await SeedEmbeddingAsync(id, i % 3 == 2 ? 7 : 0);
            planted.Add(id);
        }

        try
        {
            var report = await RunAsync(accountId);

            Assert.Equal(6, report.Loved.Targets);
            Assert.NotEmpty(report.PerCluster);
            Assert.Equal(6, report.PerCluster.Sum(c => c.TargetsNear));
            Assert.All(report.PerCluster, c => Assert.True(c.Weight > 0d));
            Assert.All(report.PerCluster, c => Assert.True(c.MemberCount >= TasteClusterer.MinClusterMembers));
            Assert.Equal(report.PerCluster.Sum(c => c.Hits), report.PerCluster.Where(c => c.Recall is not null).Sum(c => c.Hits));

            // The cluster arm replaced the single-centroid one outright; it did not run beside it.
            Assert.Equal(0, report.Loved.Hits.AnnLoved);
            Assert.True(report.Loved.Hits.AnnCluster > 0);
        }
        finally
        {
            // 66 embedded vectors in a shared HNSW index perturb unrelated semantic-search tests —
            // see TasteClusterRetrievalTests.DisposeAsync for the measurement behind this.
            await using var db = fixture.CreateContext(new AccountContext());
            await db.UserRatings.Where(r => planted.Contains(r.TitleId)).ExecuteDeleteAsync();
            await db.TitleEmbeddings.Where(e => planted.Contains(e.TitleId)).ExecuteDeleteAsync();
            await db.Titles.Where(t => planted.Contains(t.Id)).ExecuteDeleteAsync();
        }
    }

    private async Task<RetrievalRecallReport> RunAsync(
        Guid accountId, int windowDays = 180, int popCount = 700, int annCount = 500)
    {
        var scoped = new AccountContext();
        scoped.SetAccount(accountId);
        await using var db = fixture.CreateContext(scoped);
        var eligibility = new EligibilityQueryBuilder(db);
        var clusters = new TasteClusterProvider(
            db,
            new Microsoft.Extensions.Caching.Memory.MemoryCache(
                new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions()),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<TasteClusterProvider>.Instance);
        var probe = new RetrievalRecallProbe(
            db,
            new Application.Ml.FeatureVectorBuilder(db),
            new CandidateRetriever(db, eligibility, clusters),
            eligibility,
            clusters);

        return await probe.RunAsync(
            accountId, windowDays, popCount, annCount, TimeSpan.FromSeconds(60), Now, CancellationToken.None);
    }

    private async Task<(int Ratings, int Scores, int Reactions, int Titles)> CountRowsAsync(Guid accountId)
    {
        var scoped = new AccountContext();
        scoped.SetAccount(accountId);
        await using var db = fixture.CreateContext(scoped);
        return (
            await db.UserRatings.CountAsync(r => r.AccountId == accountId),
            await db.TitleScores.CountAsync(s => s.AccountId == accountId),
            await db.UserTitleReactions.CountAsync(r => r.AccountId == accountId),
            await db.Titles.CountAsync());
    }

    /// <summary>
    /// An account whose eligible catalogue is exactly this suite's own titles. The database is
    /// shared with every other integration test, so without the allow-list the popularity arm would
    /// be filled by unrelated fixtures and every recall number here would be a statement about
    /// them.
    /// </summary>
    private async Task<Guid> SeedAccountAsync()
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var account = new Account
        {
            Id = Guid.NewGuid(),
            TraktUserSlug = $"recall-{Guid.NewGuid():N}",
            CreatedAt = DateTime.UtcNow,
            LastSeenAt = DateTime.UtcNow,
            PipelineStageChangedAt = DateTime.UtcNow,
        };
        db.Add(account);
        db.ContentFilters.Add(new ContentFilter
        {
            Id = Guid.NewGuid(),
            AccountId = account.Id,
            Kind = FilterKind.IncludeGenre,
            Value = FixtureGenre,
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        return account.Id;
    }

    private async Task<Guid> SeedTitleAsync(string name, decimal popularity, DateTime createdAt)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var title = new Title
        {
            Id = Guid.NewGuid(),
            MediaType = MediaType.Movie,
            TmdbId = Random.Shared.NextInt64(1, long.MaxValue),
            TraktSlug = string.Empty,
            Name = $"{name} {Guid.NewGuid():N}",
            Language = "en",
            PosterPath = "/p.jpg", // the feed lane requires a poster and a TMDB id
            Genres = [FixtureGenre],
            Year = 2015,
            RuntimeMinutes = 110,
            TmdbPopularity = popularity,
            CreatedAt = createdAt,
        };
        db.Add(title);
        await db.SaveChangesAsync();
        return title.Id;
    }

    private async Task SeedRatingAsync(Guid accountId, Guid titleId, short rating, DateTime firstRatedAt)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        db.UserRatings.Add(new UserRating
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            TitleId = titleId,
            SubjectType = RatingSubjectType.Movie,
            Rating = rating,
            RatedAt = firstRatedAt,
            FirstRatedAt = firstRatedAt,
            SyncedAt = firstRatedAt,
        });
        await db.SaveChangesAsync();
    }

    /// <summary>A unit plot embedding along one axis — identical (cos 1) or orthogonal (cos 0) pairs.</summary>
    private async Task SeedEmbeddingAsync(Guid titleId, int axis)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var vector = new float[EmbeddingDims];
        vector[axis] = 1f;
        db.TitleEmbeddings.Add(new TitleEmbedding
        {
            TitleId = titleId,
            Embedding = new Vector(vector),
            EmbeddingModel = "test",
            SourceTextHash = $"hash-{titleId:N}",
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }
}
