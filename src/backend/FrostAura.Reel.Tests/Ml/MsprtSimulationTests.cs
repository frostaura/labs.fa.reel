using FrostAura.Reel.Application.Experiments;

namespace FrostAura.Reel.Tests.Ml;

/// <summary>
/// The always-valid test, measured rather than asserted.
///
/// <para>The whole promise of the interleaving panel is that the founder may look at the number
/// whenever he likes and the error rate does not move. That is a mathematical claim about a
/// supermartingale, and the adversarial review was explicit that the claim must be SIMULATED before
/// it is trusted: ten thousand runs under the null, peeking after every single slate, and the rate
/// at which the test ever crosses α has to stay at or under α. A test that only checked the formula
/// against itself would pass while the harness quietly ran at three times its stated error rate.</para>
///
/// <para>Seeded throughout. A simulation that decides the build's fate must fail for a reason, not
/// on a draw.</para>
/// </summary>
public class MsprtSimulationTests
{
    private const double Tau = 0.15d;
    private const double Alpha = 0.05d;
    private const int Runs = 10_000;
    private const int Horizon = 500;

    /// <summary>
    /// Walks one stream of decided slates, peeking after every one, and reports whether the
    /// always-valid p ever crossed α — and, if it did, how many slates that took.
    /// </summary>
    private static (bool Crossed, int At) Walk(Random rng, double pA, int horizon, double alpha = Alpha)
    {
        var wins = 0;
        var best = 0d;
        var threshold = Math.Log(1d / alpha);
        for (var n = 1; n <= horizon; n++)
        {
            if (rng.NextDouble() < pA)
            {
                wins++;
            }

            var log = Msprt.LogLambda(n, wins, Tau);
            if (log > best)
            {
                best = log;
            }

            if (best >= threshold)
            {
                return (true, n);
            }
        }

        return (false, horizon);
    }

    [Fact]
    public void Ten_thousand_null_runs_with_continuous_peeking_stay_under_alpha()
    {
        var rng = new Random(20260910);
        var falsePositives = 0;
        for (var run = 0; run < Runs; run++)
        {
            if (Walk(rng, 0.5d, Horizon).Crossed)
            {
                falsePositives++;
            }
        }

        var rate = (double)falsePositives / Runs;

        // Ville's inequality bounds this by α for ANY stopping rule, including "peek every slate
        // forever". The bound is not asymptotic, so there is no small-n exemption to allow for.
        Assert.True(
            rate <= Alpha,
            FormattableString.Invariant($"Type-I under continuous peeking was {rate:P2}, above the stated {Alpha:P0}."));

        // And it must not be so conservative that the instrument can never decide anything. A rate
        // of zero over 10,000 runs would mean the threshold is doing no work at this horizon.
        Assert.True(rate > 0d, "The test never fired under the null — check the threshold, not the luck.");
    }

    [Fact]
    public void Peeking_only_at_the_end_is_no_safer_than_peeking_throughout()
    {
        // The point of the always-valid construction, stated as a test: the continuous-peeking rate
        // above and the fixed-horizon rate here are both under α. A fixed-sample z-test would show
        // ~5% here and ~3× that above; this one does not.
        var rng = new Random(4242);
        var fixedHorizon = 0;
        for (var run = 0; run < Runs; run++)
        {
            var wins = 0;
            for (var n = 0; n < Horizon; n++)
            {
                if (rng.NextDouble() < 0.5d)
                {
                    wins++;
                }
            }

            if (Msprt.LogLambda(Horizon, wins, Tau) >= Math.Log(1d / Alpha))
            {
                fixedHorizon++;
            }
        }

        Assert.True((double)fixedHorizon / Runs <= Alpha);
    }

    [Fact]
    public void The_error_bound_holds_at_a_tighter_alpha_too()
    {
        var rng = new Random(777);
        var crossings = Enumerable.Range(0, Runs).Count(_ => Walk(rng, 0.5d, Horizon, 0.01d).Crossed);

        Assert.True((double)crossings / Runs <= 0.01d);
    }

    [Fact]
    public void A_sixty_five_thirty_five_preference_is_detected_in_the_projected_number_of_slates()
    {
        // What the panel promises the founder, checked against simulation. The projection walks the
        // expected path; a real stream is noisy either side of it, so the median crossing is the
        // honest comparison and it must land in the same neighbourhood.
        var projected = Msprt.SlatesToDecision(0.65d, Tau, Alpha);
        Assert.InRange(projected, 60, 200);

        var rng = new Random(31337);
        var crossedAt = new List<int>();
        var detected = 0;
        for (var run = 0; run < 2_000; run++)
        {
            var (crossed, at) = Walk(rng, 0.65d, 4 * projected);
            if (crossed)
            {
                detected++;
                crossedAt.Add(at);
            }
        }

        crossedAt.Sort();
        var median = crossedAt[crossedAt.Count / 2];

        // Power: a real 65/35 preference is found nearly always within four times the projection.
        Assert.True(detected / 2_000d > 0.95d, FormattableString.Invariant($"Only {detected} of 2000 runs decided."));
        // And the projection is not a fantasy — the median run crosses within a factor of two of it.
        Assert.InRange(median, projected / 2, projected * 2);
    }

