import { NavigationType, matchPath, type Location } from "react-router";

/**
 * The shell's one gate on in-app navigation (IREV3S-04). React Router allows a single active
 * `useBlocker`, so every surface that can lose unsaved work registers a question here and the
 * shell's `<NavigationGuard>` asks it before a link, the Back or Forward button, or a programmatic
 * `navigate()` leaves the page. A question returns the sentence to ask, or null when nothing is
 * at stake right now.
 */
export type LeaveQuestion = () => string | null;

const questions = new Set<LeaveQuestion>();

/** Registers a question; the returned function removes it again. */
export function guardNavigation(question: LeaveQuestion): () => void {
  questions.add(question);
  return () => {
    questions.delete(question);
  };
}

/** The sentence to ask before leaving, or null when no registered question has anything at stake. */
export function pendingLeaveQuestion(): string | null {
  for (const question of questions) {
    const asked = question();
    if (asked !== null) return asked;
  }
  return null;
}

type RouterLocation = Pick<Location, "pathname" | "search" | "state">;

/**
 * Navigation state for a navigate() whose caller already asked `pendingLeaveQuestion()` itself —
 * sign-out asks BEFORE it ends the session, so the blocker must not ask a second time afterwards.
 * Honoured on a push or replace only: Back or Forward onto an entry that once carried it is a new
 * departure nobody has asked about.
 */
export const LEAVE_CONFIRMED = { leaveConfirmed: true } as const;

function isLeaveConfirmed(location: RouterLocation): boolean {
  return (location.state as { leaveConfirmed?: unknown } | null)?.leaveConfirmed === true;
}

/**
 * The page a location shows. The title modal is opened with `state.backgroundLocation` and keeps
 * that page mounted underneath, so for the title route the page is the background, not the modal.
 * App.tsx pins its main `<Routes>` to this same location.
 */
export function pageLocation<L extends RouterLocation>(location: L): L {
  const background = (location.state as { backgroundLocation?: L } | null)?.backgroundLocation;
  return background && matchPath("/title/:mediaType/:tmdbId", location.pathname) != null ? background : location;
}

/**
 * Whether going from `current` to `next` unmounts the page on screen. A hash jump or a title modal
 * opened over (or closed back onto) the same page keeps it, and its drafts, where they are.
 */
export function leavesPage(current: RouterLocation, next: RouterLocation): boolean {
  const from = pageLocation(current);
  const to = pageLocation(next);
  return from.pathname !== to.pathname || from.search !== to.search;
}

/** The blocker's question about one navigation, before any draft is consulted. */
export function departureNeedsAsking(navigation: {
  currentLocation: RouterLocation;
  nextLocation: RouterLocation;
  historyAction: NavigationType;
}): boolean {
  const { currentLocation, nextLocation, historyAction } = navigation;
  if (historyAction !== NavigationType.Pop && isLeaveConfirmed(nextLocation)) return false;
  return leavesPage(currentLocation, nextLocation);
}
