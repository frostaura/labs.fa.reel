/**
 * Small decisions the title and person pages make about routing and failure, kept out of the
 * components so they can be asserted without a DOM.
 */

/** A fetch failure the page can name: the server's 404 is "not found", anything else is "error". */
export type TitleFailure = "not-found" | "error";

/** Maps an RTK Query error to the copy a page shows. Only an HTTP 404 is "not found". */
export function titleFailure(error: unknown): TitleFailure {
  const status = typeof error === "object" && error !== null ? (error as { status?: unknown }).status : undefined;
  return status === 404 ? "not-found" : "error";
}

/**
 * Whether Close can step back to a page the user came from. A card opens the title over its page
 * with `backgroundLocation`; any other in-app navigation leaves a history index above zero
 * (react-router keeps `idx` in `history.state`). A deep link has neither, and Back from it would
 * leave the app.
 */
export function canGoBack(locationState: unknown, historyState: unknown = readHistoryState()): boolean {
  const background = (locationState as { backgroundLocation?: unknown } | null)?.backgroundLocation;
  if (background) return true;
  const idx = (historyState as { idx?: unknown } | null)?.idx;
  return typeof idx === "number" && idx > 0;
}

function readHistoryState(): unknown {
  return typeof window === "undefined" ? null : window.history.state;
}