    [Fact]
    public void The_projection_says_months_and_not_weeks_at_one_decided_slate_a_day()
    {
        // The cadence the founder is owed honestly (panel B1, §A-15). At roughly one DECIDED slate a
        // day — outcomes are sparse, most slates tie at zero credit apiece — a 65/35 preference is
        // a two-to-three-month wait per comparison, and comparisons serialise. This test exists so
        // that number cannot drift optimistically without someone deciding to change it.
        var slates = Msprt.SlatesToDecision(0.65d, Tau, Alpha);
        var weeks = slates / 7d;

        Assert.True(weeks > 6d, FormattableString.Invariant($"Projection collapsed to {weeks:F1} weeks — verify before believing it."));
        Assert.True(weeks < 26d);
    }

    [Fact]
    public void A_dead_heat_never_decides_and_a_landslide_decides_fast()
    {
        Assert.Equal(100_000, Msprt.SlatesToDecision(0.5d, Tau, Alpha, cap: 100_000));
        Assert.True(Msprt.SlatesToDecision(0.9d, Tau, Alpha) < 20);
    }

    [Fact]
    public void The_always_valid_p_is_monotone_and_never_exceeds_one()
    {
        var rng = new Random(11);
        var stream = new List<bool>();
        var previous = 1d;
        for (var n = 0; n < 300; n++)
        {
            stream.Add(rng.NextDouble() < 0.7d);
            var p = Msprt.AlwaysValidP(stream, Tau);

            Assert.InRange(p, 0d, 1d);
            Assert.True(p <= previous + 1e-12d, "The always-valid p-value went back up.");
            previous = p;
        }

        Assert.True(previous < Alpha);
    }

    [Fact]
    public void The_confidence_interval_narrows_and_covers_the_truth()
    {
        Assert.Equal(0.5d, Msprt.CiHalfWidth(0, Tau, Alpha));
        Assert.True(Msprt.CiHalfWidth(500, Tau, Alpha) < Msprt.CiHalfWidth(50, Tau, Alpha));

        // Coverage is the property that matters: an always-valid interval must contain the true
        // rate at EVERY n simultaneously, not just at a pre-chosen one.
        var rng = new Random(9001);
        var missed = 0;
        for (var run = 0; run < 2_000; run++)
        {
            var wins = 0;
            for (var n = 1; n <= 200; n++)
            {
                if (rng.NextDouble() < 0.6d)
                {
                    wins++;
                }

                var half = Msprt.CiHalfWidth(n, Tau, Alpha);
                if (Math.Abs(((double)wins / n) - 0.6d) > half)
                {
                    missed++;
                    break;
                }
            }
        }

        Assert.True((double)missed / 2_000 <= Alpha);
    }

    [Fact]
    public void The_sign_test_agrees_on_the_obvious_cases_and_is_always_looser_to_beat()
    {
        Assert.Equal(1d, SignTest.TwoSidedP(0, 0));
        Assert.Equal(1d, SignTest.TwoSidedP(10, 5), 10);
        Assert.Equal(0.0625d, SignTest.TwoSidedP(5, 5), 6);
        Assert.True(SignTest.TwoSidedP(100, 65) < 0.01d);

        // The gap between the two IS the price of peeking, and at a given (n, wins) it always runs
        // this way round: the fixed-sample p is smaller, so quoting it as the decision would be
        // reading the experiment as though the founder had committed in advance to looking exactly
        // once, exactly then. Compared against Λ at that n rather than against the running minimum,
        // because the running minimum also carries the evidence of every earlier prefix — an
        // all-wins-then-all-losses stream is significant partway through and the honest reading says
        // so, which would make this comparison about ordering rather than about the peeking premium.
        foreach (var (n, wins) in new[] { (100, 62), (200, 118), (60, 40) })
        {
            var pointwise = Math.Exp(-Msprt.LogLambda(n, wins, Tau));
            Assert.True(
                SignTest.TwoSidedP(n, wins) < pointwise,
                FormattableString.Invariant($"At {wins}/{n} the sign test was not the looser bar."));
        }
    }
}
