import { useEffect, useState } from "react";

/**
 * Opens once `settled` has been true, or once `capMs` has passed since mount, whichever comes
 * first — and then stays open for good.
 *
 * It exists for layout stability (HREV1-D03, CLS ≤ 0.05). A page that draws each panel the moment
 * its own read lands moves everything below that panel down, once per read: on /settings the
 * conditions editor grew from a three-bar skeleton into a page-tall form, then grew again as its
 * chip list and its cost counts landed, and every card under it jumped three times. Holding a
 * region back until its reads have settled turns those jumps into one appearance, which is not a
 * layout shift at all.
 *
 * The cap is the honesty valve: a read that never answers must not hold the page hostage, so after
 * `capMs` the region is drawn anyway and simply fills in late. The latch matters as much as the
 * cap — a region that has been shown is never swapped back for a placeholder because a later
 * refetch is in flight.
 */
export function useSettledGate(settled: boolean, capMs: number): boolean {
  const [open, setOpen] = useState(settled);
  // Adopted during render (React's documented alternative to a syncing effect), so the frame in
  // which the last read lands is the frame that draws the region — not one frame later.
  if (settled && !open) setOpen(true);

  useEffect(() => {
    if (open) return;
    const handle = window.setTimeout(() => setOpen(true), capMs);
    return () => window.clearTimeout(handle);
  }, [open, capMs]);

  return open;
}
