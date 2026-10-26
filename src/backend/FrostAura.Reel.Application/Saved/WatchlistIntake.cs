namespace FrostAura.Reel.Application.Saved;

/// <summary>What the watchlist ingest did with one Trakt watchlist entry.</summary>
public enum WatchlistDecision
{
    /// <summary>Became a live save, dated when it was listed on Trakt.</summary>
    Save,

    /// <summary>Already a live save — usually Reel's own write, which put it on the watchlist.</summary>
    AlreadySaved,

    /// <summary>
    /// Reel's own write, not the user's intent: a watchlist write-back for this title has not reached
    /// Trakt yet, or Reel revoked the save after the entry was listed (the user un-saved, or watched
    /// or rated it, and the removal is queued, in flight or gave up).
    /// </summary>
    ReelsOwnWrite,

    /// <summary>Fully watched or rated at or after it was listed — the same rule as a consumed save.</summary>
    Consumed,

    /// <summary>The user turned it down in Reel (not interested, or dropped); that answer stands.</summary>
    Declined,
}

/// <summary>Everything Reel knows locally about one watchlisted title, gathered by the ingest.</summary>
/// <param name="HasLiveSave">A live <c>SaveForLater</c> exists.</param>
/// <param name="HasLiveDecline">A live <c>NotInterested</c> or <c>Dropped</c> exists.</param>
/// <param name="LatestSaveRevokedAt">When Reel last revoked a save of this title, if ever.</param>
/// <param name="WatchlistWriteInFlight">An AddToWatchlist or RemoveFromWatchlist write-back is Pending or Failed (retrying).</param>
/// <param name="LastConsumedAt">The latest full watch or rating of the title, if any.</param>
public readonly record struct WatchlistFacts(
    bool HasLiveSave,
    bool HasLiveDecline,
    DateTime? LatestSaveRevokedAt,
    bool WatchlistWriteInFlight,
    DateTime? LastConsumedAt);

/// <summary>
/// Whether a Trakt watchlist entry becomes a Saved item (N-5).
///
/// <para><b>Why.</b> The watchlist is the strongest intent the user expresses outside Reel, and Reel
/// only ever wrote to it. The 2026-06-19 lock puts a watchlisted title on Saved, never back in the
/// picks, so the ingest writes a save and nothing else — Saved is already an exclusion on every
/// suggestion surface.</para>
///
/// <para><b>Reel's own writes are not intent.</b> Every Reel save is also written to the watchlist,
/// so the watchlist echoes Reel. An echo of a live save changes nothing; an echo that outlived its
/// save — un-saved, watched or rated in Reel while the removal was still queued, or after it gave up —
/// must not resurrect it. The rule is temporal: an entry listed <i>before</i> Reel's latest
/// revocation is Reel's leftover; one listed after it is the user putting it back.</para>
///
/// <para><b>Read only.</b> The ingest writes nothing to Trakt: the title is already on the watchlist,
/// and a sync that pushed every watchlisted title onto "Reel — Up Next" would be a write burst the
/// user never asked for. Un-saving one in Reel removes it from the watchlist as any save does.</para>
/// </summary>
public static class WatchlistIntake
{
    public static WatchlistDecision Decide(DateTime listedAt, WatchlistFacts facts)
    {
        if (facts.HasLiveSave)
        {
            return WatchlistDecision.AlreadySaved;
        }

        if (facts.WatchlistWriteInFlight
            || (facts.LatestSaveRevokedAt is { } revokedAt && revokedAt >= listedAt))
        {
            return WatchlistDecision.ReelsOwnWrite;
        }

        if (facts.LastConsumedAt is { } consumedAt && consumedAt >= listedAt)
        {
            return WatchlistDecision.Consumed;
        }

        return facts.HasLiveDecline ? WatchlistDecision.Declined : WatchlistDecision.Save;
    }
}
