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

/** Raises the undo toast for one completed card action. */
export type CardUndoAnnounce = (action: CardAction) => void;

/** Provided by `components/rec/CardUndo.tsx`'s `CardUndoProvider`. */
export const CardUndoContext = createContext<CardUndoAnnounce | null>(null);

/**
 * The "that happened — Undo" a quick action on a card raises, or null on a surface that does not
 * offer one (Ask Reel keeps its own toast for its own two actions).
 */
export function useCardUndo(): CardUndoAnnounce | null {
  return useContext(CardUndoContext);
}
