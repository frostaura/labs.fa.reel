using System.Runtime.CompilerServices;
using FrostAura.Reel.Application.CrossReference;
using FrostAura.Reel.Application.Ingestion;
using FrostAura.Reel.Application.Jobs;
using FrostAura.Reel.Application.Llm;
using FrostAura.Reel.Application.Ml;
using FrostAura.Reel.Application.Ml.ClosedLoop;
using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Application.Pipeline;
using FrostAura.Reel.Application.Ranking;
using FrostAura.Reel.Application.Search;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.CrossReference;
using FrostAura.Reel.Domain.Feed;
using FrostAura.Reel.Domain.Filters;
using FrostAura.Reel.Domain.Lens;
using FrostAura.Reel.Domain.Library;
using FrostAura.Reel.Domain.Ml;
using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Domain.Ports.Tmdb;
using FrostAura.Reel.Domain.Sync;
using FrostAura.Reel.Domain.Tenancy;
using FrostAura.Reel.Infrastructure.Ml;
using FrostAura.Reel.Infrastructure.Persistence;
using FrostAura.Reel.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Pgvector;

namespace FrostAura.Reel.Tests.IntegrationTests;

/// <summary>
/// The cross-reference JOB, against real Postgres and a stub judge: what it writes, what it
/// refuses to write, where it stops, and what it never does twice.
///
/// <para>Every test here is one of the failure modes the design named in advance. They are worth
/// more than the happy path because each one is a way this pass could quietly become expensive or
/// dishonest: rows that look like judgements but are outages, a run that judges the same titles
/// every night, a run that re-queues itself into a loop, a fingerprint that moves under a
/// half-written key, or a ceiling that stops nothing.</para>
///
/// <para><b>Fixture hygiene.</b> The catalogue and its embeddings are GLOBAL and shared by every
/// class in the postgres collection. Rows seeded here therefore carry a per-instance genre scope
/// and a language nothing else uses, so they are invisible to sibling accounts, sit on a reserved
/// embedding dimension block far from every sibling query vector, and are DELETED in
/// <see cref="DisposeAsync"/> — an embedding left behind changes what an unrelated class's
/// nearest-neighbour window contains, which is the contamination class
/// <c>CandidateRetrieverTests</c> documents.</para>
/// </summary>
[Collection("postgres")]
public class CrossReferenceJobTests(PostgresFixture fixture) : EmbeddedCatalogTestBase(fixture)
{
    /// <summary>
    /// The dimension block every embedding in this file lives on. Reserved far above the
    /// single- and low-double-digit dimensions sibling fixtures use, so these titles are exactly
    /// orthogonal to every query they issue. Deterministic on purpose — a hash-derived dimension
    /// is randomised per process and makes contamination come and go.
    /// </summary>
    private const int EmbeddingDimensionBase = 1200;

    /// <summary>A language nothing else in the collection uses; the eligibility layer is English-only by default.</summary>
    private const string TestLanguage = "zz";

    private readonly string _scope = $"xrefjob{Guid.NewGuid():N}";

    private readonly List<Guid> _accounts = [];

    private int _seeded;

    /// <summary>
    /// Removes this instance's rows from the shared catalogue. Raw SQL in FK order rather than EF,
    /// because the point is to delete rows regardless of which account context is ambient.
    /// </summary>
    public override async Task DisposeAsync()
    {
        await base.DisposeAsync();
        if (!Fixture.Available)
        {
            return;
        }

        await using var db = Fixture.CreateContext(new AccountContext());
        var accounts = _accounts.ToArray();
        await db.Database.ExecuteSqlAsync(
            $"""
             DELETE FROM "TitleCrossReferenceVerdicts" WHERE "TitleId" IN (SELECT "Id" FROM "Titles" WHERE {_scope} = ANY("Genres"));
             """);
        await db.Database.ExecuteSqlAsync(
            $"""
             DELETE FROM "TitleScores" WHERE "TitleId" IN (SELECT "Id" FROM "Titles" WHERE {_scope} = ANY("Genres"));
             """);
        await db.Database.ExecuteSqlAsync(
            $"""
             DELETE FROM "TitleAttributes" WHERE "TitleId" IN (SELECT "Id" FROM "Titles" WHERE {_scope} = ANY("Genres"));
             """);
        await db.Database.ExecuteSqlAsync(
            $"""
             DELETE FROM "TitleEmbeddings" WHERE "TitleId" IN (SELECT "Id" FROM "Titles" WHERE {_scope} = ANY("Genres"));
             """);
        await db.Database.ExecuteSqlAsync(
            $"""
             DELETE FROM "UserRatings" WHERE "TitleId" IN (SELECT "Id" FROM "Titles" WHERE {_scope} = ANY("Genres"));
             """);
        await db.Database.ExecuteSqlAsync(
            $"""
             DELETE FROM "UserTitleReactions" WHERE "TitleId" IN (SELECT "Id" FROM "Titles" WHERE {_scope} = ANY("Genres"));
             """);
        await db.Database.ExecuteSqlAsync(
            $"""DELETE FROM "FeedItems" WHERE "TitleId" IN (SELECT "Id" FROM "Titles" WHERE {_scope} = ANY("Genres"));""");
        await db.Database.ExecuteSqlAsync($"""DELETE FROM "Titles" WHERE {_scope} = ANY("Genres");""");
        await db.Database.ExecuteSqlAsync($"""DELETE FROM "ExternalApiUsages" WHERE "AccountId" = ANY({accounts});""");
        await db.Database.ExecuteSqlAsync($"""DELETE FROM "SyncJobs" WHERE "AccountId" = ANY({accounts});""");
    }

