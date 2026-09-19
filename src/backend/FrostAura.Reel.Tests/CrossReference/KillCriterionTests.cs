using FrostAura.Reel.Application.CrossReference;
using FrostAura.Reel.Domain.CrossReference;

namespace FrostAura.Reel.Tests.CrossReference;

/// <summary>
/// The gate, and the interval it reads. Pure arithmetic, hand-computed where it matters.
///
/// <para><b>The test this file exists for is the third one.</b> An observed false-rule-out rate of
/// 9% looks comfortably under a 15% ceiling and is, at n≈55, entirely consistent with a true rate
/// of 20%. Reading the point estimate is reading sampling noise as a result — which is the same
/// error the M2 ledger made for twelve iterations before the CI was added, and the reason this
/// criterion was specified as a BOUND before a single verdict was written.</para>
/// </summary>
public class KillCriterionTests
{
    [Fact]
    public void No_observations_is_a_completely_uninformative_interval()
    {
        // [0, 1] is the correct statement that nothing is known — and it makes the gate fail by
        // arithmetic as well as by the explicit lovedCount clause.
        Assert.Equal((0m, 1m), CrossReferenceBacktest.WilsonInterval(0, 0));
    }

    [Fact]
    public void The_interval_matches_a_hand_computed_Wilson_score()
    {
        // 3 of 55, z = 1.959964:
        //   p        = 3/55                       = 0.0545455
        //   centre   = p + z²/(2n)                = 0.0545455 + 3.8415/110      = 0.0894681
        //   margin   = z·sqrt(p(1−p)/n + z²/4n²)  = 1.959964·sqrt(0.00093779 + 0.00031748)
        //            = 1.959964 · 0.0354298       = 0.0694411
        //   denom    = 1 + z²/n                   = 1 + 0.0698455                = 1.0698455
        //   low      = (0.0894681 − 0.0694411)/1.0698455 = 0.018720
        //   high     = (0.0894681 + 0.0694411)/1.0698455 = 0.148535
        var (low, high) = CrossReferenceBacktest.WilsonInterval(3, 55);

        Assert.Equal(0.0187m, low, 3);
        Assert.Equal(0.1485m, high, 3);
    }

    [Fact]
    public void Zero_observed_failures_still_leaves_a_real_upper_bound()
    {
        // Never ruling out a loved title in 20 tries is not proof the rate is zero; the upper bound
        // is ~16%, which is above the ceiling. A point-estimate gate would have gated here.
        var (low, high) = CrossReferenceBacktest.WilsonInterval(0, 20);

        Assert.Equal(0m, low);
        Assert.True(high > CrossReferenceBacktest.FalseRuleOutCeiling, $"upper bound was {high}");
        Assert.Equal(0.1611m, high, 3);
    }

    [Fact]
    public void A_point_estimate_under_the_ceiling_with_an_upper_bound_over_it_is_demote_only()
    {
        // THE test. 5 of 55 = 9.1%, comfortably under 15% — and the 95% upper bound is over 20%.
        var (_, high) = CrossReferenceBacktest.WilsonInterval(5, 55);
        Assert.True(high > CrossReferenceBacktest.FalseRuleOutCeiling, $"upper bound was {high}");

        Assert.Equal(
            GatingDecisions.DemoteOnly,
            CrossReferenceBacktest.Decide(high, trueRuleOutRate: 0.55m, unsureRate: 0.40m, lovedCount: 55));
    }

    [Fact]
    public void Every_clause_must_hold_for_the_gate_to_open()
    {
        const decimal goodBound = 0.12m;

        Assert.Equal(
            GatingDecisions.Gating,
            CrossReferenceBacktest.Decide(goodBound, 0.55m, 0.40m, 200));

        // …a judge that rules almost nothing out is harmless and useless.
        Assert.Equal(
            GatingDecisions.DemoteOnly,
            CrossReferenceBacktest.Decide(goodBound, trueRuleOutRate: 0.39m, unsureRate: 0.40m, lovedCount: 200));

        // …and one that answers "unsure" to most of the pool is honest and useless — the same
        // failure mode the calibrator collapse guard exists for.
        Assert.Equal(
            GatingDecisions.DemoteOnly,
            CrossReferenceBacktest.Decide(goodBound, trueRuleOutRate: 0.55m, unsureRate: 0.61m, lovedCount: 200));

        // …and no loved holdout at all is no evidence, whatever the arithmetic says.
        Assert.Equal(
            GatingDecisions.DemoteOnly,
            CrossReferenceBacktest.Decide(goodBound, trueRuleOutRate: 0.55m, unsureRate: 0.40m, lovedCount: 0));
    }

    [Fact]
    public void The_boundary_values_gate_rather_than_fail()
    {
        // Exactly at each threshold is inside it — the thresholds are written as "≤ 0.15", "≥ 0.40"
        // and "≤ 0.60" and the code must agree with the prose.
        Assert.Equal(
            GatingDecisions.Gating,
            CrossReferenceBacktest.Decide(0.15m, trueRuleOutRate: 0.40m, unsureRate: 0.60m, lovedCount: 100));
    }

    [Fact]
    public void The_caveat_names_n_and_the_interval()
    {
        // A true-rule-out rate far above the false one, so the discrimination sentence stays off and
        // this test keeps testing exactly what it says it does.
        var caveat = CrossReferenceBacktest.BuildCaveat(
            loved: 55, disliked: 40, judged: 300, falseRate: 0.0909m, low: 0.0394m, high: 0.1990m,
            noEmbedding: 402, trueRate: 0.55m, auc: 0.78m);

        Assert.Contains("55 holdout title(s)", caveat, StringComparison.Ordinal);
        Assert.Contains("40 it", caveat, StringComparison.Ordinal);
        Assert.Contains("300 judged", caveat, StringComparison.Ordinal);
        Assert.Contains("[0.039, 0.199]", caveat, StringComparison.Ordinal);
        Assert.Contains("UPPER bound", caveat, StringComparison.Ordinal);
        // Coverage is stated, not rounded away: those titles are in no denominator on the row.
        Assert.Contains("402 eligible holdout title(s) were skipped", caveat, StringComparison.Ordinal);
    }

    [Fact]
    public void The_caveat_is_invariant_under_a_comma_decimal_culture()
    {
        var original = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            var caveat = CrossReferenceBacktest.BuildCaveat(55, 40, 300, 0.0909m, 0.0394m, 0.1990m, 402, 0.55m, 0.78m);

            Assert.Contains("0.091", caveat, StringComparison.Ordinal);
            Assert.DoesNotContain("0,091", caveat, StringComparison.Ordinal);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = original;
        }
    }
}
