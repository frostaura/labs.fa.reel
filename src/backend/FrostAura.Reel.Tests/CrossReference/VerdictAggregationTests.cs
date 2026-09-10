using FrostAura.Reel.Application.CrossReference;
using FrostAura.Reel.Domain.CrossReference;

namespace FrostAura.Reel.Tests.CrossReference;

/// <summary>
/// The aggregation rule, as arithmetic. Pure by construction, so every clause below is a statement
/// about the DESIGN rather than about a database — which matters because PR1 ships this rule
/// without a feed that reads it, and a later PR must not be able to change it by accident.
/// </summary>
public class VerdictAggregationTests
{
    [Fact]
    public void No_observations_orders_exactly_as_today()
    {
        var factor = VerdictAggregator.Aggregate([]);

        Assert.Equal(AggregateVerdict.None, factor.Verdict);
        Assert.Equal(1.0d, factor.Factor);
        Assert.True(factor.HeroAllowed);
    }

    [Fact]
    public void Two_fresh_keeps_and_no_out_is_a_confirmed_keep_bounded_at_plus_a_fifth()
    {
        var factor = VerdictAggregator.Aggregate([Keep(CrossReferenceRound.First, 1.0d), Keep(CrossReferenceRound.Second, 1.0d)]);

        Assert.Equal(AggregateVerdict.ConfirmedKeep, factor.Verdict);
        // The cap is the point: the model is trained on this person's own labels and the judge is
        // not, so a keep must never outweigh a rating point of model opinion.
        Assert.Equal(1.2d, factor.Factor, 6);
        Assert.True(factor.HeroAllowed);
    }

    [Fact]
    public void One_keep_is_provisional_and_lifts_half_as_much()
    {
        var factor = VerdictAggregator.Aggregate([Keep(CrossReferenceRound.First, 1.0d)]);

        Assert.Equal(AggregateVerdict.ProvisionalKeep, factor.Verdict);
        Assert.Equal(1.1d, factor.Factor, 6);
        Assert.True(factor.HeroAllowed);
    }

    [Fact]
    public void A_confident_out_halves_the_rank_key_and_bars_the_hero()
    {
        var factor = VerdictAggregator.Aggregate([Out(CrossReferenceRound.First, 0.70d)]);

        Assert.Equal(AggregateVerdict.Out, factor.Verdict);
        Assert.Equal(0.5d, factor.Factor);
        // A bar is honest where a multiplier is a hope: rails order by RankKey, so a title demoted
        // only in the hero key would sit atop every rail regardless.
        Assert.False(factor.HeroAllowed);
    }

    [Fact]
    public void An_out_just_under_the_threshold_is_unsure_and_changes_nothing()
    {
        var factor = VerdictAggregator.Aggregate([Out(CrossReferenceRound.First, 0.69d)]);

        Assert.Equal(AggregateVerdict.Unsure, factor.Verdict);
        Assert.Equal(1.0d, factor.Factor);
        Assert.True(factor.HeroAllowed);
    }

    [Fact]
    public void A_confident_out_beats_any_number_of_keeps()
    {
        var factor = VerdictAggregator.Aggregate(
        [
            Keep(CrossReferenceRound.First, 0.95d),
            Keep(CrossReferenceRound.Second, 0.95d),
            Out(CrossReferenceRound.Second, 0.80d),
        ]);

        // Ruling out is the judge's actual job and the prompt makes `out` the confident answer, so
        // one confident out carries more information than two keeps.
        Assert.Equal(AggregateVerdict.Out, factor.Verdict);
        Assert.False(factor.HeroAllowed);
    }

    [Fact]
    public void Keep_and_a_weak_out_disagreeing_is_unsure_and_never_a_tie_break()
    {
        var factor = VerdictAggregator.Aggregate(
        [
            Keep(CrossReferenceRound.First, 0.95d),
            Keep(CrossReferenceRound.Second, 0.95d),
            Out(CrossReferenceRound.Second, 0.40d),
        ]);

        // A contradiction between two passes is evidence about the JUDGE, not the title; resolving
        // it by confidence would convert judge noise into ranking signal.
        Assert.Equal(AggregateVerdict.Unsure, factor.Verdict);
        Assert.Equal(1.0d, factor.Factor);
    }

