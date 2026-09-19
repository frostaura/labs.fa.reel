using FrostAura.Reel.Application.Ranking;
using FrostAura.Reel.Application.Ranking.Interleaving;

namespace FrostAura.Reel.Tests.Ml;

/// <summary>
/// The draft itself. Interleaving's entire claim to being a fair instrument rests on three
/// mechanical properties — balanced contributions, no duplicate cards, and a reproducible draft —
/// and none of them is visible from the feed. So they are pinned here, without a database.
/// </summary>
public class TeamDraftInterleavingTests
{
    private static IReadOnlyList<Guid> Ids(int n) =>
        [.. Enumerable.Range(1, n).Select(i => new Guid(i, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0))];

    /// <summary>A's order is 1..n; B's is the reverse, so the two teams never want the same card next.</summary>
    private static (IReadOnlyList<Guid> A, IReadOnlyList<Guid> B) Opposed(int n)
    {
        var a = Ids(n);
        return (a, [.. a.Reverse()]);
    }

    [Theory]
    [InlineData(10, 5)]
    [InlineData(10, 10)]
    [InlineData(11, 5)]
    [InlineData(200, 10)]
    public void Contributions_are_balanced_to_within_one_seat(int pool, int size)
    {
        var (a, b) = Opposed(pool);
        foreach (var aFirst in new[] { true, false })
        {
            var picks = TeamDraft.Draft(a, b, size, aFirst);
            var fromA = picks.Count(p => p.Team == InterleaveTeam.A);
            var fromB = picks.Count - fromA;

            Assert.Equal(size, picks.Count);
            Assert.Equal(aFirst ? (size + 1) / 2 : size / 2, fromA);
            Assert.Equal(aFirst ? size / 2 : (size + 1) / 2, fromB);
            Assert.True(Math.Abs(fromA - fromB) <= 1);
        }
    }

    [Fact]
    public void A_slate_never_repeats_a_title_even_when_both_rankers_want_it_first()
    {
        // Identical orders: every turn, the team on turn wants the card the other team just took.
        var order = Ids(20);
        var picks = TeamDraft.Draft(order, order, 10, aFirst: true);

        Assert.Equal(10, picks.Count);
        Assert.Equal(10, picks.Select(p => p.TitleId).Distinct().Count());
        // With identical lists the draft walks the shared order top-down, alternating attribution.
        Assert.Equal(order.Take(10), picks.Select(p => p.TitleId));
        Assert.Equal(5, picks.Count(p => p.Team == InterleaveTeam.A));
    }

    [Fact]
    public void Each_team_takes_its_own_highest_ranked_available_card()
    {
        var (a, b) = Opposed(6);
        var picks = TeamDraft.Draft(a, b, 6, aFirst: true);

        // A drafts from the front, B from the back, converging.
        Assert.Equal(new[] { a[0], a[5], a[1], a[4], a[2], a[3] }, picks.Select(p => p.TitleId));
        Assert.All(picks.Where((_, i) => i % 2 == 0), p => Assert.Equal(InterleaveTeam.A, p.Team));
    }

    [Fact]
    public void A_short_pool_yields_a_short_slate_that_is_still_balanced()
    {
        var (a, b) = Opposed(3);
        var picks = TeamDraft.Draft(a, b, 10, aFirst: true);

        Assert.Equal(3, picks.Count);
        Assert.Equal(2, picks.Count(p => p.Team == InterleaveTeam.A));
        Assert.Equal(1, picks.Count(p => p.Team == InterleaveTeam.B));
    }

    [Fact]
    public void Relaxing_the_admissibility_predicate_does_not_cost_balance()
    {
        // Mirrors the hero: a first phase that only a few candidates clear, then an open phase.
        var (a, b) = Opposed(20);
        var premium = Ids(20).Take(3).ToHashSet();
        var draft = new TeamDraft(a, b, aFirst: true);

        draft.Fill(6, premium.Contains);
        draft.Fill(6);

        Assert.Equal(6, draft.Picks.Count);
        Assert.Equal(3, draft.Picks.Count(p => p.Team == InterleaveTeam.A));
        Assert.Equal(6, draft.Picks.Select(p => p.TitleId).Distinct().Count());
    }

    [Fact]
    public void The_seed_is_stable_across_processes_and_differs_per_slate()
    {
        var snapshot = Guid.Parse("11111111-2222-3333-4444-555555555555");

        Assert.Equal(InterleaveSeed.ADraftsFirst(snapshot, "hero"), InterleaveSeed.ADraftsFirst(snapshot, "hero"));
        Assert.Equal(InterleaveSeed.GroupId(snapshot, "hero"), InterleaveSeed.GroupId(snapshot, "hero"));
        Assert.NotEqual(InterleaveSeed.GroupId(snapshot, "hero"), InterleaveSeed.GroupId(snapshot, "deepcuts"));
        Assert.NotEqual(InterleaveSeed.GroupId(snapshot, "hero"), InterleaveSeed.GroupId(Guid.NewGuid(), "hero"));

        // Pinned literal: a value derived from a per-process hash would fail this on the second run,
        // and a slate that cannot be re-derived is a slate that cannot be audited.
        Assert.Equal(Guid.Parse("4e6a5c7b-ec28-5104-e690-13c86a236c6d"), InterleaveSeed.GroupId(snapshot, "hero"));
    }

