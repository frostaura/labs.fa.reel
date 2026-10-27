import { describe, expect, it } from "vitest";
import { cn } from "./cn";

describe("cn", () => {
  it("merges class names and drops falsy values", () => {
    expect(cn("a", false, null, undefined, "c")).toBe("a c");
  });

  it("lets later Tailwind utilities win over earlier conflicting ones", () => {
    expect(cn("px-2", "px-4")).toBe("px-4");
  });

  // R1-37 (WS-2B CR-2): tailwind-merge knew none of Reel's own tokens, so `text-caption` read as a
  // colour and erased the chip's `text-2`, and the base `text-sm` survived beside it.
  describe("Reel's type scale is a font size, never a colour", () => {
    it.each(["caption", "body", "card-title", "row", "page", "display"])("text-%s replaces text-sm and keeps the colour", (size) => {
      expect(cn("text-sm text-2", `text-${size}`)).toBe(`text-2 text-${size}`);
    });

    it("an earlier type token gives way to a later Tailwind size", () => {
      expect(cn("text-caption", "text-lg")).toBe("text-lg");
    });

    it("a type token replaces the leading it would set", () => {
      expect(cn("leading-none", "text-body")).toBe("text-body");
    });
  });

  describe("the three text steps are colours", () => {
    it.each(["1", "2", "3"])("text-%s replaces another text colour and keeps the size", (step) => {
      expect(cn("text-sm text-fa-frost", `text-${step}`)).toBe(`text-sm text-${step}`);
    });

    it("a frost colour replaces a text step", () => {
      expect(cn("text-2", "text-fa-frost-bright")).toBe("text-fa-frost-bright");
    });
  });

  describe("the pure size roles (fa-caption, fa-body) are font sizes", () => {
    // `.fa-caption` lives in @layer components, so a `text-sm` beside it always won in the cascade:
    // the UndoToast's caption-sized pill rendered at 14 px.
    it.each(["fa-caption", "fa-body"])("%s drops an earlier Tailwind size", (role) => {
      expect(cn("text-sm text-2", role)).toBe(`text-2 ${role}`);
    });

    it("a later Tailwind size drops the role class", () => {
      expect(cn("fa-caption", "text-sm")).toBe("text-sm");
    });

    it("a later role class drops an earlier one", () => {
      expect(cn("fa-body", "fa-caption")).toBe("fa-caption");
    });

    // A bundle role (size, weight, case and tracking) is not a size: a later size must not strip
    // its uppercase and tracking with it.
    it("fa-overline is a bundle and stays beside a size", () => {
      expect(cn("fa-overline", "text-sm")).toBe("fa-overline text-sm");
    });
  });

  describe("the shape tokens merge with their Tailwind siblings", () => {
    it.each([
      ["rounded-control", "rounded-full"],
      ["rounded-full", "rounded-card"],
      ["rounded-md", "rounded-panel"],
      ["px-4", "px-gutter"],
      ["-mx-4", "-mx-gutter"],
      ["w-40", "w-card"],
      ["max-w-7xl", "max-w-shell"],
      ["shadow-lg", "shadow-fa-glass"],
      ["bg-none", "bg-fa-gradient"],
    ])("%s gives way to %s", (earlier, later) => {
      expect(cn(earlier, later)).toBe(later);
    });

    it("a gradient background is an image, so a colour beside it stays", () => {
      expect(cn("bg-fa-ink", "bg-fa-gradient")).toBe("bg-fa-ink bg-fa-gradient");
    });

    it("the card shadow is a shadow, so a shadow colour beside it stays", () => {
      expect(cn("shadow-black/20", "shadow-fa-glass")).toBe("shadow-black/20 shadow-fa-glass");
    });
  });

  it("the touch-target variant merges like any other variant", () => {
    expect(cn("coarse:min-h-9", "coarse:min-h-11")).toBe("coarse:min-h-11");
    expect(cn("coarse:min-h-11", "min-h-9")).toBe("coarse:min-h-11 min-h-9");
  });
});
