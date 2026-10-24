import { useSelector } from "react-redux";
import type { RootState } from "../store";
import { formatRankedAt } from "../lib/format";

/**
 * Says so when the page is showing the service worker's saved copy instead of a live answer
 * (I-11). Without it an hour-old feed looked exactly like tonight's.
 */
export default function OfflineBanner() {
  const { fromCache, savedAt } = useSelector((s: RootState) => s.connectivity);
  if (!fromCache) return null;

  return (
    <div
      role="status"
      data-testid="offline-banner"
      className="border-b border-fa-warning/30 bg-fa-ink/90 px-4 py-2 text-center fa-caption text-fa-warning"
    >
      {savedAt
        ? `Offline — showing what Reel saved ${formatRankedAt(savedAt)}.`
        : "Offline — showing what Reel saved earlier."}
    </div>
  );
}
