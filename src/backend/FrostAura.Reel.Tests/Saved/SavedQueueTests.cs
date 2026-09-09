using FrostAura.Reel.Application.Saved;

namespace FrostAura.Reel.Tests.Saved;

/// <summary>
/// The Saved queue's ordering arithmetic, with no database in sight. The rules these pin are the
/// ones a reader would otherwise have to infer from a LINQ chain, and two of them are load-bearing
/// product decisions rather than implementation detail:
///
/// <list type="bullet">
/// <item>a missing prediction or runtime sorts LAST, never first — absence of knowledge must not
/// outrank a real number, or an un-enriched title leads the shelf;</item>
/// <item>the pinned "watch tonight" pick refuses titles with an unknown runtime, because the pin's
/// entire claim is "this fits your evening".</item>
/// </list>
/// </summary>
public class SavedQueueTests
{
    private static readonly DateTime Now = new(2026, 9, 9, 20, 0, 0, DateTimeKind.Utc);

    private static SavedQueueEntry Entry(
        string idSeed,
        decimal? predicted = null,
        int? runtime = null,
        bool availableNow = false,
        int savedDaysAgo = 0) =>
        new(Id(idSeed), predicted, runtime, availableNow, Now.AddDays(-savedDaysAgo));

    /// <summary>Deterministic ids so the final tie-break is reproducible across runs.</summary>
    private static Guid Id(string seed) => new($"00000000-0000-0000-0000-0000000000{seed}");

    [Theory]
    [InlineData(null, SavedSort.Tonight)]
    [InlineData("", SavedSort.Tonight)]
    [InlineData("  TONIGHT ", SavedSort.Tonight)]
    [InlineData("predicted", SavedSort.Predicted)]
    [InlineData("added", SavedSort.Added)]
    [InlineData("runtime", SavedSort.Runtime)]
    // A bookmarked URL from an older build must still render the shelf, not 400.
    [InlineData("whatever", SavedSort.Tonight)]
    public void Sort_values_parse_leniently_and_default_to_tonight(string? raw, SavedSort expected) =>
        Assert.Equal(expected, SavedQueue.ParseSort(raw));

    [Fact]
    public void Tonight_puts_available_first_then_best_then_shortest()
    {
        // available + 8.0 + 200min  → beats everything unavailable, loses to a better available one
        // available + 9.0 + 100min  → the winner
        // available + 9.0 + 90min   → same score, shorter: wins the pair
        // unavailable + 9.9 + 80min → the strongest card in the queue, and still below every
        //                             available one: "can I watch it" is asked before "is it good"
        var best = Entry("11", predicted: 9.0m, runtime: 100, availableNow: true);
        var bestShorter = Entry("12", predicted: 9.0m, runtime: 90, availableNow: true);
        var good = Entry("13", predicted: 8.0m, runtime: 200, availableNow: true);
        var unavailable = Entry("14", predicted: 9.9m, runtime: 80);

        var ordered = SavedQueue.Order([good, unavailable, best, bestShorter], SavedSort.Tonight);

        Assert.Equal(
            [bestShorter.TitleId, best.TitleId, good.TitleId, unavailable.TitleId],
            ordered.Select(e => e.TitleId).ToArray());
    }

    [Fact]
    public void Tonight_sorts_unknown_predictions_and_runtimes_last_within_their_group()
    {
        // Both available; one has no prediction. The unscored one must not lead the shelf just
        // because a null sorted high — this is the exact regression the backfill exists to prevent
        // ever being visible, and the ordering is the second line of defence.
        var scored = Entry("21", predicted: 5.0m, runtime: 120, availableNow: true);
        var unscored = Entry("22", runtime: 90, availableNow: true);
        // Equal predictions, one runtime unknown: known-and-longer still beats unknown.
        var knownLong = Entry("23", predicted: 7.0m, runtime: 175, availableNow: true);
        var unknownRuntime = Entry("24", predicted: 7.0m, availableNow: true);

        var ordered = SavedQueue.Order([unscored, scored, unknownRuntime, knownLong], SavedSort.Tonight);

        Assert.Equal(
            [knownLong.TitleId, unknownRuntime.TitleId, scored.TitleId, unscored.TitleId],
            ordered.Select(e => e.TitleId).ToArray());
    }

    [Fact]
    public void Predicted_added_and_runtime_sorts_each_answer_their_own_question()
    {
        var oldHigh = Entry("31", predicted: 9.0m, runtime: 200, savedDaysAgo: 40);
        var newLow = Entry("32", predicted: 4.0m, runtime: 80, savedDaysAgo: 1);
        var midUnscored = Entry("33", runtime: 100, savedDaysAgo: 10);
        SavedQueueEntry[] all = [oldHigh, newLow, midUnscored];

        Assert.Equal(
            [oldHigh.TitleId, newLow.TitleId, midUnscored.TitleId],
            SavedQueue.Order(all, SavedSort.Predicted).Select(e => e.TitleId).ToArray());

        Assert.Equal(
            [newLow.TitleId, midUnscored.TitleId, oldHigh.TitleId],
            SavedQueue.Order(all, SavedSort.Added).Select(e => e.TitleId).ToArray());

        Assert.Equal(
            [newLow.TitleId, midUnscored.TitleId, oldHigh.TitleId],
            SavedQueue.Order(all, SavedSort.Runtime).Select(e => e.TitleId).ToArray());
    }

