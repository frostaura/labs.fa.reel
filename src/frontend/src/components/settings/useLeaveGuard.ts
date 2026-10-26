import { useEffect, useId } from "react";
import { guardNavigation } from "../../lib/navigationGuard";

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
 * Every dirty draft on screen, by hook instance. ONE beforeunload listener and ONE navigation
 * question serve them all: with a listener per card, a page with two dirty drafts (the conditions
 * and the email) asked "Leave?" twice for one click, and answering OK to the first still left the
 * user on the page.
 */
const drafts = new Map<string, string>();

/** Removes the shell navigation question while any draft is registered; null while none is. */
let stopGuardingNavigation: (() => void) | null = null;

function onBeforeUnload(event: BeforeUnloadEvent) {
  event.preventDefault();
  // Chromium still wants the legacy assignment before it shows the prompt.
  event.returnValue = "";
}

function register(id: string, what: string) {
  const first = drafts.size === 0;
  drafts.set(id, what);
  if (first) {
    window.addEventListener("beforeunload", onBeforeUnload);
    stopGuardingNavigation = guardNavigation(() => (drafts.size > 0 ? leaveGuardMessage([...drafts.values()]) : null));
  }
}

function unregister(id: string) {
  if (!drafts.delete(id) || drafts.size > 0) return;
  window.removeEventListener("beforeunload", onBeforeUnload);
  stopGuardingNavigation?.();
  stopGuardingNavigation = null;
}

/**
 * Asks before an unsaved draft is lost — once per navigation, however many drafts on the page are
 * dirty.
 *
 * Two exits are covered:
 *  - reload, close and cross-site navigation, through `beforeunload` (the browser shows its own
 *    wording; a page cannot supply the text);
 *  - every in-app navigation — the header and tab-bar links, the links on the page, the avatar
 *    menu's programmatic `navigate()`, and the browser's Back and Forward buttons — through the
 *    shell's single `useBlocker` (`components/NavigationGuard`), which this registry feeds one
 *    question while any draft is dirty.
 */
export function useLeaveGuard(dirty: boolean, what: string = DEFAULT_WHAT): void {
  const id = useId();
  useEffect(() => {
    if (!dirty) return;
    register(id, what);
    return () => unregister(id);
  }, [dirty, what, id]);
}
