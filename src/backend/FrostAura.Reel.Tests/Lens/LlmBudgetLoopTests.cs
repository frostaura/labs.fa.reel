using FrostAura.Reel.Application.Llm;
using FrostAura.Reel.Application.Ranking;
using FrostAura.Reel.Domain.Ports;

namespace FrostAura.Reel.Tests.Lens;

/// <summary>
/// The batch-loop semantics, pinned once instead of drifting apart in three copies.
///
/// <para>Each rule below cost something to learn. The first batch always running is what stops a
/// cold cache under load from never warming — measured, a 4s cap against a ~7s batch pinned every
/// request at 4s for ever, which is strictly worse in steady state than one slow request followed
/// by 46ms. Checking the budget only BETWEEN batches is because a cancelled batch caches nothing,
/// so the spend is simply lost. Releasing the gate only when it was acquired is because releasing
/// a semaphore that was never entered permanently inflates its count and uncaps concurrency for
/// the life of the process.</para>
/// </summary>
public class LlmBudgetLoopTests
{
    private static readonly int[] TwentyItems = Enumerable.Range(0, 20).ToArray();

    [Fact]
    public async Task Every_batch_runs_when_nothing_gets_in_the_way()
    {
        using var loop = new LlmBudgetLoop(
            new LlmBudgetOptions(TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1), 9), CancellationToken.None);

        var seen = new List<int>();
        var stop = await loop.RunAsync(TwentyItems, (batch, _) =>
        {
            seen.AddRange(batch);
            return Task.CompletedTask;
        });

        Assert.Equal(LlmBudgetStop.Completed, stop);
        Assert.Equal(TwentyItems, seen);
        Assert.Equal(3, loop.BatchesCompleted);
        Assert.Equal(20, loop.ItemsProcessed);
    }

    [Fact]
    public async Task The_first_batch_always_finishes_even_with_no_budget_at_all()
    {
        using var loop = new LlmBudgetLoop(
            new LlmBudgetOptions(TimeSpan.Zero, TimeSpan.FromMinutes(1), 9), CancellationToken.None);

        var batches = 0;
        var stop = await loop.RunAsync(TwentyItems, (_, _) =>
        {
            batches++;
            return Task.CompletedTask;
        });

        // One batch of forward progress, then it stops. Zero would mean the cache never warms.
        Assert.Equal(LlmBudgetStop.SoftBudget, stop);
        Assert.Equal(1, batches);
    }

    [Fact]
    public async Task A_second_run_on_the_same_loop_shares_the_clock_and_the_counter()
    {
        // The feed's why pass runs a loop per rail against ONE deadline, and the hero-swap pass
        // afterwards draws on the same clock — a swap is part of that pass, not a second budget.
        using var loop = new LlmBudgetLoop(
            new LlmBudgetOptions(TimeSpan.Zero, TimeSpan.FromMinutes(1), 9), CancellationToken.None);

        await loop.RunAsync(TwentyItems, (_, _) => Task.CompletedTask);
        var batches = 0;
        var second = await loop.RunAsync(TwentyItems, (_, _) =>
        {
            batches++;
            return Task.CompletedTask;
        });

        // The budget is already spent, so the second run does nothing at all — its "first batch"
        // privilege belonged to the loop, not to the call.
        Assert.Equal(LlmBudgetStop.SoftBudget, second);
        Assert.Equal(0, batches);
    }

    [Fact]
    public async Task A_provider_failure_keeps_the_batches_that_already_landed()
    {
        using var loop = new LlmBudgetLoop(
            new LlmBudgetOptions(TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1), 9), CancellationToken.None);

        var batches = 0;
        var stop = await loop.RunAsync(TwentyItems, (_, _) =>
        {
            batches++;
            return batches == 2 ? throw new HttpRequestException("provider down") : Task.CompletedTask;
        });

        Assert.Equal(LlmBudgetStop.Failed, stop);
        Assert.Equal(1, loop.BatchesCompleted);
    }

    [Fact]
    public async Task The_hard_ceiling_reports_itself_rather_than_throwing()
    {
        using var loop = new LlmBudgetLoop(
            new LlmBudgetOptions(TimeSpan.FromMinutes(1), TimeSpan.FromMilliseconds(50), 9), CancellationToken.None);

        var stop = await loop.RunAsync(TwentyItems, async (_, batchCt) =>
            await Task.Delay(TimeSpan.FromSeconds(5), batchCt));

        Assert.Equal(LlmBudgetStop.HardCeiling, stop);
    }

    [Fact]
    public async Task Outer_cancellation_still_propagates_because_that_is_the_caller_leaving()
    {
        using var outer = new CancellationTokenSource();
        using var loop = new LlmBudgetLoop(
            new LlmBudgetOptions(TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1), 9), outer.Token);

        await outer.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            loop.RunAsync(TwentyItems, (_, batchCt) => Task.FromCanceled(batchCt)));
    }

    [Fact]
    public async Task The_gate_is_acquired_and_released_exactly_once()
    {
        using var gate = new SemaphoreSlim(1);
        using (var loop = new LlmBudgetLoop(
            new LlmBudgetOptions(TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1), 9, gate), CancellationToken.None))
        {
            Assert.True(await loop.TryAcquireGateAsync());
            Assert.Equal(0, gate.CurrentCount);
            // Idempotent: a caller that asks twice must not take two slots.
            Assert.True(await loop.TryAcquireGateAsync());
            Assert.Equal(0, gate.CurrentCount);
        }

        Assert.Equal(1, gate.CurrentCount);
    }

    [Fact]
    public async Task A_gate_that_never_frees_inside_the_ceiling_is_reported_and_not_released()
    {
        using var gate = new SemaphoreSlim(1);
        await gate.WaitAsync();

        using (var loop = new LlmBudgetLoop(
            new LlmBudgetOptions(TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(40), 9, gate),
            CancellationToken.None))
        {
            Assert.False(await loop.TryAcquireGateAsync());
        }

        // Still zero. Releasing a semaphore we never entered would uncap concurrency for the life
        // of the process — the exact bug the release-only-if-held rule exists to prevent.
        Assert.Equal(0, gate.CurrentCount);
    }

    [Fact]
    public async Task An_unbounded_loop_just_batches()
    {
        using var loop = new LlmBudgetLoop(LlmBudgetOptions.Unbounded(6), CancellationToken.None);
        var batches = 0;
        var stop = await loop.RunAsync(TwentyItems, (_, _) =>
        {
            batches++;
            return Task.CompletedTask;
        });

        Assert.Equal(LlmBudgetStop.Completed, stop);
        Assert.Equal(4, batches);
        Assert.False(loop.BudgetExhausted);
    }
}