    [Fact]
    public void Ordering_is_total_so_the_shelf_cannot_shuffle_between_refetches()
    {
        // Identical on every visible fact except id: the order must still be stable and the SAME
        // both times, which is what stops cards swapping places under a user's cursor.
        var a = Entry("41", predicted: 7.0m, runtime: 100, availableNow: true);
        var b = Entry("42", predicted: 7.0m, runtime: 100, availableNow: true);

        var first = SavedQueue.Order([b, a], SavedSort.Tonight).Select(e => e.TitleId).ToArray();
        var second = SavedQueue.Order([a, b], SavedSort.Tonight).Select(e => e.TitleId).ToArray();

        Assert.Equal(first, second);
        Assert.Equal([a.TitleId, b.TitleId], first);
    }

    [Fact]
    public void Watch_tonight_takes_the_best_available_title_that_fits_the_budget()
    {
        var winner = Entry("51", predicted: 8.4m, runtime: 118, availableNow: true);
        var betterButTooLong = Entry("52", predicted: 9.5m, runtime: 180, availableNow: true);
        var betterButUnavailable = Entry("53", predicted: 9.9m, runtime: 95);
        var alsoFits = Entry("54", predicted: 7.1m, runtime: 92, availableNow: true);

        var pick = SavedQueue.PickWatchTonight(
            [betterButTooLong, betterButUnavailable, winner, alsoFits], eveningBudgetMinutes: 150);

        Assert.Equal(winner.TitleId, pick!.Value.TitleId);

        // The client's "not tonight" walks this list, so its ORDER is part of the contract.
        var candidates = SavedQueue.WatchTonightCandidates(
            [betterButTooLong, betterButUnavailable, winner, alsoFits], 150);
        Assert.Equal([winner.TitleId, alsoFits.TitleId], candidates.Select(e => e.TitleId).ToArray());
    }

    [Fact]
    public void Watch_tonight_refuses_unknown_runtimes_and_unscored_titles()
    {
        // Both are available and would otherwise win. Neither can honestly claim the slot: one
        // cannot promise it fits the evening, the other cannot promise it is any good.
        var unknownRuntime = Entry("61", predicted: 9.9m, availableNow: true);
        var unscored = Entry("62", runtime: 90, availableNow: true);

        Assert.Null(SavedQueue.PickWatchTonight([unknownRuntime, unscored], 150));
    }

    [Fact]
    public void Watch_tonight_is_null_rather_than_relaxing_a_rule()
    {
        // Nothing streaming, nothing short enough. The surface says so; it does not pin a title the
        // user cannot actually watch tonight.
        var notStreaming = Entry("71", predicted: 9.0m, runtime: 100);
        var tooLong = Entry("72", predicted: 9.0m, runtime: 151, availableNow: true);

        Assert.Null(SavedQueue.PickWatchTonight([notStreaming, tooLong], 150));
        // Exactly at the budget is inside it.
        Assert.NotNull(SavedQueue.PickWatchTonight([Entry("73", 9.0m, 150, availableNow: true)], 150));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(0, null)]
    [InlineData(-5, null)]
    [InlineData(58, "58m")]
    [InlineData(60, "1h")]
    [InlineData(118, "1h 58m")]
    [InlineData(150, "2h 30m")]
    public void Runtimes_format_as_durations_not_locale_numbers(int? minutes, string? expected) =>
        Assert.Equal(expected, SavedQueue.FormatRuntime(minutes));

    [Theory]
    [InlineData(null, null)]      // untouched
    [InlineData(0, null)]         // an explicit clear, not a clamp to the floor
    [InlineData(-1, null)]
    [InlineData(10, 30)]          // clamped up to the floor
    [InlineData(90, 90)]
    [InlineData(100000, 600)]     // clamped down to the ceiling
    public void Evening_budget_normalises_to_a_sane_range_or_clears(int? requested, int? expected) =>
        Assert.Equal(expected, SavedQueue.NormaliseEveningBudget(requested));

    [Fact]
    public void Evening_budget_falls_back_to_the_default_rather_than_being_written_on_the_users_behalf()
    {
        Assert.Equal(SavedQueue.DefaultEveningBudgetMinutes, SavedQueue.EffectiveEveningBudget(null));
        Assert.Equal(90, SavedQueue.EffectiveEveningBudget(90));
    }

    [Fact]
    public void The_pinned_reason_states_the_error_band_and_omits_it_when_unmeasured()
    {
        var measured = SavedQueue.WatchTonightReason(8.4m, 1.9d, 118, 150, "Netflix");
        Assert.Contains("8.4 for you (±1.9)", measured, StringComparison.Ordinal);
        Assert.Contains("1h 58m against your 2h 30m", measured, StringComparison.Ordinal);
        Assert.Contains("on Netflix", measured, StringComparison.Ordinal);

        // No pairs yet ⇒ no band. "±0.0" would be the most dishonest number on the screen.
        var unmeasured = SavedQueue.WatchTonightReason(8.4m, null, 118, 150, null);
        Assert.DoesNotContain("±", unmeasured, StringComparison.Ordinal);
        Assert.Contains("streaming now", unmeasured, StringComparison.Ordinal);
    }
}
