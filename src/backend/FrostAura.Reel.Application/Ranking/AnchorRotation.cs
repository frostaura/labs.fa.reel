namespace FrostAura.Reel.Application.Ranking;

/// <summary>
/// Chooses which loved titles anchor this build's "Because you loved …" rows.
///
/// The problem it solves: the anchors used to be the strict rating-first top of the loved set, so an
/// account with thousands of ratings had a top-10 that could not move and saw literally the same
/// three rows for ever (founder, 2026-08-07: <i>"why are the because you watched sections always
/// seemingly the same movies … Those never seem to change"</i>). One reserved drift slot was not
/// enough — it left the other two permanently pinned.
///
/// The rule here keeps the feed coherent while guaranteeing it moves:
/// <list type="number">
/// <item><b>Drift slot</b> — the loved title nearest the user's RECENT-taste centroid, so one row
/// always reflects where taste is now. Unchanged in spirit from the original single drift pick.</item>
/// <item><b>Rotating slots</b> — drawn from the wider, recency-weighted loved pool starting at an
/// offset derived from the build's own timestamp, so successive days walk to a different part of
/// the pool instead of re-reading the same head.</item>
/// </list>
///
/// The rotation guarantee is deliberately DAY-granular. The first cut of this fix rotated per
/// build (minute-grained offset, previous build's anchors held back), which answered the founder's
/// complaint by over-correcting: delta sync polls hot accounts every 5 minutes, every poll rebuilt
/// the feed, and the rows the user was reading changed 3×/hour in QA. The standing contract is
/// therefore <b>stable within a UTC day, guaranteed fresh across days</b> (FeedBuildPolicy owns the
/// build-mode half; user actions still change things immediately). Properties, both tested:
/// <list type="bullet">
/// <item><b>Different across days.</b> <see cref="OffsetFor"/> is constant inside a UTC day and
/// moves every day, and the caller holds back the PREVIOUS day's anchors (or, for a user-forced
/// refresh, the superseded snapshot's), so a new day — or a manual refresh — cannot re-deal the
/// rows it replaces while any unused loved title remains.</item>
/// <item><b>Stable within a build.</b> Selection is a pure function of its inputs — no
/// <c>Random</c>, no <c>DateTime.Now</c> read inside. The seed is passed in and is the snapshot's
/// own persisted <c>GeneratedAt</c>, so any build can be replayed exactly from what it stored.</item>
/// </list>
/// </summary>
public static class AnchorRotation
{
    /// <summary>
    /// The rotation offset for a build, derived from the timestamp the snapshot will persist.
    /// Days, not minutes or ticks: every build inside one UTC day reads the loved pool from the
    /// same starting point (stability), consecutive days start somewhere else (freshness), and the
    /// number stays legible and reproducible when replaying a build from its GeneratedAt.
    /// </summary>
    public static int OffsetFor(DateTime generatedAt) =>
        (int)((ulong)(generatedAt.Ticks / TimeSpan.TicksPerDay) % int.MaxValue);

