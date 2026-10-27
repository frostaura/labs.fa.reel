import { useRef, type ReactNode } from "react";
import { CardGrid } from "./parts";
import type { AskCard } from "../../lib/askReelStream";

/**
 * One collapsed tray of cards — the floor tray, the near misses, the unchecked, and an earlier
 * answer kept under its reply. A native `<details>`, so it opens from the keyboard (Enter or Space
 * on the summary) with no script, and announces its own expanded state.
 *
 * Expanding it moves focus to its heading (WS-2 task 3, §4.9 accessibility). Opening a tray puts a
 * whole grid of new content under the reader; without the move, a keyboard or screen-reader user is
 * left on the summary with no cue that anything appeared, and the next Tab lands on the first card
 * with no account of what the cards are. The heading is focusable by script only (`tabIndex=-1`),
 * so it is not an extra stop in the tab order.
 */
export default function CardTray({
  testId,
  toggleTestId,
  summary,
  heading,
  caption,
  cards,
  onSaveTapped,
  extra,
}: {
  testId: string;
  toggleTestId: string;
  summary: string;
  heading: string;
  caption: string;
  cards: AskCard[];
  onSaveTapped: (card: AskCard) => void;
  /** Rendered under the caption — the floor tray's cards may be absent while its count is not. */
  extra?: ReactNode;
}) {
  const headingRef = useRef<HTMLHeadingElement>(null);
  return (
    <details
      className="border-t border-fa-frost/10 pt-5"
      data-testid={testId}
      onToggle={(e) => {
        if (e.currentTarget.open) headingRef.current?.focus();
      }}
    >
      {/* `min-h-11` and padding rather than `flex`: a `display: flex` summary loses its own
          disclosure marker, and the triangle is the affordance that says the block opens. The box
          still measures 44 px, which is what a thumb needs. */}
      <summary
        data-testid={toggleTestId}
        className="fa-body min-h-11 cursor-pointer py-3 text-fa-frost-dim marker:text-3 hover:text-fa-frost-bright"
      >
        {summary}
      </summary>
      {/* An h2: the page's one h1 is "Ask Reel", and a tray is a section of the answer under it. An
          h3 here skipped a level, which axe fails as heading-order. */}
      <h2
        ref={headingRef}
        tabIndex={-1}
        data-testid={`${testId}-heading`}
        className="fa-section-title mt-1 focus:outline-none focus-visible:ring-2 focus-visible:ring-fa-frost/60 rounded"
      >
        {heading}
      </h2>
      <p className="fa-caption text-3 mt-1">{caption}</p>
      {extra}
      {cards.length > 0 && (
        <div className="mt-3">
          <CardGrid cards={cards} onSaveTapped={onSaveTapped} />
        </div>
      )}
    </details>
  );
}
