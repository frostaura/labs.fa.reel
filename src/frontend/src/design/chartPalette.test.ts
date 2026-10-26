import { describe, expect, it } from "vitest";
import { CHART_CHROME, CHART_DIVERGING, CHART_SEMANTIC, CHART_SEQUENTIAL, CHART_SERIES, seriesColour } from "./chartPalette";
import { contrast } from "./contrast";

const INKS = ["#06121F", "#0B1B30", "#102744"];

describe("chart palette", () => {
  it.each([...CHART_SERIES, ...Object.values(CHART_SEMANTIC)])("%s holds 3:1 against every ink surface", (colour) => {
    for (const ink of INKS) expect(contrast(colour, ink)).toBeGreaterThanOrEqual(3);
  });

  it("has no repeated series colour", () => {
    expect(new Set(CHART_SERIES.map((c) => c.toLowerCase())).size).toBe(CHART_SERIES.length);
  });

  it("wraps series indexes, negative ones included", () => {
    expect(seriesColour(0)).toBe(CHART_SERIES[0]);
    expect(seriesColour(CHART_SERIES.length)).toBe(CHART_SERIES[0]);
    expect(seriesColour(-1)).toBe(CHART_SERIES[CHART_SERIES.length - 1]);
  });

  it("orders the sequential ramp dark to light, so more always reads as brighter", () => {
    const vsInk = CHART_SEQUENTIAL.map((c) => contrast(c, "#06121F"));
    for (let i = 1; i < vsInk.length; i++) expect(vsInk[i]).toBeGreaterThan(vsInk[i - 1]);
  });

  it("keeps every diverging step distinguishable from the ink it sits on", () => {
    for (const colour of CHART_DIVERGING) expect(contrast(colour, "#0B1B30")).toBeGreaterThanOrEqual(3);
  });

  it("keeps axis and tooltip text readable (AA for text)", () => {
    expect(contrast(CHART_CHROME.axis, "#06121F")).toBeGreaterThanOrEqual(4.5);
    expect(contrast(CHART_CHROME.tooltipText, CHART_CHROME.tooltipBackground)).toBeGreaterThanOrEqual(4.5);
  });
});
