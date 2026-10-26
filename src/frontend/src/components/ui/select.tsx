import * as React from "react";
import * as SelectPrimitive from "@radix-ui/react-select";
import { Check, ChevronDown } from "lucide-react";
import { cn } from "../../lib/utils";

export interface SelectOption {
  value: string;
  label: React.ReactNode;
  disabled?: boolean;
}

export interface SelectProps {
  /** Required: every select is labelled. Shown above the trigger unless `hideLabel`. */
  label: React.ReactNode;
  hideLabel?: boolean;
  options: readonly SelectOption[];
  value: string | undefined;
  onValueChange: (value: string) => void;
  /** Shown while nothing is chosen. */
  placeholder?: string;
  disabled?: boolean;
  id?: string;
  className?: string;
  /** Forwarded to the trigger, so specs can find it. */
  "data-testid"?: string;
}

/**
 * A labelled single-choice select (WS-9 task 8), on Radix: the listbox, typeahead, keyboard and
 * focus return come from the primitive, and the menu is styled with the tokens instead of the
 * operating system's. A native select element is counted by the design ratchet and may not be added.
 */
export function Select({
  label,
  hideLabel = false,
  options,
  value,
  onValueChange,
  placeholder,
  disabled,
  id,
  className,
  "data-testid": testId,
}: SelectProps) {
  const generated = React.useId();
  const triggerId = id ?? generated;
  return (
    <div className={cn("flex flex-col gap-1.5", className)}>
      <label htmlFor={triggerId} className={cn("text-caption font-medium text-2", hideLabel && "sr-only")}>
        {label}
      </label>
      <SelectPrimitive.Root value={value} onValueChange={onValueChange} disabled={disabled}>
        <SelectPrimitive.Trigger
          id={triggerId}
          data-testid={testId}
          className={cn(
            "inline-flex h-9 items-center justify-between gap-2 rounded-control border border-fa-edge bg-fa-glass px-3 text-sm text-1 transition",
            "hover:border-fa-edge-strong data-[placeholder]:text-3",
            "focus-visible:outline-none focus-visible:border-fa-frost/60 focus-visible:ring-2 focus-visible:ring-fa-frost/30",
            "disabled:cursor-not-allowed disabled:opacity-50"
          )}
        >
          <SelectPrimitive.Value placeholder={placeholder} />
          <SelectPrimitive.Icon asChild>
            <ChevronDown aria-hidden className="h-4 w-4 text-3" />
          </SelectPrimitive.Icon>
        </SelectPrimitive.Trigger>
        <SelectPrimitive.Portal>
          <SelectPrimitive.Content
            position="popper"
            sideOffset={4}
            className="z-50 max-h-[min(20rem,var(--radix-select-content-available-height))] min-w-[var(--radix-select-trigger-width)] overflow-hidden rounded-card border border-fa-edge bg-fa-ink-2 shadow-2xl"
          >
            <SelectPrimitive.Viewport className="p-1">
              {options.map((option) => (
                <SelectPrimitive.Item
                  key={option.value}
                  value={option.value}
                  disabled={option.disabled}
                  className={cn(
                    "relative flex cursor-pointer select-none items-center rounded-control py-1.5 pl-8 pr-3 text-sm text-2",
                    "outline-none data-[highlighted]:bg-fa-glass-strong data-[highlighted]:text-1 focus:bg-fa-glass-strong",
                    "data-[disabled]:pointer-events-none data-[disabled]:opacity-50"
                  )}
                >
                  <span className="absolute left-2 inline-flex h-4 w-4 items-center justify-center">
                    <SelectPrimitive.ItemIndicator>
                      <Check aria-hidden className="h-4 w-4 text-1" />
                    </SelectPrimitive.ItemIndicator>
                  </span>
                  <SelectPrimitive.ItemText>{option.label}</SelectPrimitive.ItemText>
                </SelectPrimitive.Item>
              ))}
            </SelectPrimitive.Viewport>
          </SelectPrimitive.Content>
        </SelectPrimitive.Portal>
      </SelectPrimitive.Root>
    </div>
  );
}
