import { useSelector } from "react-redux";
import type { RootState } from "../store";
import { offlineSnapshot } from "../store/connectivitySlice";
import { formatRankedAt } from "../lib/format";

/**
 * Says so when the page is showing the service worker's saved copy instead of a live answer
 * (I-11). Without it an hour-old feed looked exactly like tonight's.
 *
 * It does not say "offline": the worker also falls back after an 8-second network timeout on a
 * connection that is merely slow, so the copy names what happened, not why.
 */
/** A query is on screen while something is subscribed to it, not merely while RTK still caches it. */
const onScreen = (s: RootState) => (key: string) => Object.keys(s.reelApi.subscriptions[key] ?? {}).length > 0;

export default function OfflineBanner() {
  const fromCache = useSelector((s: RootState) => offlineSnapshot(s.connectivity, onScreen(s)).fromCache);
  const savedAt = useSelector((s: RootState) => offlineSnapshot(s.connectivity, onScreen(s)).savedAt);
  if (!fromCache) return null;

  return (
    <div
      role="status"
      data-testid="offline-banner"
      className="border-b border-fa-warning/30 bg-fa-ink/90 px-4 py-2 text-center fa-caption text-fa-warning"
    >
      {savedAt
        ? `Couldn't reach Reel — showing what was saved ${formatRankedAt(savedAt)}.`
        : "Couldn't reach Reel — showing what was saved earlier."}
    </div>
  );
}
