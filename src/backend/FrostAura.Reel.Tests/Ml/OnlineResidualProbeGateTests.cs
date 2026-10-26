using FrostAura.Reel.Application.Ml;

namespace FrostAura.Reel.Tests.Ml;

/// <summary>
/// The online head's probe gate (WS-5 task 7) as arithmetic: a batch is kept when it leaves the head
/// no worse on the fixed probe than before it, or within the tolerance of the base alone; a head
/// walking away from the account's own ratings is stopped.
/// </summary>
public class OnlineResidualProbeGateTests
{
    // Three probe titles, one feature; the base is exact on all three.
    private static readonly OnlineResidualModel.Probe ExactBase = new(
        [[1f], [-1f], [0.5f]],
        [7d, 5d, 6d],
        [7d, 5d, 6d]);

    [Fact]
    public void A_step_that_improves_the_probe_is_kept()
    {
        var probe = ExactBase with { BaseScores = [7.4d, 5.4d, 6.4d] }; // the base over-predicts by 0.4
        var verdict = OnlineResidualModel.JudgeOnProbe(probe, [0f], 0d, [0f], -0.2d);

        Assert.True(verdict.Accept);
        Assert.True(verdict.AfterMae < verdict.BeforeMae);
    }

    [Fact]
    public void A_small_worsening_inside_the_tolerance_of_the_base_is_kept()
    {
        var verdict = OnlineResidualModel.JudgeOnProbe(ExactBase, [0f], 0d, [0f], OnlineResidualModel.ProbeTolerance / 2);

        Assert.True(verdict.Accept);
        Assert.Equal(0d, verdict.BaseMae, 9);
    }

    [Fact]
    public void A_head_walking_away_from_the_accounts_ratings_is_refused()
    {
        // Already 0.04 off, and the batch would take it to 0.3 off: worse than before AND past the band.
        var verdict = OnlineResidualModel.JudgeOnProbe(ExactBase, [0f], 0.04d, [0f], 0.3d);

        Assert.False(verdict.Accept);
        Assert.Equal(0.3d, verdict.AfterMae, 9);
        Assert.Equal(0.04d, verdict.BeforeMae, 9);
    }

    [Fact]
    public void A_head_already_outside_the_band_may_still_move_back_towards_it()
    {
        var verdict = OnlineResidualModel.JudgeOnProbe(ExactBase, [0f], 0.5d, [0f], 0.4d);

        Assert.True(verdict.Accept);
    }
}
