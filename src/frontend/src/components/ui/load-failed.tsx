import { AlertTriangle, RefreshCw } from "lucide-react";
import { Button } from "./button";

interface Props {
  /** What failed to arrive, in the page's own words — "your taste DNA", "the track record". */
  what: string;
  onRetry: () => void;
  /** True while the retry is in flight, so a second tap cannot queue a second request. */
  retrying?: boolean;
  /**
   * `page` replaces the whole page body (nothing else on it can render without this read);
   * `inline` sits above content that is still on screen from an earlier successful read, or stands
   * in for one panel of a page whose other panels loaded.
   */
  variant?: "page" | "inline";
  testId?: string;
}

/**
 * The error state of a read. It exists because the two ways this app used to fail a read were both
 * lies: an empty list rendered as "nothing here yet", or a shimmer that never stopped. A failed read
 * is a third state, not a variant of loading or of empty, and it always offers the way back.
 *
 * Lives under `settings/` only because that is where lane 10B may add files; it is meant to move to
 * `components/ui/` with WS-9's primitives (requested in the WS-10B report).
 */
export default function LoadFailed({ what, onRetry, retrying = false, variant = "page", testId = "load-failed" }: Props) {
  const shell =
    variant === "page"
      ? "fa-card border-fa-danger/40 px-6 py-8 flex flex-col items-center gap-3 text-center"
      : "fa-card border-fa-danger/40 px-4 py-3 flex flex-wrap items-center gap-3";
  return (
    <section role="alert" className={shell} data-testid={testId}>
      <AlertTriangle className="h-5 w-5 shrink-0 text-fa-danger" aria-hidden />
      <div className={variant === "page" ? "space-y-1" : "flex-1 min-w-0 space-y-0.5"}>
        <p className="fa-body text-fa-frost-bright">Couldn&apos;t load {what}.</p>
        <p className="fa-caption text-fa-frost-dim">
          The request failed — nothing on your account changed. Try again in a moment.
        </p>
      </div>
      <Button variant="outline" size="sm" onClick={onRetry} disabled={retrying} data-testid={`${testId}-retry`}>
        <RefreshCw className={`h-4 w-4 ${retrying ? "animate-spin" : ""}`} aria-hidden />
        {retrying ? "Retrying…" : "Retry"}
      </Button>
    </section>
  );
}
