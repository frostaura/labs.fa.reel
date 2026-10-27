import type { SavedQueueItem } from "../store/savedTypes";
import { formatRuntime } from "./format";

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

/**
 * The line on a pin the client advanced to with "Not tonight" (HREV1-D05).
 *
 * Only the server's own pin carries `watchTonightReason`, so the card "Not tonight" moved to had no
 * line at all — and the founder saw Seized at 5.0, under his For-you bar of 6.0, under "WATCH
 * TONIGHT" with nothing qualifying it. The server's rule (SavedQueue.WatchTonightReason, R1-18) is
 * that a pick under the bar says so in its FIRST words; this holds the advanced pin to it:
 *
 * - **Under the bar** (compared at the badge's own 1dp, as the server compares): "Under 6.0 for you —
 *   the next thing in your queue…". Never the server's "Nothing in your queue… reaches 6.0": the pick
 *   the user just skipped may well have reached it.
 * - **Otherwise** an ordinal, not a verdict: "Next-highest-predicted thing in your queue…". With the
 *   bar unknown that is all the client can truthfully say, and it says no more.
 *
 * `bar` is the server's pin bar when it sends one (`SavedQueueResponse.pinBar`), else the account's
 * For-you minimum, else null. It never claims a provider: "streaming now", as the server does when it
 * has not established which provider the card shows.
 */
export function advancedPinLine(
  item: SavedQueueItem,
  context: { bar: number | null; expectedAbsoluteError: number | null; eveningBudgetMinutes: number }
): string | null {
  if (item.predictedRating == null || !Number.isFinite(item.predictedRating)) return null;
  const runtime = formatRuntime(item.runtimeMinutes);
  const budget = formatRuntime(context.eveningBudgetMinutes);
  if (!runtime || !budget) return null;
  const shown = Number(item.predictedRating.toFixed(1));
  const error = context.expectedAbsoluteError;
  const band = error != null && Number.isFinite(error) && error > 0 ? ` (±${Number(error.toFixed(1))})` : "";
  // "inside a … evening", never "your …": the served budget is the resolved one, and an account that
  // never set a budget gets the default — which the server's own pin never calls "yours" (HREV1-D10).
  const facts = `${shown.toFixed(1)} for you${band}, ${runtime} inside a ${budget} evening.`;
  if (context.bar != null && shown < context.bar) {
    return `Under ${context.bar.toFixed(1)} for you — the next thing in your queue that's streaming now and fits tonight: ${facts}`;
  }
  return `Next-highest-predicted thing in your queue that's streaming now and fits tonight — ${facts}`;
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

/**
 * What Saved says about the managed "Reel — Up Next" list (R1-09). Only a save made in Reel is
 * written to it (the save endpoint queues ListAdd); a title that came in from the Trakt watchlist is
 * saved here and deliberately NOT pushed to Up Next (WatchlistIntake is read-only), which is why a
 * card can say "not on Up Next". So neither line may say that everything on the shelf lands on the
 * list. Plex, Kodi and Infuse showing the list is their behaviour, not something Reel checks.
 */
export const MANAGED_LIST_TOOLTIP =
  "Reel keeps a Trakt list called “Reel — Up Next”. What you save in Reel lands on it, and shows up in Plex, Kodi, Infuse and every Trakt-connected app. Titles from your Trakt watchlist are saved here too but stay off that list.";

/** The shelf's intro, split around the list's name, which the page sets in its own colour. */
export const SAVED_INTRO = {
  before: "Your queue, in the order worth watching it. What you save in Reel syncs to the managed",
  listName: "Reel — Up Next",
  after:
    "list — visible in Plex, Kodi, Infuse and every Trakt-connected app. Your Trakt watchlist lands here too, and stays off that list. Watch something and it leaves by itself.",
} as const;

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
