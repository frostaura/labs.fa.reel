import { PAID_ADDS, proseList } from "../components/settings/planCopy";

/**
 * The free tier's locked-rows card on Home (R1-40).
 *
 * It used to say the paid feed "unlocks every row, natural-language search and the managed Trakt
 * queue". Only FeedEndpoints gates by tier; Ask Reel and Saved work on Free. What Paid adds is
 * planCopy's PAID_ADDS — the one list kept equal to the server's checks — so this card quotes it
 * rather than keeping a second, drifting copy.
 */
export function lockedRowsHeadline(count: number): string {
  return `${count} more personalised row${count === 1 ? "" : "s"} on the full feed`;
}

export function lockedRowsCaption(): string {
  return `The free shortlist gives you three picks a day. Paid adds ${proseList(PAID_ADDS)}.`;
}
