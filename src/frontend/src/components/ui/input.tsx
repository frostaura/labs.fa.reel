import * as React from "react";
import { cn } from "../../lib/utils";

export interface InputProps extends Omit<React.InputHTMLAttributes<HTMLInputElement>, "id"> {
  /** Required: every input is labelled. Shown above the field unless `hideLabel`. */
  label: React.ReactNode;
  /** Keeps the label for assistive tech only, for a field whose purpose the layout already shows. */
  hideLabel?: boolean;
  /** Help under the field, tied to it with `aria-describedby`. */
  hint?: React.ReactNode;
  /** An error under the field. Sets `aria-invalid` and is announced with the field. */
  error?: React.ReactNode;
  id?: string;
  /** Classes for the wrapper; `className` goes on the input itself. */
  containerClassName?: string;
}

/** A labelled text input (WS-9 task 8). An input without a label is not offered. */
export const Input = React.forwardRef<HTMLInputElement, InputProps>(
  ({ label, hideLabel = false, hint, error, id, className, containerClassName, ...props }, ref) => {
    const generated = React.useId();
    const inputId = id ?? generated;
    const hintId = hint ? `${inputId}-hint` : undefined;
    const errorId = error ? `${inputId}-error` : undefined;
    const describedBy = [props["aria-describedby"], hintId, errorId].filter(Boolean).join(" ") || undefined;
    return (
      <div className={cn("flex flex-col gap-1.5", containerClassName)}>
        <label htmlFor={inputId} className={cn("text-caption font-medium text-2", hideLabel && "sr-only")}>
          {label}
        </label>
        <input
          ref={ref}
          id={inputId}
          aria-invalid={error ? true : undefined}
          {...props}
          aria-describedby={describedBy}
          className={cn(
            "h-9 rounded-control border border-fa-edge bg-fa-glass px-3 text-sm text-1 placeholder:text-3 transition coarse:min-h-11",
            "focus-visible:outline-none focus-visible:border-fa-frost/60 focus-visible:ring-2 focus-visible:ring-fa-frost/30",
            "disabled:cursor-not-allowed disabled:opacity-50",
            error && "border-fa-danger/60",
            className
          )}
        />
        {hint && (
          <p id={hintId} className="text-caption text-3">
            {hint}
          </p>
        )}
        {error && (
          <p id={errorId} className="text-caption text-fa-danger">
            {error}
          </p>
        )}
      </div>
    );
  }
);
Input.displayName = "Input";
