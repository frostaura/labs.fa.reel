/**
 * The after-rating moment: what Reel said, what you said, and what happens next.
 *
 * The whole point of this copy is that it is not marketing. On the live data the model over-promises
 * by roughly a point and its predictions are close to uncorrelated with the founder's ratings, so
 * most of these lines are the product admitting it was wrong. Making them apologetic, cute or
 * defensive would all be worse than making them plain — a scorecard nobody trusts is worth nothing,
 * and the one thing that buys trust is saying the number out loud before being asked.
 *
 * Rules the strings hold to:
 *  - second person, always ("you said 5", never "the user rated it 5");
 *  - the two numbers first, because they are the content;
 *  - one clause about what happens next, and only when something actually does;
 *  - no exclamation marks, no "oops", no "our AI", no promises about future accuracy.
 */

/** What the rating endpoint hands back when Reel had committed to a number for the title. */
export interface RatingVerdict {
  /** The prediction Reel had made, at two decimals. */
  predictedBefore: number;
  /** Where it was shown — null when the evidence is a stored score rather than a logged serve. */
  surface: string | null;
  /** When it was shown — null for the same reason. */
  shownAt: string | null;
  /** predicted − actual. Positive means Reel over-promised. */
  delta: number;
}

/** How far off a prediction was, as a band the copy can branch on. */
export type VerdictBand = "spot-on" | "close" | "over" | "under";

/** Within this, Reel and you agreed — no hedging needed. */
export const SPOT_ON_TOLERANCE = 0.5;

/** Within this it counts as close; outside it, the copy names the direction of the miss. */
export const CLOSE_TOLERANCE = 1;

export function verdictBand(delta: number): VerdictBand {
  const magnitude = Math.abs(delta);
  if (magnitude <= SPOT_ON_TOLERANCE) return "spot-on";
  if (magnitude <= CLOSE_TOLERANCE) return "close";
  return delta > 0 ? "over" : "under";
}

/** The number as the card renders it — one decimal, so the toast and the card agree. */
export function formatPredicted(predicted: number): string {
  return predicted.toFixed(1);
}

/**
 * The lead clause. Always both numbers, always in that order: Reel's claim is what is on trial.
 */
export function verdictHeadline(predicted: number, actual: number): string {
  return `Reel said ${formatPredicted(predicted)} · you said ${actual}`;
}

/**
 * What happens next, in one clause. Deliberately different per band, and deliberately silent when
 * nothing happens: a line promising a retrain after a rating that changed nothing would be the
 * first thing the founder caught out.
 *
 * "Next", never "tonight" (R1-40): the nightly job retrains only when RetrainGate says one is owed
 * (10 new ratings, a 14-day-old model or a calibration breach), and one rating moves none of those.
 */
export function verdictFollowUp(band: VerdictBand): string {
  switch (band) {
    case "spot-on":
      return "that one landed";
    case "close":
      return "close enough to count";
    case "over":
      return "noted — that goes into your next retrain";
    case "under":
      return "under-called it — that goes into your next retrain";
  }
}

/**
 * How long ago the card was in front of you, when the exposure log can prove it. Null whenever it
 * cannot: the retro fallback knows a score existed but not that you ever saw it, and inventing
 * "shown 12 days ago" from a scoring timestamp would be the one dishonest string on the page.
 */
export function verdictWhen(shownAt: string | null, now: Date = new Date()): string | null {
  if (!shownAt) return null;
  const shown = new Date(shownAt);
  if (Number.isNaN(shown.getTime())) return null;

  const days = Math.floor((now.getTime() - shown.getTime()) / 86_400_000);
  if (days < 0) return null;
  if (days === 0) return "shown today";
  if (days === 1) return "shown yesterday";
  if (days < 30) return `shown ${days} days ago`;

  const months = Math.round(days / 30);
  return months <= 1 ? "shown last month" : `shown ${months} months ago`;
}

/** Human names for the surfaces the toast might report. Unknown values pass through unchanged. */
const SURFACE_LABELS: Record<string, string> = {
  FeedHero: "tonight's picks",
  FeedRow: "your feed",
  FeedRefill: "your feed",
  FeedPage: "your feed",
  GenreRail: "a genre rail",
  Digest: "the weekly digest",
  AskReel: "Ask Reel",
  Similar: "a more-like-this rail",
  Saved: "your queue",
  Search: "search",
};

export function verdictSurface(surface: string | null): string | null {
  if (!surface) return null;
  return SURFACE_LABELS[surface] ?? null;
}

/**
 * The whole line. One string, so every rating path renders identical copy and the Playwright
 * assertion has one thing to match.
 */
export function verdictCopy(verdict: RatingVerdict, actual: number, now: Date = new Date()): string {
  const band = verdictBand(verdict.delta);
  const parts = [verdictHeadline(verdict.predictedBefore, actual)];

  const when = verdictWhen(verdict.shownAt, now);
  const where = verdictSurface(verdict.surface);
  if (when && where) {
    parts.push(`${when} on ${where}`);
  } else if (when) {
    parts.push(when);
  }

  parts.push(verdictFollowUp(band));
  return parts.join(" — ");
}