    // ── What one run writes ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_run_writes_one_verdict_per_candidate_and_never_queues_itself()
    {
        if (!Fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        await SeedRatedNeighboursAsync(accountId, 3);
        var candidates = await SeedCandidatesAsync(accountId, 5, embedded: true);

        var judge = new StubJudge();
        var events = new RecordingEventHub();
        var job = await RunAsync(accountId, judge, events);

        await using var db = Fixture.CreateContext(new AccountContext());
        var rows = await db.TitleCrossReferenceVerdicts
            .Where(v => v.AccountId == accountId)
            .ToListAsync();

        Assert.Equal(candidates.Count, rows.Count);
        Assert.All(rows, r => Assert.Equal(CrossReferenceRound.First, r.Round));
        Assert.All(rows, r => Assert.Equal(judge.ModelId, r.ModelId));
        Assert.All(rows, r => Assert.Equal(judge.PromptVersion, r.PromptVersion));
        Assert.Single(rows.Select(r => r.ProfileHash).Distinct());
        // The evidence is persisted with the verdict, so a judgement can be replayed rather than
        // taken on trust.
        Assert.All(rows, r => Assert.NotEqual("[]", r.NeighbourIdsJson));
        Assert.Equal(1, judge.Calls);

        // It never re-enqueues itself: the partial unique index would refuse it, and the absorbing
        // save at the enqueue site would swallow the refusal without a sound.
        Assert.False(await db.SyncJobs.AnyAsync(j =>
            j.AccountId == accountId && j.Kind == JobKind.CrossReference
            && (j.Status == JobStatus.Pending || j.Status == JobStatus.Running)));

        // It asks for a Preserve-contract rebuild, and NOT with a reason that would force Fresh.
        var rebuild = await db.SyncJobs.SingleAsync(j => j.AccountId == accountId && j.Kind == JobKind.BuildFeed);
        Assert.Equal(CrossReferenceJobHandler.CrossReferenceRebuildReason, rebuild.TriggerReason);
        Assert.False(FeedBuildPolicy.IsUserForced(rebuild.TriggerReason));

        // Progress carries the count the pill renders, and the completion frame says what happened.
        var progress = Assert.Single(events.Progress);
        Assert.Equal("xref", progress["kind"]);
        Assert.Equal(candidates.Count, progress["current"]);
        Assert.Equal(candidates.Count, progress["total"]);
        Assert.Equal(
            $"Cross-referencing {candidates.Count} titles · round 1 of 3 · 0 ruled out so far",
            progress["message"]);

        var completed = Assert.Single(events.Completed);
        Assert.Equal("xref", completed["kind"]);
        Assert.Equal(candidates.Count, completed["judged"]);
        Assert.Equal(0, completed["picksChanged"]);
        // No backtest has ever run for this account, and the band may only quote a real number.
        Assert.Equal(CrossReferenceJobHandler.Unmeasured, completed["gating"]);
        Assert.Equal(100m, job.ProgressPct);
    }

    [Fact]
    public async Task A_second_run_over_an_unchanged_pool_makes_zero_judge_calls()
    {
        if (!Fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        await SeedRatedNeighboursAsync(accountId, 3);
        await SeedCandidatesAsync(accountId, 5, embedded: true);

        await RunAsync(accountId, new StubJudge(), new RecordingEventHub());

        // The warm skip, and the reason the resume state is an index rather than a cursor: this is
        // the number the whole steady-state cost case rests on.
        var second = new StubJudge();
        await RunAsync(accountId, second, new RecordingEventHub());
        Assert.Equal(0, second.Calls);

        await using var db = Fixture.CreateContext(new AccountContext());
        Assert.Equal(5, await db.TitleCrossReferenceVerdicts.CountAsync(v => v.AccountId == accountId));
        // Nothing was judged, so nothing changed, so no rebuild was asked for: an unconditional
        // request here would make a ping-pong out of build → cross-reference → build.
        Assert.Equal(1, await db.SyncJobs.CountAsync(j => j.AccountId == accountId && j.Kind == JobKind.BuildFeed));
    }

    // ── Failure modes ───────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_provider_outage_writes_no_rows_at_all_and_lands_on_the_job_error()
    {
        if (!Fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        await SeedRatedNeighboursAsync(accountId, 3);
        await SeedCandidatesAsync(accountId, 5, embedded: true);

        var judge = new StubJudge { Fail = true };
        var events = new RecordingEventHub();

        // Thrown, because that is the only way this runner records anything on job.Error: it
        // clears the column on success. The runner retries and eventually marks the job Failed —
        // what must never happen is a Failed VERDICT, which would be indistinguishable from a
        // judgement to the resume anti-join and would suppress its own retry for ever.
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => RunAsync(accountId, judge, events));
        Assert.Contains("no verdicts were written", error.Message, StringComparison.Ordinal);

        await using var db = Fixture.CreateContext(new AccountContext());
        Assert.Equal(0, await db.TitleCrossReferenceVerdicts.CountAsync(v => v.AccountId == accountId));
        Assert.False(await db.SyncJobs.AnyAsync(j => j.AccountId == accountId && j.Kind == JobKind.BuildFeed));
        // And no completion frame: a run that answered nothing has not completed, and telling the
        // pill it did moments before the runner retries the same job is the pipeline contradicting
        // itself on screen.
        Assert.Empty(events.Completed);
    }

    [Fact]
    public async Task The_daily_ceiling_stops_the_run_after_the_batch_that_hit_it_and_keeps_what_landed()
    {
        if (!Fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        var candidates = await SeedCandidatesAsync(accountId, (CrossReferenceJobHandler.BatchSize * 2) + 10, embedded: false);

        // One call's worth of allowance, and a judge that bills the ledger the way the real adapter
        // does. The gate is re-checked PER BATCH precisely because the ledger is day-grained and a
        // batch loop can spend a day's allowance inside one 15-second flush window.
        var judge = new StubJudge { LedgerAccountId = accountId, Fixture = Fixture };
        await RunAsync(accountId, judge, new RecordingEventHub(), Config(("LLM_XREF_DAILY_CALLS", "1")));

        Assert.Equal(1, judge.Calls);

        await using var db = Fixture.CreateContext(new AccountContext());
        var judged = await db.TitleCrossReferenceVerdicts
            .Where(v => v.AccountId == accountId)
            .Select(v => v.TitleId)
            .ToListAsync();

        // Exactly one batch of work, kept — exhaustion is not an error, and a half-judged pool is a
        // smaller instrument rather than a wrong one.
        Assert.Equal(CrossReferenceJobHandler.BatchSize, judged.Count);
        Assert.Equal(
            candidates.Count - CrossReferenceJobHandler.BatchSize,
            candidates.Except(judged).Count());
    }

    [Fact]
    public async Task The_fingerprint_is_pinned_at_run_start_even_when_taste_moves_mid_run()
    {
        if (!Fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        var rated = await SeedRatedNeighboursAsync(accountId, 3);
        await SeedCandidatesAsync(accountId, CrossReferenceJobHandler.BatchSize + 10, embedded: false);

        var before = await ProfileHashAsync(accountId);

        // A dismissal lands between the first batch and the second — the taste fingerprint moves
        // under a half-written key, which is exactly the race the pin exists for.
        var judge = new StubJudge
        {
            Hook = async call =>
            {
                if (call != 1)
                {
                    return;
                }

                await using var db = Fixture.CreateContext(Scoped(accountId));
                db.UserTitleReactions.Add(new UserTitleReaction
                {
                    Id = Guid.NewGuid(),
                    AccountId = accountId,
                    TitleId = rated[0],
                    Kind = ReactionKind.NotInterested,
                    CreatedAt = DateTime.UtcNow,
                });
                await db.SaveChangesAsync();
            },
        };

        await RunAsync(accountId, judge, new RecordingEventHub());

        var after = await ProfileHashAsync(accountId);
        // If this fails the fixture stopped moving the fingerprint and the test below proves
        // nothing — the assertion is here so it cannot pass vacuously.
        Assert.NotEqual(before, after);

        await using var db = Fixture.CreateContext(new AccountContext());
        var hashes = await db.TitleCrossReferenceVerdicts
            .Where(v => v.AccountId == accountId)
            .Select(v => v.ProfileHash)
            .Distinct()
            .ToListAsync();

        Assert.Equal([before], hashes);
    }

    [Fact]
    public async Task Titles_carrying_a_verdict_at_a_superseded_fingerprint_are_judged_first()
    {
        if (!Fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        var candidates = await SeedCandidatesAsync(accountId, (CrossReferenceJobHandler.BatchSize * 2) + 10, embedded: false);

        // Three titles judged against a fingerprint the account has since moved off. They are the
        // one place the instrument is actively out of date: a title nobody has judged is merely
        // unknown, which is the state everything starts in.
        var stale = candidates.TakeLast(3).ToList();
        await SeedStaleVerdictsAsync(accountId, stale);

        var judge = new StubJudge { LedgerAccountId = accountId, Fixture = Fixture };
        await RunAsync(accountId, judge, new RecordingEventHub(), Config(("LLM_XREF_DAILY_CALLS", "1")));

        await using var db = Fixture.CreateContext(new AccountContext());
        var current = await ProfileHashAsync(accountId);
        var judged = await db.TitleCrossReferenceVerdicts
            .Where(v => v.AccountId == accountId && v.ProfileHash == current)
            .Select(v => v.TitleId)
            .ToListAsync();

        Assert.Equal(CrossReferenceJobHandler.BatchSize, judged.Count);
        Assert.All(stale, id => Assert.Contains(id, judged));

        // …and the stale rows are still there. A fingerprint move demotes evidence to half strength;
        // it never deletes it.
        Assert.Equal(3, await db.TitleCrossReferenceVerdicts
            .CountAsync(v => v.AccountId == accountId && v.ProfileHash != current));
    }

    [Fact]
    public async Task A_candidate_with_neither_an_embedding_nor_attributes_is_counted_and_never_becomes_permanent_work()
    {
        if (!Fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        await SeedCandidatesAsync(accountId, 2, embedded: false);
        await SeedCandidatesAsync(accountId, 3, embedded: false, attributed: false);

        var events = new RecordingEventHub();
        await RunAsync(accountId, new StubJudge(), events);

        await using var db = Fixture.CreateContext(new AccountContext());
        Assert.Equal(2, await db.TitleCrossReferenceVerdicts.CountAsync(v => v.AccountId == accountId));

        var completed = Assert.Single(events.Completed);
        Assert.Equal(3, completed["noEmbedding"]);

        // The part that matters: an unjudgeable title must not read as pending work for ever. It
        // would otherwise have every future build queue a run that judged nothing, asked for a
        // rebuild, and queued itself again through the front door.
        Assert.False(await SchedulerFiresAsync(accountId));
    }

    // ── The enqueue point, through the real feed build ──────────────────────────────────────

    [Fact]
    public async Task A_preserve_build_queues_the_pass_when_work_is_pending_and_leaves_it_alone_when_it_is_not()
    {
        if (!Fixture.Available)
        {
            return;
        }

        var accountId = await SeedBuildableAccountAsync();
        await SeedCandidatesAsync(accountId, 6, embedded: false, scored: false);

        // Build one: Fresh (there is nothing to preserve yet). It writes the TitleScores the pool
        // is defined over, and queues the pass on the way out.
        await RunBuildAsync(accountId);
        Assert.True(await CrossReferenceQueuedAsync(accountId));

        // The run happens and finishes. Nothing is judged yet, so there is still work.
        await CompleteCrossReferenceJobsAsync(accountId);

        // Build two: same day, same artifact, no user-forced reason ⇒ Preserve. The pass is queued
        // again, because a Preserve build means the PAGE did not move, not that the pool did not.
        await RunBuildAsync(accountId);
        Assert.True(await CrossReferenceQueuedAsync(accountId));
        await AssertLatestBuildWasNotFreshAsync(accountId);

        // Now judge everything at the current fingerprint, and build again. With no unjudged work
        // the anti-join answers no, and nothing is queued — the brake that stops a background pass
        // from being a nightly no-op job.
        await CompleteCrossReferenceJobsAsync(accountId);
        await RunAsync(accountId, new StubJudge(), new RecordingEventHub());
        await CompleteCrossReferenceJobsAsync(accountId);

        await RunBuildAsync(accountId);
        Assert.False(await CrossReferenceQueuedAsync(accountId));
    }

    // ── Fixture ─────────────────────────────────────────────────────────────────────────────

    private static AccountContext Scoped(Guid accountId)
    {
        var context = new AccountContext();
        context.SetAccount(accountId);
        return context;
    }

    private static IConfiguration Config(params (string Key, string Value)[] settings) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(settings.ToDictionary(s => s.Key, s => (string?)s.Value))
            .Build();

    /// <summary>Runs the handler exactly as the job runner would, and hands back the job row.</summary>
    private async Task<SyncJob> RunAsync(
        Guid accountId, StubJudge judge, RecordingEventHub events, IConfiguration? configuration = null)
    {
        await using var db = Fixture.CreateContext(Scoped(accountId));
        var cache = new MemoryCache(new MemoryCacheOptions());
        var features = new FeatureVectorBuilder(db);
        var dislikes = new DislikeSignal(db, cache);
        var handler = new CrossReferenceJobHandler(
            db,
            new EligibilityQueryBuilder(db),
            features,
            new TasteFactsBuilder(db, dislikes, features, cache),
            new PersonalisationContext(db, dislikes),
            new CrossReferenceCandidateBuilder(db, new NeighbourFinder(db)),
            new CrossReferenceService(db, NullLogger<CrossReferenceService>.Instance),
            new CrossReferenceBudget(db, configuration ?? Config()),
            judge,
            new FeedRefreshCoordinator(db, NullLogger<FeedRefreshCoordinator>.Instance),
            events,
            NullLogger<CrossReferenceJobHandler>.Instance);

        var job = new SyncJob
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            Kind = JobKind.CrossReference,
            EnqueuedAt = DateTime.UtcNow,
        };
        await handler.ExecuteAsync(job, CancellationToken.None);
        return job;
    }

    /// <summary>Would the enqueue gate fire right now? The same three brakes the build asks through.</summary>
    private async Task<bool> SchedulerFiresAsync(Guid accountId)
    {
        await using var db = Fixture.CreateContext(Scoped(accountId));
        var scheduler = new CrossReferenceScheduler(
            db,
            new EligibilityQueryBuilder(db),
            new CrossReferenceService(db, NullLogger<CrossReferenceService>.Instance),
            new CrossReferenceBudget(db, Config()),
            new StubJudge(),
            NullLogger<CrossReferenceScheduler>.Instance);
        return await scheduler.EnqueueIfWorkPendingAsync(
            accountId, await ProfileHashAsync(accountId), DateTime.UtcNow, CancellationToken.None);
    }

    private async Task<string> ProfileHashAsync(Guid accountId)
    {
        await using var db = Fixture.CreateContext(Scoped(accountId));
        var cache = new MemoryCache(new MemoryCacheOptions());
        var features = new FeatureVectorBuilder(db);
        var facts = new TasteFactsBuilder(db, new DislikeSignal(db, cache), features, cache);
        var now = DateTime.UtcNow;
        var taste = await features.BuildTasteStateAsync(accountId, now, CancellationToken.None);
        return (await facts.BuildAsync(accountId, taste, now, CancellationToken.None)).ProfileHash;
    }

    private async Task<bool> CrossReferenceQueuedAsync(Guid accountId)
    {
        await using var db = Fixture.CreateContext(new AccountContext());
        return await db.SyncJobs.AnyAsync(j =>
            j.AccountId == accountId && j.Kind == JobKind.CrossReference
            && (j.Status == JobStatus.Pending || j.Status == JobStatus.Running));
    }

    /// <summary>Marks any queued pass as finished, the way the runner would once it had run.</summary>
    private async Task CompleteCrossReferenceJobsAsync(Guid accountId)
    {
        await using var db = Fixture.CreateContext(new AccountContext());
        await db.Database.ExecuteSqlAsync(
            $"""
             UPDATE "SyncJobs" SET "Status" = 'Succeeded', "CompletedAt" = {DateTime.UtcNow}
             WHERE "AccountId" = {accountId} AND "Kind" = 'CrossReference'
             """);
    }

    private async Task AssertLatestBuildWasNotFreshAsync(Guid accountId)
    {
        await using var db = Fixture.CreateContext(new AccountContext());
        var mode = await db.FeedBuildStats
            .Where(s => s.AccountId == accountId)
            .OrderByDescending(s => s.BuiltAt)
            .Select(s => s.Mode)
            .FirstAsync();
        Assert.NotEqual(FeedBuildOutcome.Fresh, mode);
    }

    private async Task<Guid> SeedAccountAsync()
    {
        await using var db = Fixture.CreateContext(new AccountContext());
        var account = new Account
        {
            Id = Guid.NewGuid(),
            TraktUserSlug = $"xrefjob-{Guid.NewGuid():N}",
            CreatedAt = DateTime.UtcNow,
            LastSeenAt = DateTime.UtcNow,
            PipelineStageChangedAt = DateTime.UtcNow,
        };
        db.Add(account);
        // Narrows the eligible pool to this instance's own rows — the catalogue is global — and
        // opts into the language they carry, without which the account's pool is English-only and
        // therefore empty.
        db.ContentFilters.Add(new ContentFilter
        {
            Id = Guid.NewGuid(),
            AccountId = account.Id,
            Kind = FilterKind.IncludeGenre,
            Value = _scope,
            CreatedAt = DateTime.UtcNow,
        });
        db.ContentFilters.Add(new ContentFilter
        {
            Id = Guid.NewGuid(),
            AccountId = account.Id,
            Kind = FilterKind.Language,
            Value = TestLanguage,
            CreatedAt = DateTime.UtcNow,
        });
        db.ModelArtifacts.Add(new ModelArtifact
        {
            Id = Guid.NewGuid(),
            AccountId = account.Id,
            Version = 1,
            ArtifactBytes = [1],
            TrainingRunId = Guid.NewGuid(),
            Status = ArtifactStatus.Active,
            TrainedAt = DateTime.UtcNow,
            FeatureSchemaJson = System.Text.Json.JsonSerializer.Serialize(
                new { version = FeatureSchema.Version, names = FeatureSchema.AllNames() }),
        });
        await db.SaveChangesAsync();
        _accounts.Add(account.Id);
        return account.Id;
    }

    /// <summary>The same account, but with an artifact a real feed build can actually score with.</summary>
    private async Task<Guid> SeedBuildableAccountAsync()
    {
        var accountId = await SeedAccountAsync();
        var rng = new Random(41);
        var names = FeatureSchema.AllNames();
        var rows = new float[120][];
        var labels = new float[120];
        for (var i = 0; i < rows.Length; i++)
        {
            var v = new float[names.Length];
            for (var j = 0; j < v.Length; j++)
            {
                v[j] = (float)rng.NextDouble();
            }

            rows[i] = v;
            labels[i] = Math.Clamp(3f + (4f * v[0]), 1f, 8f);
        }

        var trained = new FastTreeModelEngine().Train(
            new TrainingMatrix(rows, labels, names),
            """{"Trees":10,"Leaves":4,"MinExamplesPerLeaf":5,"LearningRate":0.2}""");

        await using var db = Fixture.CreateContext(new AccountContext());
        await db.Database.ExecuteSqlAsync(
            $"""UPDATE "ModelArtifacts" SET "ArtifactBytes" = {trained.ArtifactBytes} WHERE "AccountId" = {accountId}""");
        return accountId;
    }

    /// <summary>Titles the account has RATED, with embeddings — the neighbours the judge is shown.</summary>
    private async Task<List<Guid>> SeedRatedNeighboursAsync(Guid accountId, int count)
    {
        var ids = new List<Guid>();
        await using var db = Fixture.CreateContext(new AccountContext());
        for (var i = 0; i < count; i++)
        {
            var title = NewTitle($"Rated {i}");
            db.Add(title);
            db.TitleEmbeddings.Add(NewEmbedding(title.Id));
            db.UserRatings.Add(new UserRating
            {
                Id = Guid.NewGuid(),
                AccountId = accountId,
                TitleId = title.Id,
                SubjectType = RatingSubjectType.Movie,
                Rating = (short)(i % 2 == 0 ? 9 : 3),
                RatedAt = DateTime.UtcNow.AddDays(-100 + i),
                FirstRatedAt = DateTime.UtcNow.AddDays(-100 + i),
                SyncedAt = DateTime.UtcNow.AddDays(-100 + i),
            });
            ids.Add(title.Id);
        }

        await db.SaveChangesAsync();
        return ids;
    }

    /// <summary>
    /// Eligible, unseen titles carrying a score from the active artifact — the pool the pass judges.
    /// <paramref name="attributed"/> false and <paramref name="embedded"/> false together make a
    /// title unjudgeable, which is a state the pool query is required to recognise.
    /// </summary>
    private async Task<List<Guid>> SeedCandidatesAsync(
        Guid accountId, int count, bool embedded, bool attributed = true, bool scored = true)
    {
        var ids = new List<Guid>();
        await using var db = Fixture.CreateContext(new AccountContext());
        var artifactId = await db.ModelArtifacts
            .Where(a => a.AccountId == accountId)
            .Select(a => a.Id)
            .FirstAsync();

        for (var i = 0; i < count; i++)
        {
            var title = NewTitle($"Candidate {Guid.NewGuid():N}");
            db.Add(title);
            if (embedded)
            {
                db.TitleEmbeddings.Add(NewEmbedding(title.Id));
            }

            if (attributed)
            {
                db.TitleAttributes.Add(new TitleAttributes
                {
                    TitleId = title.Id,
                    ExtractorModel = "xrefjob/test",
                    ExtractorVersion = 2,
                    Status = AttributeExtractionStatus.Done,
                    AttemptCount = 1,
                    ExtractedAt = DateTime.UtcNow,
                    Tone = null,
                    Themes = ["revenge"],
                    Pacing = 0.5m,
                });
            }

            if (scored)
            {
                db.TitleScores.Add(new TitleScore
                {
                    Id = Guid.NewGuid(),
                    AccountId = accountId,
                    TitleId = title.Id,
                    ModelArtifactId = artifactId,
                    PredictedRating = 7.5m,
                    ScoredAt = DateTime.UtcNow,
                });
            }

            ids.Add(title.Id);
        }

        await db.SaveChangesAsync();
        return ids;
    }

    /// <summary>Verdicts written against a fingerprint the account has since moved off.</summary>
    private async Task SeedStaleVerdictsAsync(Guid accountId, IReadOnlyList<Guid> titleIds)
    {
        await using var db = Fixture.CreateContext(new AccountContext());
        foreach (var id in titleIds)
        {
            db.TitleCrossReferenceVerdicts.Add(new TitleCrossReferenceVerdict
            {
                Id = Guid.NewGuid(),
                AccountId = accountId,
                TitleId = id,
                ProfileHash = "stale0000beef",
                PromptVersion = StubJudge.Version,
                Round = CrossReferenceRound.First,
                Verdict = CrossReferenceVerdict.Keep,
                ReasonCode = CrossReferenceReasons.NeighbourLoved,
                Confidence = 0.800m,
                NeighbourIdsJson = "[]",
                ModelId = "stub/test",
                BatchIndex = 0,
                GeneratedAt = DateTime.UtcNow.AddDays(-1),
            });
        }

        await db.SaveChangesAsync();
    }

    private Title NewTitle(string name) => new()
    {
        Id = Guid.NewGuid(),
        MediaType = MediaType.Movie,
        TmdbId = Random.Shared.NextInt64(1, long.MaxValue),
        TraktSlug = $"xrefjob-{Guid.NewGuid():N}",
        Name = name,
        Language = TestLanguage,
        PosterPath = "/p.jpg",
        Genres = ["drama", _scope],
        TmdbPopularity = 1m,
        Overview = "A synthetic title for the cross-reference job tests.",
        LastMetadataRefreshAt = DateTime.UtcNow,
        CreatedAt = DateTime.UtcNow,
    };

    private TitleEmbedding NewEmbedding(Guid titleId)
    {
        var vector = new float[1536];
        vector[EmbeddingDimensionBase + (Interlocked.Increment(ref _seeded) % 300)] = 1f;
        return new TitleEmbedding
        {
            TitleId = titleId,
            Embedding = new Vector(vector),
            EmbeddingModel = "test",
            SourceTextHash = Guid.NewGuid().ToString("N"),
            CreatedAt = DateTime.UtcNow,
        };
    }

    /// <summary>Runs the real feed build, with the cross-reference scheduler wired in as DI supplies it.</summary>
    private async Task RunBuildAsync(Guid accountId)
    {
        await using var db = Fixture.CreateContext(Scoped(accountId));
        var cache = new MemoryCache(new MemoryCacheOptions());
        var features = new FeatureVectorBuilder(db);
        var eligibility = new EligibilityQueryBuilder(db);
        var dislikes = new DislikeSignal(db, cache);
        var tmdb = new OfflineTmdbClient();
        var handler = new BuildFeedJobHandler(
            db,
            features,
            new CandidateGenerator(db, tmdb, eligibility, NullLogger<CandidateGenerator>.Instance),
            new CandidateRetriever(db, eligibility),
            eligibility,
            new FeedStarvationDiagnostic(db, eligibility),
            new TitleHydrator(db, tmdb, NullLogger<TitleHydrator>.Instance),
            new FastTreeModelEngine(),
            new OnlineResidualModel(
                db, features, new FastTreeModelEngine(), cache, NullLogger<OnlineResidualModel>.Instance),
            new RatingCalibrationService(db, cache),
            dislikes,
            new UnavailableSearchAgent(),
            new TasteFactsBuilder(db, dislikes, features, cache),
            new PersonalisationContext(db, dislikes),
            cache,
            new RecordingEventHub(),
            new ExposureLog(db, NullLogger<ExposureLog>.Instance),
            NullLogger<BuildFeedJobHandler>.Instance,
            crossReference: new CrossReferenceScheduler(
                db,
                eligibility,
                new CrossReferenceService(db, NullLogger<CrossReferenceService>.Instance),
                new CrossReferenceBudget(db, Config()),
                new StubJudge(),
                NullLogger<CrossReferenceScheduler>.Instance));

        await handler.ExecuteAsync(
            new SyncJob
            {
                Id = Guid.NewGuid(),
                AccountId = accountId,
                Kind = JobKind.BuildFeed,
                EnqueuedAt = DateTime.UtcNow,
            },
            CancellationToken.None);
    }

    /// <summary>
    /// A judge that answers deterministically, counts its calls, and can be told to fail, to bill
    /// the ledger, or to run a hook between batches.
    /// </summary>
    private sealed class StubJudge : ICrossReferenceJudge
    {
        public const int Version = 7;

        public int Calls { get; private set; }

        public bool Fail { get; init; }

        /// <summary>Set to bill one ledger call per batch, the way the real adapter's finally does.</summary>
        public Guid? LedgerAccountId { get; init; }

        public PostgresFixture? Fixture { get; init; }

        /// <summary>Runs after the call is counted, so a test can move the world mid-run.</summary>
        public Func<int, Task>? Hook { get; init; }

        public bool IsAvailable => true;

        public string ModelId => "stub/cross-reference";

        public int PromptVersion => Version;

        public async Task<CrossReferenceBatchResult> JudgeAsync(
            IReadOnlyList<CrossReferenceCandidate> candidates, LensPersona persona, CrossReferenceRound round,
            string usagePurpose, CancellationToken ct = default)
        {
            Assert.Equal(ApiUsagePurpose.CrossReference, usagePurpose);
            Calls++;

            if (LedgerAccountId is { } accountId && Fixture is { } postgres)
            {
                await using var db = postgres.CreateContext(new AccountContext());
                db.ExternalApiUsages.Add(new ExternalApiUsage
                {
                    Id = Guid.NewGuid(),
                    Provider = ApiProvider.OpenRouter,
                    Day = DateTime.UtcNow.Date,
                    AccountId = accountId,
                    Model = ModelId,
                    Purpose = ApiUsagePurpose.CrossReference,
                    CallCount = 1,
                    TokensUsed = 10,
                });
                await db.SaveChangesAsync(ct);
            }

            if (Hook is { } hook)
            {
                await hook(Calls);
            }

            if (Fail)
            {
                // No rows, not Failed rows — absence is what the resume anti-join reads as "still
                // to do".
                return new CrossReferenceBatchResult([], Succeeded: false, null, null, 0);
            }

            var entries = candidates
                .Select(c => new CrossReferenceEntry(
                    c.TitleId, CrossReferenceVerdict.Keep, CrossReferenceReasons.NeighbourLoved, 0.8d))
                .ToList();
            return new CrossReferenceBatchResult(entries, Succeeded: true, 100, 20, 0);
        }
    }

    /// <summary>Captures the frames the pill reads, so the copy and the counts are assertable.</summary>
    private sealed class RecordingEventHub : IPipelineEventHub
    {
        public List<IReadOnlyDictionary<string, object?>> Progress { get; } = [];

        public List<IReadOnlyDictionary<string, object?>> Completed { get; } = [];

        public void Publish(Guid accountId, string type, IReadOnlyDictionary<string, object?> data)
        {
            if (data.TryGetValue("kind", out var kind) && !Equals(kind, CrossReferenceJobHandler.SseKind))
            {
                return;
            }

            if (type == PipelineEventTypes.JobProgress)
            {
                Progress.Add(data);
            }
            else if (type == PipelineEventTypes.JobCompleted)
            {
                Completed.Add(data);
            }
        }

        public async IAsyncEnumerable<PipelineSseEvent> SubscribeAsync(
            Guid accountId, long? lastEventId, [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    /// <summary>A TMDB port with no corpus: discovery adds nothing, so the pool is what was seeded.</summary>
    private sealed class OfflineTmdbClient : ITmdbClient
    {
        public Task<TmdbTitleDetails?> GetMovieAsync(long tmdbId, CancellationToken ct = default) =>
            Task.FromResult<TmdbTitleDetails?>(null);

        public Task<TmdbTitleDetails?> GetTvAsync(long tmdbId, CancellationToken ct = default) =>
            Task.FromResult<TmdbTitleDetails?>(null);

        public Task<TmdbDiscoverPage> DiscoverSeedPageAsync(
            bool movies, int year, int minVoteCount, int page, CancellationToken ct = default) =>
            Task.FromResult(new TmdbDiscoverPage([], page, 0, 0));

        public Task<IReadOnlyList<TmdbListItem>> DiscoverAsync(
            bool movies, int? genreId, string? region, DateTime? releasedAfter, int page, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TmdbListItem>>([]);

        public Task<IReadOnlyList<TmdbListItem>> GetTrendingAsync(bool movies, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TmdbListItem>>([]);

        public Task<IReadOnlyList<TmdbListItem>> GetRecommendationsAsync(
            bool movies, long tmdbId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TmdbListItem>>([]);

        public Task<IReadOnlyList<TmdbListItem>> GetSimilarAsync(
            bool movies, long tmdbId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TmdbListItem>>([]);

        public Task<IReadOnlyList<TmdbKeyword>> SearchKeywordsAsync(string query, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TmdbKeyword>>([]);

        public Task<IReadOnlyList<TmdbListItem>> SearchTitlesAsync(
            bool movies, string query, int page, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TmdbListItem>>([]);

        public Task<TmdbPerson?> SearchPersonAsync(string name, CancellationToken ct = default) =>
            Task.FromResult<TmdbPerson?>(null);

        public Task<IReadOnlyList<TmdbListItem>> GetPersonCreditsAsync(
            long personId, PersonCreditRole role, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TmdbListItem>>([]);

        public Task<IReadOnlyList<TmdbListItem>> DiscoverByConceptAsync(
            bool movies, IReadOnlyList<int> genreIds, IReadOnlyList<int> keywordIds, string? region,
            DateTime? releasedAfter, int page, IReadOnlyList<int>? excludeGenreIds = null,
            IReadOnlyList<int>? excludeKeywordIds = null, DateTime? releasedBefore = null,
            int? maxRuntimeMinutes = null, TmdbDiscoverOptions? options = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TmdbListItem>>([]);

        public Task<IReadOnlyList<TmdbWatchProvider>> GetWatchProvidersAsync(
            long tmdbId, bool movie, string region, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TmdbWatchProvider>>([]);
    }
}
