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
/// offset derived from the build's own timestamp, so consecutive builds walk to a different part of
/// the pool instead of re-reading the same head.</item>
/// </list>
///
/// Two properties matter and are both tested:
/// <list type="bullet">
/// <item><b>Different between builds.</b> Anchors used by the PREVIOUS build are held back while any
/// unused loved title remains, so two consecutive builds over identical ratings cannot produce the
/// same anchor set. This is the guarantee; the timestamp offset only adds variety on top of it.</item>
/// <item><b>Stable within a build.</b> Selection is a pure function of its inputs — no
/// <c>Random</c>, no <c>DateTime.Now</c> read inside. The seed is passed in and is the snapshot's
/// own persisted <c>GeneratedAt</c>, so any build can be replayed exactly from what it stored.</item>
/// </list>
/// </summary>
public static class AnchorRotation
{
    /// <summary>
    /// The rotation offset for a build, derived from the timestamp the snapshot will persist.
    /// Minutes, not ticks: fine enough that ordinary rebuilds land on different offsets, coarse
    /// enough to stay a legible, reproducible number when replaying a build from its GeneratedAt.
    /// </summary>
    public static int OffsetFor(DateTime generatedAt) =>
        (int)(generatedAt.Ticks / TimeSpan.TicksPerMinute & int.MaxValue);

    /// <summary>
    /// Picks up to <paramref name="count"/> anchor ids.
    /// </summary>
    /// <param name="driftPick">The recent-taste pick, or null when there is no recent-centroid signal.</param>
    /// <param name="anchorPool">The wider recency-weighted loved pool, best first.</param>
    /// <param name="allTimeOrder">The strict rating-first loved order — the last-resort backfill.</param>
    /// <param name="previousAnchors">Anchors the previous build used; avoided while alternatives exist.</param>
    /// <param name="rotationOffset">See <see cref="OffsetFor"/>.</param>
    public static List<Guid> Select(
        Guid? driftPick,
        IReadOnlyList<Guid> anchorPool,
        IReadOnlyList<Guid> allTimeOrder,
        IReadOnlySet<Guid> previousAnchors,
        int rotationOffset,
        int count)
    {
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

    /// <summary>The list read starting at a strided <paramref name="offset"/> and wrapping once.</summary>
    private static List<Guid> Rotate(IReadOnlyList<Guid> source, int offset)
    {
        if (source.Count == 0)
        {
            return [];
        }

        var start = (int)((long)offset * RotationStride % source.Count);
        var rotated = new List<Guid>(source.Count);
        for (var i = 0; i < source.Count; i++)
        {
            rotated.Add(source[(start + i) % source.Count]);
        }

        return rotated;
    }
}
