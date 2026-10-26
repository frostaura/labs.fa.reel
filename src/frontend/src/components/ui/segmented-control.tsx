import * as React from "react";
import { cn } from "../../lib/utils";

export interface SegmentedOption<T extends string> {
  value: T;
  label: React.ReactNode;
  disabled?: boolean;
}

export interface SegmentedControlProps<T extends string> {
  /** Names the group for assistive tech (`aria-label`). */
  label: string;
  options: readonly SegmentedOption<T>[];
  value: T;
  onValueChange: (value: T) => void;
  size?: "sm" | "default";
  className?: string;
}

/**
 * One choice out of a few, all visible (WS-9 task 8): a window (7d / 30d / all), a sort, a view.
 * A radio group under the hood — Tab reaches the chosen segment only, and the arrow keys, Home and
 * End move the choice, as the ARIA radio-group pattern expects.
 */
export function SegmentedControl<T extends string>({
  label,
  options,
  value,
  onValueChange,
  size = "default",
  className,
}: SegmentedControlProps<T>) {
  const refs = React.useRef<(HTMLButtonElement | null)[]>([]);
  const enabled = options.map((o, i) => (o.disabled ? -1 : i)).filter((i) => i >= 0);
  const current = options.findIndex((o) => o.value === value);
  // Tab lands on the chosen segment, or the first enabled one when nothing valid is chosen.
  const tabStop = current >= 0 && !options[current].disabled ? current : (enabled[0] ?? -1);

  const move = (from: number, step: number | "first" | "last") => {
    if (enabled.length === 0) return;
    const pos = enabled.indexOf(from);
    const nextPos =
      step === "first" ? 0 : step === "last" ? enabled.length - 1 : (pos + step + enabled.length) % enabled.length;
    const next = enabled[nextPos];
    onValueChange(options[next].value);
    refs.current[next]?.focus();
  };

  const onKeyDown = (event: React.KeyboardEvent, index: number) => {
    const step =
      event.key === "ArrowRight" || event.key === "ArrowDown"
        ? 1
        : event.key === "ArrowLeft" || event.key === "ArrowUp"
          ? -1
          : event.key === "Home"
            ? "first"
            : event.key === "End"
              ? "last"
              : null;
    if (step === null) return;
    event.preventDefault();
    move(index, step);
  };

  return (
    <div
      role="radiogroup"
      aria-label={label}
      className={cn("inline-flex items-center gap-0.5 rounded-control border border-fa-edge bg-fa-glass p-0.5", className)}
    >
      {options.map((option, i) => {
        const checked = i === current;
        return (
          <button
            key={option.value}
            ref={(el) => {
              refs.current[i] = el;
            }}
            type="button"
            role="radio"
            aria-checked={checked}
            data-state={checked ? "on" : "off"}
            disabled={option.disabled}
            tabIndex={i === tabStop ? 0 : -1}
            onClick={() => onValueChange(option.value)}
            onKeyDown={(e) => onKeyDown(e, i)}
            className={cn(
              "rounded-[5px] font-medium transition",
              "focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-fa-frost/60",
              "disabled:pointer-events-none disabled:opacity-50",
              size === "sm" ? "px-2.5 py-1 text-xs" : "px-3 py-1.5 text-sm",
              checked ? "bg-fa-frost/15 text-1" : "text-2 hover:text-1 hover:bg-fa-glass"
            )}
          >
            {option.label}
          </button>
        );
      })}
    </div>
  );
}
