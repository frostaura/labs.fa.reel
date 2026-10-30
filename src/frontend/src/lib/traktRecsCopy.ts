import type { TraktRecommendations } from "../store/feedTypes";
import { relTime } from "./format";

/**
 * Everything the "Trakt recommends" rail says, decided in one pure place so every state's words are
 * unit-tested without a DOM.
 *
 * The rail's honesty rules:
 * - It is Trakt's list, not Reel's. The heading names Trakt, and the caption says the order is
 *   Trakt's — a Reel "for you" number on a card is a second opinion, never the reason it is there.
 * - An empty rail is never just empty. "Trakt sent nothing", "you've seen everything Trakt sent",
 *   "Trakt isn't answering" and "the link needs renewing" are four different pieces of news, and each
 *   is said in its own words. A read that failed outright is the page's `ReadFailedLine`, not this.
 * - What Reel took out is counted, never hidden: seen, set aside, ruled out by a condition, and
 *   held under the "for you" bar each get their number.
 */
export const TRAKT_RECS_TITLE = "Trakt recommends";

/**
 * Whose list this is — the caption's opening sentence, and the WHOLE caption while the list loads,
 * so the line under the heading is already there when the cards land (on a wide screen the counts
 * then extend the same line, and nothing below it moves).
 */
export const TRAKT_RECS_PROVENANCE = "Trakt's own picks for you, in Trakt's order.";

/** "3 you've already seen, 2 you've saved or turned down" — the parts of the list Reel took out. */
export function hiddenParts(r: TraktRecommendations): string[] {
  const parts: string[] = [];
  if (r.hiddenAsSeen > 0) parts.push(`${r.hiddenAsSeen} you've already seen`);
  if (r.hiddenAsSetAside > 0) parts.push(`${r.hiddenAsSetAside} you've saved or turned down`);
  if (r.hiddenByConditions > 0) parts.push(`${r.hiddenByConditions} your conditions rule out`);
  if (r.heldBelowBar > 0) {
    const bar = r.bar != null ? ` ${r.bar.toFixed(1)}` : "";
    parts.push(`${r.heldBelowBar} under your${bar} "for you" bar`);
  }
  return parts;
}

function joinParts(parts: string[]): string {
  if (parts.length <= 1) return parts.join("");
  return `${parts.slice(0, -1).join(", ")} and ${parts[parts.length - 1]}`;
}

/** Where the list came from, said first: Trakt's order, or Trakt's last list when it is not answering. */
function provenance(r: TraktRecommendations): string {
  return r.state === "Stale"
    ? `Trakt isn't answering right now, so this is the list it gave ${relTime(r.fetchedAt)}, in Trakt's order.`
    : TRAKT_RECS_PROVENANCE;
}

/** The line under the heading when there ARE cards: whose list it is, and what was taken out. */
export function traktRecsCaption(r: TraktRecommendations): string {
  const parts = hiddenParts(r);
  return parts.length > 0 ? `${provenance(r)} Not shown: ${joinParts(parts)}.` : provenance(r);
}

/** What the rail says when it has NO cards. Null only when there are cards to show. */
export function traktRecsNotice(r: TraktRecommendations): { text: string; retry: boolean } | null {
  switch (r.state) {
    case "NotLinked":
      return { text: "This account has no Trakt link, so there are no Trakt recommendations to show.", retry: false };
    case "RelinkRequired":
      return { text: "Trakt needs you to sign in with it again before it will share its recommendations.", retry: false };
    case "Unavailable":
      return { text: "Trakt isn't answering right now, so its recommendations can't be shown.", retry: true };
    default:
      break;
  }
  if (r.items.length > 0) return null;
  if (r.returnedByTrakt === 0) {
    return { text: "Trakt has no recommendations for you right now.", retry: false };
  }
  const parts = hiddenParts(r);
  const stale = r.state === "Stale" ? " (Trakt isn't answering, so this is its last list)" : "";
  return parts.length > 0
    ? { text: `Nothing left to show from Trakt's ${r.returnedByTrakt}${stale}: ${joinParts(parts)}.`, retry: r.state === "Stale" }
    : { text: `None of Trakt's ${r.returnedByTrakt} recommendations can be opened in Reel yet.`, retry: false };
}
