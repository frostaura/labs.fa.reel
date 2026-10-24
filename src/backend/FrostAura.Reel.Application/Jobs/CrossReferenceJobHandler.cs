using System.Globalization;
using System.Text.Json;
using FrostAura.Reel.Application.CrossReference;
using FrostAura.Reel.Application.Llm;
using FrostAura.Reel.Application.Ml;
using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Application.Pipeline;
using FrostAura.Reel.Application.Ranking;
using FrostAura.Reel.Application.Search;
using FrostAura.Reel.Domain.CrossReference;
using FrostAura.Reel.Domain.Lens;
using FrostAura.Reel.Domain.Ml;
using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Domain.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FrostAura.Reel.Application.Jobs;

/// <summary>
/// The cross-reference pass. Judges the account's whole eligible pool — not the top forty — in
/// batches of sixty, each candidate shown beside the k nearest titles the viewer has ACTUALLY
/// rated, and persists one closed-enum verdict per candidate.
///
/// <para><b>What it writes is KEEP-ONLY at the ranking layer.</b> Since 2026-09-11 the feed reads
/// these verdicts, and a Keep lifts a title's rank key by at most a fifth. An <c>out</c> still
/// demotes nothing and bars nothing: on the founder's own holdout at prompt v2 it caught 16.2%
/// [Wilson 12.3, 21.0] of the titles he went on to rate 8 or higher against 22.8% of the ones he
/// rated 4 or lower. The rule and its measurements live on <c>VerdictAggregator</c>; this handler
/// only writes the evidence.</para>
///
/// <para><b>Why it is a job at all.</b> A ten-minute pass inside <c>BuildFeedJobHandler</c> takes
/// the feed down — the same reasoning <c>PersonaliseTitlesJobHandler</c> is built on, one order of
/// magnitude worse. It runs afterwards at priority 2, boxed well under the runner's twelve-minute
/// stale threshold, and holds a process-wide gate so five accounts cannot occupy four runner slots
/// for ten minutes each.</para>
///
/// <para><b>It never re-enqueues itself.</b> The partial unique index on (AccountId, Kind) WHERE
/// Pending/Running guarantees a 23505 for a per-account kind, and the absorbing save at the enqueue
/// site would swallow the continuation without a sound. A run boxes itself, saves per batch, and
/// stops; the enqueue point in the feed build picks up the remainder on the next build, gated on
/// the same three brakes as the first one. The resume state is the verdict table's own sweep index
/// — correct after a crash, a budget stop, a partial batch and a concurrent run, none of which a
/// stored cursor survives.</para>
/// </summary>
public sealed class CrossReferenceJobHandler(
    IReelDbContext db,
    EligibilityQueryBuilder eligibility,
    FeatureVectorBuilder featureBuilder,
    TasteFactsBuilder tasteFacts,
    PersonalisationContext personalisation,
    CrossReferenceCandidateBuilder candidateBuilder,
    CrossReferenceService verdicts,
    CrossReferenceBudget budget,
    ICrossReferenceJudge judge,
    FeedRefreshCoordinator feedRefresh,
    IPipelineEventHub events,
    ILogger<CrossReferenceJobHandler> logger,
    // Optional so a hand-built handler (a test) needs no breaker; DI always supplies it.
    CrossReferenceRunBreaker? breaker = null) : IJobHandler
{
    /// <summary>Candidates per provider call. The whole budget arithmetic is written against 60.</summary>
    public const int BatchSize = 60;

    /// <summary>The short SSE vocabulary name. Mirrored in <c>src/frontend/src/lib/syncCopy.ts</c>.</summary>
    public const string SseKind = "xref";

    /// <summary>
    /// The trigger reason the completion rebuild carries. Deliberately absent from
    /// <c>FeedBuildPolicy.IsUserForced</c>: this is drift, not a request, and forcing a Fresh build
    /// would reinstate by hand exactly the hero churn the stable-tonight contract exists to prevent.
    /// </summary>
    public const string CrossReferenceRebuildReason = "cross-reference";

    /// <summary>What the completion frame says when no backtest has ever run for this account.</summary>
    public const string Unmeasured = "unmeasured";

    /// <summary>
    /// How many verdicts a run must have written before it asks for a rebuild. Every build brings
    /// a handful of newly scored titles into the pool, so a run over three of them that then asks
    /// for a build — which brings three more — is the ping-pong the live job table showed: one
    /// refresh click, four feed builds, four cross-reference runs, each a single call over a few
    /// titles. A run under the floor keeps its verdicts; the next build the account earns on its
    /// own reads them.
    /// </summary>
    public const int MinJudgedForRebuild = 20;

    /// <summary>
    /// What the pill calls the round. Only the first pass exists today, so the copy names the pass
    /// it is on and promises nothing about passes that are not built — "round 1 of 3" counted to a
    /// total the user would never see reached, which is the opposite of the honest indicator the
    /// feature was asked for. When round 2 ships it gets its own name here, not a denominator.
    /// </summary>
    public static string RoundLabel(CrossReferenceRound round) => round switch
    {
        CrossReferenceRound.First => "first pass",
        CrossReferenceRound.Second => "second pass",
        _ => "deep pass",
    };

    /// <summary>
    /// The soft budget, checked between batches. Eight minutes leaves four before the runner's
    /// twelve-minute stale threshold would reclaim the job and run it twice.
    /// </summary>
    public static readonly TimeSpan SoftBudget = TimeSpan.FromMinutes(8);

    /// <summary>The hard backstop against a provider that has stopped answering at all.</summary>
    public static readonly TimeSpan HardCeiling = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Consecutive failed batches after which the run gives up on the provider.
    ///
    /// <para>Without it a dead provider costs a full ten-minute box of transport timeouts per run,
    /// per account, every build. Three is enough to ride out a single bad response and short enough
    /// that an outage costs one minute rather than ten.</para>
    /// </summary>
    public const int MaxConsecutiveFailures = 3;

    /// <summary>
    /// The process-wide concurrency gate. ONE cross-reference run at a time across every account.
    ///
    /// <para>The runner has four worker slots and this job holds one for up to ten minutes. Without
    /// this, five accounts whose builds finish together would take every slot and the queue — delta
    /// syncs, feed builds, training — would stall behind a background pass nobody is waiting on.
    /// The cost is that a second account's run waits, which is the correct trade: the anti-join
    /// makes waiting free, because whatever it does not judge today it judges tomorrow.</para>
    /// </summary>
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public JobKind Kind => JobKind.CrossReference;

    /// <summary>What one run did, in the numbers the completion frame and the log line both read.</summary>
    private sealed record Tally
    {
        public int Judged { get; set; }

        public int Kept { get; set; }

        public int RuledOut { get; set; }

        public int Unsure { get; set; }

        public int Processed { get; set; }

        public int NoSignal { get; set; }

        public int FailedBatches { get; set; }

        public int SucceededBatches { get; set; }

        /// <summary>Failed batches whose failure is transient (a dropped call, a 5xx, a timeout) — worth the runner's retry.</summary>
        public int TransientFailures { get; set; }

        /// <summary>Batches the provider refused, or that an open breaker or the budget stopped before sending.</summary>
        public int StoppedBeforeAnswer { get; set; }

        /// <summary>The run stopped at the daily ceiling (an ordinary stop, never an error).</summary>
        public bool StoppedByBudget { get; set; }
    }

    public async Task ExecuteAsync(SyncJob job, CancellationToken ct)
    {
        var accountId = job.AccountId ?? throw new InvalidOperationException("CrossReference requires an account.");

        if (!judge.IsAvailable)
        {
            // Configuration, not an outage: no key means the feature is off, and a job that failed
            // for it would retry three times and surface a red pill for a deliberate setting.
            await FinishAsync(job, "cross-reference is not configured", ct);
            return;
        }

        if (breaker?.OpenBreaker is { } open)
        {
            // Paused, not failed: the breaker already says why on the status API, and a run that
            // went ahead would send nothing — the client refuses every call behind an open breaker.
            await FinishAsync(job, $"cross-reference paused ({open.ReasonCode})", ct);
            return;
        }

        var now = DateTime.UtcNow;
        var artifactId = await db.ModelArtifacts
            .Where(a => a.AccountId == accountId && a.Status == ArtifactStatus.Active)
            .OrderByDescending(a => a.Version)
            .Select(a => (Guid?)a.Id)
            .FirstOrDefaultAsync(ct);
        if (artifactId is not { } artifact)
        {
            await FinishAsync(job, "no active model to cross-reference against", ct);
            return;
        }

        var taste = await featureBuilder.BuildTasteStateAsync(accountId, now, ct);

        // ── The fingerprint is PINNED here, for the whole run ───────────────────────────────
        // A rating landing mid-run moves the account's taste, and a run that re-read the
        // fingerprint per batch would write half its verdicts under one key and half under
        // another — two half-instruments instead of one. Verdicts written against a fingerprint
        // the account has since moved off are not wrong, they are STALE: half strength, re-judged
        // first, never deleted.
        var built = await tasteFacts.BuildAsync(accountId, taste, now, ct);
        var profileHash = built.ProfileHash;

        // Round 2 (survivors, different neighbours) is PR4. The round is threaded through every
        // step below rather than hard-coded at the bottom so that adding it is a caller change.
        const CrossReferenceRound round = CrossReferenceRound.First;

        var pool = await CrossReferencePool.Judgeable(db, eligibility, accountId, artifact)
            // Deterministic in, deterministic batches. The judge shuffles WITHIN a batch, so the
            // batching itself must not depend on Postgres row order or two runs disagree about
            // which sixty titles were seen together.
            .OrderBy(id => id)
            .ToListAsync(ct);
        var noSignal = await CrossReferencePool.WithoutSignal(db, eligibility, accountId, artifact).CountAsync(ct);

        var unjudged = await verdicts.UnjudgedAsync(accountId, pool, profileHash, judge.PromptVersion, round, ct);
        var work = await verdicts.StaleFirstAsync(accountId, unjudged, profileHash, ct);
        if (work.Count == 0)
        {
            // The warm-skip path, and the one that must cost ZERO provider calls: a second run over
            // an unchanged pool has nothing to ask.
            // Covered at the CURRENT key — this fingerprint, this prompt generation, this round —
            // which is the same key the scheduler's brake and the resume anti-join read, so "already
            // done" here and "work pending" at the enqueue point can no longer disagree.
            await FinishAsync(
                job,
                string.Create(CultureInfo.InvariantCulture, $"everything eligible is already cross-referenced at prompt v{judge.PromptVersion}"),
                ct);
            await PublishCompletedAsync(accountId, new Tally { NoSignal = noSignal }, ct);
            return;
        }

        var persona = await personalisation.PersonaForAsync(accountId, taste, ct);
        var predictions = await db.TitleScores
            .Where(s => s.AccountId == accountId && s.ModelArtifactId == artifact)
            .Select(s => new { s.TitleId, s.PredictedRating })
            .ToDictionaryAsync(s => s.TitleId, s => s.PredictedRating, ct);

        var tally = new Tally { NoSignal = noSignal };
        var stopped = await RunRoundAsync(job, accountId, artifact, profileHash, round, work, persona, predictions, tally, ct);

        // The zero-answer check comes FIRST — before any progress, completion copy or rebuild is
        // written. A run that answered nothing has not progressed, and the old order wrote "100%"
        // and a completion line onto a job the runner then failed and retried, which the pill kept
        // re-showing on every retry.
        //
        // Total provider failure is reported the only way this runner CAN report it: by throwing, so
        // the message lands on job.Error — the success path clears that column. Nothing was
        // written, so nothing is lost: the anti-join offers the same work to the next run. Rows are
        // never written as Failed — two thousand of those would be indistinguishable from
        // judgements to the resume anti-join and would suppress their own retry for ever.
        if (tally.SucceededBatches == 0 && tally.FailedBatches > 0)
        {
            var tripped = breaker?.RecordRun(
                judge.RunKey, judge.PromptVersion, 0, tally.FailedBatches - tally.StoppedBeforeAnswer) ?? false;

            // Deterministic failures are not retried: a truncation or an unreadable answer on this
            // model and prompt comes back the same on the runner's next attempt, and a refusal or an
            // open breaker sends nothing at all. Only a transient failure earns the runner's retry.
            // The runner retries while AttemptCount is under its cap, so a deterministic failure
            // spends the cap here.
            if (tripped || tally.TransientFailures == 0)
            {
                job.AttemptCount = Math.Max(job.AttemptCount, JobRunnerLimits.MaxAttempts);
            }

            throw new InvalidOperationException(FailureMessage(tally, tripped));
        }

        breaker?.RecordRun(judge.RunKey, judge.PromptVersion, tally.SucceededBatches, tally.FailedBatches);

        job.CursorJson = JsonSerializer.Serialize(new
        {
            round = (int)round,
            judgedSoFar = tally.Judged,
            ruledOutSoFar = tally.RuledOut,
        });
        job.ProgressPct = 100;
        job.ProgressMessage = CompletionMessage(tally);

        // A run that wrote nothing changed nothing, so it asks for nothing — and a run that wrote
        // a handful asks for nothing either (see MinJudgedForRebuild). The guard is not tidiness:
        // a rebuild request from a run over the few titles the last build brought in makes a
        // ping-pong out of build → cross-reference → build, each one doing almost no work and
        // asking for the next.
        if (tally.Judged > 0 && tally.Judged < MinJudgedForRebuild)
        {
            logger.LogInformation(
                "Cross-reference for {AccountId} judged {Judged}, under the {Floor} that earns a rebuild — the next build reads them.",
                accountId, tally.Judged, MinJudgedForRebuild);
        }

        if (tally.Judged >= MinJudgedForRebuild)
        {
            // Preserve-contract rebuild, deliberately NOT the feed-scores-updated frame: that
            // event's contract is {titleId, predictedRating} with no slot for order, and pushing an
            // LLM opinion into PredictedRating would make /api/accuracy uninterpretable. And
            // deliberately NOT a forced-Fresh reason either — "cross-reference" is drift, not a
            // request, and forcing Fresh would reinstate the hero churn the stable-tonight contract
            // exists to prevent.
            //
            // The next Fresh build reads these verdicts; a Preserve rebuild keeps tonight's seats
            // by contract, which is why this asks for a rebuild rather than promising a change now.
            await feedRefresh.RequestRebuildAsync(accountId, CrossReferenceRebuildReason, ct);
        }

        await db.SaveAbsorbingRebuildRaceAsync(ct);

        logger.LogInformation(
            "Cross-reference for {AccountId} at {ProfileHash} (prompt v{Version}): {Judged} judged of {Total} "
            + "({Kept} keep, {RuledOut} out, {Unsure} unsure), {NoSignal} unjudgeable, {Failed} failed batch(es) — {Stop}.",
            accountId, profileHash, judge.PromptVersion, tally.Judged, work.Count,
            tally.Kept, tally.RuledOut, tally.Unsure, tally.NoSignal, tally.FailedBatches, stopped);

        await PublishCompletedAsync(accountId, tally, ct);
    }

    /// <summary>
    /// One round over <paramref name="work"/>, boxed and gated. Returns why it stopped.
    ///
    /// <para>The budget is re-checked <b>per batch</b>, not once per run. That is the
    /// catalogue-ceiling lesson written into <c>CLAUDE.md</c>: the token ledger flushes every
    /// fifteen seconds, so a run that starts inside its allowance can spend well past it inside a
    /// single window. <c>CrossReferenceBudget</c> reads the recorder's in-flight counters for
    /// exactly this reason.</para>
    /// </summary>
    private async Task<LlmBudgetStop> RunRoundAsync(
        SyncJob job,
        Guid accountId,
        Guid artifactId,
        string profileHash,
        CrossReferenceRound round,
        IReadOnlyList<Guid> work,
        LensPersona persona,
        IReadOnlyDictionary<Guid, decimal> predictions,
        Tally tally,
        CancellationToken ct)
    {
        using var loop = new LlmBudgetLoop(new LlmBudgetOptions(SoftBudget, HardCeiling, BatchSize, Gate), ct);
        if (!await loop.TryAcquireGateAsync())
        {
            logger.LogInformation(
                "Cross-reference for {AccountId} yielded: another account's run held the gate for the whole box. "
                + "Its work is unchanged and the next build queues it again.", accountId);
            return LlmBudgetStop.GateUnavailable;
        }

        var neighbourOptions = round == CrossReferenceRound.First
            ? new NeighbourFinder.Options(NeighbourFinder.DefaultK, Skip: 0, AsOf: null, IncludeNearestDisliked: false)
            // Round 2's evidence — ranks 4–6 plus the nearest disliked neighbour. Unreachable until
            // PR4 supplies the round; written here because the options are part of what the round
            // MEANS, not part of the caller that asks for it.
            : new NeighbourFinder.Options(
                NeighbourFinder.DefaultK, NeighbourFinder.SecondRoundSkip, AsOf: null, IncludeNearestDisliked: true);

        var consecutiveFailures = 0;
        var abandoned = false;
        var batchIndex = 0;

        var stop = await loop.RunAsync(work, async (slice, batchCt) =>
        {
            if (abandoned)
            {
                return;
            }

            var allowance = await budget.AllowanceAsync(accountId, batchCt);
            if (!allowance.HasHeadroom)
            {
                // Exhaustion is not an error. The run keeps what it judged and stops; verdicts are
                // additive, so a half-judged pool is a smaller instrument, never a wrong one.
                logger.LogInformation(
                    "Cross-reference for {AccountId} stopped at its daily ceiling after {Judged} verdict(s) ({Allowance}).",
                    accountId, tally.Judged, allowance.Describe());
                tally.StoppedByBudget = true;
                abandoned = true;
                return;
            }

            var index = batchIndex++;
            var batch = await candidateBuilder.BuildAsync(accountId, slice, predictions, neighbourOptions, batchCt);

            // Anything the builder still refuses is unjudgeable for a reason the pool query could
            // not see (a title row that vanished under us, say). Counted, never rounded into the
            // judged figure.
            tally.NoSignal += batch.SkippedForNoSignal;
            if (batch.Candidates.Count == 0)
            {
                tally.Processed += slice.Count;
                if (tally.SucceededBatches > 0)
                {
                    await ReportProgressAsync(job, accountId, tally, round, batchCt);
                }

                return;
            }

            var result = await judge.JudgeAsync(
                batch.Candidates, persona, round, ApiUsagePurpose.CrossReference, batchCt);
            if (!result.Succeeded)
            {
                tally.FailedBatches++;
                consecutiveFailures++;
                if (result.Failure is AgentFailureKind.ProviderError or AgentFailureKind.TimedOut)
                {
                    tally.TransientFailures++;
                }

                var stoppedBeforeAnswer = result.Failure is AgentFailureKind.ProviderRefused
                    or AgentFailureKind.ProviderUnavailable or AgentFailureKind.BudgetSpent;
                if (stoppedBeforeAnswer)
                {
                    tally.StoppedBeforeAnswer++;
                }

                // Fail fast: an unanswered FIRST batch ends the run — on a reasoning model that
                // truncated, every later batch is the same request at the same ceiling, and the old
                // three-strikes rule bought two of them per run. Once a batch has been answered the
                // provider is evidently working, and the consecutive rule rides out a bad response.
                if (tally.SucceededBatches == 0 || stoppedBeforeAnswer || consecutiveFailures >= MaxConsecutiveFailures)
                {
                    logger.LogWarning(
                        "Cross-reference for {AccountId} stopped after {Failed} unanswered batch(es) ({Failure}); {Answered} answered.",
                        accountId, tally.FailedBatches, result.Failure?.ToString() ?? "unknown", tally.SucceededBatches);
                    abandoned = true;
                }

                return;
            }

            consecutiveFailures = 0;
            tally.SucceededBatches++;
            // Counted only once the batch has actually resolved. A batch the provider never
            // answered is still to do, and a counter that included it would tell the pill the pass
            // is further through the pool than it is.
            tally.Processed += slice.Count;

            await verdicts.RecordBatchAsync(
                accountId, profileHash, judge.PromptVersion, round, judge.ModelId, artifactId, index,
                result.Entries, batch.Neighbours, DateTime.UtcNow, batchCt);

            tally.Judged += result.Entries.Count;
            tally.Kept += result.Entries.Count(e => e.Verdict == CrossReferenceVerdict.Keep);
            tally.Unsure += result.Entries.Count(e => e.Verdict == CrossReferenceVerdict.Unsure);
            tally.RuledOut += result.Entries.Count(e =>
                e.Verdict == CrossReferenceVerdict.Out && e.Confidence >= VerdictAggregator.OutConfidenceThreshold);

            await ReportProgressAsync(job, accountId, tally, round, batchCt);
        });

        return abandoned ? LlmBudgetStop.SoftBudget : stop;
    }

    /// <summary>
    /// The pass's progress — written only once a batch has been ANSWERED, and with no denominator.
    ///
    /// <para>No percentage and no "of N": a pass over a pool that can legally take four days at ten
    /// thousand titles has no honest fraction, and "Cross-referencing 2,242 titles … 100%" was the
    /// message a failing run left on screen through every retry. The pill names the pass and counts
    /// what it has ruled out; the frame carries <c>current</c> (titles processed) and no
    /// <c>total</c>, so the pill shows no count-of-total either.</para>
    /// </summary>
    private async Task ReportProgressAsync(
        SyncJob job, Guid accountId, Tally tally, CrossReferenceRound round, CancellationToken ct)
    {
        var message = string.Create(
            CultureInfo.InvariantCulture,
            $"Cross-referencing · {RoundLabel(round)} · {tally.RuledOut:N0} ruled out so far");

        job.ProgressPct = null;
        job.ProgressMessage = message;
        // Persisted before it is published, the same order every other handler uses: the pill has
        // two sources — this frame and a five-second poll of the job row — and a reload mid-pass
        // must not read a message from ten minutes ago.
        await db.SaveChangesAsync(ct);
        events.Publish(accountId, PipelineEventTypes.JobProgress, new Dictionary<string, object?>
        {
            ["kind"] = SseKind,
            ["message"] = message,
            ["current"] = tally.Processed,
        });
    }

    /// <summary>
    /// The job error for a run that answered nothing. Starts with the status line where the provider
    /// refused, so the status API classifies it as the refusal it was (payment_required, …) rather
    /// than as an unanswered batch; otherwise "answered none", which classifies as no_answer.
    /// </summary>
    private string FailureMessage(Tally tally, bool tripped)
    {
        var refusal = tally.StoppedBeforeAnswer > 0 && breaker?.OpenBreaker is { Purpose: null } provider
            ? provider.ReasonCode switch
            {
                ProviderBreakerReasons.Unauthorized => "HTTP 401 Unauthorized — ",
                ProviderBreakerReasons.PaymentRequired => "HTTP 402 Payment Required — ",
                ProviderBreakerReasons.Forbidden => "HTTP 403 Forbidden — ",
                _ => string.Empty,
            }
            : string.Empty;
        var suffix = tripped
            ? $" Off for the rest of the UTC day: {CrossReferenceRunBreaker.ConsecutiveFailedRuns} consecutive runs on this model and prompt answered nothing."
            : string.Empty;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{refusal}The cross-reference judge answered none of {tally.FailedBatches} batch(es); no verdicts were written.{suffix}");
    }

    /// <summary>
    /// The completion frame.
    ///
    /// <para><c>picksChanged</c> is a literal zero rather than an omission, and it is honest: the
    /// completion rebuild is a PRESERVE rebuild, so tonight's seats do not move — the verdicts
    /// shape the page at the next Fresh build, which this run cannot count. A missing field would
    /// leave the post-run copy waiting for a number that is never coming; a guessed one would
    /// promise a change the user could go and fail to find.</para>
    ///
    /// <para><c>gating</c> comes from the most recent backtest this account has actually run — the
    /// pooled mean row, never a per-origin one — and is <c>unmeasured</c> when none exists. The
    /// band that reads it may only ever quote a real number.</para>
    /// </summary>
    private async Task PublishCompletedAsync(Guid accountId, Tally tally, CancellationToken ct)
    {
        var gating = await LatestGatingDecisionAsync(accountId, ct);
        events.Publish(accountId, PipelineEventTypes.JobCompleted, new Dictionary<string, object?>
        {
            ["kind"] = SseKind,
            ["judged"] = tally.Judged,
            ["ruledOut"] = tally.RuledOut,
            ["kept"] = tally.Kept,
            ["unsure"] = tally.Unsure,
            ["noEmbedding"] = tally.NoSignal,
            ["picksChanged"] = 0,
            ["gating"] = gating,
        });
    }

    /// <summary>The newest pooled backtest verdict for this account, or "unmeasured".</summary>
    private async Task<string> LatestGatingDecisionAsync(Guid accountId, CancellationToken ct) =>
        await db.CrossReferenceEvaluations
            .Where(e => e.AccountId == accountId && e.SplitFraction == null)
            .OrderByDescending(e => e.EvaluatedAt)
            .Select(e => e.GatingDecision)
            .FirstOrDefaultAsync(ct) ?? Unmeasured;

    /// <summary>The job's closing line — counted, with no denominator, and saying why a run that judged nothing stopped.</summary>
    private static string CompletionMessage(Tally tally) =>
        tally.Judged > 0
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"Cross-referenced {tally.Judged:N0} titles · {tally.RuledOut:N0} ruled out")
            : tally.StoppedByBudget
                ? "stopped at today's cross-reference ceiling before judging anything"
                : "nothing judgeable this run";

    private async Task FinishAsync(SyncJob job, string message, CancellationToken ct)
    {
        job.ProgressMessage = message;
        await db.SaveChangesAsync(ct);
    }
}
