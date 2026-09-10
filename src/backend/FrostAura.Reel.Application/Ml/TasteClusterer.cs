using System.Globalization;
using System.Numerics;

namespace FrostAura.Reel.Application.Ml;

/// <summary>
/// Spherical k-means over the whole of one account's taste — the fix for COMPRESSION, which is a
/// different failure from the one the founder diagnosed and the one the numbers actually show.
///
/// <para><b>The measurement that justifies this class</b> (live database, 2026-09-10). Nothing is
/// windowed away: <see cref="FeatureVectorBuilder.BuildTasteStateAsync"/> reads every rating ever
/// made. But all 652 of the founder's loved titles are averaged into ONE mean vector, and a
/// leave-one-out test of that single centroid says what that costs: of those 652 loved titles, only
/// <b>93 (14%)</b> rank inside the centroid's top-500 among the 4,963 embedded titles; 218 (33%)
/// inside the top-1,200; 486 (75%) inside the top-3,000. At <c>FirstPassAnnPool = 500</c> the
/// centroid arm therefore cannot reach 86% of the founder's own loves — not because they were
/// filtered out, but because the mean of holiday comedies and <i>Ad Astra</i> points at neither.
/// <see cref="Domain.Feed.FeedBuildStat.ShortlistP90"/> was checked for the competing explanation
/// and does not support it (5.85 → 6.50 while the pool grew 512 → 2,242), so this is compression,
/// not reservoir exhaustion.</para>
///
/// <para><b>Pure and I/O-free, like <see cref="TasteMath"/>.</b> Everything here is a deterministic
/// function of its inputs, so the arithmetic that decides which pockets of taste get their own
/// retrieval budget is unit-testable without a database — and, more importantly, is REPLAYABLE: two
/// builds over the same history must retrieve the same pool or the feed's no-op suppression stops
/// working. That is why the input is ordered by <c>TitleId</c> before anything runs (Postgres row
/// order would otherwise change k-means++ initialisation and therefore the partition), why the RNG
/// is a local FNV-seeded generator rather than <see cref="Random"/> (whose sequence is explicitly
/// not stable across .NET versions), and why every tie resolves to the lowest id.</para>
///
/// <para><b>What it is not.</b> A cluster is a RETRIEVAL budget, never a feature: a partition of the
/// user's own labels cannot be leave-one-out'd, so feeding cluster membership to the model would be
/// an unbounded, unmeasurable leak. Nothing here is read by <see cref="FeatureVectorBuilder"/>.</para>
/// </summary>
public static class TasteClusterer
{
    /// <summary>
    /// The most inputs one clustering pass will consider, trimmed by weight (rating × recency).
    /// Spherical k-means is O(n·k·d) per iteration and d is 1536; at 2,000 × 12 × 1536 × 20 that is
    /// ~740 M multiply-adds, which is the ceiling worth paying inside a feed build.
    /// </summary>
    public const int MaxInputs = 2000;

    /// <summary>Fewest clusters the k rule may ask for once the input set is big enough to sustain them.</summary>
    public const int MinK = 3;

    /// <summary>
    /// Most clusters the k rule may ask for. Twelve × <c>MinPerClusterPull</c> is already 300 of the
    /// 500-slot ANN budget, so a larger k would not be able to give each pocket a meaningful pull.
    /// </summary>
    public const int MaxK = 12;

    /// <summary>Lloyd iterations. Assignments almost always settle in well under ten; this is the guard.</summary>
    public const int MaxIterations = 20;

    /// <summary>
    /// Members a cluster needs to survive. Below this it is a weekend, not a taste, and giving it a
    /// <c>MinPerClusterPull</c> slice of the ANN budget would spend real retrieval on noise.
    /// </summary>
    public const int MinClusterMembers = 3;

    /// <summary>
    /// Cosine above which two centroids are the same direction and are merged. At 1536 dims two
    /// genuinely different pockets sit far below this; 0.98 only catches a split that k-means made
    /// because k was too large for the data, which is exactly the degeneracy to fold back.
    /// </summary>
    public const double MergeCosine = 0.98d;

