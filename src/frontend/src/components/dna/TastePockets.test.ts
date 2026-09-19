import { describe, expect, it } from "vitest";
import { MIN_POCKET_SHARE, pocketSummary, visiblePockets } from "../../lib/tastePockets";
import type { TasteCluster } from "../../store/api";

const pocket = (ordinal: number, share: number, names: string[] = ["A", "B", "C"]): TasteCluster => ({
  ordinal,
  share,
  titleCount: 100 * (ordinal + 1),
  exemplars: names.map((name) => ({ name, year: 2020, posterPath: null })),
});

describe("visiblePockets", () => {
  it("drops a pocket under the share floor — a weekend is not a taste", () => {
    const shown = visiblePockets([pocket(0, 0.4), pocket(1, MIN_POCKET_SHARE - 0.001), pocket(2, 0.2)]);
    expect(shown.map((p) => p.ordinal)).toEqual([0, 2]);
  });

  it("drops a pocket with nothing to show, and orders by share then ordinal", () => {
    const shown = visiblePockets([pocket(3, 0.2), pocket(1, 0.3, []), pocket(0, 0.2)]);
    expect(shown.map((p) => p.ordinal)).toEqual([0, 3]);
  });

  it("is empty for a cold account", () => {
    expect(visiblePockets(undefined)).toEqual([]);
    expect(visiblePockets([])).toEqual([]);
  });
});

describe("pocketSummary", () => {
  it("rounds the share and groups the count", () => {
    expect(pocketSummary({ ordinal: 0, share: 0.152, titleCount: 252, exemplars: [] })).toBe("252 titles · 15% of what you love");
    expect(pocketSummary({ ordinal: 0, share: 0.0349, titleCount: 1234, exemplars: [] })).toBe("1,234 titles · 3% of what you love");
  });
});
