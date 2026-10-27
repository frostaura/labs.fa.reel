import { describe, expect, it } from "vitest";
import { readBaseline, regressions, scan, scannedFiles, scanTree } from "./designRatchet";

describe("design ratchet — the scanner", () => {
  it("counts raw buttons outside ui/, and none inside it", () => {
    const text = `<button className="x">a</button><button>b</button>`;
    expect(scan("src/pages/Home.tsx", text).rawButton).toBe(2);
    expect(scan("src/components/ui/button.tsx", text).rawButton).toBeUndefined();
  });

  it("counts native selects", () => {
    expect(scan("src/pages/Settings.tsx", `<select value={x}><option/></select>`).nativeSelect).toBe(1);
  });

  it("counts hex colours but not ids, entities or the chart module", () => {
    expect(scan("src/a.tsx", `const c = "#A4D4F4"; const d = 'bg-[#fff]'; stroke="#0B1B30cc"`).hexColour).toBe(3);
    expect(scan("src/a.tsx", `href="#main" id="#settings-email" &#123; "#add-row"`).hexColour).toBeUndefined();
    expect(scan("src/design/chartPalette.ts", `"#A4D4F4"`).hexColour).toBeUndefined();
  });

  it("counts text opacity below 80, and leaves 80 and above, and other utilities, alone", () => {
    const text = `"text-fa-frost/60 text-fa-frost-dim/5 text-fa-frost/80 text-fa-frost/95 bg-fa-ink/40 border-fa-edge/50"`;
    expect(scan("src/a.tsx", text).lowTextOpacity).toBe(2);
  });

  it("counts outline-none only where no focus style sits in the same class string", () => {
    expect(scan("src/a.tsx", `"outline-none" 'px-2 outline-none'`).outlineWithoutFocus).toBe(2);
    expect(
      scan("src/a.tsx", `"focus-visible:outline-none focus-visible:ring-2" "outline-none focus:bg-fa-glass"`).outlineWithoutFocus,
    ).toBeUndefined();
  });

  // PH2-03: the TSX literal check never saw CSS, and .fa-range set `outline: none` with no focus style.
  it("counts a CSS outline:none only where no :focus or :focus-visible rule restyles the same selector", () => {
    const bare = `.fa-range { appearance: none; outline: none; }`;
    expect(scan("src/index.css", bare).outlineWithoutFocus).toBe(1);
    expect(scan("src/index.css", `.a{outline:0}\n.b { outline: none }`).outlineWithoutFocus).toBe(2);
    // A focus style for the same selector, on the element or on a part of it, answers it.
    expect(
      scan("src/index.css", `${bare}\n.fa-range:focus-visible::-webkit-slider-thumb { box-shadow: 0 0 0 3px red; }`)
        .outlineWithoutFocus,
    ).toBeUndefined();
    expect(scan("src/index.css", `${bare}\n.fa-range:focus { box-shadow: 0 0 0 3px red; }`).outlineWithoutFocus).toBeUndefined();
    // A focus style for a DIFFERENT selector does not; nor does one that only removes the outline.
    expect(scan("src/index.css", `${bare}\n.fa-ranger:focus-visible { box-shadow: 0 0 0 3px red; }`).outlineWithoutFocus).toBe(1);
    expect(scan("src/index.css", `.x:focus { outline: none; }`).outlineWithoutFocus).toBe(1);
    // Inside an at-rule, in an @apply, and never in a comment.
    expect(scan("src/index.css", `@media (hover: hover) { .y { outline: none; } }`).outlineWithoutFocus).toBe(1);
    expect(scan("src/index.css", `.z { @apply px-2 outline-none; }`).outlineWithoutFocus).toBe(1);
    expect(scan("src/index.css", `.z { @apply outline-none focus:border-fa-frost/40; }`).outlineWithoutFocus).toBeUndefined();
    expect(scan("src/index.css", `/* .c { outline: none } */ .c { color: red; }`).outlineWithoutFocus).toBeUndefined();
  });

  it("holds CSS to the outline rule only: its hex tokens and @apply opacity are the token layer's business", () => {
    expect(scan("src/index.css", `/* #A4D4F4 */ .i { @apply text-fa-frost-dim/60; }`)).toEqual({});
  });

  it("flags a rise above the baseline and a new file's first violation, never a fall", () => {
    const baseline = { "src/a.tsx": { rawButton: 2 } };
    expect(regressions({ "src/a.tsx": { rawButton: 1 } }, baseline)).toEqual([]);
    expect(regressions({ "src/a.tsx": { rawButton: 3 } }, baseline)).toHaveLength(1);
    expect(regressions({ "src/new.tsx": { hexColour: 1 } }, baseline)).toEqual([
      expect.stringContaining("src/new.tsx: 1 × a hex colour"),
    ]);
  });
});

describe("design ratchet — the tree", () => {
  it("scans the stylesheets as well as the TS and TSX sources", () => {
    expect(scannedFiles()).toEqual(expect.arrayContaining(["src/index.css", "src/App.tsx"]));
  });

  // The gate itself. A failure names each file and rule; fix the code, never raise the baseline.
  it("no file has more raw buttons, selects, hex colours, faint text or bare outline-none than its baseline", () => {
    expect(regressions(scanTree(), readBaseline())).toEqual([]);
  });
});