    /// <summary>Titles named per cluster, nearest the centroid — the evidence a human reads it by.</summary>
    public const int ExemplarCount = 3;

    /// <summary>
    /// Weight floor for a rated member. A title rated at or below the user's own mean still says
    /// something about where their attention goes; a zero or negative weight would silently delete
    /// it from the partition it belongs to.
    /// </summary>
    public const double MinRatedWeight = 0.5d;

    /// <summary>Plays beyond this stop adding weight — a rewatch is evidence, a marathon is not more evidence.</summary>
    public const int MaxCountedPlays = 3;

    /// <summary>Per-play weight of an UNRATED rewatch. Deliberately small: it is a weight, never a pseudo-rating.</summary>
    public const double RewatchPlayWeight = 0.5d;

    /// <summary>
    /// One title the account has demonstrated taste on, with its L2-normalisable embedding.
    ///
    /// <para><paramref name="Rating"/> null means the title reaches this set through
    /// <c>WatchedTitles.Plays &gt;= 2</c> alone — a REWATCH, which is behaviour rather than a label.
    /// It is weighted as behaviour (see <see cref="RewatchPlayWeight"/>) and never converted into a
    /// pretend rating: inventing a label would change what every downstream number means.</para>
    /// </summary>
    public readonly record struct Input(Guid TitleId, float[] Embedding, short? Rating, DateTime At, int Plays);

    /// <summary>
    /// One pocket of taste: where it points, how much of the account's attention it holds, how many
    /// titles voted for it, and three titles a human can recognise it by.
    /// <para><see cref="Ordinal"/> is the canonical rank — weight descending, then lowest member id —
    /// and is what the persisted row, the retrieval budget split and the probe all key on.</para>
    /// </summary>
    public sealed record Cluster(
        int Ordinal,
        float[] Centroid,
        double Weight,
        int MemberCount,
        IReadOnlyList<Guid> ExemplarTitleIds);

    /// <summary>
    /// The number of clusters for <paramref name="n"/> inputs: <c>clamp(round(sqrt(n/10)), 3, 12)</c>,
    /// then <c>min(k, n/20)</c>.
    ///
    /// <para>The second half is the one that matters and it is not decoration. The first half alone
    /// asks for three clusters at n = 1, which k-means will happily produce and which would hand a
    /// third of the ANN budget to a partition built from a single title. Integer division is
    /// deliberate: it makes the bar a whole number of titles per cluster, so the boundary cannot sit
    /// between two counts. Below 60 inputs it yields k &lt; 3 — including k = 1, which is exactly
    /// today's single-centroid behaviour and the honest answer for a thin history.</para>
    /// </summary>
    public static int ClusterCountFor(int n) =>
        n <= 0 ? 0 : Math.Min(Math.Clamp((int)Math.Round(Math.Sqrt(n / 10d), MidpointRounding.AwayFromZero), MinK, MaxK), n / 20);