/// <summary>
/// The two halves of the LLM position-bias mitigation: a shuffle that is deterministic without
/// being order-dependent, and an average that does not privilege whichever verdict landed last.
/// </summary>
public class RerankOrderTests
{
    private static RerankInput Input(int i) => new(
        Guid.Parse($"{i:00000000}-0000-0000-0000-000000000000"),
        $"Title {i}", 2020, "Movie", ["crime"], null, 7m);

    [Fact]
    public void The_shuffle_is_deterministic()
    {
        var batch = Enumerable.Range(1, 9).Select(Input).ToList();
        Assert.Equal(
            RerankOrder.Shuffle(batch, "q", null).Select(t => t.TitleId),
            RerankOrder.Shuffle(batch, "q", null).Select(t => t.TitleId));
    }

    [Fact]
    public void The_shuffle_does_not_depend_on_the_input_order()
    {
        var batch = Enumerable.Range(1, 9).Select(Input).ToList();
        Assert.Equal(
            RerankOrder.Shuffle(batch, "q", null).Select(t => t.TitleId),
            RerankOrder.Shuffle(batch.AsEnumerable().Reverse().ToList(), "q", null).Select(t => t.TitleId));
    }

    [Fact]
    public void The_shuffle_actually_reorders_and_loses_nothing()
    {
        var batch = Enumerable.Range(1, 9).Select(Input).ToList();
        var shuffled = RerankOrder.Shuffle(batch, "tonight", null);

        Assert.Equal(batch.Count, shuffled.Count);
        Assert.Equal(
            batch.Select(t => t.TitleId).OrderBy(id => id),
            shuffled.Select(t => t.TitleId).OrderBy(id => id));
        Assert.NotEqual(batch.Select(t => t.TitleId), shuffled.Select(t => t.TitleId));
    }

