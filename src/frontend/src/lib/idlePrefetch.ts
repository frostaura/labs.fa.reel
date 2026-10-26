/**
 * Fetches a lazy route's chunk while the browser has nothing better to do (I-17), so the first tap
 * on a poster opens the title sheet without a chunk round-trip. Skipped when the user has asked the
 * browser to save data. Returns a cancel function for an effect's cleanup.
 */
export function prefetchWhenIdle(load: () => Promise<unknown>, win: IdleWindow = globalThis as unknown as IdleWindow): () => void {
  if (win.navigator?.connection?.saveData === true) return () => {};
  const run = () => {
    // A failed prefetch costs nothing: the route's own lazy() load retries, and a failed chunk
    // there is the error boundary's to handle.
    load().catch(() => undefined);
  };
  if (typeof win.requestIdleCallback === "function") {
    const handle = win.requestIdleCallback(run, { timeout: IDLE_TIMEOUT_MS });
    return () => win.cancelIdleCallback?.(handle);
  }
  // Safari has no requestIdleCallback: wait out the first paint's busy period instead.
  const handle = win.setTimeout(run, FALLBACK_DELAY_MS);
  return () => win.clearTimeout(handle);
}

/** The latest the prefetch waits for an idle period. */
export const IDLE_TIMEOUT_MS = 5_000;
/** Without requestIdleCallback, the delay before prefetching. */
export const FALLBACK_DELAY_MS = 2_000;

export interface IdleWindow {
  navigator?: { connection?: { saveData?: boolean } };
  requestIdleCallback?: (cb: () => void, opts?: { timeout: number }) => number;
  cancelIdleCallback?: (handle: number) => void;
  setTimeout: (cb: () => void, ms: number) => number;
  clearTimeout: (handle: number) => void;
}
