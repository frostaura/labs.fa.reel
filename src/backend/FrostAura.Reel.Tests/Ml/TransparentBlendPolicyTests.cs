using FrostAura.Reel.Application.Ranking.Interleaving;

namespace FrostAura.Reel.Tests.Ml;

/// <summary>
/// The transparent challenger (WS-5 task 11) as arithmetic: a deterministic permutation ranked by
/// z-scored, checkable signals, with weights chosen only from the pre-registered grid.
/// </summary>
public class TransparentBlendPolicyTests
{
    private static PolicyCandidate Candidate(int n, ChallengerSignals? signals) =>
        new(new Guid(n, 0, 0, [0, 0, 0, 0, 0, 0, 0, 0]), 7m, 1m, 1d, 10m, Challenger: signals);

    [Fact]
    public void It_ranks_by_crowd_votes_affinity_and_loved_similarity_and_returns_every_candidate_once()
    {
        PolicyCandidate[] pool =
        [
            Candidate(1, new ChallengerSignals(6.4, Math.Log(200), 6.0, 0.20)),
            Candidate(2, new ChallengerSignals(7.9, Math.Log(40_000), 7.5, 0.45)), // best on every signal
            Candidate(3, new ChallengerSignals(null, Math.Log(10), 6.5, 0.30)),    // no crowd score: mean, not penalised
            Candidate(4, null),                                                    // no signals at all: the pool mean
        ];

        var ordered = new TransparentBlendPolicy().Order(pool);

        Assert.Equal(pool[1].TitleId, ordered[0].TitleId);
        Assert.Equal(pool.Select(c => c.TitleId).Order(), ordered.Select(c => c.TitleId).Order());
        Assert.Equal(ordered, new TransparentBlendPolicy().Order([.. pool.Reverse()]));
    }

    [Fact]
    public void The_weights_change_the_order_the_way_they_say()
    {
        // A: crowd darling the account has no affinity for. B: niche title squarely in the account's taste.
        var a = new ChallengerSignals(8.2, Math.Log(50_000), 5.0, 0.20);
        var b = new ChallengerSignals(6.6, Math.Log(300), 8.5, 0.50);

        var crowdOnly = TransparentBlendPolicy.Scores([a, b], affinityWeight: 0d, similarityWeight: 0d);
        var tasteHeavy = TransparentBlendPolicy.Scores([a, b], affinityWeight: 4d, similarityWeight: 4d);

        Assert.True(crowdOnly[0] > crowdOnly[1]);
        Assert.True(tasteHeavy[1] > tasteHeavy[0]);
    }

    [Fact]
    public void The_fit_picks_from_the_grid_the_pair_that_ranks_the_loved_titles_first()
    {
        // Ratings follow affinity and run AGAINST the crowd: only an affinity weight above the crowd's
        // fixed 1 ranks the loved titles first, and the smallest such grid weight (2) wins the tie.
        var signals = Enumerable.Range(0, 20)
            .Select(i => new ChallengerSignals(8.0 - (i * 0.05), Math.Log(1_000), 5.0 + (i * 0.2), 0.3))
            .ToList();
        short[] ratings = [.. Enumerable.Range(0, 20).Select(i => (short)Math.Clamp(3 + (i / 3), 1, 10))];

        var fit = TransparentBlendPolicy.Fit([(signals, ratings, 8m)]);

        Assert.NotNull(fit);
        Assert.Contains(fit!.Value.Affinity, TransparentBlendPolicy.WeightGrid);
        Assert.Equal(2d, fit.Value.Affinity);
        Assert.Null(TransparentBlendPolicy.Fit([]));
    }

    [Fact]
    public void The_catalogue_knows_the_challenger_by_its_stable_id() =>
        Assert.IsType<TransparentBlendPolicy>(RankingPolicyCatalog.Find("transparentblend"));
}
