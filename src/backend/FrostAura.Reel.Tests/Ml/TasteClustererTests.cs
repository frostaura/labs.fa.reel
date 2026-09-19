using System.Globalization;
using FrostAura.Reel.Application.Ml;
using FrostAura.Reel.Application.Ranking;

namespace FrostAura.Reel.Tests.Ml;

/// <summary>
/// The taste partition's arithmetic, in isolation.
///
/// <para>Everything asserted here is about REPLAYABILITY or about the budget split, and both are
/// load-bearing for reasons that are invisible from the code. Two feed builds over identical data
/// must retrieve identical pools, or the no-op suppression that keeps tonight's picks stable stops
/// working and the hero churns — the exact 2026-08-07 complaint the stable-tonight contract exists
/// to answer. And <see cref="CandidateRetriever.MinPerClusterPull"/> is what stops multi-centroid
/// retrieval degenerating into a single centroid plus overhead.</para>
/// </summary>
public class TasteClustererTests
{
    private static readonly Guid Account = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private static readonly DateTime AsOf = new(2026, 9, 10, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>Deliberately not a multiple of the SIMD width, so <c>Dot</c>'s remainder loop runs.</summary>
    private const int Dim = 34;

    [Fact]
    public void One_hundred_runs_over_the_same_inputs_are_byte_identical()
    {
        var inputs = Pockets();
        var first = Describe(TasteClusterer.Partition(inputs, Account, AsOf, 7m));

        for (var run = 0; run < 100; run++)
        {
            Assert.Equal(first, Describe(TasteClusterer.Partition(inputs, Account, AsOf, 7m)));
        }
    }

    /// <summary>
    /// The Postgres-row-order bug, pinned. k-means++ picks its first centre by INDEX, so an input
    /// list in whatever order the query plan produced would silently make the partition a function of
    /// that plan — and two builds seconds apart could then retrieve different pools with nothing in
    /// the data having changed. The clusterer sorts by TitleId before anything reads the order.
    /// </summary>
    [Fact]
    public void The_partition_does_not_depend_on_the_order_the_inputs_arrive_in()
    {
        var inputs = Pockets();
        var expected = Describe(TasteClusterer.Partition(inputs, Account, AsOf, 7m));

        // Reversed, and two different deterministic shuffles — no Random, so a failure is reproducible.
        Assert.Equal(expected, Describe(TasteClusterer.Partition(inputs.AsEnumerable().Reverse().ToList(), Account, AsOf, 7m)));
        Assert.Equal(expected, Describe(TasteClusterer.Partition(Rotate(inputs, 17), Account, AsOf, 7m)));
        Assert.Equal(expected, Describe(TasteClusterer.Partition(Rotate(inputs, 43), Account, AsOf, 7m)));
    }

    /// <summary>
    /// <c>clamp(round(sqrt(n/10)), 3, 12)</c> then <c>min(k, n/20)</c>. The second half is the whole
    /// point: without it a one-title history asks for three clusters.
    /// </summary>
    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 0)]
    [InlineData(29, 1)]
    [InlineData(30, 1)]
    [InlineData(100, 3)]
    [InlineData(652, 8)]
    [InlineData(20000, 12)]
    public void The_k_rule_is_bounded_at_both_ends(int n, int expected) =>
        Assert.Equal(expected, TasteClusterer.ClusterCountFor(n));

    /// <summary>
    /// Two clusters pointing the same way are one pocket found twice, and a cluster below three
    /// members is a weekend. Both are folded back, so k is an upper bound on what is returned rather
    /// than a promise — a partition that returned twelve directions when the data holds two would
    /// split one taste's retrieval budget twelve ways.
    /// </summary>
    [Fact]
    public void Duplicated_directions_and_thin_clusters_are_merged_away()
    {
        // 80 titles in exactly two directions. The k rule asks for 3 (sqrt(8) → 3, 80/20 = 4), so at
        // least one cluster has to collapse.
        var inputs = new List<TasteClusterer.Input>();
        for (var i = 0; i < 40; i++)
        {
            inputs.Add(Rated(i, Axis(0), 9));
            inputs.Add(Rated(1000 + i, Axis(7), 9));
        }

        var clusters = TasteClusterer.Partition(inputs, Account, AsOf, 7m);

        Assert.Equal(2, clusters.Count);
        Assert.All(clusters, c => Assert.True(c.MemberCount >= TasteClusterer.MinClusterMembers));
        // Two surviving centroids must genuinely point different ways.
        Assert.True(TasteClusterer.Dot(clusters[0].Centroid, clusters[1].Centroid) <= TasteClusterer.MergeCosine);
    }

    /// <summary>
    /// An empty cluster is reseeded at the point its own centroid serves worst, and that choice is
    /// deterministic — lowest similarity, then lowest id. An empty cluster left in place would make k
    /// a lie: the budget split would hand a slice of the ANN pool to a direction no title voted for.
    /// </summary>
    [Fact]
    public void An_empty_cluster_reseeds_deterministically_rather_than_surviving_empty()
    {
        // Every title in ONE direction plus a single outlier, so k-means++ has to strand at least one
        // centre and repair it. 60 inputs ⇒ k = 3.
        var inputs = new List<TasteClusterer.Input>();
        for (var i = 0; i < 59; i++)
        {
            inputs.Add(Rated(i, Axis(0), 9));
        }

        inputs.Add(Rated(999, Axis(11), 9));

        var first = Describe(TasteClusterer.Partition(inputs, Account, AsOf, 7m));
        for (var run = 0; run < 20; run++)
        {
            Assert.Equal(first, Describe(TasteClusterer.Partition(Rotate(inputs, run * 7), Account, AsOf, 7m)));
        }

        var clusters = TasteClusterer.Partition(inputs, Account, AsOf, 7m);
        Assert.All(clusters, c => Assert.True(c.MemberCount > 0));
    }

    /// <summary>
    /// Weights, by hand. A rated title contributes <c>max(0.5, rating − userMean + 1) × recency</c>;
    /// an UNRATED rewatch contributes <c>0.5 × min(plays, 3)</c> with NO recency factor, because the
    /// only date such a row carries is the sync, and decaying it would make the partition a function
    /// of when the account linked Trakt.
    /// </summary>
    [Fact]
    public void Member_weights_are_the_hand_computed_values()
    {
        var year = AsOf.AddDays(-365);

        // rating − mean + 1, recency 1.
        Assert.Equal(3.0d, TasteClusterer.WeightOf(Rated(1, Axis(0), 9), AsOf, 7m), 6);
        Assert.Equal(1.0d, TasteClusterer.WeightOf(Rated(2, Axis(0), 7), AsOf, 7m), 6);
        // The floor: a title rated below the user's own mean still counts, at 0.5.
        Assert.Equal(0.5d, TasteClusterer.WeightOf(Rated(3, Axis(0), 5), AsOf, 7m), 6);
        // One half-life back halves it — a weight, never a cutoff.
        Assert.Equal(1.5d, TasteClusterer.WeightOf(Rated(4, Axis(0), 9, year), AsOf, 7m), 6);

        // Rewatches: 0.5 per play, capped at three plays, no recency.
        Assert.Equal(1.0d, TasteClusterer.WeightOf(Rewatch(5, Axis(0), 2), AsOf, 7m), 6);
        Assert.Equal(1.5d, TasteClusterer.WeightOf(Rewatch(6, Axis(0), 3), AsOf, 7m), 6);
        Assert.Equal(1.5d, TasteClusterer.WeightOf(Rewatch(7, Axis(0), 9), AsOf, 7m), 6);
        Assert.Equal(1.5d, TasteClusterer.WeightOf(Rewatch(8, Axis(0), 7, year), AsOf, 7m), 6);
    }

    /// <summary>
    /// The budget split, exhaustively: for every k in 3..12 and every ANN budget in 0..500, the
    /// cluster targets plus the drift target must sum to EXACTLY the budget, and every served cluster
    /// must clear <see cref="CandidateRetriever.MinPerClusterPull"/>.
    ///
    /// <para>Exactness matters because the shortlist cut is sized against the pool the retriever was
    /// asked for; a floor-and-hope split silently under-fills it and a round-and-hope split
    /// over-fills it. The floor matters because a pocket served four slots is a pocket that was not
    /// served — which is multi-centroid retrieval degenerating back into one centroid.</para>
    /// </summary>
    [Fact]
    public void The_budget_split_is_exact_and_honours_the_floor_for_every_k_and_every_budget()
    {
        for (var k = TasteClusterer.MinK; k <= TasteClusterer.MaxK; k++)
        {
            // Steeply unequal weights — the shape a real account has, and the one a naive
            // proportional split starves the tail of.
            var weights = Enumerable.Range(0, k).Select(i => Math.Pow(0.6d, i)).ToList();

            for (var annCount = 0; annCount <= 500; annCount++)
            {
                var driftTarget = (int)Math.Ceiling(annCount * CandidateRetriever.DriftFraction);
                var clusterBudget = annCount - driftTarget;
                var targets = CandidateRetriever.SplitClusterBudget(weights, clusterBudget, CandidateRetriever.AllClusters);

                Assert.Equal(annCount, targets.Sum() + driftTarget);

                if (clusterBudget >= CandidateRetriever.MinPerClusterPull)
                {
                    Assert.All(targets, t => Assert.True(
                        t >= CandidateRetriever.MinPerClusterPull,
                        $"k={k} annCount={annCount} produced a pull of {t}"));
                }

                // Never more clusters than the floor affords, and never more than exist.
                Assert.True(targets.Count <= k);
            }
        }
    }

    /// <summary>The paginator's ceiling: three clusters, whatever the partition holds.</summary>
    [Fact]
    public void MaxClusters_caps_how_many_pockets_a_request_path_pulls_from()
    {
        var weights = Enumerable.Range(0, 9).Select(i => Math.Pow(0.7d, i)).ToList();
        var targets = CandidateRetriever.SplitClusterBudget(weights, 90, 3);

        Assert.Equal(3, targets.Count);
        Assert.Equal(90, targets.Sum());
        Assert.All(targets, t => Assert.True(t >= CandidateRetriever.MinPerClusterPull));
    }

    /// <summary>No partition, or no budget, means the caller takes today's exact single-centroid path.</summary>
    [Fact]
    public void No_clusters_or_no_budget_yields_no_cluster_arms()
    {
        Assert.Empty(CandidateRetriever.SplitClusterBudget([], 500, CandidateRetriever.AllClusters));
        Assert.Empty(CandidateRetriever.SplitClusterBudget([1d, 1d, 1d], 0, CandidateRetriever.AllClusters));
        Assert.Empty(CandidateRetriever.SplitClusterBudget([1d, 1d, 1d], 500, 0));
    }

    /// <summary>
    /// Under a comma-decimal culture the partition is byte-identical. Nothing here formats a number
    /// into a string, and this test is what keeps it that way: the day someone reaches for
    /// <c>ToString()</c> on a weight or a centroid component, this goes red rather than a persisted
    /// row quietly becoming unparseable on another machine.
    /// </summary>
    [Fact]
    public void A_de_DE_culture_scope_produces_the_identical_partition()
    {
        var inputs = Pockets();
        var invariant = Describe(TasteClusterer.Partition(inputs, Account, AsOf, 7m));

        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            Assert.Equal(invariant, Describe(TasteClusterer.Partition(inputs, Account, AsOf, 7m)));
            Assert.Equal(
                "tasteclusters:11111111-2222-3333-4444-555555555555:20260910",
                TasteClusterProvider.CacheKey(Account, AsOf));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    /// <summary>Nothing to cluster is an empty partition, not a crash and not a one-title centroid.</summary>
    [Fact]
    public void An_empty_or_tiny_history_produces_no_clusters()
    {
        Assert.Empty(TasteClusterer.Partition([], Account, AsOf, 7m));
        Assert.Empty(TasteClusterer.Partition([Rated(1, Axis(0), 9)], Account, AsOf, 7m));
    }

    /// <summary>
    /// The cap is applied by weight and then the survivors are re-sorted by id, so the trim cannot
    /// leak its own ordering into k-means++ initialisation.
    /// </summary>
    [Fact]
    public void The_input_cap_keeps_the_heaviest_members_and_stays_order_invariant()
    {
        var inputs = new List<TasteClusterer.Input>();
        for (var i = 0; i < TasteClusterer.MaxInputs + 500; i++)
        {
            // Alternating directions and a rating that rises with the index, so the trim has a real
            // preference and dropping the wrong end would move the centroids visibly.
            inputs.Add(Rated(i, Axis(i % 2 == 0 ? 0 : 7), (short)(i % 2 == 0 ? 7 : 10)));
        }

        var expected = Describe(TasteClusterer.Partition(inputs, Account, AsOf, 7m));
        Assert.Equal(expected, Describe(TasteClusterer.Partition(inputs.AsEnumerable().Reverse().ToList(), Account, AsOf, 7m)));
        Assert.Equal(expected, Describe(TasteClusterer.Partition(Rotate(inputs, 991), Account, AsOf, 7m)));
    }

    /// <summary>Two distinct pockets, one much heavier than the other — the shape B1 exists for.</summary>
    private static List<TasteClusterer.Input> Pockets()
    {
        var inputs = new List<TasteClusterer.Input>();
        for (var i = 0; i < 60; i++)
        {
            inputs.Add(Rated(i, Near(0, 0.02f * (i % 5)), (short)(8 + (i % 3))));
        }

        for (var i = 0; i < 8; i++)
        {
            inputs.Add(Rewatch(500 + i, Near(7, 0.03f * (i % 4)), 2 + (i % 3)));
        }

        for (var i = 0; i < 12; i++)
        {
            inputs.Add(Rated(900 + i, Near(19, 0.02f * (i % 3)), 7));
        }

        return inputs;
    }

    /// <summary>
    /// A partition rendered as a string, exactly enough of it that two runs cannot differ without
    /// this differing. Centroid components are rendered invariantly on purpose — the values under
    /// test must not be culture-sensitive, and comparing them as text is how that gets noticed.
    /// </summary>
    private static string Describe(IReadOnlyList<TasteClusterer.Cluster> clusters) =>
        string.Join(
            "|",
            clusters.Select(c => string.Create(
                CultureInfo.InvariantCulture,
                $"{c.Ordinal}:{c.Weight:F6}:{c.MemberCount}:{string.Join(",", c.ExemplarTitleIds)}:"
                + $"{string.Join(",", c.Centroid.Select(v => v.ToString("F6", CultureInfo.InvariantCulture)))}")));

    private static List<TasteClusterer.Input> Rotate(IReadOnlyList<TasteClusterer.Input> inputs, int by) =>
        inputs.Skip(by % inputs.Count).Concat(inputs.Take(by % inputs.Count)).ToList();

    private static TasteClusterer.Input Rated(int seed, float[] embedding, short rating, DateTime? at = null) =>
        new(Id(seed), embedding, rating, at ?? AsOf, 0);

    private static TasteClusterer.Input Rewatch(int seed, float[] embedding, int plays, DateTime? at = null) =>
        new(Id(seed), embedding, null, at ?? AsOf, plays);

    /// <summary>Deterministic ids so "ties resolve to the lowest id" is a testable statement.</summary>
    private static Guid Id(int seed) => new(seed, 0, 0, [0, 0, 0, 0, 0, 0, 0, 0]);

    private static float[] Axis(int axis)
    {
        var v = new float[Dim];
        v[axis] = 1f;
        return v;
    }

    private static float[] Near(int axis, float nudge)
    {
        var v = Axis(axis);
        v[(axis + 1) % Dim] = nudge;
        return v;
    }
}