    [Fact]
    public void A_stale_out_demotes_at_three_quarters_and_stops_barring_the_hero()
    {
        var factor = VerdictAggregator.Aggregate([Out(CrossReferenceRound.First, 0.95d, fresh: false)]);

        Assert.Equal(AggregateVerdict.Out, factor.Verdict);
        Assert.Equal(0.75d, factor.Factor);
        Assert.True(factor.HeroAllowed);
        Assert.True(factor.Stale);
    }

    [Fact]
    public void A_fresh_out_outranks_a_stale_one()
    {
        var factor = VerdictAggregator.Aggregate(
        [
            Out(CrossReferenceRound.First, 0.99d, fresh: false),
            Out(CrossReferenceRound.Second, 0.75d, fresh: true),
        ]);

        // The current fingerprint is the taste being served.
        Assert.Equal(0.5d, factor.Factor);
        Assert.False(factor.HeroAllowed);
        Assert.False(factor.Stale);
    }

    [Fact]
    public void Stale_keeps_lift_at_half_strength()
    {
        var confirmed = VerdictAggregator.Aggregate(
            [Keep(CrossReferenceRound.First, 1.0d, fresh: false), Keep(CrossReferenceRound.Second, 1.0d, fresh: false)]);
        Assert.Equal(AggregateVerdict.ConfirmedKeep, confirmed.Verdict);
        Assert.Equal(1.1d, confirmed.Factor, 6);
        Assert.True(confirmed.Stale);

        var provisional = VerdictAggregator.Aggregate([Keep(CrossReferenceRound.First, 1.0d, fresh: false)]);
        Assert.Equal(1.05d, provisional.Factor, 6);
    }

    [Fact]
    public void Aggregation_does_not_depend_on_the_order_the_observations_arrive_in()
    {
        // An order-dependent aggregate would make the feed depend on batch order.
        var observations = new[]
        {
            Keep(CrossReferenceRound.First, 0.6d),
            Out(CrossReferenceRound.Second, 0.85d, fresh: false),
            Keep(CrossReferenceRound.Second, 0.9d),
        };

        var forwards = VerdictAggregator.Aggregate(observations);
        var backwards = VerdictAggregator.Aggregate([.. Enumerable.Reverse(observations)]);

        Assert.Equal(forwards, backwards);
    }

    [Fact]
    public void The_confidence_lift_scales_with_the_confidence()
    {
        var weak = VerdictAggregator.Aggregate([Keep(CrossReferenceRound.First, 0.5d), Keep(CrossReferenceRound.Second, 0.5d)]);
        var strong = VerdictAggregator.Aggregate([Keep(CrossReferenceRound.First, 0.9d), Keep(CrossReferenceRound.Second, 0.9d)]);

        Assert.Equal(1.1d, weak.Factor, 6);
        Assert.Equal(1.18d, strong.Factor, 6);
        Assert.True(strong.Factor > weak.Factor);
    }

    [Fact]
    public void The_fit_stand_in_exists_only_for_a_confirmed_keep()
    {
        var confirmed = VerdictAggregator.Aggregate([Keep(CrossReferenceRound.First, 0.4d), Keep(CrossReferenceRound.Second, 0.4d)]);
        Assert.Equal(0.9d, VerdictAggregator.FitFor(confirmed), 6);

        // Every other state keeps whatever fit it already had; asking for a stand-in is a bug, not
        // a default.
        Assert.Throws<ArgumentException>(() => VerdictAggregator.FitFor(VerdictFactor.Neutral));
        Assert.Throws<ArgumentException>(
            () => VerdictAggregator.FitFor(VerdictAggregator.Aggregate([Out(CrossReferenceRound.First, 0.9d)])));
    }

    [Fact]
    public void The_fit_stand_in_stays_on_the_fit_scale()
    {
        var confirmed = VerdictAggregator.Aggregate([Keep(CrossReferenceRound.First, 1.0d), Keep(CrossReferenceRound.Second, 1.0d)]);
        Assert.Equal(1.0d, VerdictAggregator.FitFor(confirmed), 6);
    }

    private static VerdictObservation Keep(CrossReferenceRound round, double confidence, bool fresh = true) =>
        new(round, CrossReferenceVerdict.Keep, confidence, fresh);

    private static VerdictObservation Out(CrossReferenceRound round, double confidence, bool fresh = true) =>
        new(round, CrossReferenceVerdict.Out, confidence, fresh);
}
