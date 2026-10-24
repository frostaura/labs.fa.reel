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
        // A rebuild's worth: under MinJudgedForRebuild the run keeps its verdicts and asks for nothing.
        var candidates = await SeedCandidatesAsync(accountId, CrossReferenceJobHandler.MinJudgedForRebuild, embedded: true);

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

        // Progress names the pass and counts what it ruled out — with NO denominator: no total and
        // no percentage, because a pass that can take four days has no honest fraction.
        var progress = Assert.Single(events.Progress);
        Assert.Equal("xref", progress["kind"]);
        Assert.Equal(candidates.Count, progress["current"]);
        Assert.False(progress.ContainsKey("total"));
        Assert.False(progress.ContainsKey("pct"));
        Assert.Equal("Cross-referencing · first pass · 0 ruled out so far", progress["message"]);

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
        // Neither run asked for a rebuild: the second judged nothing, and the first judged five —
        // under MinJudgedForRebuild, which is what stops build → cross-reference → build from
        // ping-ponging over the handful of titles each build brings in.
        Assert.Equal(0, await db.SyncJobs.CountAsync(j => j.AccountId == accountId && j.Kind == JobKind.BuildFeed));
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
    public async Task A_build_the_last_pass_asked_for_does_not_queue_another_pass()
    {
        if (!Fixture.Available)
        {
            return;
        }

        var accountId = await SeedBuildableAccountAsync();
        await SeedCandidatesAsync(accountId, 6, embedded: false, scored: false);
        await RunBuildAsync(accountId);
        await CompleteCrossReferenceJobsAsync(accountId);

        // The pool is still unjudged, so an ordinary build queues the pass — and does.
        Assert.True(await SchedulerFiresAsync(accountId));
        await CompleteCrossReferenceJobsAsync(accountId);

        // The same pool, but the build asking is the one the pass itself requested. Queueing here
        // is the loop the live job table showed: pass → rebuild → pass, one click, four builds.
        Assert.False(await SchedulerFiresAsync(accountId, CrossReferenceJobHandler.CrossReferenceRebuildReason));
        Assert.False(await CrossReferenceQueuedAsync(accountId));
    }

    [Fact]
    public async Task Only_a_fresh_build_queues_the_pass_and_a_preserve_build_never_does()
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

        // Build two: same day, same artifact, no user-forced reason ⇒ Preserve. Work IS pending,
        // and the pass is still NOT queued: every delta poll and every save used to queue one, and
        // on the live table that was a day of passes each paying for a batch the judge then failed.
        // The next Fresh build (tomorrow, a new model, a manual refresh) picks the work up.
        await RunBuildAsync(accountId);
        await AssertLatestBuildWasNotFreshAsync(accountId);
        Assert.False(await CrossReferenceQueuedAsync(accountId));

        // A user-forced build is Fresh, and with work pending it queues.
        await RunBuildAsync(accountId, FeedBuildPolicy.ManualRefreshReason);
        Assert.True(await CrossReferenceQueuedAsync(accountId));

        // Now judge everything at the current fingerprint, and force another Fresh build. With no
        // unjudged work the anti-join answers no, and nothing is queued.
        await CompleteCrossReferenceJobsAsync(accountId);
        await RunAsync(accountId, new StubJudge(), new RecordingEventHub());
        await CompleteCrossReferenceJobsAsync(accountId);

        await RunBuildAsync(accountId, FeedBuildPolicy.ManualRefreshReason);
        Assert.False(await CrossReferenceQueuedAsync(accountId));
    }

    [Theory]
    [InlineData(FeedBuildOutcome.Fresh, null, true)]
    [InlineData(FeedBuildOutcome.Fresh, "model-trained", true)]
    [InlineData(FeedBuildOutcome.Fresh, FeedBuildPolicy.ManualRefreshReason, true)]
    [InlineData(FeedBuildOutcome.Preserve, null, false)]
    [InlineData(FeedBuildOutcome.Preserve, "delta-sync", false)]
    [InlineData(FeedBuildOutcome.NoOp, null, false)]
    [InlineData(FeedBuildOutcome.Skipped, null, false)]
    [InlineData(FeedBuildOutcome.Fresh, "saved for later", false)]
    [InlineData(FeedBuildOutcome.Fresh, "not interested", false)]
    [InlineData(FeedBuildOutcome.Fresh, CrossReferenceJobHandler.CrossReferenceRebuildReason, false)]
    public void Should_enqueue_is_fresh_and_not_asked_for_by_a_reaction_or_the_last_pass(
        FeedBuildOutcome outcome, string? reason, bool expected) =>
        Assert.Equal(expected, CrossReferenceScheduler.ShouldEnqueue(outcome, reason));

    // ── WS-1 task 4: a failed re-rank never reaches the page as model output ─────────────────

    [Fact]
    public async Task A_truncating_reranker_writes_no_model_labelled_or_invented_why_into_the_feed()
    {
        if (!Fixture.Available)
        {
            return;
        }

        var accountId = await SeedBuildableAccountAsync();
        await SeedCandidatesAsync(accountId, 12, embedded: false, scored: false);

        // The real adapter over a provider that truncates every completion: each batch fails after
        // the client's one 2x retry and every verdict is a labelled fallback with an EMPTY why.
        var provider = new TruncatingProvider();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["OPENROUTER_API_KEY"] = "test-key-not-a-real-one" })
            .Build();
        var scopeFactory = Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions
            .BuildServiceProvider(new Microsoft.Extensions.DependencyInjection.ServiceCollection())
            .GetService(typeof(Microsoft.Extensions.DependencyInjection.IServiceScopeFactory)) as Microsoft.Extensions.DependencyInjection.IServiceScopeFactory;
        var recorder = new FrostAura.Reel.Infrastructure.Telemetry.ApiUsageRecorder(
            scopeFactory!, NullLogger<FrostAura.Reel.Infrastructure.Telemetry.ApiUsageRecorder>.Instance);
        var keyless = new ConfigurationBuilder().Build();
        var agent = new FrostAura.Reel.Infrastructure.Adapters.OpenRouterSearchAgent(
            new HttpClient(provider) { BaseAddress = new Uri("https://openrouter.invalid/api/v1/") },
            new FrostAura.Reel.Infrastructure.Adapters.OpenRouterSearchInterpreter(
                new HttpClient(), recorder, keyless, NullLogger<FrostAura.Reel.Infrastructure.Adapters.OpenRouterSearchInterpreter>.Instance),
            recorder,
            config,
            NullLogger<FrostAura.Reel.Infrastructure.Adapters.OpenRouterSearchAgent>.Instance);

        await RunBuildAsync(accountId, agent: agent);

        Assert.True(provider.Calls > 0); // the why pass really did ask

        await using var db = Fixture.CreateContext(Scoped(accountId));
        var whys = await db.FeedItems.Where(i => i.AccountId == accountId).Select(i => i.WhyThisSentence).ToListAsync();
        Assert.NotEmpty(whys);
        Assert.DoesNotContain(whys, w => w.Contains("aligned with your taste", StringComparison.OrdinalIgnoreCase));
        var sources = await db.ExposureEvents.Where(e => e.AccountId == accountId).Select(e => e.WhySource).ToListAsync();
        Assert.NotEmpty(sources);
        Assert.DoesNotContain(ExposureWhySource.Llm.Name, sources);
    }

    /// <summary>Every completion stops at its ceiling.</summary>
    private sealed class TruncatingProvider : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            var body = System.Text.Json.JsonSerializer.Serialize(new
            {
                choices = new[] { new { message = new { content = "{\"results\":[" }, finish_reason = "length" } },
                usage = new { prompt_tokens = 900, completion_tokens = 900 },
            });
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }

    // ── Fail fast, never retry the deterministic, and the cross-run breaker ──────────────────

    [Fact]
    public async Task An_unanswered_first_batch_ends_the_run_and_writes_no_progress()
    {
        if (!Fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        await SeedCandidatesAsync(accountId, (CrossReferenceJobHandler.BatchSize * 2) + 10, embedded: false);
        var judge = new StubJudge { Fail = true, FailWith = AgentFailureKind.Truncated };
        var events = new RecordingEventHub();

        var (job, error) = await RunCapturingAsync(accountId, judge, events);

        // One paid batch, not the old three: at the same ceiling every later batch truncates too.
        Assert.Equal(1, judge.Calls);
        Assert.NotNull(error);
        Assert.Equal(JobFailureReason.NoAnswer, JobFailureReason.Classify(error!.Message));
        Assert.Empty(events.Progress);
        Assert.Empty(events.Completed);
        Assert.Null(job.ProgressPct);
        Assert.Null(job.ProgressMessage);
    }

    [Theory]
    [InlineData(AgentFailureKind.Truncated, true)]
    [InlineData(AgentFailureKind.MalformedAnswer, true)]
    [InlineData(AgentFailureKind.ProviderError, false)]
    [InlineData(AgentFailureKind.TimedOut, false)]
    public async Task A_deterministic_failure_is_not_retried_by_the_runner_and_a_transient_one_is(
        AgentFailureKind failure, bool spendsTheAttemptCap)
    {
        if (!Fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        await SeedCandidatesAsync(accountId, 5, embedded: false);

        var (job, error) = await RunCapturingAsync(
            accountId, new StubJudge { Fail = true, FailWith = failure }, new RecordingEventHub(), attemptCount: 1);

        Assert.NotNull(error);
        Assert.Equal(spendsTheAttemptCap, job.AttemptCount >= JobRunnerLimits.MaxAttempts);
    }

    [Fact]
    public async Task Two_fully_failed_runs_turn_the_purpose_off_for_the_day_and_the_third_sends_nothing()
    {
        if (!Fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        await SeedCandidatesAsync(accountId, 5, embedded: false);
        var health = new FrostAura.Reel.Infrastructure.Llm.ProviderHealthService();
        var breaker = new CrossReferenceRunBreaker(health);
        var judge = new StubJudge { Fail = true, FailWith = AgentFailureKind.Truncated };

        var (_, first) = await RunCapturingAsync(accountId, judge, new RecordingEventHub(), breaker: breaker);
        Assert.Null(breaker.OpenBreaker);
        var (_, second) = await RunCapturingAsync(accountId, judge, new RecordingEventHub(), breaker: breaker);
        Assert.NotNull(first);
        Assert.Contains("Off for the rest of the UTC day", second!.Message, StringComparison.Ordinal);

        // The reason is on the status API's reading, as a purpose breaker until the next UTC midnight.
        var snapshot = health.Current();
        Assert.Equal(ProviderHealthState.Degraded, snapshot.State);
        var open = Assert.Single(snapshot.Breakers);
        Assert.Equal(ApiUsagePurpose.CrossReference, open.Purpose);
        Assert.Equal(ProviderBreakerReasons.TruncationBreaker, open.ReasonCode);
        Assert.Equal(DateTime.UtcNow.Date.AddDays(1), open.RetryAt);

        // The third run is paused before it judges anything, and the scheduler queues nothing.
        var (paused, third) = await RunCapturingAsync(accountId, judge, new RecordingEventHub(), breaker: breaker);
        Assert.Null(third);
        Assert.Equal(2, judge.Calls);
        Assert.Equal("cross-reference paused (truncation_breaker)", paused.ProgressMessage);
        Assert.False(await SchedulerFiresAsync(accountId, breaker: breaker, outcome: FeedBuildOutcome.Fresh));
    }

    [Fact]
    public async Task An_answered_batch_resets_the_cross_run_count()
    {
        if (!Fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        await SeedCandidatesAsync(accountId, 5, embedded: false);
        var breaker = new CrossReferenceRunBreaker(new FrostAura.Reel.Infrastructure.Llm.ProviderHealthService());

        await RunCapturingAsync(accountId, new StubJudge { Fail = true, FailWith = AgentFailureKind.Truncated }, new RecordingEventHub(), breaker: breaker);
        await RunCapturingAsync(accountId, new StubJudge(), new RecordingEventHub(), breaker: breaker);
        await SeedCandidatesAsync(accountId, 5, embedded: false);
        await RunCapturingAsync(accountId, new StubJudge { Fail = true, FailWith = AgentFailureKind.Truncated }, new RecordingEventHub(), breaker: breaker);

        Assert.Null(breaker.OpenBreaker);
    }

    [Fact]
    public async Task A_refused_run_lands_on_the_job_as_the_refusal_it_was()
    {
        if (!Fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        await SeedCandidatesAsync(accountId, 5, embedded: false);
        var health = new FrostAura.Reel.Infrastructure.Llm.ProviderHealthService();
        var breaker = new CrossReferenceRunBreaker(health);
        // The adapter's client opens the provider breaker on the 402; the stub stands in for both.
        var judge = new StubJudge
        {
            Fail = true,
            FailWith = AgentFailureKind.ProviderRefused,
            Hook = _ =>
            {
                health.ReportRefused(ApiProvider.OpenRouter, 402);
                return Task.CompletedTask;
            },
        };

        var (job, error) = await RunCapturingAsync(accountId, judge, new RecordingEventHub(), breaker: breaker);

        Assert.Equal(JobFailureReason.PaymentRequired, JobFailureReason.Classify(error!.Message));
        Assert.True(job.AttemptCount >= JobRunnerLimits.MaxAttempts);
        // A refusal is not evidence about the model: the cross-run count did not move.
        health.ReportAnswered(ApiProvider.OpenRouter);
        Assert.Null(breaker.OpenBreaker);
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

    /// <summary>As <see cref="RunAsync"/>, but hands back the job row AND the exception the runner would have caught.</summary>
    private async Task<(SyncJob Job, Exception? Error)> RunCapturingAsync(
        Guid accountId, StubJudge judge, RecordingEventHub events, IConfiguration? configuration = null,
        CrossReferenceRunBreaker? breaker = null, int attemptCount = 1)
    {
        var job = new SyncJob
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            Kind = JobKind.CrossReference,
            EnqueuedAt = DateTime.UtcNow,
            AttemptCount = attemptCount,
        };
        try
        {
            await RunAsync(accountId, judge, events, configuration, breaker, job);
            return (job, null);
        }
        catch (InvalidOperationException ex)
        {
            return (job, ex);
        }
    }

    /// <summary>Runs the handler exactly as the job runner would, and hands back the job row.</summary>
    private async Task<SyncJob> RunAsync(
        Guid accountId, StubJudge judge, RecordingEventHub events, IConfiguration? configuration = null,
        CrossReferenceRunBreaker? breaker = null, SyncJob? job = null)
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
            NullLogger<CrossReferenceJobHandler>.Instance,
            breaker);

        job ??= new SyncJob
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
    private async Task<bool> SchedulerFiresAsync(
        Guid accountId, string? buildTriggerReason = null, CrossReferenceRunBreaker? breaker = null,
        FeedBuildOutcome? outcome = null)
    {
        await using var db = Fixture.CreateContext(Scoped(accountId));
        var scheduler = new CrossReferenceScheduler(
            db,
            new EligibilityQueryBuilder(db),
            new CrossReferenceService(db, NullLogger<CrossReferenceService>.Instance),
            new CrossReferenceBudget(db, Config()),
            new StubJudge(),
            NullLogger<CrossReferenceScheduler>.Instance,
            breaker);
        return await scheduler.EnqueueIfWorkPendingAsync(
            accountId, await ProfileHashAsync(accountId), DateTime.UtcNow, CancellationToken.None, buildTriggerReason, outcome);
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
    private async Task RunBuildAsync(Guid accountId, string? triggerReason = null, ISearchAgent? agent = null)
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
            agent ?? new UnavailableSearchAgent(),
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
                TriggerReason = triggerReason,
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

        /// <summary>Why a failing batch failed, as the real adapter reports it (null: an older double that does not say).</summary>
        public AgentFailureKind? FailWith { get; init; }

        /// <summary>Set to bill one ledger call per batch, the way the real adapter's finally does.</summary>
        public Guid? LedgerAccountId { get; init; }

        public PostgresFixture? Fixture { get; init; }

        /// <summary>Runs after the call is counted, so a test can move the world mid-run.</summary>
        public Func<int, Task>? Hook { get; init; }

        public bool IsAvailable => true;

        public string ModelId => "stub/cross-reference";

        public int PromptVersion => Version;

        /// <summary>
        /// The EXPLICIT-generation overload, which is the only abstract member on the port — the
        /// serving job still calls the five-argument one and arrives here through the interface's
        /// default implementation, carrying <see cref="PromptVersion"/>.
        /// </summary>
        public async Task<CrossReferenceBatchResult> JudgeAsync(
            IReadOnlyList<CrossReferenceCandidate> candidates, LensPersona persona, CrossReferenceRound round,
            string usagePurpose, CrossReferenceJudgeOptions options, CancellationToken ct = default)
        {
            Assert.Equal(ApiUsagePurpose.CrossReference, usagePurpose);
            Assert.Equal(Version, options.PromptVersion);
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
                return new CrossReferenceBatchResult([], Succeeded: false, null, null, 0, FailWith);
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