    [Fact]
    public void A_different_query_places_the_same_titles_differently()
    {
        var batch = Enumerable.Range(1, 9).Select(Input).ToList();
        Assert.NotEqual(
            RerankOrder.Shuffle(batch, "tonight", null).Select(t => t.TitleId),
            RerankOrder.Shuffle(batch, "deep cuts", null).Select(t => t.TitleId));
    }

    [Fact]
    public void A_single_item_batch_is_returned_untouched()
    {
        var batch = new[] { Input(1) };
        Assert.Same(batch, RerankOrder.Shuffle(batch, "q", null));
    }

    [Fact]
    public void Repeat_verdicts_are_averaged_not_overwritten()
    {
        // A title on two rails is judged twice at two different prompt positions. Keeping the last
        // verdict would freeze in that prompt's position bias AND make the answer depend on
        // request ordering.
        var id = Guid.Parse("11111111-0000-0000-0000-000000000000");
        var debiased = new PositionDebiasedFit();
        debiased.Observe(new RerankResult(id, 0.9d, "first"));
        debiased.Observe(new RerankResult(id, 0.5d, "second"));

        var averaged = debiased.For(id);
        Assert.NotNull(averaged);
        Assert.Equal(0.7d, averaged!.Fit, 6);
        Assert.Equal(2, debiased.CountFor(id));
        // Prose is not averageable, so the first accepted line is kept.
        Assert.Equal("first", averaged.Why);
    }

    [Fact]
    public void An_unjudged_title_has_no_verdict_rather_than_a_default_one()
    {
        Assert.Null(new PositionDebiasedFit().For(Guid.NewGuid()));
        Assert.Equal(0, new PositionDebiasedFit().CountFor(Guid.NewGuid()));
    }
}

/// <summary>
/// The unscripted-format demotion — and, more importantly, the condition on it. A recommender that
/// silently refuses to show a whole format to someone who loves it is broken in a way they can
/// never name, so the default is revocable by one sentence in the taste narrative.
/// </summary>
public class UnscriptedFormatsTests
{
    private const string Scripted = "You reach for bleak, procedural crime and slow science fiction.";

    private const string Mentions = "You will happily put on a reality competition when the week has been long.";

    [Theory]
    [InlineData("reality")]
    [InlineData("Reality-TV")]
    [InlineData("talk show")]
    [InlineData("game-show")]
    public void Unscripted_genres_are_recognised(string genre) =>
        Assert.True(UnscriptedFormats.IsUnscripted([genre]));

    [Theory]
    [InlineData("documentary")]
    [InlineData("drama")]
    public void Scripted_and_documentary_are_not(string genre) =>
        Assert.False(UnscriptedFormats.IsUnscripted([genre]));

    [Fact]
    public void An_unscripted_title_is_demoted_when_the_narrative_is_silent_about_the_format()
    {
        Assert.Equal(0.9d * UnscriptedFormats.FitScale, UnscriptedFormats.Adjust(0.9d, ["reality"], Scripted), 6);
    }

    [Fact]
    public void It_is_not_demoted_when_the_narrative_mentions_the_format_at_all()
    {
        // A mention is treated as consent, deliberately: the narrative is written honestly and
        // would say "you avoid reality television" as readily as the opposite, and the cost of
        // being wrong permissively is one card scrolled past.
        Assert.Equal(0.9d, UnscriptedFormats.Adjust(0.9d, ["reality"], Mentions), 6);
        Assert.True(UnscriptedFormats.NarrativeMentions(Mentions));
        Assert.False(UnscriptedFormats.NarrativeMentions(Scripted));
    }

    [Fact]
    public void A_scripted_title_is_never_touched()
    {
        Assert.Equal(0.9d, UnscriptedFormats.Adjust(0.9d, ["crime", "thriller"], Scripted), 6);
    }

    [Fact]
    public void An_empty_narrative_still_demotes_rather_than_throwing()
    {
        Assert.Equal(0.9d * UnscriptedFormats.FitScale, UnscriptedFormats.Adjust(0.9d, ["reality"], null), 6);
    }
}
