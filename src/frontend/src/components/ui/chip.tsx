import * as React from "react";
import { cn } from "../../lib/utils";

export interface ChipProps extends Omit<React.ButtonHTMLAttributes<HTMLButtonElement>, "aria-pressed"> {
  /** On or off. Announced as a toggle (`aria-pressed`), shown with a filled frost border. */
  selected: boolean;
  /** Optional leading icon or count; decorative content should carry `aria-hidden`. */
  leading?: React.ReactNode;
}

/**
 * A toggle chip (WS-9 task 8): a filter, a mood, a genre. A one-shot action shaped like a chip is
 * `<Button variant="chip">` instead, because it has no on or off state to announce.
 */
export const Chip = React.forwardRef<HTMLButtonElement, ChipProps>(
  ({ selected, leading, className, children, type, ...props }, ref) => (
    <button
      ref={ref}
      type={type ?? "button"}
      aria-pressed={selected}
      data-state={selected ? "on" : "off"}
      className={cn(
        "inline-flex items-center gap-1.5 rounded-full border px-3 py-1 text-caption transition",
        "focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-fa-frost/60 focus-visible:ring-offset-1 focus-visible:ring-offset-fa-ink",
        "disabled:pointer-events-none disabled:opacity-50",
        selected
          ? "border-fa-frost/60 bg-fa-frost/15 text-1"
          : "border-fa-edge bg-fa-glass text-2 hover:text-1 hover:border-fa-edge-strong",
        className
      )}
      {...props}
    >
      {leading}
      {children}
    </button>
  )
);
Chip.displayName = "Chip";
