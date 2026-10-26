/**
 * The chart palette (WS-9 task 7). Charts are the one place colour literals are allowed outside the
 * token layer, because Recharts writes them into SVG presentation attributes, where `var()` is not
 * reliable. Every chart imports its colours from here; a hex colour anywhere else in a TSX file is
 * lint-banned.
 *
 * Every series colour holds at least 3:1 against each ink surface (WCAG 1.4.11, non-text contrast),
 * which chartPalette.test.ts checks. Series are ordered so the first two are the most distinct pair.
 */

/** Categorical series, in the order a chart should take them. */
export const CHART_SERIES = [
  "#A4D4F4", // frost
  "#F6C667", // amber
  "#7CE3B6", // mint
  "#F08484", // coral
  "#B9A6F2", // lavender
  "#6FD3D8", // teal
] as const;

/** The series colour for index `i`, wrapping past the end. */
export function seriesColour(i: number): string {
  const n = CHART_SERIES.length;
  return CHART_SERIES[((i % n) + n) % n];
}

/** Meaning-bearing colours: a chart that encodes good/bad uses these, never a series colour. */
export const CHART_SEMANTIC = {
  positive: "#7CE3B6",
  negative: "#F08484",
  caution: "#F6C667",
  neutral: "#7FA3C6",
} as const;

/** Low → high ramp for one quantity (heat, density). Ends at frost-bright. */
export const CHART_SEQUENTIAL = ["#2B5A85", "#4C7FAE", "#73A6D2", "#A4D4F4", "#D4ECFF"] as const;

/** Below → above a midpoint (error, delta). The midpoint is the neutral tertiary text colour. */
export const CHART_DIVERGING = ["#F08484", "#D49A9E", "#7FA3C6", "#8FCFB3", "#7CE3B6"] as const;

/** Chart furniture: grid, axes, tooltip. */
export const CHART_CHROME = {
  grid: "rgba(164, 212, 244, 0.12)",
  axis: "#7FA3C6",
  axisLine: "rgba(164, 212, 244, 0.18)",
  label: "#A4D4F4",
  tooltipBackground: "#0B1B30",
  tooltipBorder: "rgba(164, 212, 244, 0.18)",
  tooltipText: "#E6F2FF",
} as const;
