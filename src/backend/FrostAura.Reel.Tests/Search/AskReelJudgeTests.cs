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
        Assert.Equal(2, summary.Batches);
        Assert.Equal(2, summary.FailedBatches);
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
