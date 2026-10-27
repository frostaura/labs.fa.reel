import clsx, { type ClassValue } from "clsx";
import { extendTailwindMerge } from "tailwind-merge";

/**
 * tailwind-merge taught Reel's own tokens (R1-37). Out of the box it knows only Tailwind's default
 * scale, so it misfiled every custom token: `text-caption` read as a text COLOUR (a chip's
 * `text-2` vanished under it, and the base `text-sm` survived beside it), and `rounded-control`,
 * `px-gutter`, `w-card`, `max-w-shell`, `shadow-fa-glass` and `bg-fa-gradient` were either unknown
 * (both sides kept, the cascade picks) or the wrong group. Every key below mirrors
 * tailwind.config.js; cn.test.ts holds each one.
 */
const twMerge = extendTailwindMerge<"fa-size-role">({
  extend: {
    theme: {
      borderRadius: ["control", "card", "panel"],
      spacing: ["gutter"],
    },
    classGroups: {
      "font-size": [{ text: ["caption", "body", "card-title", "row", "page", "display"] }],
      "text-color": [{ text: ["1", "2", "3"] }],
      w: [{ w: ["card"] }],
      "max-w": [{ "max-w": ["shell"] }],
      shadow: [{ shadow: ["fa-glass"] }],
      "bg-image": [{ bg: ["fa-gradient"] }],
      // The two index.css roles that set only a size and its leading. `.fa-caption` sits in
      // @layer components, so a `text-sm` beside it won the cascade every time; as a group that
      // conflicts with font-size, whichever comes later wins. `fa-overline` is deliberately absent:
      // it bundles weight, case and tracking, which a later size must not strip.
      "fa-size-role": ["fa-caption", "fa-body"],
    },
    conflictingClassGroups: {
      "fa-size-role": ["font-size", "leading"],
      "font-size": ["fa-size-role"],
    },
  },
});

export function cn(...inputs: ClassValue[]) {
  return twMerge(clsx(inputs));
}
