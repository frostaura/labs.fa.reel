/**
 * The line that accounts for a person's credits the conditions hold out (R1-41).
 *
 * PersonEndpoints lists only credits that pass every content rule and the maturity ceiling (the
 * same rule typeahead follows, FD-5) and reports the rest as `hiddenByConditions`. Nothing may
 * vanish unexplained, and a person whose every credit is held out must not read as having none.
 */
export function personHiddenLine(hidden: number | null | undefined, shown: number): string | null {
  if (!hidden || hidden <= 0) return null;
  if (shown === 0) {
    return hidden === 1
      ? "Their 1 title in Reel is hidden by your conditions."
      : `All ${hidden} of their titles in Reel are hidden by your conditions.`;
  }
  return hidden === 1
    ? "1 more of their titles is hidden by your conditions."
    : `${hidden} more of their titles are hidden by your conditions.`;
}
