import { createContext, useContext } from "react";

/** One completed card action the undo toast can reverse. */
export type CardAction =
  | { kind: "save"; titleId: string; mediaType: string; tmdbId: number; name: string }
  | { kind: "dismiss"; titleId: string; mediaType: string; tmdbId: number; name: string }
  | { kind: "rate"; titleId: string; mediaType: string; tmdbId: number; name: string; rating: number };

/**
 * The toast's words. Each says what actually happened to the title — where a save went, that a
 * dismissal hides it from picks (it is not deleted from anything), and the score a rating recorded —
 * because "Done" with an Undo beside it does not tell a person what they would be undoing.
 */
export function cardUndoCopy(action: CardAction, undoFailed = false): { message: string; undoLabel: string } {
  const name = `“${action.name}”`;
  if (undoFailed) {
    const still =
      action.kind === "save"
        ? `${name} is still saved`
        : action.kind === "dismiss"
          ? `${name} is still hidden from your picks`
          : `Your ${action.rating} for ${name} still stands`;
    return { message: `Couldn’t undo — ${still}.`, undoLabel: "Try again" };
  }
  const message =
    action.kind === "save"
      ? `Saved ${name} to your queue.`
      : action.kind === "dismiss"
        ? `${name} won’t be picked for you again.`
        : `Rated ${name} ${action.rating} — logged as seen.`;
  return { message, undoLabel: "Undo" };
}

/**
 * Where keyboard focus goes around the toast, for an action taken from the keyboard (HREV4-PH-02).
 * The card that held focus leaves the rail, so focus is moved to the toast's Undo, and when the
 * toast closes it goes to `returnTo()` — a neighbouring card, else the rail or the page — never to
 * <body>.
 */
export interface CardUndoFocus {
  returnTo: () => HTMLElement | null;
}

/** Raises the undo toast for one completed card action. */
export type CardUndoAnnounce = (action: CardAction, focus?: CardUndoFocus) => void;

/** The cards an action's focus can return to, in the order a rail or the hero renders them. */
const CARD_SELECTOR = '[data-testid="rec-card"], [data-testid="hero-card"]';

/** A card's own way in: its first link or button, which is what Tab reaches first on it. */
function entryOf(card: Element): HTMLElement | null {
  return card.querySelector<HTMLElement>('a[href], button:not([disabled]), [tabindex]:not([tabindex="-1"])');
}

/** Makes a region a focus target of last resort, without putting it in the Tab order. */
function asTarget(region: Element | null): HTMLElement | null {
  if (!(region instanceof HTMLElement) || !region.isConnected) return null;
  if (!region.hasAttribute("tabindex")) region.setAttribute("tabindex", "-1");
  return region;
}

/**
 * Where focus should land once `from`'s card has left: the next card in its rail, else the previous
 * one, else the nearest card on the page after the rail, else before it, else the rail itself, else
 * the page's <main> — each region made focusable for the purpose. A rail can leave with its last
 * card (the hero does), which is why the page's other cards come before the rail. The candidates
 * are captured now, while the card is still among them, and resolved when called, so anything that
 * has left since is skipped. Pure DOM, so it is unit-tested without a page.
 */
export function focusReturnTarget(from: Element): () => HTMLElement | null {
  const card = from.closest(CARD_SELECTOR);
  const rail = from.closest("section") ?? card?.parentElement ?? null;
  const main = from.closest("main");
  const inRail = rail ? Array.from(rail.querySelectorAll(CARD_SELECTOR)) : [];
  const onPage = Array.from(from.ownerDocument.querySelectorAll(CARD_SELECTOR)).filter((c) => !inRail.includes(c));
  const at = card ? inRail.indexOf(card) : -1;
  // Where the rail sits among the page's other cards, in document order.
  const split = rail ? onPage.findIndex((c) => rail.compareDocumentPosition(c) & Node.DOCUMENT_POSITION_FOLLOWING) : -1;
  const later = split < 0 ? [] : onPage.slice(split);
  const earlier = (split < 0 ? onPage : onPage.slice(0, split)).reverse();
  const candidates = [...inRail.slice(at + 1), ...inRail.slice(0, Math.max(at, 0)).reverse(), ...later, ...earlier];
  return () => {
    for (const candidate of candidates) {
      if (candidate === card || !candidate.isConnected) continue;
      const entry = entryOf(candidate);
      if (entry) return entry;
    }
    return asTarget(rail) ?? asTarget(main);
  };
}

/**
 * Whether closing the toast may move focus: only while focus is still on the toast or has already
 * fallen to <body>. Someone who has tabbed on to something else is left where they went.
 */
export function focusMayReturn(active: Element | null, toast: Element | null): boolean {
  if (!active || active === active.ownerDocument.body || active === active.ownerDocument.documentElement) return true;
  return !!toast && toast.contains(active);
}

/** Provided by `components/rec/CardUndo.tsx`'s `CardUndoProvider`. */
export const CardUndoContext = createContext<CardUndoAnnounce | null>(null);

/**
 * The "that happened — Undo" a quick action on a card raises, or null on a surface that does not
 * offer one (Ask Reel keeps its own toast for its own two actions).
 */
export function useCardUndo(): CardUndoAnnounce | null {
  return useContext(CardUndoContext);
}
