import * as React from "react";
import { Slot } from "@radix-ui/react-slot";
import { cva, type VariantProps } from "class-variance-authority";
import { cn } from "../../lib/utils";

/**
 * The one button (WS-9 task 8). Every clickable control outside `ui/` is this, or another `ui/`
 * primitive built on the same tokens: a raw `<button className=…>` is counted by the design
 * ratchet (scripts/designRatchet.ts) and may not be added.
 *
 * Every variant carries the same visible keyboard focus ring.
 */
const buttonVariants = cva(
  "inline-flex items-center justify-center gap-2 whitespace-nowrap rounded-control text-sm font-medium transition " +
    "focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-fa-frost/60 focus-visible:ring-offset-1 focus-visible:ring-offset-fa-ink " +
    "disabled:pointer-events-none disabled:opacity-50 " +
    // A 44 × 44 target on any touch-first pointer, whatever the size below says (IREV3S-08, O-15);
    // min-height and min-width outrank a caller's `h-7 w-7`, so dense rows stay compact under a mouse
    // only. An inline `link` button is the one exception (WCAG 2.5.8's inline-text carve-out).
    "coarse:min-h-11 coarse:min-w-11",
  {
    variants: {
      variant: {
        default: "border border-fa-edge bg-fa-glass-strong hover:bg-fa-glass hover:border-fa-edge-strong text-1",
        primary: "bg-fa-frost/15 border border-fa-frost/40 text-1 hover:bg-fa-frost/25",
        destructive: "border border-fa-danger/40 bg-fa-danger/10 text-fa-danger hover:bg-fa-danger/20",
        outline: "border border-fa-edge bg-transparent text-2 hover:bg-fa-glass hover:text-1 hover:border-fa-edge-strong",
        ghost: "text-2 hover:text-1 hover:bg-fa-glass",
        link: "text-1 underline-offset-4 hover:underline coarse:min-h-0 coarse:min-w-0",
        /** A compact rounded action, e.g. a suggestion or a filter shortcut. A chip that is ON or OFF is `<Chip>`. */
        chip: "rounded-full border border-fa-edge bg-fa-glass text-2 hover:text-1 hover:border-fa-edge-strong",
      },
      size: {
        default: "px-4 py-2",
        sm: "px-3 py-1.5 text-xs",
        lg: "px-6 py-2.5",
        chip: "px-3 py-1 text-caption",
        /** Square, for an icon with an aria-label. 36 px; `icon-sm` is 28 px for dense rows. Both are 44 px under touch. */
        icon: "h-9 w-9 p-0",
        "icon-sm": "h-7 w-7 p-0",
      },
    },
    compoundVariants: [{ variant: "chip", size: "default", className: "px-3 py-1 text-caption" }],
    defaultVariants: {
      variant: "default",
      size: "default",
    },
  }
);

export interface ButtonProps
  extends React.ButtonHTMLAttributes<HTMLButtonElement>,
    VariantProps<typeof buttonVariants> {
  asChild?: boolean;
}

const Button = React.forwardRef<HTMLButtonElement, ButtonProps>(
  ({ className, variant, size, asChild = false, type, ...props }, ref) => {
    const Comp = asChild ? Slot : "button";
    return (
      <Comp
        className={cn(buttonVariants({ variant, size, className }))}
        ref={ref}
        // A button inside a form submits by default; every Reel button that should submit says so.
        type={asChild ? type : (type ?? "button")}
        {...props}
      />
    );
  }
);
Button.displayName = "Button";

// buttonVariants stays module-local: exporting a non-component alongside a component breaks
// Fast Refresh (react-refresh/only-export-components), and nothing outside this file uses it.
export { Button };
