using FrostAura.Reel.Application.Search.AskReel;
using FrostAura.Reel.Application.Search.AskReel.Engine;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Ports;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrostAura.Reel.Tests.Search;

/// <summary>
/// HREV2-ASK-04: a model call stuck on a slow route is sent once more and the first good answer is
/// used — the integrator's turn waited 14.6 s on one judge batch whose siblings took about 2 s. The
/// hedge is pinned on its own (when it fires, which answer wins, what is cancelled) and through the
/// judge, whose batches and early first batch it bounds.
/// </summary>
public class AskReelHedgeTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task A_call_that_answers_in_time_is_never_sent_twice()
    {
        var calls = new ScriptedCalls(("ok", TimeSpan.Zero));
        var hedged = 0;

        var result = await AskReelHedge.RunAsync(calls.Next, r => r == "ok", TimeSpan.FromSeconds(5), () => hedged++, CancellationToken.None);

        Assert.Equal("ok", result);
        Assert.Equal(1, calls.Started);
        Assert.Equal(0, hedged);
    }

    [Fact]
    public async Task A_slow_call_is_sent_once_more_the_faster_good_answer_wins_and_the_slow_one_is_cancelled()
    {
        var calls = new ScriptedCalls(("slow", Timeout.InfiniteTimeSpan), ("hedge", TimeSpan.Zero));
        var hedged = 0;

        var result = await AskReelHedge.RunAsync(calls.Next, r => r.Length > 0, TimeSpan.FromMilliseconds(50), () => hedged++, CancellationToken.None)
            .WaitAsync(Wait);

        Assert.Equal("hedge", result);
        Assert.Equal(2, calls.Started);
        Assert.Equal(1, hedged);
        await calls.Cancelled(0).WaitAsync(Wait);
    }

    /// <summary>A failed answer never beats a good one, whichever comes back first; both failing returns the first call's own.</summary>
    [Theory]
    [InlineData("fail", "late-ok", "late-ok")]
    [InlineData("late-ok", "fail", "late-ok")]
    [InlineData("fail-first", "fail-second", "fail-first")]
    public async Task A_failed_answer_waits_for_the_other_and_two_failures_return_the_first(string first, string second, string expected)
    {
        // The first call answers after the hedge is sent; a failure always lands before a success.
        TimeSpan Delay(string r) => r.StartsWith("fail", StringComparison.Ordinal) ? TimeSpan.FromMilliseconds(120) : TimeSpan.FromMilliseconds(300);
        var calls = new ScriptedCalls((first, Delay(first)), (second, Delay(second)));

        var result = await AskReelHedge.RunAsync(
                calls.Next, r => !r.StartsWith("fail", StringComparison.Ordinal), TimeSpan.FromMilliseconds(50), null, CancellationToken.None)
            .WaitAsync(Wait);

        Assert.Equal(expected, result);
        Assert.Equal(2, calls.Started);
    }

    [Fact]
    public async Task A_threshold_of_zero_never_hedges()
    {
        var calls = new ScriptedCalls(("ok", TimeSpan.FromMilliseconds(100)));

        var result = await AskReelHedge.RunAsync(calls.Next, _ => true, TimeSpan.Zero, null, CancellationToken.None);

        Assert.Equal("ok", result);
        Assert.Equal(1, calls.Started);
    }

    [Fact]
    public async Task The_callers_cancellation_cancels_both_calls()
    {
        var calls = new ScriptedCalls(("slow", Timeout.InfiniteTimeSpan), ("slow too", Timeout.InfiniteTimeSpan));
        using var cts = new CancellationTokenSource();

        var running = AskReelHedge.RunAsync(calls.Next, _ => true, TimeSpan.FromMilliseconds(30), () => cts.CancelAfter(50), cts.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running.WaitAsync(Wait));
        await calls.Cancelled(0).WaitAsync(Wait);
        await calls.Cancelled(1).WaitAsync(Wait);
    }

    /// <summary>
    /// Through the judge: every batch whose first call hangs is sent once more and judged by the
    /// second, well inside the judging deadline. Varied: a hedge that hangs too leaves the batch
    /// unjudged at the deadline — the old outcome, now bounded by the hedge rather than produced by it.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_judge_batch_stuck_on_its_first_call_is_judged_by_its_hedge(bool hedgeHangsToo)
    {
        var titles = Enumerable.Range(0, 16).Select(_ => Title(Guid.NewGuid())).ToList();
        var model = new FirstCallHangsModel(hedgeHangsToo);
        var judge = new RequestJudge(model, new MemoryCache(new MemoryCacheOptions()), NullLogger<RequestJudge>.Instance)
        {
            HedgeAfter = TimeSpan.FromMilliseconds(50),
            StageDeadline = TimeSpan.FromMilliseconds(1500),
        };

        var verdicts = new List<JudgeVerdict>();
        await foreach (var v in judge.JudgeAsync(new JudgeInput(Guid.NewGuid(), "ninja", "persona", titles), CancellationToken.None))
        {
            verdicts.Add(v);
        }

        Assert.Equal(titles.Select(t => t.TitleId).Order(), verdicts.Select(v => v.TitleId).Order());
        Assert.All(verdicts, v => Assert.Equal(!hedgeHangsToo, v.Judged));
        Assert.Equal(RequestJudge.Batches(titles.Select(t => t.TitleId).ToList()).Count * 2, model.Calls);
    }

    [Fact]
    public async Task The_early_first_batch_is_hedged_too()
    {
        var titles = Enumerable.Range(0, 4).Select(_ => Title(Guid.NewGuid())).ToList();
        var model = new FirstCallHangsModel(hedgeHangsToo: false);
        var judge = new RequestJudge(model, new MemoryCache(new MemoryCacheOptions()), NullLogger<RequestJudge>.Instance)
        {
            HedgeAfter = TimeSpan.FromMilliseconds(50),
        };

        var elapsed = await judge.PrejudgeAsync(new JudgeInput(Guid.NewGuid(), "ninja", "persona", titles), CancellationToken.None).WaitAsync(Wait);

        Assert.NotNull(elapsed);
        Assert.Equal(2, model.Calls);
    }

    private static JudgeTitle Title(Guid id) => new(id, $"Title {id:N}", 2020, MediaType.Movie, ["action"], ["ninja"], "A ninja.", [], null);

    /// <summary>Answers each call with the next scripted result after its delay (infinite: until cancelled).</summary>
    private sealed class ScriptedCalls(params (string Result, TimeSpan Delay)[] script)
    {
        private readonly TaskCompletionSource[] _cancelled = [.. script.Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously))];
        private int _started;

        public int Started => Volatile.Read(ref _started);

        public Task Cancelled(int call) => _cancelled[call].Task;

        public async Task<string> Next(CancellationToken ct)
        {
            var index = Interlocked.Increment(ref _started) - 1;
            var (result, delay) = script[index];
            try
            {
                await Task.Delay(delay, ct);
            }
            catch (OperationCanceledException)
            {
                _cancelled[index].TrySetResult();
                throw;
            }

            return result;
        }
    }

    /// <summary>Every batch's first call hangs until cancelled; its second answers yes (or hangs too).</summary>
    private sealed class FirstCallHangsModel(bool hedgeHangsToo) : IRequestJudgeModel
    {
        private readonly HashSet<string> _seen = [];
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public bool IsAvailable => true;

        public string ModelId => "first-call-hangs";

        public string CacheSalt => $"first-call-hangs-{Guid.NewGuid():N}";

        public async Task<JudgeBatchResult> JudgeBatchAsync(JudgeInput input, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            var key = string.Join(',', input.Titles.Select(t => t.TitleId).Order());
            bool first;
            lock (_seen)
            {
                first = _seen.Add(key);
            }

            if (first || hedgeHangsToo)
            {
                await Task.Delay(Timeout.Infinite, ct);
            }

            var verdicts = input.Titles
                .Select(t => new JudgeVerdict(t.TitleId, Satisfies.Yes, 0.8, "It fits.", "keyword: ninja", ResultSource.Llm, ResultSource.Llm))
                .ToList();
            return new JudgeBatchResult(verdicts, new LlmCallMeta("first-call-hangs", "none", false, 1, 3), false);
        }
    }
}