    /// <summary>
    /// Partitions <paramref name="inputs"/> into taste clusters, canonically ordered.
    ///
    /// <para>Deterministic in every respect: same inputs (in ANY order), same account, same asOf ⇒
    /// byte-identical output. <paramref name="accountId"/> only seeds the initialisation, so two
    /// accounts with identical histories get identically-shaped partitions from different starts —
    /// which is the point of seeding at all, and why the seed must not be the clock.</para>
    /// </summary>
    /// <param name="userMean">The account's own mean rating — the zero point the weights are measured from.</param>
    public static IReadOnlyList<Cluster> Partition(
        IReadOnlyList<Input> inputs, Guid accountId, DateTime asOf, decimal userMean)
    {
        ArgumentNullException.ThrowIfNull(inputs);

        // ── Canonicalise before anything reads the order ────────────────────────────────────
        // The caller hands us rows in whatever order Postgres produced. k-means++ picks its first
        // centre by index, so an unordered input silently makes the partition a function of the
        // query plan. Sort by TitleId first, trim by weight second, then sort by TitleId AGAIN so
        // the trim itself cannot leak its own ordering into initialisation.
        var prepared = inputs
            .Where(i => i.Embedding is { Length: > 0 })
            .GroupBy(i => i.TitleId)
            .Select(g => g.OrderByDescending(i => i.Rating ?? -1).ThenByDescending(i => i.Plays).First())
            .Select(i => new Member(i.TitleId, Normalise(i.Embedding), WeightOf(i, asOf, userMean)))
            .OrderBy(m => m.TitleId)
            .ToList();

        if (prepared.Count > MaxInputs)
        {
            prepared = prepared
                .OrderByDescending(m => m.Weight)
                .ThenBy(m => m.TitleId)
                .Take(MaxInputs)
                .OrderBy(m => m.TitleId)
                .ToList();
        }

        var k = ClusterCountFor(prepared.Count);
        if (k <= 0)
        {
            return [];
        }

        var dimension = prepared[0].Vector.Length;
        // A mixed-dimension input set cannot be clustered and must not be silently half-clustered:
        // drop the minority so the centroid arithmetic below is total. In practice every row comes
        // from the same 1536-dim provider, so this never fires outside a test.
        prepared = prepared.Where(m => m.Vector.Length == dimension).ToList();
        k = ClusterCountFor(prepared.Count);
        if (k <= 0)
        {
            return [];
        }

        var centroids = SeedCentroids(prepared, k, accountId);
        var assignment = new int[prepared.Count];
        Array.Fill(assignment, -1);

        for (var iteration = 0; iteration < MaxIterations; iteration++)
        {
            var moved = false;
            for (var i = 0; i < prepared.Count; i++)
            {
                var best = NearestCentroid(prepared[i].Vector, centroids);
                if (assignment[i] != best)
                {
                    assignment[i] = best;
                    moved = true;
                }
            }

            // Recompute, then reseed anything that emptied. An empty cluster left in place makes k
            // a lie — the budget split would hand a slice to a direction no title voted for.
            RecomputeCentroids(prepared, assignment, centroids, dimension);
            moved |= ReseedEmptyClusters(prepared, assignment, centroids);

            if (!moved)
            {
                break;
            }
        }

        return Finalise(prepared, assignment, centroids);
    }

    /// <summary>
    /// One member's contribution to its cluster's weight.
    ///
    /// <para>A RATED title contributes <c>max(0.5, rating − userMean + 1) × RecencyWeight</c>: the
    /// same 365-day half-life every other taste estimate uses, over a rating expressed relative to
    /// the user's own scale, so a 7 from someone who averages 5 counts like a 9 from someone who
    /// averages 7. An UNRATED rewatch contributes <c>0.5 × min(plays, 3)</c> and takes no recency
    /// factor, because the only date such a row carries is <c>FirstSyncedAt</c> — decaying it would
    /// be decaying the INGEST, not the watching, and would make an account's clusters a function of
    /// when it linked Trakt.</para>
    /// </summary>
    public static double WeightOf(Input input, DateTime asOf, decimal userMean) =>
        input.Rating is { } rating
            ? Math.Max(MinRatedWeight, rating - (double)userMean + 1d) * TasteMath.RecencyWeight(input.At, asOf)
            : RewatchPlayWeight * Math.Min(Math.Max(input.Plays, 0), MaxCountedPlays);

    /// <summary>
    /// Cosine similarity of two ALREADY-NORMALISED vectors, i.e. their dot product, hardware-widened
    /// through <see cref="Vector{T}"/>. In-box SIMD on purpose: <c>System.Numerics.Tensors</c> would
    /// buy a few percent and cost a package on a hot path that is already fast enough.
    /// </summary>
    public static double Dot(float[] a, float[] b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        if (a.Length != b.Length)
        {
            return 0d;
        }

        var width = Vector<float>.Count;
        var accumulator = Vector<float>.Zero;
        var i = 0;
        for (; i <= a.Length - width; i += width)
        {
            accumulator += new Vector<float>(a, i) * new Vector<float>(b, i);
        }

        var total = (double)Vector.Sum(accumulator);
        for (; i < a.Length; i++)
        {
            total += (double)a[i] * b[i];
        }

        return total;
    }

