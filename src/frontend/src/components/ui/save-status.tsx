import { Button } from "./button";
import type { SaveMessage } from "./save-outcome";

interface Props {
  message: SaveMessage | null;
  /** Re-runs the save that failed. Offered only when the failure is one a retry can fix. */
  onRetry?: () => void;
  retrying?: boolean;
  testId?: string;
}

/**
 * The one way a settings save reports back. Success is green and polite; a failure is in danger
 * tone, announced as an alert, and stays until the user saves again — a message that faded after
 * 2.5 s left a failed save looking exactly like a successful one.
 */
export default function SaveStatus({ message, onRetry, retrying = false, testId = "save-status" }: Props) {
  if (!message) return null;
  const success = message.tone === "success";
  return (
    <span
      role={success ? "status" : "alert"}
      data-testid={testId}
      data-tone={message.tone}
      className={`inline-flex flex-wrap items-center gap-2 fa-caption ${success ? "text-fa-success" : "text-fa-danger"}`}
    >
      {message.text}
      {!success && message.retryable && onRetry && (
        <Button variant="destructive" size="sm" onClick={onRetry} disabled={retrying} data-testid={`${testId}-retry`}>
          {retrying ? "Retrying…" : "Retry"}
        </Button>
      )}
    </span>
  );
}
