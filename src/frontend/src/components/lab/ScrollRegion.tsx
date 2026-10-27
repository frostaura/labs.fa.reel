import type { ReactNode } from "react";

/**
 * A table that may scroll sideways on a phone, reachable from the keyboard (PH-02).
 *
 * A scroller with nothing focusable inside it cannot be scrolled without a pointer, which axe
 * reports as scrollable-region-focusable (serious) — the iteration ledger on /lab was one. The region
 * takes focus itself, is named for what it holds, and shows the focus ring the rest of the app uses.
 */
export default function ScrollRegion({ label, children, testId }: { label: string; children: ReactNode; testId?: string }) {
  return (
    <div
      role="region"
      aria-label={label}
      tabIndex={0}
      className="overflow-x-auto rounded-md focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-fa-frost/50"
      data-testid={testId}
    >
      {children}
    </div>
  );
}
