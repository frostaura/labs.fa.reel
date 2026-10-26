import type { ModelLearned } from "../store/api";
import { relTime } from "./format";

/**
 * How long a retrain stays news on Home's status line. The segment is about ONE retrain, and the
 * band it replaced had to be dismissed by hand per retrain; a status line has no room for a dismiss
 * control (and none of the 44 px a phone target needs), so the segment simply ages out instead.
 */
export const LEARNED_NEWS_MS = 48 * 60 * 60 * 1000;

/**
 * The retrain as one segment of Home's status line (FD-15) — "Retrained 3h ago on 14 new ratings,
 * 3 picks changed" — or null once it is no longer news (older than {@link LEARNED_NEWS_MS}, or a
 * timestamp that does not parse). The time goes through `relTime` like every other relative date.
 *
 * It says what the retrain learned from and how much of tonight's page moved — and NOT whether the
 * model "runs closer on your scores": the server stopped sending that number (WS-5), because 0 of
 * 101 evaluations beat popularity and a retrain-to-retrain MAE delta on that footing is noise
 * dressed as progress. A clause whose number is zero is dropped — "0 picks changed" is padding.
 */
export function learnedStatusSegment(learned: ModelLearned | null | undefined, now: number = Date.now()): string | null {
  const parts = learnedStatusParts(learned, now);
  return parts === null ? null : `${parts.when}${parts.ratings}${parts.changed}`;
}

/**
 * The same segment in its three clauses, so the status line can drop the middle one where the line
 * must share a row with the mood chips (`lg`–`2xl`) without dropping the other two: when it
 * retrained ("Retrained 3h ago"), what it learned from (" on 14 new ratings") and what moved
 * (", 3 picks changed", or "" when nothing did).
 */
export function learnedStatusParts(
  learned: ModelLearned | null | undefined,
  now: number = Date.now()
): { when: string; ratings: string; changed: string } | null {
  if (!learned) return null;
  const trainedAt = new Date(learned.trainedAt).getTime();
  if (Number.isNaN(trainedAt) || now - trainedAt > LEARNED_NEWS_MS) return null;
  return {
    when: `Retrained ${relTime(learned.trainedAt)}`,
    ratings: ` on ${learned.newRatingsSinceLast.toLocaleString("en-US")} new ${
      learned.newRatingsSinceLast === 1 ? "rating" : "ratings"
    }`,
    changed:
      learned.picksChanged > 0
        ? `, ${learned.picksChanged.toLocaleString("en-US")} ${learned.picksChanged === 1 ? "pick" : "picks"} changed`
        : "",
  };
}
