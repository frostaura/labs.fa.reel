import { describe, expect, it } from "vitest";
import { readBaseline, regressions, scan, scanTree } from "./designRatchet";

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
  // The gate itself. A failure names each file and rule; fix the code, never raise the baseline.
  it("no file has more raw buttons, selects, hex colours, faint text or bare outline-none than its baseline", () => {
    expect(regressions(scanTree(), readBaseline())).toEqual([]);
  });
});
