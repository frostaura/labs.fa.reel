import { Component, type ErrorInfo, type ReactNode } from "react";
import { isChunkLoadError, reloadOnceForNewBuild } from "../lib/deployReload";
import { Button } from "./ui/button";

const BUTTON_CLASS = "rounded-full focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-fa-frost/50";

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
          {/* Says only what this boundary knows. It cannot know whether the report reached the
              server (the sink may be down, deduplicated or capped), whether anything saved was
              affected, or why a chunk would not load (a deploy, or simply no connection). */}
          <h1 className="text-xl font-light text-fa-frost-bright">
            {chunk ? "Couldn't load this page" : root ? "Something went wrong" : "Something went wrong on this page"}
          </h1>
          <p className="fa-body text-fa-frost-dim">
            {chunk
              ? "Reel may have been updated, or the connection dropped. Reloading usually fixes it."
              : root
                ? "Reloading Reel usually fixes it."
                : "Try again, or open another page — the rest of Reel still works."}
          </p>
          <div className="flex items-center justify-center gap-3">
            {!root && !chunk && (
              <Button type="button" variant="outline" onClick={this.retry} className={BUTTON_CLASS}>
                Try again
              </Button>
            )}
            <Button type="button" variant="outline" onClick={() => window.location.reload()} className={BUTTON_CLASS}>
              Reload Reel
            </Button>
          </div>
        </div>
      </div>
    );
  }
}
