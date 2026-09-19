using System.Globalization;

namespace FrostAura.Reel.Application.Sync;

/// <summary>
/// What one delta sync actually did, in the terms the user cares about.
///
/// <para>Every field here is a number the pipeline already had and threw away. The counts the sync
/// used to report were "rows walked from Trakt" — a full paginated refetch of the whole library, so
/// rendering them would have read "Pulled 3,412 new ratings" after every single sync, forever. These
/// are the rows that were genuinely new or genuinely different.</para>
///
/// <para><see cref="PicksChanged"/> is nullable because it is not knowable yet at the moment the sync
/// finishes: the rebuild is enqueued asynchronously (the sync must never block on it), so the sync
/// reports what it ingested and the build's own <c>feed-ready</c> frame reports what that did to the
/// page. Null means "not yet", never "zero".</para>
/// </summary>
public readonly record struct SyncSummary(
    int RatingsAdded,
    int RatingsChanged,
    int WatchedAdded,
    bool RetrainQueued,
    DateTime? RetrainAt,
    int? PicksChanged)
{
    /// <summary>Nothing arrived at all — the "Already up to date" case.</summary>
    public bool NothingArrived => RatingsAdded == 0 && RatingsChanged == 0 && WatchedAdded == 0;
}

/// <summary>
/// The post-sync sentence. Rendered on the SERVER as well as the client, and from the same rules,
/// because the header pill has two independent sources: the live SSE frame and the 5-second poll of
/// the persisted <c>ProgressMessage</c>. Two renderers meant the user could watch the sentence
/// change under them when the poll overtook the frame.
///
/// <para>Mirrored in <c>src/frontend/src/lib/syncCopy.ts</c> (<c>syncSummaryCopy</c>), which renders
/// the same shape from the SSE payload. Change one, change the other — both are covered by tests
/// asserting the same three example sentences.</para>
/// </summary>
public static class SyncSummaryCopy
{
    /// <summary>The idle sentence when a sync found nothing at all.</summary>
    public const string UpToDate = "Already up to date";

    /// <summary>The whole sentence: what arrived, then the one consequence worth naming.</summary>
    public static string Describe(SyncSummary summary)
    {
        var lead = Lead(summary);
        if (summary.NothingArrived)
        {
            // "Already up to date — retraining tonight" is still worth saying: the model is moving
            // even when the library is not.
            var idleTail = Tail(summary);
            return idleTail is null ? lead : $"{lead} — {idleTail}";
        }

        var tail = Tail(summary);
        return tail is null ? lead : $"{lead} — {tail}";
    }

    /// <summary>
    /// Invariant culture, deliberately: this sentence is user copy, and a server whose locale groups
    /// with a space or a dot would quote the user a number they cannot read back. The same call the
    /// feed's degraded-reason banner already makes, for the same reason.
    /// </summary>
    private static string Count(int value) => value.ToString("N0", CultureInfo.InvariantCulture);

    private static string Lead(SyncSummary summary)
    {
        if (summary.RatingsAdded > 0)
        {
            return $"Pulled {Count(summary.RatingsAdded)} new {Plural(summary.RatingsAdded, "rating")} from Trakt";
        }

        if (summary.RatingsChanged > 0)
        {
            return $"Updated {Count(summary.RatingsChanged)} {Plural(summary.RatingsChanged, "rating")} from Trakt";
        }

        if (summary.WatchedAdded > 0)
        {
            return $"Pulled {Count(summary.WatchedAdded)} new {Plural(summary.WatchedAdded, "watch", "watches")} from Trakt";
        }

        return UpToDate;
    }

    private static string? Tail(SyncSummary summary)
    {
        // A concrete change to the page outranks a promise about tonight: "3 picks changed" is
        // something the user can go and look at.
        if (summary.PicksChanged is > 0)
        {
            return $"{Count(summary.PicksChanged.Value)} {Plural(summary.PicksChanged.Value, "pick")} changed";
        }

        if (summary.RetrainQueued)
        {
            return summary.RetrainAt is { } at
                ? $"retraining tonight at {at.ToString("HH:mm", CultureInfo.InvariantCulture)} UTC"
                : "retraining now";
        }

        return null;
    }

    private static string Plural(int count, string singular, string? plural = null) =>
        count == 1 ? singular : plural ?? singular + "s";
}
