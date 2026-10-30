import { RotateCw } from "lucide-react";
import { Button } from "../ui/button";
import { RecCardTextSkeleton } from "../rec/RecCard";

interface Props {
  onRetry: () => void;
  /** "poster" trails a rail of 2:3 cards; "inline" stands alone where a rail has nothing to show. */
  shape?: "poster" | "inline";
  /** Poster shape in an anchored rail: hold the evidence slot as its cards do (see `RecRow`). */
  reserveEvidence?: boolean;
}

/**
 * What a rail shows once its automatic retries are spent (O-7): a plain statement and one button,
 * in place of the shimmer that used to promise cards forever. Polite, not an alert — eight failing
 * rails must not shout eight times.
 */
export default function RailRetryTile({ onRetry, shape = "poster", reserveEvidence = false }: Props) {
  const body = (
    <div
      role="status"
      className="flex h-full flex-col items-center justify-center gap-3 rounded-lg border border-fa-edge bg-fa-ink-2 p-4 text-center"
      data-testid="rail-retry"
    >
      <p className="fa-body text-fa-frost">Couldn&apos;t load</p>
      <Button onClick={onRetry} className="min-h-11" data-testid="rail-retry-button">
        <RotateCw className="h-4 w-4" aria-hidden />
        Retry
      </Button>
    </div>
  );

  return shape === "poster" ? (
    // In a card's slot, a card's height: the poster's box and the text block's held lines under it,
    // so a rail whose placeholder turns into this tile does not shrink (HREV8-DESK-01).
    <div className="snap-start w-card shrink-0">
      <div className="aspect-[2/3]">{body}</div>
      <RecCardTextSkeleton evidence={reserveEvidence} />
    </div>
  ) : (
    <div className="py-4">{body}</div>
  );
}
