import { RotateCw } from "lucide-react";

interface Props {
  onRetry: () => void;
  /** "poster" trails a rail of 2:3 cards; "inline" stands alone where a rail has nothing to show. */
  shape?: "poster" | "inline";
}

/**
 * What a rail shows once its automatic retries are spent (O-7): a plain statement and one button,
 * in place of the shimmer that used to promise cards forever. Polite, not an alert — eight failing
 * rails must not shout eight times.
 */
export default function RailRetryTile({ onRetry, shape = "poster" }: Props) {
  const body = (
    <div
      role="status"
      className="flex h-full flex-col items-center justify-center gap-3 rounded-lg border border-fa-edge bg-fa-ink-2 p-4 text-center"
      data-testid="rail-retry"
    >
      <p className="fa-body text-fa-frost">Couldn&apos;t load</p>
      <button type="button" onClick={onRetry} className="fa-button min-h-11" data-testid="rail-retry-button">
        <RotateCw className="h-4 w-4" />
        Retry
      </button>
    </div>
  );

  return shape === "poster" ? (
    <div className="snap-start w-44 sm:w-48 shrink-0">
      <div className="aspect-[2/3]">{body}</div>
    </div>
  ) : (
    <div className="py-4">{body}</div>
  );
}
