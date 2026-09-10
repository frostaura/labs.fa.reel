using FrostAura.Reel.Application.CrossReference;
using FrostAura.Reel.Application.Ml;
using FrostAura.Reel.Application.Ranking;
using FrostAura.Reel.Application.Search;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.CrossReference;
using FrostAura.Reel.Domain.Filters;
using FrostAura.Reel.Domain.Lens;
using FrostAura.Reel.Domain.Library;
using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Domain.Tenancy;
using FrostAura.Reel.Tests.IntegrationTests;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Pgvector;

namespace FrostAura.Reel.Tests.CrossReference;

/// <summary>
/// The backtest end to end against a scripted judge: rows land for every origin plus a mean, the
/// Wilson maths matches a hand computation, and the gate reads the bound rather than the estimate.
///
/// <para>The judge is scripted rather than stubbed so the arithmetic under test is the BACKTEST's,
/// not the model's — a real stub's verdicts depend on seeded ratings and would make the expected
/// numbers a function of the fixture instead of a hand-checkable constant.</para>
/// </summary>
[Collection("postgres")]
public class CrossReferenceBacktestTests(PostgresFixture fixture)
{
    /// <summary>
    /// A language nothing else in the postgres collection uses.
    ///
    /// <para>The catalogue is GLOBAL and shared by every class in this collection, and the
    /// eligibility layer's language allow-list defaults to English-only when an account configures
    /// no <c>Language</c> filter. Seeding this file's synthetic titles as English therefore put a
    /// hundred rows into every sibling test's eligible pool and displaced their fixtures — which is
    /// exactly the contamination class <c>CandidateRetrieverTests</c> already documents. A
    /// non-English language makes them invisible to every account but the ones seeded here, which
    /// opt into it explicitly.</para>
    /// </summary>
    /// <summary>
    /// The base dimension every embedding in this file is placed on.
    ///
    /// <para>The catalogue and its embeddings are GLOBAL and shared by every class in the postgres
    /// collection, and retrieval takes a fixed-size nearest-neighbour window BEFORE eligibility is
    /// applied — so a title seeded here that happens to sit near another test's query vector
    /// displaces that test's own fixture from the window, and the eligibility filter then removes it
    /// again, leaving the sibling short. Sibling fixtures use single-digit and low double-digit
    /// dimensions; reserving a block far above them makes every title seeded here exactly orthogonal
    /// to every query they issue, which is the smallest similarity there is.</para>
    ///
    /// <para>It must also be DETERMINISTIC. An earlier version derived the dimension from
    /// <c>string.GetHashCode</c>, which .NET randomises per process — so the fixture landed on a
    /// different set of dimensions on every run and the contamination came and went.</para>
    /// </summary>
    private const int EmbeddingDimensionBase = 1200;

    private const string TestLanguage = "zz";

    /// <summary>Guards the one-time seed; xUnit builds a new instance per test.</summary>
    private static readonly SemaphoreSlim HistoryLock = new(1, 1);

    private static Guid? _sharedHistoryAccountId;

    private static int _seeded;

    private readonly string _scope = $"xrefbt{Guid.NewGuid():N}";

    [Fact]
    public async Task An_account_below_the_history_floor_produces_no_run_at_all()
    {
        if (!fixture.Available)
        {
            return;
        }

        // Silence, not a row of zeros: a run with no holdout has measured nothing, and a persisted
        // zero would read on the dev surface as a perfect false-rule-out rate.
        var accountId = await SeedAccountAsync();
        for (var i = 0; i < 10; i++)
        {
            await SeedRatedAsync(accountId, $"Early {i}", 8, DateTime.UtcNow.AddDays(-200 + i));
        }

        Assert.Null(await RunAsync(accountId, new ScriptedJudge(_ => CrossReferenceVerdict.Unsure)));
    }

    [Fact]
    public async Task A_run_writes_one_row_per_evaluable_origin_plus_a_mean()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedHistoryAsync();
        var report = await RunAsync(accountId, new ScriptedJudge(_ => CrossReferenceVerdict.Unsure));

        Assert.NotNull(report);
        Assert.NotEmpty(report.Origins);
        Assert.All(report.Origins, o => Assert.NotNull(o.SplitFraction));
        Assert.Null(report.Mean.SplitFraction);

