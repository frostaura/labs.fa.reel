import { readFileSync } from "node:fs";
import path from "node:path";
import { describe, expect, it } from "vitest";
import { contrast } from "./contrast";

/**
 * The token layer has two halves — the CSS channels in index.css and Tailwind's theme — and they
 * must say the same thing. These fail when one moves without the other.
 */
const css = readFileSync(path.resolve(import.meta.dirname, "../index.css"), "utf8");

function channels(name: string): string {
  const m = new RegExp(`--c-${name}:\\s*([0-9]+ [0-9]+ [0-9]+);`).exec(css);
  if (!m) throw new Error(`--c-${name} is missing from index.css`);
  return m[1];
}

// The config is plain JS outside tsconfig's `src`; a computed specifier keeps tsc from demanding types.
const TAILWIND_CONFIG = "../../tailwind.config.js";
const tailwind = (await import(/* @vite-ignore */ TAILWIND_CONFIG)) as { default: { theme: { extend: unknown } } };
const theme = tailwind.default.theme.extend as {
  colors: Record<string, unknown>;
  textColor: Record<string, string>;
  fontSize: Record<string, [string, Record<string, string>]>;
  borderRadius: Record<string, string>;
};

const INKS = ["ink", "ink-2", "ink-3"];

describe("design tokens", () => {
  it.each(["ink", "ink-2", "ink-3", "frost", "frost-bright", "frost-dim", "success", "warning", "danger"])(
    "fa-%s reads its channels with an alpha slot, so opacity modifiers work",
    (name) => {
      channels(name);
      expect(theme.colors[`fa-${name}`]).toBe(`rgb(var(--c-${name}) / <alpha-value>)`);
    },
  );

  it("keeps the translucent tokens on the frost and ink channels", () => {
    const frost = channels("frost").split(" ").join(", ");
    const ink = channels("ink").split(" ").join(", ");
    for (const name of ["fa-glass", "fa-glass-strong", "fa-edge", "fa-edge-strong"]) {
      expect(theme.colors[name]).toMatch(new RegExp(`^rgba\\(${frost}, 0\\.\\d+\\)$`));
    }
    for (const name of ["fa-scrim", "fa-scrim-strong"]) {
      expect(theme.colors[name]).toMatch(new RegExp(`^rgba\\(${ink}, 0\\.\\d+\\)$`));
    }
  });

  it.each([
    ["text-1", "230 242 255"],
    ["text-2", "164 212 244"],
    ["text-3", "127 163 198"],
  ])("%s is the specified colour and is AA (4.5:1) on every ink surface", (name, expected) => {
    expect(channels(name)).toBe(expected);
    expect(theme.textColor[name.slice(-1)]).toBe(`rgb(var(--c-${name}) / <alpha-value>)`);
    for (const ink of INKS) expect(contrast(channels(name), channels(ink))).toBeGreaterThanOrEqual(4.5);
  });

  it("has the specified type scale", () => {
    const size = (k: string) => theme.fontSize[k][0];
    expect(size("caption")).toBe("12.5px");
    expect(size("body")).toBe("15px");
    expect(size("card-title")).toBe("15px");
    expect(theme.fontSize["card-title"][1].fontWeight).toBe("600");
    expect(size("row")).toBe("20px");
    expect(size("page")).toBe("30px");
    expect(size("display")).toMatch(/^clamp\(36px, .+, 44px\)$/);
  });

  it("has the specified radii", () => {
    expect(css).toMatch(/--fa-radius-control:\s*6px;/);
    expect(css).toMatch(/--fa-radius-card:\s*10px;/);
    expect(css).toMatch(/--fa-radius-panel:\s*16px;/);
    expect(theme.borderRadius.control).toBe("var(--fa-radius-control)");
    expect(theme.borderRadius.card).toBe("var(--fa-radius-card)");
    expect(theme.borderRadius.panel).toBe("var(--fa-radius-panel)");
  });
});
