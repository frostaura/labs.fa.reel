import { describe, expect, it } from "vitest";
import { divergingBars, genreBarGeometry, LARGEST_BAR_PCT, meanMarkerPct, MIN_SCALE } from "./tasteDna";

/**
 * Regression for "the genre fingerprint lies below the midpoint". The old width was
 * `Math.max(6, ((affinity - 5) / 5) * 100)`: negative for every affinity at or under 5, so 5.0, 3.0
 * and 0.5 rendered an identical stub with the correct number printed next to it — on the page that
 * is the product's viral loop. The rule these tests pin: the bar is the affinity on a literal 0–10
 * track, so every point of the scale gets its own length, and the mean tick is what carries
 * above/below the user's own average.
 */
describe("genreBarGeometry", () => {
  describe("across the full 0–10 scale, against a mean of 5", () => {
    const MEAN = 5;

    it("draws 0 as the minimum affordance, not an empty track", () => {
      expect(genreBarGeometry(0, MEAN)).toEqual({ fillPct: 1.5, aboveMean: false });
    });

    it("draws 2.5 as a quarter of the track", () => {
      expect(genreBarGeometry(2.5, MEAN)).toEqual({ fillPct: 25, aboveMean: false });
    });

    it("draws 5 — the mean itself — as half the track, on the at-or-above side", () => {
      expect(genreBarGeometry(5, MEAN)).toEqual({ fillPct: 50, aboveMean: true });
    });

    it("draws 7.5 as three quarters of the track", () => {
      expect(genreBarGeometry(7.5, MEAN)).toEqual({ fillPct: 75, aboveMean: true });
    });

    it("draws 10 as the whole track", () => {
      expect(genreBarGeometry(10, MEAN)).toEqual({ fillPct: 100, aboveMean: true });
    });

    it("gives the three values the old formula collapsed together three distinct bars", () => {
      const fills = [5, 3, 0.5].map((affinity) => genreBarGeometry(affinity, MEAN).fillPct);
      expect(fills).toEqual([50, 30, 5]);
    });
  });

  it("keeps the full-track resolution that made the anchored fill win over a diverging span", () => {
    // The founder's real top genres: an 0.7-point spread that a mean-anchored span would have
    // squeezed into a few pixels.
    expect(genreBarGeometry(6.5, 6.8).fillPct).toBe(65);
    expect(genreBarGeometry(7.2, 6.8).fillPct).toBe(72);
  });

  it("re-anchors direction on the user's own mean, not on the scale's midpoint", () => {
    expect(genreBarGeometry(6.5, 6.8).aboveMean).toBe(false);
    expect(genreBarGeometry(7.2, 6.8).aboveMean).toBe(true);
    // A 4.0 genre is above the bar of someone who rates everything low.
    expect(genreBarGeometry(4, 3.2).aboveMean).toBe(true);
  });

  it("decides direction at the precision the page prints, so 6.8 is never 'below your 6.8'", () => {
    expect(genreBarGeometry(6.79, 6.8).aboveMean).toBe(true);
    expect(genreBarGeometry(6.84, 6.8).aboveMean).toBe(true);
    expect(genreBarGeometry(6.74, 6.8).aboveMean).toBe(false);
  });

  it("never lets a bar escape the track, and clamps junk rather than painting outside it", () => {
    expect(genreBarGeometry(42, 5).fillPct).toBe(100);
    expect(genreBarGeometry(-3, 5).fillPct).toBe(1.5);
    expect(genreBarGeometry(Number.NaN, 5).fillPct).toBe(1.5);
  });
});

describe("meanMarkerPct", () => {
  it("places the tick at the mean's literal position on the 0–10 axis", () => {
    expect(meanMarkerPct(0)).toBe(0);
    expect(meanMarkerPct(5)).toBe(50);
    expect(meanMarkerPct(6.8)).toBe(68);
    expect(meanMarkerPct(10)).toBe(100);
  });

  it("clamps out-of-range means onto the track", () => {
    expect(meanMarkerPct(-1)).toBe(0);
    expect(meanMarkerPct(11)).toBe(100);
  });
});

describe("divergingBars (the page's charts, P1 G-12)", () => {
  it("spreads the founder-shaped data: genres within ±0.4 of the mean still fill at least 40% of the chart", () => {
    // Live shape: eight genres between 6.5 and 7.2 around a 6.8 mean.
    const chart = divergingBars([7.2, 7.0, 6.9, 6.8, 6.7, 6.6, 6.5, 7.1], 6.8);
    const widest = Math.max(...chart.bars.map((b) => b.widthPct));
    expect(widest).toBeGreaterThanOrEqual(40);
    expect(chart.scale).toBe(0.4);
  });

  it("puts a genre above the mean right of centre and one below left of it", () => {
    const { bars } = divergingBars([7.2, 6.4], 6.8);
    expect(bars[0]).toMatchObject({ side: "above", leftPct: 50, deltaLabel: "+0.4" });
    expect(bars[1]).toMatchObject({ side: "below", deltaLabel: "−0.4" });
    expect(bars[1].leftPct + bars[1].widthPct).toBe(50);
  });

  it("draws a value on the mean (at displayed precision) as level, with no bar", () => {
    const { bars } = divergingBars([6.79, 7.3], 6.8);
    expect(bars[0]).toEqual({ leftPct: 50, widthPct: 0, side: "level", deltaLabel: "±0.0" });
  });

  it("does not blow rounding noise up to full width: the scale never drops under MIN_SCALE", () => {
    const chart = divergingBars([6.86, 6.74], 6.8);
    expect(chart.scale).toBe(MIN_SCALE);
    expect(Math.max(...chart.bars.map((b) => b.widthPct))).toBeLessThan(LARGEST_BAR_PCT);
  });

  it("keeps every bar inside the chart, whatever the input", () => {
    const { bars } = divergingBars([10, 0, Number.NaN, 42], 5);
    for (const bar of bars) {
      expect(bar.leftPct).toBeGreaterThanOrEqual(0);
      expect(bar.leftPct + bar.widthPct).toBeLessThanOrEqual(100);
    }
  });
});
