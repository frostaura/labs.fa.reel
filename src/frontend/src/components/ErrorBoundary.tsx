import { Component, type ErrorInfo, type ReactNode } from "react";
import { isChunkLoadError, reloadOnceForNewBuild } from "../lib/deployReload";

interface Props {
  /** "route" keeps the shell (header, nav) and replaces only the page; "root" replaces everything. */
  scope: "route" | "root";
  /** A change clears a caught error — the route boundary passes the pathname, so navigating away recovers. */
  resetKey?: string;
  children: ReactNode;
}

interface State {
  error: unknown;
  resetKey: string | undefined;
}

/**
 * Catches render errors so one broken page no longer unmounts the whole app (I-4). Reporting is not
 * done here: React 19 hands every caught error to the root's `onCaughtError` (main.tsx), which is
 * the one place errors leave the browser, so a boundary never double-reports.
 *
 * A failed lazy-chunk load is the expected aftermath of a deploy under an open tab: the boundary
 * reloads once for it (guarded, see lib/deployReload.ts) and only shows itself if that reload
 * already happened and did not help.
 */
export default class ErrorBoundary extends Component<Props, State> {
  state: State = { error: null, resetKey: this.props.resetKey };

  static getDerivedStateFromError(error: unknown): Partial<State> {
    return { error: error ?? new Error("Unknown render error") };
  }

  static getDerivedStateFromProps(props: Props, state: State): Partial<State> | null {
    if (props.resetKey !== state.resetKey) return { error: null, resetKey: props.resetKey };
    return null;
  }

  componentDidCatch(error: unknown, _info: ErrorInfo): void {
    if (isChunkLoadError(error)) reloadOnceForNewBuild();
  }

  private retry = () => this.setState({ error: null });

  render() {
    const { error } = this.state;
    if (error == null) return this.props.children;

    const chunk = isChunkLoadError(error);
    const root = this.props.scope === "root";
    return (
      <div
        role="alert"
        data-testid={root ? "root-error-boundary" : "route-error-boundary"}
        className={`flex items-center justify-center px-6 ${root ? "min-h-screen" : "py-24"}`}
      >
        <div className="max-w-md text-center space-y-4">
          <h1 className="text-xl font-light text-fa-frost-bright">
            {chunk ? "Reel was updated" : root ? "Reel hit a problem" : "This page hit a problem"}
          </h1>
          <p className="fa-body text-fa-frost-dim">
            {chunk
              ? "This tab is running an older version. Reload to pick up the new one."
              : "The error has been reported. Nothing you saved is affected."}
          </p>
          <div className="flex items-center justify-center gap-3">
            {!root && !chunk && (
              <button
                type="button"
                onClick={this.retry}
                className="inline-flex items-center justify-center rounded-full border border-fa-edge bg-fa-glass px-4 py-2 fa-caption text-fa-frost-bright transition hover:border-fa-frost/40 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-fa-frost/50"
              >
                Try again
              </button>
            )}
            <button
              type="button"
              onClick={() => window.location.reload()}
              className="inline-flex items-center justify-center rounded-full border border-fa-edge bg-fa-glass px-4 py-2 fa-caption text-fa-frost-bright transition hover:border-fa-frost/40 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-fa-frost/50"
            >
              Reload Reel
            </button>
          </div>
        </div>
      </div>
    );
  }
}
