import { useEffect, type ReactNode } from "react";
import { Navigate } from "react-router";
import { useGetSessionQuery } from "../store/api";
import { isSignedOutError } from "../lib/sessionErrors";

/**
 * Frost splash while the session resolves; the landing page only when the session is really gone.
 *
 * "Really gone" is a 401 that survived the refresh the base query already attempted. Anything else
 * — a 502 from a restarting API, a dropped connection, an offline phone — used to sign the user
 * out to the landing pitch and lose the URL they were on (H-15). Now it keeps the URL and offers a
 * retry, and retries by itself when the browser comes back online.
 */
export default function RequireAuth({ children }: { children: ReactNode }) {
  const { data, isLoading, isFetching, error, refetch } = useGetSessionQuery();

  useEffect(() => {
    if (!error || isSignedOutError(error)) return;
    const retry = () => refetch();
    window.addEventListener("online", retry);
    return () => window.removeEventListener("online", retry);
  }, [error, refetch]);

  if (isLoading) {
    return (
      <div className="min-h-screen flex items-center justify-center">
        <div className="text-2xl font-light tracking-wide text-fa-frost animate-pulse">Reel</div>
      </div>
    );
  }
  // A session already in hand outlives a failed re-check; only a missing one can strand the page.
  if (data) return <>{children}</>;
  if (error && isSignedOutError(error)) return <Navigate to="/" replace />;
  if (error) {
    const offline = typeof navigator !== "undefined" && navigator.onLine === false;
    return (
      <div className="min-h-screen flex items-center justify-center px-6" data-testid="session-retry">
        <div className="max-w-sm text-center space-y-4" role="alert">
          <div className="text-2xl font-light tracking-wide text-fa-frost">Reel</div>
          <p className="fa-body text-fa-frost">
            {offline ? "You're offline." : "Reel couldn't reach its server."}
          </p>
          <p className="fa-caption text-fa-frost-dim">
            {offline
              ? "You're still signed in. This page loads as soon as you're back online."
              : "You're still signed in. Try again in a moment."}
          </p>
          <button
            type="button"
            onClick={() => refetch()}
            disabled={isFetching}
            className="inline-flex items-center justify-center rounded-full border border-fa-edge bg-fa-glass px-4 py-2 fa-caption text-fa-frost-bright transition hover:border-fa-frost/40 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-fa-frost/50 disabled:opacity-60"
          >
            {isFetching ? "Trying…" : "Try again"}
          </button>
        </div>
      </div>
    );
  }
  return <>{children}</>;
}
