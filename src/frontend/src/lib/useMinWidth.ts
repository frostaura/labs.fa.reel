import { useCallback, useSyncExternalStore } from "react";

/**
 * Reads a `min-width` media query as React state.
 *
 * Tailwind's breakpoints do the layout; this exists only for the handful of decisions that are
 * arithmetic rather than CSS — how many columns a two-row grid actually has, and therefore how many
 * dots the hero draws and how many images it loads eagerly. CSS cannot hand those numbers to JS.
 *
 * `useSyncExternalStore` rather than the usual state-plus-effect pair: the viewport is an external
 * store, and the effect version has to re-read on mount (the query can have moved between the first
 * render and commit — a rotation mid-load, a restored window size), which is a setState inside an
 * effect body and a cascading render. Subscribing to the store gets that re-read for free.
 *
 * Deliberately defensive about `matchMedia`: the frontend has no SSR today, but the vitest
 * environment is `node` and a jsdom one supplies a `window` with no `matchMedia` at all. A hook that
 * threw in either place would make the hero untestable outside a browser, so an absent `matchMedia`
 * reads as "does not match" — the single-row answer, which is the safe one. The `addEventListener`
 * guard covers the same ground for Safari < 14's listener-only MediaQueryList.
 */
export function useMinWidth(minWidthPx: number): boolean {
  const query = `(min-width: ${minWidthPx}px)`;

  const subscribe = useCallback(
    (onStoreChange: () => void) => {
      const mql = mediaQuery(query);
      if (!mql || typeof mql.addEventListener !== "function") return () => {};
      mql.addEventListener("change", onStoreChange);
      return () => mql.removeEventListener("change", onStoreChange);
    },
    [query]
  );

  const getSnapshot = useCallback(() => mediaQuery(query)?.matches ?? false, [query]);

  return useSyncExternalStore(subscribe, getSnapshot, getServerSnapshot);
}

/** No window, no viewport: the narrow answer, which is the one that degrades gracefully. */
function getServerSnapshot(): boolean {
  return false;
}

function mediaQuery(query: string): MediaQueryList | null {
  if (typeof window === "undefined" || typeof window.matchMedia !== "function") return null;
  try {
    return window.matchMedia(query);
  } catch {
    return null;
  }
}

/** Tailwind's `md` breakpoint, in pixels — the one place this number is written down. */
export const MD_BREAKPOINT_PX = 768;
