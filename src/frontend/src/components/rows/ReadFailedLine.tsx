import { Button } from "../ui/button";
import type { ReadRetryPhase } from "../../lib/useReadRetry";

interface Props {
  /** What did not load, as it reads after "Couldn't load": "your genre rows", "Continue watching". */
  what: string;
  /** What the user should still know while it is missing, e.g. that the conditions still apply. */
  note?: string;
  phase: Exclude<ReadRetryPhase, "ok">;
  onRetry: () => void;
  testId: string;
  className?: string;
}

/**
 * One compact line where a section of Home failed to load (HREV8-DESK-02), in place of the section
 * silently not being there: "Couldn't load your genre rows · Retry". While the automatic retries
 * run it says so instead of offering a button that would only race them. Polite, not an alert, like
 * the rail Retry tile: several failed reads must not shout several times.
 */
export default function ReadFailedLine({ what, note, phase, onRetry, testId, className = "" }: Props) {
  return (
    <p
      role="status"
      className={`fa-caption flex flex-wrap items-center gap-x-1 text-fa-warning ${className}`}
      data-testid={testId}
      data-phase={phase}
    >
      <span>
        Couldn&apos;t load {what}
        {note ? ` — ${note}` : ""}
        {phase === "retrying" ? " — trying again…" : " ·"}
      </span>
      {phase === "failed" && (
        <Button
          variant="link"
          onClick={onRetry}
          className="h-auto p-0 text-xs font-normal text-fa-frost underline underline-offset-2 coarse:min-h-11"
          data-testid={`${testId}-retry`}
        >
          Retry
        </Button>
      )}
    </p>
  );
}
