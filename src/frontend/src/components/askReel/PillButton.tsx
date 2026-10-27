import { forwardRef } from "react";
import { Button, type ButtonProps } from "../ui/button";
import { cn } from "../../lib/utils";

/**
 * The one pill-shaped action on Ask Reel — retry, "Show me more", a refine chip, a mood preset, a
 * clarifying reading, a relaxation, `+ New`, `Recent ▾`, Stop and the phone's conditions button.
 *
 * Built on the design system's `<Button>` (WS-9 task 8) rather than beside it: every one of these
 * was a raw button element carrying its own copy of the same twelve-class string, so the page had ten
 * near-identical pills that drifted a class at a time. One component, three tones:
 *
 * - `accent` — something to ask (a preset, a reading, a relaxation);
 * - `strong` — the way forward from a state (retry, Show me more);
 * - `quiet` — housekeeping (New, Recent, Stop, a refine chip, the conditions count).
 *
 * Every pill is at least 44 × 44 px, the touch minimum the page was audited to.
 *
 * The type size is `text-xs`, written out rather than inherited from `fa-caption`: the primitive
 * carries `text-sm` in its base, and `fa-caption` is a component-layer class that a utility always
 * beats, so a pill with `fa-caption` alone renders at 14 px. `text-xs` is a size tailwind-merge
 * recognises, so it replaces `text-sm` instead of losing to it.
 */
export type PillTone = "accent" | "strong" | "quiet";

const TONE: Record<PillTone, string> = {
  accent: "border-fa-frost/25 text-fa-frost hover:text-fa-frost-bright hover:border-fa-frost/50",
  strong: "border-fa-frost/30 text-fa-frost-bright hover:border-fa-frost/60",
  quiet: "border-fa-frost/25 text-fa-frost-dim hover:text-fa-frost-bright hover:border-fa-frost/50",
};

export interface PillButtonProps extends Omit<ButtonProps, "variant" | "size"> {
  tone?: PillTone;
}

const PillButton = forwardRef<HTMLButtonElement, PillButtonProps>(({ tone = "quiet", className, ...props }, ref) => (
  <Button
    ref={ref}
    variant="outline"
    className={cn(
      "min-h-11 min-w-11 gap-1.5 rounded-full bg-transparent px-4 py-0 text-xs font-normal leading-snug",
      "hover:bg-fa-frost/10 disabled:opacity-40",
      TONE[tone],
      className,
    )}
    {...props}
  />
));
PillButton.displayName = "PillButton";

export default PillButton;