        await using var db = fixture.CreateContext(new AccountContext());
        var stored = db.CrossReferenceEvaluations.Where(e => e.RunId == report.RunId).ToList();
        Assert.Equal(report.Origins.Count + 1, stored.Count);
        Assert.All(stored, e => Assert.False(string.IsNullOrWhiteSpace(e.Caveat)));
        Assert.All(stored, e => Assert.Equal(GatingDecisions.DemoteOnly, e.GatingDecision));
    }

    [Fact]
    public async Task A_judge_that_rules_out_everything_scores_a_false_rule_out_rate_of_one()
    {
        if (!fixture.Available)
        {
            return;
        }

        // The degenerate case, and the reason the gate has a true-rule-out clause at all: a judge
        // that says "out" to everything has a perfect true-rule-out rate and is worthless.
        var accountId = await SeedHistoryAsync();
        var report = await RunAsync(accountId, new ScriptedJudge(_ => CrossReferenceVerdict.Out));

        Assert.NotNull(report);
        Assert.True(report.Mean.LovedCount > 0, "the fixture must produce a loved holdout");
        Assert.Equal(1m, report.Mean.FalseRuleOutRate);
        Assert.Equal(1m, report.Mean.TrueRuleOutRate);
        Assert.Equal(GatingDecisions.DemoteOnly, report.Mean.GatingDecision);

        // Every loved holdout title ruled out ⇒ the interval sits at the top of the range.
        Assert.True(report.Mean.WilsonHigh > 0.9m, $"upper bound was {report.Mean.WilsonHigh}");
    }

    [Fact]
    public async Task A_perfect_judge_still_cannot_gate_on_a_small_holdout()
    {
        if (!fixture.Available)
        {
            return;
        }

        // The whole point of the bound. This judge never rules out a loved title and rules out
        // every disliked one — flawless on the point estimates — and at this sample size the 95%
        // upper bound on the false-rule-out rate is still well above 15%.
        var accountId = await SeedHistoryAsync();
        var report = await RunAsync(accountId, new ScriptedJudge(
            rating => rating <= CrossReferenceBacktest.DislikedCeiling
                ? CrossReferenceVerdict.Out
                : CrossReferenceVerdict.Keep));

        Assert.NotNull(report);
        Assert.Equal(0m, report.Mean.FalseRuleOutRate);
        Assert.Equal(1m, report.Mean.TrueRuleOutRate);
        Assert.Equal(0m, report.Mean.UnsureRate);

        var (low, high) = CrossReferenceBacktest.WilsonInterval(0, report.Mean.LovedCount);
        Assert.Equal(low, report.Mean.WilsonLow);
        Assert.Equal(high, report.Mean.WilsonHigh);
        Assert.Equal(
            high <= CrossReferenceBacktest.FalseRuleOutCeiling ? GatingDecisions.Gating : GatingDecisions.DemoteOnly,
            report.Mean.GatingDecision);
    }

    [Fact]
    public async Task The_run_bills_to_the_eval_purpose_and_never_to_the_serving_one()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedHistoryAsync();
        var judge = new ScriptedJudge(_ => CrossReferenceVerdict.Unsure);
        await RunAsync(accountId, judge);

        // Measuring the judge must never be able to consume the budget the judge runs on.
        Assert.NotEmpty(judge.Purposes);
        Assert.All(judge.Purposes, p => Assert.Equal(Domain.Sync.ApiUsagePurpose.CrossReferenceEval, p));
    }

    [Fact]
    public async Task The_backtest_judges_round_one_only()
    {
        if (!fixture.Available)
        {
            return;
        }

        // Round 2 exists to make a Confirmed Keep mean something; the question here is entirely
        // about ruling out, and judging both would double the cost of the measurement to answer a
        // question it is not asking.
        var accountId = await SeedHistoryAsync();
        var judge = new ScriptedJudge(_ => CrossReferenceVerdict.Unsure);
        await RunAsync(accountId, judge);

        Assert.All(judge.Rounds, r => Assert.Equal(CrossReferenceRound.First, r));
    }

    [Fact]
    public async Task The_backtest_never_shows_the_judge_a_model_prediction()
    {
        if (!fixture.Available)
        {
            return;
        }

        // A TitleScore is written by a present-day artifact trained on the whole history, so
        // putting one into an as-of prompt is the same leak wearing a different hat.
        var accountId = await SeedHistoryAsync();
        var judge = new ScriptedJudge(_ => CrossReferenceVerdict.Unsure);
        await RunAsync(accountId, judge);

        Assert.NotEmpty(judge.Seen);
        Assert.All(judge.Seen, c => Assert.Null(c.Predicted));
    }

    [Fact]
    public async Task Every_judged_title_lands_as_its_own_item_row()
    {
        if (!fixture.Available)
        {
            return;
        }

        // The whole point of the table: an aggregate cannot be re-interrogated. The first live run
        // reported a false-rule-out rate of 12.0% and could not say WHICH titles it was wrong about,
        // nor whether `keep` carried signal, without repeating the entire paid run.
        var accountId = await SeedHistoryAsync();
        var report = await RunAsync(accountId, new ScriptedJudge(
            rating => rating >= 8 ? CrossReferenceVerdict.Keep : CrossReferenceVerdict.Out));

        Assert.NotNull(report);

        await using var db = fixture.CreateContext(new AccountContext());
        foreach (var origin in report.Origins)
        {
            var items = db.CrossReferenceEvaluationItems.Where(i => i.EvaluationId == origin.Id).ToList();

            // One per JUDGED title. Titles skipped for having no embedding and no attributes never
            // reached the judge and are already outside HoldoutJudged, so they are outside this
            // count too — they are reported on NoEmbeddingCount and are in no denominator.
            Assert.Equal(origin.HoldoutJudged, items.Count);
            Assert.All(items, i => Assert.Equal(origin.SplitFraction, i.SplitFraction));
            Assert.All(items, i => Assert.Equal(accountId, i.AccountId));
            // The later rating is the one field on the row the judge never saw.
            Assert.All(items, i => Assert.True(i.LaterRating is 9 or 3, $"unexpected later rating {i.LaterRating}"));
            Assert.All(items, i => Assert.False(string.IsNullOrWhiteSpace(i.ReasonCode)));
            Assert.All(
                items,
                i => Assert.True(
                    CrossReferenceReasons.IsLegal(i.Verdict, i.ReasonCode),
                    $"{i.Verdict} / {i.ReasonCode} is not a legal pair"));

            // The verdict recorded per title agrees with the aggregate it rolled into.
            Assert.Equal(origin.KeepCount, items.Count(i => i.Verdict == CrossReferenceVerdict.Keep));
            Assert.Equal(origin.OutCount, items.Count(i => i.Verdict == CrossReferenceVerdict.Out));
        }

        // The mean row summarises the origins and owns no items of its own.
        Assert.Empty(db.CrossReferenceEvaluationItems.Where(i => i.EvaluationId == report.Mean.Id).ToList());
    }

    [Fact]
    public async Task The_discrimination_metrics_separate_a_judge_that_works_from_one_that_does_not()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedHistoryAsync();

        // A judge that reads the outcome perfectly: every loved title kept, every disliked one out.
        var perfect = await RunAsync(accountId, new ScriptedJudge(
            rating => rating >= 8 ? CrossReferenceVerdict.Keep : CrossReferenceVerdict.Out));
        Assert.NotNull(perfect);
        Assert.Equal(1m, perfect.Mean.AucKeepVsOut);
        Assert.Equal(1m, perfect.Mean.KeepLovedRate);
        Assert.Equal(0m, perfect.Mean.OutLovedRate);
        Assert.True(perfect.Mean.SpearmanConfidenceVsRating > 0m);

        // …and one whose answer has nothing to do with it. This is the 2026-09-10 shape: the rates
        // look survivable in isolation and the AUC says the ordering is a coin.
        var index = 0;
        var arbitrary = await RunAsync(accountId, new ScriptedJudge(
            _ => index++ % 2 == 0 ? CrossReferenceVerdict.Keep : CrossReferenceVerdict.Out));
        Assert.NotNull(arbitrary);
        Assert.NotNull(arbitrary.Mean.AucKeepVsOut);
        Assert.True(
            Math.Abs(arbitrary.Mean.AucKeepVsOut.Value - 0.5m) < 0.2m,
            $"an uncorrelated judge should sit near 0.5, not {arbitrary.Mean.AucKeepVsOut}");
    }

    [Fact]
    public async Task A_non_discriminating_run_says_so_in_the_persisted_caveat()
    {
        if (!fixture.Available)
        {
            return;
        }

        // Out on everything: false-rule-out 1.0, true-rule-out 1.0 — identical, which is the
        // clearest possible statement that the verdict is not distinguishing anything.
        var accountId = await SeedHistoryAsync();
        var report = await RunAsync(accountId, new ScriptedJudge(_ => CrossReferenceVerdict.Out));

        Assert.NotNull(report);
        Assert.Contains(
            "not distinguishing loved from disliked on this history",
            report.Mean.Caveat,
            StringComparison.Ordinal);
        // And the caveat it is appended to still names n and the interval.
        Assert.Contains("95% Wilson interval", report.Mean.Caveat, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_requested_prompt_version_reaches_the_judge_the_rows_and_the_evidence()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedHistoryAsync();

        var v1Judge = new ScriptedJudge(_ => CrossReferenceVerdict.Unsure);
        var v1 = await RunAsync(accountId, v1Judge, CrossReferencePromptVersions.First);
        Assert.NotNull(v1);
        Assert.All(v1Judge.Options, o => Assert.Equal(CrossReferencePromptVersions.First, o.PromptVersion));
        // v1 renders neither of v2's two extra inputs, so the backtest must not be building them
        // into the options either — a prompt cannot be byte-identical if its inputs are not.
        Assert.All(v1Judge.Options, o => Assert.Null(o.GenreAffinity));
        Assert.All(v1Judge.Options, o => Assert.Null(o.LovedShare));
        Assert.Equal(CrossReferencePromptVersions.First, v1.Mean.PromptVersion);

        var v2Judge = new ScriptedJudge(_ => CrossReferenceVerdict.Unsure);
        var v2 = await RunAsync(accountId, v2Judge, CrossReferencePromptVersions.Second);
        Assert.NotNull(v2);
        Assert.All(v2Judge.Options, o => Assert.Equal(CrossReferencePromptVersions.Second, o.PromptVersion));
        // The base rates are computed from ratings cut at the split, so they are present and are a
        // real share of this fixture's history rather than a placeholder.
        Assert.All(v2Judge.Options, o => Assert.NotNull(o.LovedShare));
        Assert.All(v2Judge.Options, o => Assert.NotNull(o.DislikedShare));
        Assert.All(v2Judge.Options, o => Assert.InRange(o.LovedShare!.Value, 0m, 1m));
        Assert.Equal(CrossReferencePromptVersions.Second, v2.Mean.PromptVersion);
        Assert.All(v2.Origins, o => Assert.Equal(CrossReferencePromptVersions.Second, o.PromptVersion));

        // Two generations, two runs, both readable — which is what makes the comparison a read.
        Assert.NotEqual(v1.RunId, v2.RunId);
    }

    [Fact]
    public async Task The_version_two_base_rates_are_cut_at_the_split()
    {
        if (!fixture.Available)
        {
            return;
        }

        // The fixture's train half is 60 ratings of 6, 7 and 8, and its holdout half is 40
        // alternating 9s and 3s laid down after it. Over the WHOLE history the dislike share is
        // exactly 20/100; cut at a split it can only ever be lower, and at the earliest origin —
        // whose split point is the first holdout rating — there is not a single 3 before it at all.
        // The share is the tell, and it is the cheapest possible check that this prompt input is not
        // reading the holdout it is about to be scored on.
        var accountId = await SeedHistoryAsync();
        var judge = new ScriptedJudge(_ => CrossReferenceVerdict.Unsure);
        await RunAsync(accountId, judge, CrossReferencePromptVersions.Second);

        Assert.NotEmpty(judge.Options);
        var shares = judge.Options.Select(o => o.DislikedShare!.Value).Distinct().ToList();
        Assert.All(shares, share => Assert.True(share < 0.20m, $"dislike share {share} reaches the holdout"));
        Assert.Contains(0m, shares);
    }

    private Task<CrossReferenceBacktest.Report?> RunAsync(Guid accountId, ICrossReferenceJudge judge) =>
        RunAsync(accountId, judge, CrossReferencePromptVersions.First);

    private async Task<CrossReferenceBacktest.Report?> RunAsync(
        Guid accountId, ICrossReferenceJudge judge, int promptVersion)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var features = new FeatureVectorBuilder(db);
        var dislikes = new DislikeSignal(db, new MemoryCache(new MemoryCacheOptions()));
        var personalisation = new PersonalisationContext(db, dislikes);
        var eligibility = new EligibilityQueryBuilder(db);
        var candidates = new CrossReferenceCandidateBuilder(db, new NeighbourFinder(db));
        var backtest = new CrossReferenceBacktest(
            db, judge, features, personalisation, eligibility, candidates,
            NullLogger<CrossReferenceBacktest>.Instance);
        return await backtest.RunAsync(accountId, promptVersion, CancellationToken.None);
    }

    /// <summary>
    /// Enough history to split, with a holdout that carries both loved and disliked titles —
    /// seeded ONCE for the whole class and reused.
    ///
    /// <para><b>The sharing is not an optimisation.</b> xUnit builds a fresh instance per test, so
    /// a per-test fixture put a hundred titles and their embeddings into the GLOBAL catalogue six
    /// times over, and five hundred extra rows was enough to change what an unrelated class's
    /// retrieval window contained — the contamination class <c>CandidateRetrieverTests</c>
    /// documents, arriving here as volume rather than as similarity. The backtest is read-only over
    /// this history (it writes only its own evaluation rows, under a fresh RunId each time), so one
    /// account serves every test in the class without them observing each other.</para>
    ///
    /// <para>The ratings are laid down oldest-first on <c>FirstRatedAt</c> so the split origins land
    /// inside the range; the later half deliberately alternates loved and disliked so every origin
    /// has both denominators. Only the HOLDOUT half carries an embedding: those are the titles the
    /// judge is asked about, and an embedding on a train title would be forty more rows in the
    /// shared catalogue buying nothing.</para>
    /// </summary>
    private async Task<Guid> SeedHistoryAsync()
    {
        await HistoryLock.WaitAsync();
        try
        {
            if (_sharedHistoryAccountId is { } existing)
            {
                return existing;
            }

            var accountId = await SeedAccountAsync();
            var start = DateTime.UtcNow.AddDays(-400);

            // The train half: 60 middling ratings, no embeddings needed.
            for (var i = 0; i < 60; i++)
            {
                await SeedRatedAsync(accountId, $"Train {i}", (short)(6 + (i % 3)), start.AddDays(i), embedded: false);
            }

            // The holdout half: 40 alternating loves and dislikes, all still ELIGIBLE as-of every
            // split (no watch footprint, no reaction) so they reach the judge.
            for (var i = 0; i < 40; i++)
            {
                await SeedRatedAsync(
                    accountId, $"Holdout {i}", i % 2 == 0 ? (short)9 : (short)3, start.AddDays(100 + i));
            }

            _sharedHistoryAccountId = accountId;
            return accountId;
        }
        finally
        {
            HistoryLock.Release();
        }
    }

    private async Task<Guid> SeedAccountAsync()
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var account = new Account
        {
            Id = Guid.NewGuid(),
            TraktUserSlug = $"xrefbt-{Guid.NewGuid():N}",
            CreatedAt = DateTime.UtcNow,
            LastSeenAt = DateTime.UtcNow,
            PipelineStageChangedAt = DateTime.UtcNow,
        };
        db.Add(account);
        // Narrows the eligible pool to this instance's own rows: the catalogue is global and shared
        // by every class in the postgres collection.
        db.ContentFilters.Add(new ContentFilter
        {
            Id = Guid.NewGuid(),
            AccountId = account.Id,
            Kind = FilterKind.IncludeGenre,
            Value = _scope,
            CreatedAt = DateTime.UtcNow,
        });
        // …and opts into the language those rows carry. Without this the account's own pool would be
        // empty, because no Language filter means English-only.
        db.ContentFilters.Add(new ContentFilter
        {
            Id = Guid.NewGuid(),
            AccountId = account.Id,
            Kind = FilterKind.Language,
            Value = TestLanguage,
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        return account.Id;
    }

    private async Task<Guid> SeedRatedAsync(
        Guid accountId, string name, short rating, DateTime at, bool embedded = true)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var title = new Title
        {
            Id = Guid.NewGuid(),
            MediaType = MediaType.Movie,
            TmdbId = Random.Shared.NextInt64(1, long.MaxValue),
            TraktSlug = string.Empty,
            Name = name,
            Language = TestLanguage,
            PosterPath = "/p.jpg",
            Genres = ["drama", _scope],
            TmdbPopularity = 1m,
            Overview = "A synthetic title for the cross-reference backtest.",
            CreatedAt = DateTime.UtcNow,
        };
        db.Add(title);
        if (embedded)
        {
            var embedding = new float[1536];
            embedding[EmbeddingDimensionBase + (Interlocked.Increment(ref _seeded) % 300)] = 1f;
            db.TitleEmbeddings.Add(new TitleEmbedding
            {
                TitleId = title.Id,
                Embedding = new Vector(embedding),
                EmbeddingModel = "test",
                SourceTextHash = Guid.NewGuid().ToString("N"),
                CreatedAt = DateTime.UtcNow,
            });
        }
        db.UserRatings.Add(new UserRating
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            TitleId = title.Id,
            SubjectType = RatingSubjectType.Movie,
            Rating = rating,
            RatedAt = at,
            FirstRatedAt = at,
            SyncedAt = at,
        });
        await db.SaveChangesAsync();
        return title.Id;
    }

    /// <summary>
    /// A judge whose answer is a function of the title's LATER rating, supplied by the test through
    /// a name convention. It reads the outcome on purpose — that is what makes "a perfect judge"
    /// and "a judge that rules out everything" expressible as one-liners, and the arithmetic under
    /// test is the backtest's, not the model's.
    /// </summary>
    private sealed class ScriptedJudge(Func<short, CrossReferenceVerdict> decide) : ICrossReferenceJudge
    {
        public List<CrossReferenceCandidate> Seen { get; } = [];

        public List<string> Purposes { get; } = [];

        public List<CrossReferenceRound> Rounds { get; } = [];

        /// <summary>Every options record the backtest handed in — the prompt-version routing assertion.</summary>
        public List<CrossReferenceJudgeOptions> Options { get; } = [];

        public bool IsAvailable => true;

        public string ModelId => "scripted/test";

        public int PromptVersion => CrossReferencePromptVersions.First;

        public Task<CrossReferenceBatchResult> JudgeAsync(
            IReadOnlyList<CrossReferenceCandidate> candidates, LensPersona persona, CrossReferenceRound round,
            string usagePurpose, CrossReferenceJudgeOptions options, CancellationToken ct = default)
        {
            Seen.AddRange(candidates);
            Purposes.Add(usagePurpose);
            Rounds.Add(round);
            Options.Add(options);

            var entries = candidates.Select(c =>
            {
                // "Holdout 12" ⇒ even index ⇒ rated 9; odd ⇒ rated 3. Train rows never reach here.
                var rating = c.Name.StartsWith("Holdout ", StringComparison.Ordinal)
                    && int.TryParse(c.Name["Holdout ".Length..], out var index)
                    ? index % 2 == 0 ? (short)9 : (short)3
                    : (short)7;
                var verdict = decide(rating);
                return new CrossReferenceEntry(c.TitleId, verdict, ReasonFor(verdict), 0.9d);
            }).ToList();

            return Task.FromResult(new CrossReferenceBatchResult(entries, true, 100, 20, 0));
        }

        private static string ReasonFor(CrossReferenceVerdict verdict) => verdict switch
        {
            CrossReferenceVerdict.Keep => CrossReferenceReasons.NeighbourLoved,
            CrossReferenceVerdict.Out => CrossReferenceReasons.NeighbourDisliked,
            _ => CrossReferenceReasons.InsufficientSignal,
        };
    }
}