    /// <summary>A copy of <paramref name="vector"/> scaled to unit length; the zero vector is returned as-is.</summary>
    public static float[] Normalise(float[] vector)
    {
        ArgumentNullException.ThrowIfNull(vector);
        var copy = (float[])vector.Clone();
        NormaliseInPlace(copy);
        return copy;
    }

    private static void NormaliseInPlace(float[] vector)
    {
        var sum = 0d;
        foreach (var value in vector)
        {
            sum += (double)value * value;
        }

        if (sum <= 0d)
        {
            return;
        }

        var norm = (float)Math.Sqrt(sum);
        for (var i = 0; i < vector.Length; i++)
        {
            vector[i] /= norm;
        }
    }

    /// <summary>
    /// k-means++ over cosine distance, seeded from an FNV-1a hash of the account id — the same
    /// construction as <see cref="TasteMath.StableBucket"/>, and for the same reason: string hashing
    /// is randomised per process, so anything derived from it would reshuffle the partition on every
    /// restart. The PRNG is local rather than <see cref="Random"/> because the framework does not
    /// promise a stable sequence for a given seed across versions, and a partition that changes on a
    /// runtime upgrade is a replayability bug nobody would look for.
    /// </summary>
    private static List<float[]> SeedCentroids(List<Member> members, int k, Guid accountId)
    {
        var rng = new StableRandom(TasteMath.StableBucket(accountId.ToString("N", CultureInfo.InvariantCulture), int.MaxValue));
        var centroids = new List<float[]>(k)
        {
            (float[])members[rng.Next(members.Count)].Vector.Clone(),
        };

        // Squared cosine distance to the nearest chosen centre, maintained incrementally.
        var distances = new double[members.Count];
        for (var i = 0; i < members.Count; i++)
        {
            distances[i] = SquaredDistance(members[i].Vector, centroids[0]);
        }

        while (centroids.Count < k)
        {
            var total = distances.Sum();
            int chosen;
            if (total <= 0d)
            {
                // Every remaining point coincides with a chosen centre. Take the lowest id not yet
                // used rather than an arbitrary one, so degeneracy stays deterministic.
                chosen = FirstUnchosen(members, centroids);
            }
            else
            {
                var target = rng.NextDouble() * total;
                var running = 0d;
                chosen = members.Count - 1;
                for (var i = 0; i < members.Count; i++)
                {
                    running += distances[i];
                    if (running >= target)
                    {
                        chosen = i;
                        break;
                    }
                }
            }

            centroids.Add((float[])members[chosen].Vector.Clone());
            for (var i = 0; i < members.Count; i++)
            {
                distances[i] = Math.Min(distances[i], SquaredDistance(members[i].Vector, centroids[^1]));
            }
        }

        return centroids;
    }

    private static int FirstUnchosen(List<Member> members, List<float[]> centroids)
    {
        for (var i = 0; i < members.Count; i++)
        {
            if (!centroids.Any(c => SquaredDistance(members[i].Vector, c) <= 0d))
            {
                return i;
            }
        }

        return 0;
    }

    /// <summary>Squared Euclidean distance between unit vectors — <c>2(1 − cos)</c>, so it ranks identically to cosine.</summary>
    private static double SquaredDistance(float[] a, float[] b) => Math.Max(0d, 2d * (1d - Dot(a, b)));

    /// <summary>
    /// The index of the nearest centroid by cosine. Strictly-greater comparison is the tie rule:
    /// a point equidistant from two centroids joins the LOWER-indexed one, so ties resolve the same
    /// way on every machine.
    /// </summary>
    private static int NearestCentroid(float[] vector, List<float[]> centroids)
    {
        var best = 0;
        var bestScore = double.NegativeInfinity;
        for (var c = 0; c < centroids.Count; c++)
        {
            var score = Dot(vector, centroids[c]);
            if (score > bestScore)
            {
                bestScore = score;
                best = c;
            }
        }

        return best;
    }

