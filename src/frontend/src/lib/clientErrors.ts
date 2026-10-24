/**
 * Client error reporting — the browser half of the client-error sink (I-4).
 *
 * React 19 hands every render error to the root's `onCaughtError` / `onUncaughtError` (wired in
 * main.tsx), so this module is the single place an error leaves the browser. It posts to
 * `POST /api/client-errors` (WS-6's account-scoped, rate-limited sink) and never throws: reporting
 * a failure must not become a second failure. Until the sink exists the POST simply 404s.
 *
 * What is sent is deliberately small: the error's name, message and stack, the React component
 * stack, the PATH (never the query string, which can carry what the user typed into Ask Reel), and
 * which root hook saw it. No storage contents, no request bodies.
 */

export const CLIENT_ERROR_ENDPOINT = "/api/client-errors";

/** At most this many reports per page load — a render loop must not become a request loop. */
export const MAX_REPORTS_PER_PAGE = 10;

const MAX_FIELD = 4_000;

export type ErrorOrigin = "caught" | "uncaught";

export interface ClientErrorReport {
  name: string;
  message: string;
  stack: string | null;
  componentStack: string | null;
  origin: ErrorOrigin;
  path: string;
  occurredAt: string;
}

const clip = (value: string | null | undefined): string | null =>
  value == null ? null : value.length > MAX_FIELD ? value.slice(0, MAX_FIELD) : value;

/** Builds the report body. Pure, so the shape is unit-tested without a browser. */
export function buildClientErrorReport(
  error: unknown,
  origin: ErrorOrigin,
  componentStack: string | null | undefined,
  path: string,
  now: Date = new Date()
): ClientErrorReport {
  const err = error instanceof Error ? error : null;
  return {
    name: err?.name ?? typeof error,
    message: clip(err?.message ?? String(error)) ?? "",
    stack: clip(err?.stack),
    componentStack: clip(componentStack),
    origin,
    path,
    occurredAt: now.toISOString(),
  };
}

let sent = 0;
const seen = new Set<string>();

/** Test hook: forget what this page load has already reported. */
export function resetClientErrorBudget(): void {
  sent = 0;
  seen.clear();
}

/**
 * Sends one report, at most once per distinct error per page load and at most
 * {@link MAX_REPORTS_PER_PAGE} in all. Returns whether a request was made.
 */
export function reportClientError(
  error: unknown,
  origin: ErrorOrigin,
  componentStack?: string | null,
  send: typeof fetch = (input, init) => fetch(input, init)
): boolean {
  const report = buildClientErrorReport(error, origin, componentStack, window.location.pathname);
  const key = `${report.name}|${report.message}|${report.path}`;
  if (seen.has(key) || sent >= MAX_REPORTS_PER_PAGE) return false;
  seen.add(key);
  sent += 1;

  try {
    void send(CLIENT_ERROR_ENDPOINT, {
      method: "POST",
      credentials: "include",
      keepalive: true,
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(report),
    }).catch(() => undefined);
  } catch {
    // A synchronous throw from fetch (malformed init, a hostile polyfill) is still not our problem.
  }
  return true;
}
