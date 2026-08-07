namespace FrostAura.Reel.Application.Ranking;

/// <summary>How a feed build treats what is already on the page.</summary>
public enum FeedBuildMode
{
    /// <summary>Deal a new feed: first build, new UTC day, new model, or a user-forced refresh.</summary>
    Fresh,

    /// <summary>Keep tonight's contract: reuse the Active snapshot's hero and anchors, refill only
    /// the slots that user actions vacated, and suppress the write entirely when nothing moved.</summary>
    Preserve,
}

/// <summary>
/// The stable-"tonight" contract, decided in one pure function so the day semantics are unit-testable
/// without a clock abstraction (the handler reads UtcNow once and passes it in).
///
/// Two founder complaints pull in opposite directions and both must hold. Rows that never changed
/// (2026-08-07: "those never seem to change") demanded guaranteed rotation; the AnchorRotation fix
/// then over-corrected — delta sync polls hot accounts every 5 minutes, each poll rebuilt the feed,
/// and the hero changed 3×/hour in QA. The contract that satisfies both: the feed is <b>stable
/// within a UTC day</b> and <b>guaranteed fresh across days</b>, while explicit user actions
/// (reacting to a title, a manual refresh, a filter change) still change things immediately —
/// reactions vacate their own slots inside a Preserve build, refresh/filters force a Fresh one.
/// </summary>
public static class FeedBuildPolicy
{
    /// <summary>The feed endpoint's POST /rebuild — the user pressed the button, so rotate visibly.</summary>
    public const string ManualRefreshReason = "manual-refresh";

    /// <summary>FilterEndpoints' save path — the user changed what may appear, so re-deal under the new rules.</summary>
    public const string FiltersChangedReason = "content filters changed";

    /// <summary>Reasons that mean the USER asked for a different page, not that the data drifted.</summary>
    public static bool IsUserForced(string? triggerReason) =>
        triggerReason is ManualRefreshReason or FiltersChangedReason;

    /// <summary>
    /// Fresh when there is nothing to preserve (no previous Active snapshot), when the UTC day
    /// rolled over (the daily rotation guarantee), when the model changed (its opinion of every
    /// candidate changed with it), or when the user forced it. Preserve otherwise — which is what a
    /// 5-minute delta-sync poll lands on, keeping tonight's picks put.
    /// </summary>
    public static FeedBuildMode Decide(
        DateTime? previousGeneratedAtUtc,
        Guid? previousModelArtifactId,
        Guid currentModelArtifactId,
        string? triggerReason,
        DateTime nowUtc)
    {
        if (previousGeneratedAtUtc is null
            || previousGeneratedAtUtc.Value.Date != nowUtc.Date
            || previousModelArtifactId != currentModelArtifactId
            || IsUserForced(triggerReason))
        {
            return FeedBuildMode.Fresh;
        }

        return FeedBuildMode.Preserve;
    }
}
