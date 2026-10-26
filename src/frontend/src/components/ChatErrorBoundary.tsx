import { Component, type ErrorInfo, type ReactNode } from "react";
import { AlertTriangle, RotateCcw } from "lucide-react";
import PillButton from "./askReel/PillButton";

interface Props {
  children: ReactNode;
  /** Called after the user asks to try again, alongside the internal state reset. */
  onReset?: () => void;
}

interface State {
  error: Error | null;
}

/**
 * The last line under Ask Reel.
 *
 * Everything on this page is driven by a live stream of server-authored frames — cards, phase
 * counters, a `reason` sentence, and now a conflict message and a floor tray. A single malformed
 * frame that reaches render (a null where a card was promised, a number that arrived as a string)
 * throws during React's commit, and an uncaught throw does not degrade the page: it unmounts the
 * whole route and leaves a blank screen with the answer still sitting in the stream. That is the
 * one failure mode a chat surface must not have, because the user's transcript is the state.
 *
 * So the page body is wrapped. A render failure becomes a card with a retry that remounts the
 * subtree, rather than a white page and a reload. Deliberately a class component: error boundaries
 * have no hooks equivalent — `getDerivedStateFromError` is the only way to catch a render throw.
 *
 * It does NOT catch stream or fetch failures. Those are already handled where they happen (the
 * hook's `error` status, the retry button on the empty and failed states), and routing them here
 * would replace a recoverable inline message with a full-bleed one.
 */
export default class ChatErrorBoundary extends Component<Props, State> {
  state: State = { error: null };

  static getDerivedStateFromError(error: Error): State {
    return { error };
  }

  componentDidCatch(error: Error, info: ErrorInfo): void {
    // Console only: there is no client error sink in this app yet, and inventing one here would
    // be a silent new outbound call from a page that is mid-failure.
    console.error("Ask Reel render failed", error, info.componentStack);
  }

  private handleRetry = (): void => {
    this.setState({ error: null });
    this.props.onReset?.();
  };

  render(): ReactNode {
    if (!this.state.error) return this.props.children;
    return (
      <div className="fa-card p-8 text-center max-w-xl mx-auto space-y-3" data-testid="askreel-crashed">
        <AlertTriangle className="h-6 w-6 text-fa-warning mx-auto" />
        <p className="fa-body text-fa-frost-bright">Ask Reel lost its place</p>
        <p className="fa-caption text-fa-frost-dim">
          Something in that answer did not render. Starting the conversation again usually clears it.
        </p>
        <PillButton tone="strong" onClick={this.handleRetry} data-testid="askreel-crash-retry">
          <RotateCcw className="h-3.5 w-3.5" aria-hidden /> Start again
        </PillButton>
      </div>
    );
  }
}
