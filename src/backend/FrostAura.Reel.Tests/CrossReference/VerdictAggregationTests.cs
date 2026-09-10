using FrostAura.Reel.Application.CrossReference;
using FrostAura.Reel.Domain.CrossReference;

namespace FrostAura.Reel.Tests.CrossReference;

/// <summary>
/// The aggregation rule, as arithmetic. Pure by construction, so every clause below is a statement
/// about the DESIGN rather than about a database.
///
/// <para><b>The rule is keep-only as of 2026-09-11, and these tests are where that is pinned.</b>
/// On the founder's own holdout, five split origins pooled: prompt v2 ruled out 16.2% [Wilson 12.3,
/// 21.0] of the titles he went on to rate 8 or higher against 22.8% of the ones he rated 4 or lower
/// — so an Out demotes nothing and bars nothing, while a Keep (loved at 33.2% against a 23% base
/// rate, disliked at 8.1% against 15%) lifts by at most a fifth. The assertions that Out and Unsure
/// are exactly 1.0 are therefore not defaults waiting to be filled in; they ARE the measured
/// decision, and changing one is a claim about new evidence.</para>
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
    public void A_confident_out_is_recorded_and_changes_nothing_at_all()
    {
        var factor = VerdictAggregator.Aggregate([Out(CrossReferenceRound.First, 0.70d)]);

        // Still CLASSIFIED at the 0.70 threshold — that population is what the backtest measures
        // and what the exposure log records, so the closed loop keeps observing an opinion the
        // product does not act on.
        Assert.Equal(AggregateVerdict.Out, factor.Verdict);
        // Exactly 1.0, to the last bit. The out verdict caught 16.2% of the founder's future loves
        // against 22.8% of his future dislikes; demoting on that trades roughly one hidden love for
        // one-and-a-half demoted dislikes, against a base rate where loves are the commoner class.
        Assert.Equal(1.0d, factor.Factor);
        Assert.Equal(VerdictAggregator.OutFactor, factor.Factor);
        Assert.True(factor.HeroAllowed);
        // The confidence still travels, because the exposure row records it.
        Assert.Equal(0.70d, factor.Confidence, 6);
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
        // one confident out is the honest STANDING even beside two keeps — and the standing is what
        // the exposure log records. What it no longer does is lift the title the way the keeps
        // would have: a contradicted keep is not a keep.
        Assert.Equal(AggregateVerdict.Out, factor.Verdict);
        Assert.Equal(1.0d, factor.Factor);
        Assert.True(factor.HeroAllowed);
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
    public void A_stale_out_is_1_0_exactly_like_a_fresh_one()
    {
        var factor = VerdictAggregator.Aggregate([Out(CrossReferenceRound.First, 0.95d, fresh: false)]);

        Assert.Equal(AggregateVerdict.Out, factor.Verdict);
        // Halving nothing is still nothing. The staleness is recorded because it describes the
        // evidence; it has nothing to halve.
        Assert.Equal(1.0d, factor.Factor);
        Assert.True(factor.HeroAllowed);
        Assert.True(factor.Stale);
    }

    [Fact]
    public void A_fresh_out_still_supplies_the_recorded_confidence_over_a_stale_one()
    {
        var factor = VerdictAggregator.Aggregate(
        [
            Out(CrossReferenceRound.First, 0.99d, fresh: false),
            Out(CrossReferenceRound.Second, 0.75d, fresh: true),
        ]);

        // The current fingerprint is the taste being served, so the FRESH out's confidence is the
        // one written to the exposure row — the 0.99 judged against a taste the account has moved
        // off is not the stronger claim, it is the older one.
        Assert.Equal(0.75d, factor.Confidence, 6);
        Assert.False(factor.Stale);
        Assert.Equal(1.0d, factor.Factor);
        Assert.True(factor.HeroAllowed);
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
    public void The_hero_fit_stand_in_exists_only_for_a_keep()
    {
        var confirmed = VerdictAggregator.Aggregate([Keep(CrossReferenceRound.First, 0.4d), Keep(CrossReferenceRound.Second, 0.4d)]);
        Assert.Equal(0.9d, VerdictAggregator.HeroFitFactorFor(confirmed));

        // A single keep is a keep too: round 2 does not exist yet, so a rule that only served
        // Confirmed Keeps would be dead code on every account alive today.
        var provisional = VerdictAggregator.Aggregate([Keep(CrossReferenceRound.First, 0.4d)]);
        Assert.Equal(0.9d, VerdictAggregator.HeroFitFactorFor(provisional));

        // Nothing else stands in — and an Out supplies null rather than a number below 1.0, because
        // the hero is where a wrong demotion costs most and the out verdict has not earned one.
        Assert.Null(VerdictAggregator.HeroFitFactorFor(VerdictFactor.Neutral));
        Assert.Null(VerdictAggregator.HeroFitFactorFor(
            VerdictAggregator.Aggregate([Out(CrossReferenceRound.First, 0.9d)])));
        Assert.Null(VerdictAggregator.HeroFitFactorFor(
            VerdictAggregator.Aggregate([Out(CrossReferenceRound.First, 0.2d)])));
    }

    [Fact]
    public void The_hero_fit_stand_in_is_on_the_same_scale_as_a_judged_fit()
    {
        // The hero key multiplies by NeutralFit + fit = 0.5 + fit, so a keep at confidence c has to
        // arrive as 0.5 + c to be worth exactly what a judged fit of c is worth. Anything else
        // values a keep against a different yardstick than the pass it stands in for.
        var confirmed = VerdictAggregator.Aggregate([Keep(CrossReferenceRound.First, 1.0d), Keep(CrossReferenceRound.Second, 1.0d)]);
        Assert.Equal(1.5d, VerdictAggregator.HeroFitFactorFor(confirmed));

        var lukewarm = VerdictAggregator.Aggregate([Keep(CrossReferenceRound.First, 0.0d)]);
        Assert.Equal(0.5d, VerdictAggregator.HeroFitFactorFor(lukewarm));
    }

    [Fact]
    public void Every_non_keep_standing_orders_exactly_as_it_did_before_any_of_this_existed()
    {
        // The one assertion the whole keep-only decision rests on: nothing the judge says can push
        // a title DOWN. Enumerated rather than spot-checked, so a future edit that reintroduces a
        // demotion has to delete a test that says why it should not.
        VerdictFactor[] states =
        [
            VerdictFactor.Neutral,
            VerdictAggregator.Aggregate([Out(CrossReferenceRound.First, 1.0d)]),
            VerdictAggregator.Aggregate([Out(CrossReferenceRound.First, 0.70d)]),
            VerdictAggregator.Aggregate([Out(CrossReferenceRound.First, 0.95d, fresh: false)]),
            VerdictAggregator.Aggregate([Out(CrossReferenceRound.First, 0.69d)]),
            VerdictAggregator.Aggregate([Keep(CrossReferenceRound.First, 0.9d), Out(CrossReferenceRound.Second, 0.4d)]),
            VerdictAggregator.Aggregate([Unsure(CrossReferenceRound.First, 0.9d)]),
        ];

        foreach (var state in states)
        {
            Assert.Equal(1.0d, state.Factor);
            Assert.True(state.HeroAllowed);
            Assert.Null(VerdictAggregator.HeroFitFactorFor(state));
        }
    }

    [Fact]
    public void The_exposure_label_separates_never_judged_from_judged_and_unsure()
    {
        // NULL and "Unsure" are different facts, and the accuracy denominators on the track record
        // are wrong in the flattering direction if they are conflated: "never judged" is the far
        // larger set on any account the pass has not finished.
        Assert.Null(VerdictFactor.Neutral.ExposureLabel);
        Assert.Equal(
            "Unsure", VerdictAggregator.Aggregate([Unsure(CrossReferenceRound.First, 0.5d)]).ExposureLabel);
        Assert.Equal(
            "Out", VerdictAggregator.Aggregate([Out(CrossReferenceRound.First, 0.9d)]).ExposureLabel);
        Assert.Equal(
            "ProvisionalKeep", VerdictAggregator.Aggregate([Keep(CrossReferenceRound.First, 0.9d)]).ExposureLabel);
        Assert.Equal(
            "ConfirmedKeep",
            VerdictAggregator
                .Aggregate([Keep(CrossReferenceRound.First, 0.9d), Keep(CrossReferenceRound.Second, 0.9d)])
                .ExposureLabel);
    }

    private static VerdictObservation Keep(CrossReferenceRound round, double confidence, bool fresh = true) =>
        new(round, CrossReferenceVerdict.Keep, confidence, fresh);

    private static VerdictObservation Out(CrossReferenceRound round, double confidence, bool fresh = true) =>
        new(round, CrossReferenceVerdict.Out, confidence, fresh);

    private static VerdictObservation Unsure(CrossReferenceRound round, double confidence, bool fresh = true) =>
        new(round, CrossReferenceVerdict.Unsure, confidence, fresh);
}
