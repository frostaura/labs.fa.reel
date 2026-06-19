import { RefreshCw } from "lucide-react";
import { useSelector } from "react-redux";
import type { RootState } from "../../store";
import { useRebuildFeedMutation, useTriggerSyncMutation } from "../../store/api";

/**
 * On-demand "pull from Trakt, then regenerate tonight's picks". Enqueues a DeltaSync immediately
 * followed by a BuildFeed — the job runner claims `ORDER BY Priority, EnqueuedAt`, so the rebuild
 * always scores the freshly-synced library rather than stale data (DeltaSync ingests but does not
 * itself rebuild the feed). Both jobs surface in the header sync pill; on `job-completed`,
 * RealtimeSync invalidates the Feed/TasteDna tags, so the picks refresh themselves once ready.
 *
 * BuildFeed is Founder-gated server-side; a 403 for other tiers is swallowed and the Trakt sync
 * still lands. `busy` covers both the in-flight enqueue calls and any active pipeline job (so a
 * background sync started elsewhere also disables the button), and drives the spinner.
 */
export default function RefreshPicksButton() {
  const [triggerSync, { isLoading: syncing }] = useTriggerSyncMutation();
  const [rebuildFeed, { isLoading: rebuilding }] = useRebuildFeedMutation();
  const jobActive = useSelector((s: RootState) => s.syncUi.active);
  const busy = syncing || rebuilding || jobActive;

  const refresh = async () => {
    if (busy) return;
    // Sync first (earlier EnqueuedAt → runs first), then regenerate on the fresh data.
    await triggerSync().unwrap().catch(() => undefined);
    await rebuildFeed().unwrap().catch(() => undefined);
  };

  return (
    <button
      type="button"
      onClick={refresh}
      disabled={busy}
      className="fa-button shrink-0"
      data-testid="refresh-picks"
      title="Pull the latest from Trakt, then regenerate tonight's picks"
    >
      <RefreshCw className={`h-4 w-4 ${busy ? "animate-spin" : ""}`} />
      <span className="hidden sm:inline">{busy ? "Refreshing…" : "Refresh"}</span>
    </button>
  );
}