    private static void RecomputeCentroids(List<Member> members, int[] assignment, List<float[]> centroids, int dimension)
    {
        var sums = new double[centroids.Count][];
        for (var c = 0; c < centroids.Count; c++)
        {
            sums[c] = new double[dimension];
        }

        var counts = new int[centroids.Count];
        for (var i = 0; i < members.Count; i++)
        {
            var c = assignment[i];
            counts[c]++;
            var vector = members[i].Vector;
            for (var d = 0; d < dimension; d++)
            {
                sums[c][d] += vector[d];
            }
        }

        for (var c = 0; c < centroids.Count; c++)
        {
            if (counts[c] == 0)
            {
                continue;
            }

            var centroid = new float[dimension];
            for (var d = 0; d < dimension; d++)
            {
                centroid[d] = (float)(sums[c][d] / counts[c]);
            }

            NormaliseInPlace(centroid);
            centroids[c] = centroid;
        }
    }

    /// <summary>
    /// Moves every empty cluster onto the point currently WORST served by its own centroid, which is
    /// the standard k-means repair and the one that keeps k honest. The choice is deterministic —
    /// lowest similarity, then lowest id — and a point is never handed to two reseeds in one pass.
    /// </summary>
    private static bool ReseedEmptyClusters(List<Member> members, int[] assignment, List<float[]> centroids)
    {
        var counts = new int[centroids.Count];
        foreach (var c in assignment)
        {
            counts[c]++;
        }

        var reseeded = false;
        var taken = new HashSet<int>();
        for (var c = 0; c < centroids.Count; c++)
        {
            if (counts[c] > 0)
            {
                continue;
            }

            var worst = -1;
            var worstScore = double.PositiveInfinity;
            for (var i = 0; i < members.Count; i++)
            {
                // Never strand a cluster by emptying another one to fill it.
                if (taken.Contains(i) || counts[assignment[i]] <= 1)
                {
                    continue;
                }

                var score = Dot(members[i].Vector, centroids[assignment[i]]);
                if (score < worstScore)
                {
                    worstScore = score;
                    worst = i;
                }
            }

            if (worst < 0)
            {
                continue;
            }

            counts[assignment[worst]]--;
            assignment[worst] = c;
            counts[c]++;
            centroids[c] = (float[])members[worst].Vector.Clone();
            taken.Add(worst);
            reseeded = true;
        }

        return reseeded;
    }