    [Fact]
    public void The_coin_flip_is_close_to_fair_across_slates()
    {
        // Not a test of the hash's cryptographic properties — a test that one arm does not
        // systematically draft first, which would hand it every odd seat in the product.
        var aFirst = Enumerable.Range(0, 2000)
            .Count(i => InterleaveSeed.ADraftsFirst(new Guid(i, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0), "hero"));

        Assert.InRange(aFirst, 900, 1100);
    }

    [Fact]
    public void The_calibrated_policy_reproduces_the_served_ordering_key()
    {
        // predicted × taste prior × freshness × preference boost × slate affinity — the feed's own
        // comparator.
        var low = new PolicyCandidate(Ids(2)[0], 8.0m, 1.0m, 1.0d, 500m);
        var high = new PolicyCandidate(Ids(2)[1], 7.0m, 1.1m, 1.10d, 1m, SlateAffinity: 1.2d);

        var ordered = new CalibratedModelPolicy().Order([low, high]);

        Assert.Equal(high.TitleId, ordered[0].TitleId);
        Assert.Equal(2, ordered.Count);

        // And the prior is genuinely IN the key, not merely accepted by the constructor: the same
        // pair with the winner's no-history genres demoted to the floor flips the order. This is the
        // property the whole ranking-layer fix rests on — a title the account has no history in
        // cannot lead on a fraction of a point of model score.
        var demoted = high with { TastePrior = TastePrior.NoHistoryFloor };
        Assert.Equal(low.TitleId, new CalibratedModelPolicy().Order([low, demoted])[0].TitleId);
    }

    [Fact]
    public void The_popularity_policy_ranks_on_popularity_alone()
    {
        var ids = Ids(3);
        var pool = new[]
        {
            new PolicyCandidate(ids[0], 9.5m, 1m, 1d, 3m),
            new PolicyCandidate(ids[1], 4.0m, 1m, 1d, 900m),
            new PolicyCandidate(ids[2], 7.0m, 1m, 1d, 50m),
        };

        Assert.Equal([ids[1], ids[2], ids[0]], new PopularityBaselinePolicy().Order(pool).Select(c => c.TitleId));
    }

    [Fact]
    public void The_profile_rerank_policy_falls_back_to_the_calibrated_order_when_no_fit_exists()
    {
        var ids = Ids(3);
        var pool = new[]
        {
            new PolicyCandidate(ids[0], 5m, 1m, 1d, 10m),
            new PolicyCandidate(ids[1], 9m, 1m, 1d, 10m),
            new PolicyCandidate(ids[2], 7m, 1m, 1d, 10m),
        };

        Assert.Equal(
            new CalibratedModelPolicy().Order(pool).Select(c => c.TitleId),
            new ProfileRerankPolicy().Order(pool).Select(c => c.TitleId));

        // With a fit present it leads, however weak the model's own opinion of the title.
        var withFit = pool.Append(new PolicyCandidate(Ids(4)[3], 1m, 1m, 1d, 1m, LlmFit: 0.4d)).ToList();
        Assert.Equal(Ids(4)[3], new ProfileRerankPolicy().Order(withFit)[0].TitleId);
    }

    [Theory]
    [InlineData(50)]
    [InlineData(200)]
    public void Every_policy_returns_a_permutation_of_its_input(int n)
    {
        var pool = Enumerable.Range(0, n)
            .Select(i => new PolicyCandidate(
                new Guid(i, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0),
                (decimal)(i % 11), 1m + (i % 3 * 0.05m), 1d, i % 7 * 13m, 1d + (i % 5 * 0.1d),
                // Taste priors across the whole band the rule can produce (0.4 floor → 1.08 lift),
                // so the permutation contract is checked against a live prior rather than a pinned 1.
                0.4d + (i % 7 * 0.1d),
                i % 4 == 0 ? i % 100 / 100d : (double?)null))
            .ToList();

        foreach (var policy in RankingPolicyCatalog.Policies)
        {
            var ordered = policy.Order(pool);
            Assert.Equal(pool.Count, ordered.Count);
            Assert.Equal(
                pool.Select(c => c.TitleId).OrderBy(g => g),
                ordered.Select(c => c.TitleId).OrderBy(g => g));
        }
    }

    [Fact]
    public void The_harness_is_off_unless_configuration_turns_it_on()
    {
        Assert.False(InterleaveOptions.Disabled.Enabled);
        Assert.Null(SlateInterleaver.For(InterleaveOptions.Disabled));
        Assert.NotNull(SlateInterleaver.For(new InterleaveOptions { Enabled = true }));

        // An unknown policy id in the environment degrades to the default rather than stopping a
        // build: a typo in a compose file must never be able to prevent a feed from being assembled.
        Assert.Equal(CalibratedModelPolicy.Id, RankingPolicyCatalog.Normalise("nonsense", CalibratedModelPolicy.Id));
        Assert.Equal(PopularityBaselinePolicy.Id, RankingPolicyCatalog.Resolve("popularitybaseline").PolicyId);
    }
}
