import { readFileSync } from "node:fs";
import path from "node:path";
import { describe, expect, it } from "vitest";
import { contrast } from "../../design/contrast";
import { CHIP_TONE, SUMMARY_MUTED_TEXT } from "./conditionTone";

/**
 * The condition summary is AA on every surface that hosts it (R1-44). The colours come from the
 * class names themselves — the token's channels in index.css, the fill's alpha from its Tailwind
 * modifier — so changing a class back to a dimmer token fails here before it fails axe in a browser
 * (tests-e2e/conditions-summary-contrast.spec.ts runs the same check painted).
 */
const css = readFileSync(path.resolve(import.meta.dirname, "../../index.css"), "utf8");

type Rgb = [number, number, number];

function channels(name: string): Rgb {
  const m = new RegExp(`--c-${name}:\\s*([0-9]+) ([0-9]+) ([0-9]+);`).exec(css);
  if (!m) throw new Error(`--c-${name} is missing from index.css`);
  return [Number(m[1]), Number(m[2]), Number(m[3])];
}

/** `fg` at `alpha` over the opaque `bg`. */
function over(fg: Rgb, alpha: number, bg: Rgb): Rgb {
  return [0, 1, 2].map((i) => fg[i] * alpha + bg[i] * (1 - alpha)) as Rgb;
}

const hex = (c: Rgb) => `#${c.map((v) => Math.round(v).toString(16).padStart(2, "0")).join("")}`;

/** `text-3` → the text-3 channels; `text-fa-success` → the success channels. */
function textColour(classes: string): Rgb {
  const token = classes.split(" ").find((c) => c.startsWith("text-"));
  if (!token) throw new Error(`no text colour in "${classes}"`);
  const name = token.slice("text-".length);
  return channels(/^[123]$/.test(name) ? `text-${name}` : name.replace(/^fa-/, ""));
}

/** The chip's fill as an alpha layer: `bg-fa-glass` is frost at 0.06; `bg-fa-success/15` is success at 0.15. */
function fill(classes: string): { colour: Rgb; alpha: number } {
  const token = classes.split(" ").find((c) => c.startsWith("bg-"));
  if (!token) throw new Error(`no fill in "${classes}"`);
  if (token === "bg-fa-glass") return { colour: channels("frost"), alpha: 0.06 };
  const m = /^bg-fa-([a-z-]+)\/(\d+)$/.exec(token);
  if (!m) throw new Error(`unrecognised fill ${token}`);
  return { colour: channels(m[1]), alpha: Number(m[2]) / 100 };
}

const ink = channels("ink");
const ink2 = channels("ink-2");
const ink3 = channels("ink-3");
const frost = channels("frost");

/** Where the summary is painted: the dialogs (ink-2), the Settings card (glass on ink), and nested cards. */
const HOSTS: Record<string, Rgb> = {
  "the dialog surface (ink-2)": ink2,
  "a glass card on the page (ink)": over(frost, 0.06, ink),
  "a glass card inside a dialog": over(frost, 0.06, ink2),
  "ink-3": ink3,
};

describe("condition summary contrast (R1-44)", () => {
  for (const [host, surface] of Object.entries(HOSTS)) {
    it.each(Object.entries(CHIP_TONE))(`the %s chip is AA on ${host}`, (_tone, classes) => {
      const { colour, alpha } = fill(classes);
      const background = over(colour, alpha, surface);
      expect(contrast(hex(textColour(classes)), hex(background))).toBeGreaterThanOrEqual(4.5);
    });

    it(`the labels and "+N more" are AA on ${host}`, () => {
      expect(contrast(hex(textColour(SUMMARY_MUTED_TEXT)), hex(surface))).toBeGreaterThanOrEqual(4.5);
    });
  }

  it("the old muted token fails where the browser found it failing, so this check can see the defect", () => {
    // frost-dim under the neutral chip's glass on the dialog: axe measured 4.18:1.
    const background = over(frost, 0.06, ink2);
    expect(contrast(hex(channels("frost-dim")), hex(background))).toBeLessThan(4.5);
  });
});