    /// <summary>
    /// Merges the degenerate clusters, then emits the canonical order.
    ///
    /// <para>Two merge rules, in this order. <b>Duplicated directions</b> — two centroids with cosine
    /// above <see cref="MergeCosine"/> are the same pocket found twice, and keeping both would split
    /// one taste's retrieval budget in half. <b>Thin clusters</b> — fewer than
    /// <see cref="MinClusterMembers"/> members is a weekend, and it is folded into whichever
    /// surviving cluster it actually points at. Both are done by absorbing the smaller into the
    /// larger (ties → lowest member id) so the result cannot depend on iteration order.</para>
    /// </summary>
    private static List<Cluster> Finalise(List<Member> members, int[] assignment, List<float[]> centroids)
    {
        var groups = new List<List<int>>();
        for (var c = 0; c < centroids.Count; c++)
        {
            groups.Add([]);
        }

        for (var i = 0; i < members.Count; i++)
        {
            groups[assignment[i]].Add(i);
        }

        var live = Enumerable.Range(0, centroids.Count).Where(c => groups[c].Count > 0).ToList();

        // ── Rule 1: duplicated directions ───────────────────────────────────────────────────
        bool merged;
        do
        {
            merged = false;
            for (var a = 0; a < live.Count && !merged; a++)
            {
                for (var b = a + 1; b < live.Count && !merged; b++)
                {
                    if (Dot(centroids[live[a]], centroids[live[b]]) <= MergeCosine)
                    {
                        continue;
                    }

                    var (keep, drop) = Absorb(live[a], live[b], groups, members);
                    groups[keep].AddRange(groups[drop]);
                    groups[drop].Clear();
                    centroids[keep] = MeanOf(groups[keep], members);
                    live.Remove(drop);
                    merged = true;
                }
            }
        }
        while (merged);

        // ── Rule 2: thin clusters ───────────────────────────────────────────────────────────
        // Ordered smallest-first so a chain of thin clusters collapses the same way every time.
        foreach (var thin in live
            .Where(c => groups[c].Count < MinClusterMembers)
            .OrderBy(c => groups[c].Count)
            .ThenBy(c => LowestId(groups[c], members))
            .ToList())
        {
            var host = live
                .Where(c => c != thin && groups[c].Count >= MinClusterMembers)
                .OrderByDescending(c => Dot(centroids[c], centroids[thin]))
                .ThenBy(c => LowestId(groups[c], members))
                .FirstOrDefault(-1);

            // Everything is thin — there is no host to fold into, so the partition stands as it is
            // rather than collapsing to nothing. The MinClusterInputs gate makes this unreachable in
            // production; it is here so the function is total.
            if (host < 0)
            {
                break;
            }

            groups[host].AddRange(groups[thin]);
            groups[thin].Clear();
            centroids[host] = MeanOf(groups[host], members);
            live.Remove(thin);
        }

        return live
            .Select(c => new
            {
                Centroid = centroids[c],
                Weight = groups[c].Sum(i => members[i].Weight),
                Members = groups[c],
            })
            .OrderByDescending(x => x.Weight)
            .ThenBy(x => LowestId(x.Members, members))
            .Select((x, ordinal) => new Cluster(
                ordinal,
                x.Centroid,
                x.Weight,
                x.Members.Count,
                x.Members
                    .OrderByDescending(i => Dot(members[i].Vector, x.Centroid))
                    .ThenBy(i => members[i].TitleId)
                    .Take(ExemplarCount)
                    .Select(i => members[i].TitleId)
                    .ToList()))
            .ToList();
    }

    /// <summary>Which of two clusters keeps the members: the larger, then the one holding the lowest id.</summary>
    private static (int Keep, int Drop) Absorb(int a, int b, List<List<int>> groups, List<Member> members)
    {
        if (groups[a].Count != groups[b].Count)
        {
            return groups[a].Count > groups[b].Count ? (a, b) : (b, a);
        }

        return LowestId(groups[a], members).CompareTo(LowestId(groups[b], members)) <= 0 ? (a, b) : (b, a);
    }

    private static Guid LowestId(List<int> group, List<Member> members) =>
        group.Count == 0 ? Guid.Empty : group.Min(i => members[i].TitleId);

    private static float[] MeanOf(List<int> group, List<Member> members)
    {
        var dimension = members[group[0]].Vector.Length;
        var sums = new double[dimension];
        foreach (var i in group)
        {
            var vector = members[i].Vector;
            for (var d = 0; d < dimension; d++)
            {
                sums[d] += vector[d];
            }
        }

        var centroid = new float[dimension];
        for (var d = 0; d < dimension; d++)
        {
            centroid[d] = (float)(sums[d] / group.Count);
        }

        NormaliseInPlace(centroid);
        return centroid;
    }

    private readonly record struct Member(Guid TitleId, float[] Vector, double Weight);

    /// <summary>
    /// A 64-bit xorshift* generator. Small, and — unlike <see cref="Random"/> — contractually stable:
    /// the same seed produces the same stream on every runtime and every machine, which is the whole
    /// requirement. Never use this for anything security-adjacent.
    /// </summary>
    private sealed class StableRandom(int seed)
    {
        private ulong _state = (ulong)seed ^ 0x9E3779B97F4A7C15UL;

        public int Next(int exclusiveMax) => exclusiveMax <= 1 ? 0 : (int)(NextUlong() % (ulong)exclusiveMax);

        public double NextDouble() => (NextUlong() >> 11) * (1.0d / 9007199254740992.0d);

        private ulong NextUlong()
        {
            _state ^= _state >> 12;
            _state ^= _state << 25;
            _state ^= _state >> 27;
            return _state * 0x2545F4914F6CDD1DUL;
        }
    }
}
