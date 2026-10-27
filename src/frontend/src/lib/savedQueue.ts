import type { SavedQueueItem } from "../store/savedTypes";

/** The four orders the shelf offers, in the order the control renders them. */
export const SAVED_SORTS = ["tonight", "predicted", "added", "runtime"] as const;

export type SavedSort = (typeof SAVED_SORTS)[number];

/** Control labels and the promise each order makes, used as its tooltip. */
export const SAVED_SORT_META: Record<SavedSort, { label: string; hint: string }> = {
  tonight: { label: "Tonight", hint: "Streaming now first, then best for you, then shortest" },
  predicted: { label: "Best for you", hint: "Highest predicted rating first" },
  added: { label: "Recently added", hint: "Newest save first" },
  runtime: { label: "Shortest", hint: "Shortest runtime first" },
};

/** Narrows an arbitrary string (a URL param, a stored value) to a sort we actually serve. */
export function isSavedSort(value: string | null | undefined): value is SavedSort {
  return value != null && (SAVED_SORTS as readonly string[]).includes(value);
}

/**
 * Every card that could honestly be pinned as "watch tonight", best first.
 *
 * This deliberately mirrors `SavedQueue.WatchTonightCandidates` on the server rather than trusting
 * the server's single `watchTonight` flag, because "not tonight" has to advance to the NEXT
 * candidate without a round trip. The server's pick is the authority and this list's head must
 * equal it — asserted in the tests on both sides — but the tail is the client's to walk.
 *
 * The rules, restated because they are product decisions and not filtering trivia: it must be
 * streaming now, it must carry a prediction, and it must have a KNOWN runtime inside the budget.
 * An unknown runtime is excluded because the pin's entire claim is that the title fits the evening.
 */
export function tonightCandidates(
  items: readonly SavedQueueItem[],
  eveningBudgetMinutes: number
): SavedQueueItem[] {
  return items
    .filter(
      (item) =>
        item.availableNow &&
        item.predictedRating != null &&
        Number.isFinite(item.predictedRating) &&
        item.runtimeMinutes != null &&
        item.runtimeMinutes <= eveningBudgetMinutes
    )
    .sort(
      (a, b) =>
        b.predictedRating! - a.predictedRating! ||
        a.runtimeMinutes! - b.runtimeMinutes! ||
        // Same score, same length: newest save first, then id — a total order, so the pinned card
        // cannot swap under the user between refetches.
        Date.parse(b.savedAt) - Date.parse(a.savedAt) ||
        a.titleId.localeCompare(b.titleId)
    );
}

/**
 * The card to pin, given how many times the user has said "not tonight" this session. Skipping past
 * the end returns undefined rather than wrapping: having declined every candidate, re-offering the
 * first one is the app arguing with them.
 */
export function pinnedPick(
  items: readonly SavedQueueItem[],
  eveningBudgetMinutes: number,
  skipCount: number
): SavedQueueItem | undefined {
  return tonightCandidates(items, eveningBudgetMinutes)[skipCount];
}

/** The cards below the pin: everything except whatever is currently pinned. */
export function queueBelowPin(
  items: readonly SavedQueueItem[],
  pinned: SavedQueueItem | undefined
): SavedQueueItem[] {
  return pinned ? items.filter((item) => item.titleId !== pinned.titleId) : [...items];
}

/**
 * "8.4 ± 1.9" — the honest form of the promise. The band is the account's own measured mean
 * absolute error, so it is omitted rather than defaulted when the closed loop has no pairs to
 * measure it from: "± 0.0" would be the least honest number on the screen.
 */
export function predictionWithBand(
  predictedRating: number | null | undefined,
  expectedAbsoluteError: number | null | undefined
): string | null {
  if (predictedRating == null || !Number.isFinite(predictedRating)) return null;
  const value = predictedRating.toFixed(1);
  return expectedAbsoluteError != null && Number.isFinite(expectedAbsoluteError) && expectedAbsoluteError > 0
    ? `${value} ± ${expectedAbsoluteError.toFixed(1)}`
    : value;
}

/** "saved today" / "saved yesterday" / "saved 41 days ago" — the queue's own quiet nag. */
export function savedAgo(daysSaved: number): string {
  if (daysSaved <= 0) return "saved today";
  if (daysSaved === 1) return "saved yesterday";
  return `saved ${daysSaved} days ago`;
}

/**
 * What the after-rating moment says. Reel committed to a number in public; this settles it in
 * public, including when it was badly wrong. No softening, no "close enough" — the plan's
 * "honest from day one" decision is the whole point of the strip.
 */
export function ratingDelta(predictedRating: number, actualRating: number): string {
  return `Reel said ${predictedRating.toFixed(1)} · you said ${actualRating} — noted`;
}

/** What a saved card says about the managed Trakt list, and the tooltip that explains it. */
export interface ListSyncLabel {
  text: string;
  title: string;
}

/**
 * The managed-list line on a saved card (G-3). "syncing…" is said ONLY when the server reports a
 * ListAdd write-back still waiting — it used to be said for every card not on the list, which put it
 * on 116 watched titles that nothing was ever going to sync. Every line comes from `listSync` and
 * nothing else: an older server without it, or a newer value this client does not know, gets no
 * line at all. The legacy `onManagedList` flag is deliberately NOT a fallback — it is true for a
 * write still queued as well as for one that landed, so reading it as "on Up Next" would claim a
 * pending write was done (review D6). Absent evidence is not evidence, in either direction.
 */
export function listSyncLabel(item: Pick<SavedQueueItem, "listSync">): ListSyncLabel | null {
  switch (item.listSync) {
    case "pending":
      return { text: "syncing…", title: "Queued for the managed Trakt list — Reel writes it back in the background." };
    case "onList":
      return { text: "on Up Next", title: "On your Reel — Up Next list on Trakt." };
    case "offList":
      return {
        text: "not on Up Next",
        // Neutral on purpose (WS-4 CR-4): a save that came in from the Trakt watchlist was never
        // written to Up Next, so "taken off on Trakt, or the write-back gave up" was false for it.
        title: "Saved here; not on your Reel — Up Next list on Trakt.",
      };
    default:
      return null;
  }
}

/**
 * The line that accounts for saves the conditions hold out (WS-4: a standing exclusion is airtight
 * on every channel, Saved included, and the server reports how many it held back). Nothing may
 * vanish unexplained — and when every save is held out, the shelf must not read as empty.
 */
export function heldOutLine(heldOut: number | null | undefined, shown: number): string | null {
  if (!heldOut || heldOut <= 0) return null;
  if (shown === 0) {
    return heldOut === 1
      ? "Your 1 save is hidden by your conditions."
      : `All ${heldOut} of your saves are hidden by your conditions.`;
  }
  return heldOut === 1 ? "1 save is hidden by your conditions." : `${heldOut} saves are hidden by your conditions.`;
}
