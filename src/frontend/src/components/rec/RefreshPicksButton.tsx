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
 * Renders as a compact square icon button sized to the adjacent BrowseFilters segments
 * (`p-1.5` + `h-4` icon ≈ the segment's `p-0.5` + `py-1` + 11px caption ≈ 30px), mirroring their
 * `rounded-lg border-fa-edge bg-fa-glass` chrome so it reads as a peer control.
 *
 * BuildFeed is Founder-gated server-side; a 403 for other tiers is swallowed and the Trakt sync
 * still lands. `busy` covers both the in-flight enqueue calls and any active pipeline job (so a
 * background sync started elsewhere also disables the button), and drives the spinner.
 */
export default function RefreshPicksButton({ onRequested }: { onRequested?: () => void }) {
  const [triggerSync, { isLoading: syncing }] = useTriggerSyncMutation();
  const [rebuildFeed, { isLoading: rebuilding }] = useRebuildFeedMutation();
  const jobActive = useSelector((s: RootState) => s.syncUi.active);
  const busy = syncing || rebuilding || jobActive;

  const refresh = async () => {
    if (busy) return;
    // Asking for fresh picks is an explicit request to be reset — tell the owner to adopt the
    // snapshot this produces, rather than holding it behind the "new picks ready" affordance that
    // protects an in-progress scroll. Fires up front: the enqueue returns long before the rebuild
    // lands, so the owner waits for the new generatedAt itself.
    onRequested?.();
    // Sync first (earlier EnqueuedAt → runs first), then regenerate on the fresh data.
    await triggerSync().unwrap().catch(() => undefined);
    await rebuildFeed().unwrap().catch(() => undefined);
  };

  return (
    <button
      type="button"
      onClick={refresh}
      disabled={busy}
      aria-label={busy ? "Refreshing your picks" : "Refresh — sync Trakt and regenerate picks"}
      title="Pull the latest from Trakt, then regenerate tonight's picks"
      data-testid="refresh-picks"
      className="inline-flex items-center justify-center rounded-lg border border-fa-edge bg-fa-glass p-1.5 text-fa-frost transition hover:border-fa-frost/40 hover:bg-fa-glass-strong hover:text-fa-frost-bright disabled:opacity-50 disabled:pointer-events-none"
    >
      <RefreshCw className={`h-4 w-4 ${busy ? "animate-spin" : ""}`} />
    </button>
  );
}
