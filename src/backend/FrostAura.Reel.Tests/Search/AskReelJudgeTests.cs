using FrostAura.Reel.Application.Search.AskReel;
using FrostAura.Reel.Domain.Lens;
using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Domain.Sync;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrostAura.Reel.Tests.Search;

/// <summary>
/// The judge's lifetime discipline (D7): a deadline that turns a silent provider into failed batches,
/// and no batch left running — or releasing a disposed gate — after the call returns or throws.
/// </summary>
public class AskReelJudgeTests
{
    private static readonly LensPersona Persona = new("taste", null);

    private static List<RerankInput> Pool(int count) =>
        [.. Enumerable.Range(0, count).Select(i => new RerankInput(
            Guid.NewGuid(), $"Title {i}", 2012, "Movie", ["drama"], "An overview.", null, null))];

    [Fact]
    public async Task A_silent_provider_becomes_failed_batches_at_the_deadline()
    {
        var agent = new SlowAgent(firstDelay: TimeSpan.FromMinutes(5), laterDelay: TimeSpan.FromMinutes(5));
        var judge = new AskReelJudge(agent, new MemoryCache(new MemoryCacheOptions()), NullLogger<AskReelJudge>.Instance)
        {
            StageDeadline = TimeSpan.FromMilliseconds(300),
        };

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var summary = await judge.JudgeAsync(
            Guid.NewGuid(), "q", Persona, Pool(12), ApiUsagePurpose.RerankAsk, _ => Task.CompletedTask, CancellationToken.None);

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(20), $"judging took {clock.Elapsed}.");
        Assert.Equal(3, summary.Batches);
        Assert.Equal(3, summary.FailedBatches);
        Assert.Equal(12, summary.Judged);
        Assert.Equal(12, summary.Missing);
        Assert.False(summary.JudgedAny);
        Assert.Equal(0, agent.InFlight);
    }

    /// <summary>
    /// A caller whose handler throws on the first batch: the exception surfaces, and by the time it
    /// does every sibling batch has been cancelled and has finished — none is still spending, none
    /// touches the disposed gate.
    /// </summary>
    [Fact]
    public async Task A_throwing_handler_cancels_and_awaits_every_sibling_batch()
    {
        var agent = new SlowAgent(firstDelay: TimeSpan.Zero, laterDelay: TimeSpan.FromMinutes(5));
        var judge = new AskReelJudge(agent, new MemoryCache(new MemoryCacheOptions()), NullLogger<AskReelJudge>.Instance);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => judge.JudgeAsync(
            Guid.NewGuid(), "q", Persona, Pool(16), ApiUsagePurpose.RerankAsk,
            _ => throw new InvalidOperationException("the stream is gone"),
            CancellationToken.None));

        Assert.Equal("the stream is gone", error.Message);
        Assert.Equal(0, agent.InFlight);
        Assert.Equal(3, agent.Started);
    }

    [Fact]
    public async Task A_cancelled_turn_leaves_no_batch_running()
    {
        var agent = new SlowAgent(firstDelay: TimeSpan.FromMinutes(5), laterDelay: TimeSpan.FromMinutes(5));
        var judge = new AskReelJudge(agent, new MemoryCache(new MemoryCacheOptions()), NullLogger<AskReelJudge>.Instance);
        using var turn = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => judge.JudgeAsync(
            Guid.NewGuid(), "q", Persona, Pool(16), ApiUsagePurpose.RerankAsk, _ => Task.CompletedTask, turn.Token));

        Assert.Equal(0, agent.InFlight);
    }

    /// <summary>
    /// R1-07: the first judge call carries only the top four titles, so the likeliest matches answer
    /// first; sixteen titles still fit three concurrent calls, and no call exceeds six.
    /// </summary>
    [Theory]
    [InlineData(1, new[] { 1 })]
    [InlineData(4, new[] { 4 })]
    [InlineData(5, new[] { 4, 1 })]
    [InlineData(10, new[] { 4, 6 })]
    [InlineData(12, new[] { 4, 6, 2 })]
    [InlineData(16, new[] { 4, 6, 6 })]
    public void The_first_batch_is_the_smallest_and_sixteen_fit_three_calls(int pool, int[] sizes)
    {
        var batches = AskReelJudge.Split(Pool(pool));

        Assert.Equal(sizes, batches.Select(b => b.Count));
        Assert.Equal(sizes.Length, AskReelJudge.BatchCount(pool));
        Assert.True(sizes.Length <= AskReelJudge.MaxConcurrent);
        Assert.All(batches, b => Assert.InRange(b.Count, 1, AskReelJudge.BatchSize));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(17, 3)]
    [InlineData(40, 3)]
    public void The_batch_count_never_exceeds_the_judged_sixteen(int pool, int batches) =>
        Assert.Equal(batches, AskReelJudge.BatchCount(pool));

    /// <summary>
    /// R1-01: a batch that answers for some titles and skips others is not a clean batch. Every title
    /// without a request verdict — a skipped entry (Fallback) or a model entry with no
    /// <c>satisfies</c> — counts as missing, title by title, though the batch did not fail outright.
    /// </summary>
    [Fact]
    public async Task A_skipped_entry_is_counted_missing_though_its_batch_did_not_fail()
    {
        var judge = new AskReelJudge(new PartlyAnsweringAgent(), new MemoryCache(new MemoryCacheOptions()), NullLogger<AskReelJudge>.Instance);
        var batches = new List<AskReelJudge.Batch>();

        var summary = await judge.JudgeAsync(
            Guid.NewGuid(), "q", Persona, Pool(10), ApiUsagePurpose.RerankAsk,
            b =>
            {
                lock (batches)
                {
                    batches.Add(b);
                }

                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.Equal(0, summary.FailedBatches);
        Assert.Equal(10, summary.Judged);
        // Each batch's last title is skipped (Fallback) and its second has no satisfies.
        Assert.Equal(4, summary.Missing);
        Assert.All(batches, b => Assert.Equal(2, b.Missing));
        Assert.All(batches, b => Assert.False(b.Failed));
        Assert.True(summary.JudgedAny);
    }

    [Fact]
    public void Only_a_model_verdict_with_a_satisfies_value_is_a_ruling()
    {
        var id = Guid.NewGuid();
        Assert.True(AskReelJudge.Ruled(new RerankResult(id, 0.8d, ResultSource.Llm, "Fits.", ResultSource.Llm, Satisfies.No)));
        Assert.False(AskReelJudge.Ruled(new RerankResult(id, 0.8d, ResultSource.Llm, "Fits.", ResultSource.Llm)));
        Assert.False(AskReelJudge.Ruled(new RerankResult(id, 0.6d, ResultSource.Fallback, string.Empty, ResultSource.Fallback, Satisfies.Yes)));
        Assert.False(AskReelJudge.Ruled(null));
    }

    /// <summary>Answers every title but the batch's last (a fallback) and leaves the second without a verdict.</summary>
    private sealed class PartlyAnsweringAgent : ISearchAgent
    {
        public bool IsAvailable => true;

        public string ModelId => "partly-answering";

        public Task<AgentReply> InterpretAsync(
            IReadOnlyList<ChatTurn> history, string message, string tasteSummary, IReadOnlyList<string> shownTitles,
            CancellationToken ct = default, string? standingFilters = null) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<RerankResult>> RerankBatchAsync(
            IReadOnlyList<RerankInput> titles, string query, LensPersona persona, string? anchorName = null,
            string usagePurpose = ApiUsagePurpose.Rerank, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<RerankResult>>([.. titles.Select((t, i) =>
                i == titles.Count - 1
                    ? new RerankResult(t.TitleId, 0.6d, ResultSource.Fallback, string.Empty, ResultSource.Fallback)
                    : i == 1
                        ? new RerankResult(t.TitleId, 0.7d, ResultSource.Llm, "Fits.", ResultSource.Llm)
                        : new RerankResult(t.TitleId, 0.8d, ResultSource.Llm, "Fits.", ResultSource.Llm, Satisfies.Yes))]);
    }

    /// <summary>The first call answers after <c>firstDelay</c>, every later one after <c>laterDelay</c>; all honour cancellation.</summary>
    private sealed class SlowAgent(TimeSpan firstDelay, TimeSpan laterDelay) : ISearchAgent
    {
        private int _started;
        private int _inFlight;

        public int Started => Volatile.Read(ref _started);

        public int InFlight => Volatile.Read(ref _inFlight);

        public bool IsAvailable => true;

        public string ModelId => "slow-agent";

        public Task<AgentReply> InterpretAsync(
            IReadOnlyList<ChatTurn> history, string message, string tasteSummary, IReadOnlyList<string> shownTitles,
            CancellationToken ct = default, string? standingFilters = null) =>
            throw new NotSupportedException();

        public async Task<IReadOnlyList<RerankResult>> RerankBatchAsync(
            IReadOnlyList<RerankInput> titles, string query, LensPersona persona, string? anchorName = null,
            string usagePurpose = ApiUsagePurpose.Rerank, CancellationToken ct = default)
        {
            var index = Interlocked.Increment(ref _started);
            Interlocked.Increment(ref _inFlight);
            try
            {
                await Task.Delay(index == 1 ? firstDelay : laterDelay, ct);
                return [.. titles.Select(t => new RerankResult(t.TitleId, 0.8d, ResultSource.Llm, "Fits.", ResultSource.Llm, Satisfies.Yes))];
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }
    }
}
