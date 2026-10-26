import * as React from "react";
import * as TabsPrimitive from "@radix-ui/react-tabs";
import { cn } from "../../lib/utils";

/**
 * Tabs (WS-9 task 8), on Radix: the tablist / tab / tabpanel roles, arrow-key movement and the
 * panel wiring come from the primitive. Use `SegmentedControl` instead when the choice changes what
 * a single view shows rather than switching between panels.
 */
export function Tabs(props: React.ComponentPropsWithoutRef<typeof TabsPrimitive.Root>) {
  return <TabsPrimitive.Root {...props} />;
}

export const TabsList = React.forwardRef<
  React.ElementRef<typeof TabsPrimitive.List>,
  React.ComponentPropsWithoutRef<typeof TabsPrimitive.List>
>(({ className, ...props }, ref) => (
  <TabsPrimitive.List ref={ref} className={cn("inline-flex items-center gap-1 border-b border-fa-edge", className)} {...props} />
));
TabsList.displayName = "TabsList";

export const TabsTrigger = React.forwardRef<
  React.ElementRef<typeof TabsPrimitive.Trigger>,
  React.ComponentPropsWithoutRef<typeof TabsPrimitive.Trigger>
>(({ className, ...props }, ref) => (
  <TabsPrimitive.Trigger
    ref={ref}
    className={cn(
      "-mb-px border-b-2 border-transparent px-3 py-2 text-sm font-medium text-2 transition hover:text-1",
      "focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-fa-frost/60 focus-visible:rounded-control",
      "data-[state=active]:border-fa-frost data-[state=active]:text-1",
      "disabled:pointer-events-none disabled:opacity-50",
      className
    )}
    {...props}
  />
));
TabsTrigger.displayName = "TabsTrigger";

export const TabsContent = React.forwardRef<
  React.ElementRef<typeof TabsPrimitive.Content>,
  React.ComponentPropsWithoutRef<typeof TabsPrimitive.Content>
>(({ className, ...props }, ref) => (
  <TabsPrimitive.Content
    ref={ref}
    className={cn("pt-4 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-fa-frost/40 focus-visible:rounded-control", className)}
    {...props}
  />
));
TabsContent.displayName = "TabsContent";
