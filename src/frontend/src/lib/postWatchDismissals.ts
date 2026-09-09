const STORAGE_KEY = "reel.postwatch.dismissed";

/**
 * Which loop-closing prompts this browser has been told to stop showing.
 *
 * Per-title and client-side on purpose. A dismissal is "not now, on this device" — it is not a
 * rating, it is not an opinion about the title, and it is emphatically not something to persist
 * server-side where it would look like signal the model should learn from. The server's own guard
 * (no prompt for a title that already has a rating) is the durable half; this is the polite half.
 *
 * Every access is wrapped: Safari's private mode makes `localStorage` itself throw, and a dismissed
 * prompt is not worth a blank page. Stored content is re-validated rather than trusted — it survives
 * deploys, so a value from an older build can outlive the code that wrote it.
 */
export function readDismissed(): Set<string> {
  try {
    const raw = localStorage.getItem(STORAGE_KEY);
    if (!raw) return new Set();
    const parsed: unknown = JSON.parse(raw);
    if (!Array.isArray(parsed)) return new Set();
    return new Set(parsed.filter((id): id is string => typeof id === "string"));
  } catch {
    return new Set();
  }
}

/** Adds one title to the dismissed set. Silently a no-op when storage is unavailable. */
export function dismiss(titleId: string): Set<string> {
  const next = readDismissed();
  next.add(titleId);
  try {
    localStorage.setItem(STORAGE_KEY, JSON.stringify([...next]));
  } catch {
    // Storage refused (private mode, quota, blocked). The dismissal still holds for this render;
    // it simply will not survive a reload, which is the right failure for a nicety.
  }
  return next;
}
