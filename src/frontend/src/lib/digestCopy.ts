/**
 * The weekly digest rail's heading (IR1S-03). "New & high for you this week" is a claim: every card
 * cleared the "high for you" bar. When the build had to relax that bar to fill the rail, the API
 * returns the build's own reason as `degradedReason`, and the rail must stop making the claim — it
 * is retitled and says why underneath, in the build's words.
 */
export const DIGEST_TITLE_HIGH = "New & high for you this week";
export const DIGEST_TITLE_RELAXED = "New this week";

export interface DigestRailCopy {
  title: string;
  /** The build's reason the bar was relaxed, or null when every card cleared it. */
  caption: string | null;
}

export function digestRailCopy(degradedReason: string | null | undefined): DigestRailCopy {
  const reason = degradedReason?.trim() ?? "";
  return reason.length > 0
    ? { title: DIGEST_TITLE_RELAXED, caption: reason }
    : { title: DIGEST_TITLE_HIGH, caption: null };
}
