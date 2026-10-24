import { useEffect, useId } from "react";

/** What one guarded draft is called in the question, e.g. "your conditions". */
const DEFAULT_WHAT = "your conditions";

/** Joins the dirty drafts as prose: "a", "a and b", "a, b and c". */
function joinWhat(items: readonly string[]): string {
  if (items.length <= 1) return items.join("");
  return `${items.slice(0, -1).join(", ")} and ${items[items.length - 1]}`;
}

/** The one question asked before unsaved drafts are thrown away, naming every one of them. */
export function leaveGuardMessage(whats: readonly string[]): string {
  const unique = [...new Set(whats)];
  return `You have unsaved changes to ${joinWhat(unique.length > 0 ? unique : [DEFAULT_WHAT])}. Leave without saving them?`;
}

/**
 * Whether a click is an in-app navigation away from this page that the guard should question. Kept
 * free of `window` state beyond what is passed in, so the rules are unit-testable.
 */
export function isGuardedNavigation(
  event: Pick<MouseEvent, "defaultPrevented" | "button" | "metaKey" | "ctrlKey" | "shiftKey" | "altKey">,
  anchor: { href: string; target: string; hasDownload: boolean } | null,
  here: { href: string; origin: string; pathname: string; search: string },
): boolean {
  if (event.defaultPrevented || event.button !== 0) return false;
  // A modified click opens a new tab and leaves this one, and its draft, where they are.
  if (event.metaKey || event.ctrlKey || event.shiftKey || event.altKey) return false;
  if (!anchor) return false;
  if (anchor.target && anchor.target !== "_self") return false;
  if (anchor.hasDownload) return false;
  const next = new URL(anchor.href, here.href);
  if (next.origin !== here.origin) return false; // beforeunload covers leaving the site
  return !(next.pathname === here.pathname && next.search === here.search);
}

/**
 * Every dirty draft on screen, by hook instance. ONE pair of document listeners serves them all:
 * with a listener per card, a page with two dirty drafts (the conditions and the email) asked
 * "Leave?" twice for one click, and answering OK to the first still left the user on the page.
 */
const drafts = new Map<string, string>();

function onBeforeUnload(event: BeforeUnloadEvent) {
  event.preventDefault();
  // Chromium still wants the legacy assignment before it shows the prompt.
  event.returnValue = "";
}

function onClick(event: MouseEvent) {
  const element = (event.target as Element | null)?.closest?.("a[href]");
  const anchor =
    element instanceof HTMLAnchorElement
      ? { href: element.href, target: element.target, hasDownload: element.hasAttribute("download") }
      : null;
  if (!isGuardedNavigation(event, anchor, window.location)) return;
  if (window.confirm(leaveGuardMessage([...drafts.values()]))) return;
  event.preventDefault();
  event.stopPropagation();
}

function register(id: string, what: string) {
  const first = drafts.size === 0;
  drafts.set(id, what);
  if (first) {
    window.addEventListener("beforeunload", onBeforeUnload);
    document.addEventListener("click", onClick, true);
  }
}

function unregister(id: string) {
  if (!drafts.delete(id) || drafts.size > 0) return;
  window.removeEventListener("beforeunload", onBeforeUnload);
  document.removeEventListener("click", onClick, true);
}

/**
 * Asks before an unsaved draft is lost — once per click, however many drafts on the page are dirty.
 *
 * Two exits are covered here:
 *  - reload, close and cross-site navigation, through `beforeunload` (the browser shows its own
 *    wording; a page cannot supply the text);
 *  - every in-app LINK — the header, the tab bar, the links on the page itself — through a
 *    capture-phase click listener on the document, which runs before React Router's own handler
 *    and can stop it.
 *
 * What it cannot see is a programmatic `navigate()` (the avatar menu's items) or the back button:
 * the app mounts a declarative `<BrowserRouter>`, and React Router's `useBlocker` only works under
 * a data router. Swapping the router is WS-9's (`App.tsx`), and the request is in the WS-10B
 * report; when it lands the registry here feeds one `useBlocker` and its callers do not change.
 */
export function useLeaveGuard(dirty: boolean, what: string = DEFAULT_WHAT): void {
  const id = useId();
  useEffect(() => {
    if (!dirty) return;
    register(id, what);
    return () => unregister(id);
  }, [dirty, what, id]);
}