    /// <summary>
    /// Picks up to <paramref name="count"/> anchor ids.
    /// </summary>
    /// <param name="driftPick">The recent-taste pick, or null when there is no recent-centroid signal.</param>
    /// <param name="anchorPool">The wider recency-weighted loved pool, best first.</param>
    /// <param name="allTimeOrder">The strict rating-first loved order — the last-resort backfill.</param>
    /// <param name="previousAnchors">Anchors the previous build used; avoided while alternatives exist.</param>
    /// <param name="rotationOffset">See <see cref="OffsetFor"/>.</param>
    /// <param name="count">How many anchors to return.</param>
    /// <param name="topTier">
    /// The titles rated 9–10. When given, every pass reads them FIRST — the drift pick included,
    /// which only takes its slot first when it is itself top-tier — and the 8s fill only what the
    /// top tier cannot. An 8 the user liked is weaker evidence of what they love than a 10, and the
    /// founder's rows were anchored on two 8s while 18 tens and 208 nines sat unused. Rotation and
    /// hold-back apply within each tier exactly as before, so the rows still turn over daily.
    /// Null keeps the single-tier behaviour.
    /// </param>
    public static List<Guid> Select(
        Guid? driftPick,
        IReadOnlyList<Guid> anchorPool,
        IReadOnlyList<Guid> allTimeOrder,
        IReadOnlySet<Guid> previousAnchors,
        int rotationOffset,
        int count,
        IReadOnlySet<Guid>? topTier = null)
    {
        if (topTier is { Count: > 0 })
        {
            // Two tiers, the top read first through the SAME rules: a tier-restricted drift pick,
            // pool and backfill each, then the rest. Recursion with topTier = null is the
            // single-tier rule, so the tiers cannot drift into two different definitions of it.
            bool Top(Guid id) => topTier.Contains(id);
            var chosenTop = Select(
                driftPick is { } d && Top(d) ? d : null,
                [.. anchorPool.Where(Top)],
                [.. allTimeOrder.Where(Top)],
                previousAnchors,
                rotationOffset,
                count);

            // The top tier first; a held-back top-tier anchor is only a last resort, behind every
            // fresh 8 — "different across days" outranks "rated 9+".
            var fresh = chosenTop.Where(id => !previousAnchors.Contains(id)).ToList();
            if (fresh.Count >= count)
            {
                return fresh;
            }

            var rest = Select(
                driftPick is { } d2 && !Top(d2) ? d2 : null,
                [.. anchorPool.Where(id => !Top(id))],
                [.. allTimeOrder.Where(id => !Top(id))],
                previousAnchors,
                rotationOffset,
                count);
            return fresh
                .Concat(rest.Where(id => !previousAnchors.Contains(id)))
                .Concat(chosenTop.Where(previousAnchors.Contains))
                .Concat(rest.Where(previousAnchors.Contains))
                .Distinct()
                .Take(count)
                .ToList();
        }

        var chosen = new List<Guid>(count);
        var taken = new HashSet<Guid>();

        void TryAdd(Guid id)
        {
            if (chosen.Count < count && taken.Add(id))
            {
                chosen.Add(id);
            }
        }

        // Rotate the pool so a build starts reading it at a different point than its predecessor.
        var rotated = Rotate(anchorPool, rotationOffset);

        // Pass 1 — prefer anchors the previous build did NOT use. This is what makes the rows
        // actually turn over; the drift pick goes first so the recent-taste row survives a thin pool.
        if (driftPick is { } drift && !previousAnchors.Contains(drift))
        {
            TryAdd(drift);
        }

        foreach (var id in rotated)
        {
            if (!previousAnchors.Contains(id))
            {
                TryAdd(id);
            }
        }

        // Pass 2 — the pool of unused anchors is exhausted (a user with very few loved titles), so
        // repeats are now the honest option: a repeated row beats a missing one.
        if (driftPick is { } fallbackDrift)
        {
            TryAdd(fallbackDrift);
        }

        foreach (var id in rotated)
        {
            TryAdd(id);
        }

        foreach (var id in allTimeOrder)
        {
            TryAdd(id);
        }

        return chosen;
    }

    /// <summary>
    /// How far the read head moves per unit of <see cref="OffsetFor"/>. A stride of 1 was not enough
    /// in practice: consecutive builds started one position apart, the "don't repeat the last build"
    /// rule pushed each one onto the next three entries, and the anchors settled into a two-set cycle
    /// that alternated for ever — better than frozen, but still not the variety the founder asked for.
    /// A prime stride is coprime with every pool size that is not a multiple of it, so successive
    /// builds land in genuinely different parts of the loved pool and walk all of it over time.
    /// </summary>
    private const int RotationStride = 7;

    /// <summary>
    /// The list read starting at a strided <paramref name="offset"/> and wrapping once. Public
    /// because the discovery seeds rotate on exactly the same contract as the anchors do — same
    /// day-granular offset, same prime stride — and two independent copies of "rotate a taste-ordered
    /// list" would drift into two different definitions of "fresh".
    /// </summary>
    public static List<T> Rotate<T>(IReadOnlyList<T> source, int offset)
    {
        if (source.Count == 0)
        {
            return [];
        }

        var start = (int)(Math.Abs((long)offset) * RotationStride % source.Count);
        var rotated = new List<T>(source.Count);
        for (var i = 0; i < source.Count; i++)
        {
            rotated.Add(source[(start + i) % source.Count]);
        }

        return rotated;
    }
}
