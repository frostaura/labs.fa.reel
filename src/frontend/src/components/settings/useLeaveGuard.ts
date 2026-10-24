import { useEffect } from "react";

/** What the guard asks before an unsaved draft is thrown away. */
export const LEAVE_GUARD_MESSAGE = "You have unsaved changes to your conditions. Leave without saving them?";

/**
 * Asks before an unsaved draft is lost.
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
 * report; when it lands this hook becomes a `useBlocker` call and its callers do not change.
 */
export function useLeaveGuard(dirty: boolean, message: string = LEAVE_GUARD_MESSAGE): void {
  useEffect(() => {
    if (!dirty) return;

    const onBeforeUnload = (event: BeforeUnloadEvent) => {
      event.preventDefault();
      // Chromium still wants the legacy assignment before it shows the prompt.
      event.returnValue = "";
    };

    const onClick = (event: MouseEvent) => {
      if (event.defaultPrevented || event.button !== 0) return;
      // A modified click opens a new tab and leaves this one, and its draft, where they are.
      if (event.metaKey || event.ctrlKey || event.shiftKey || event.altKey) return;
      const anchor = (event.target as Element | null)?.closest?.("a[href]");
      if (!(anchor instanceof HTMLAnchorElement)) return;
      if (anchor.target && anchor.target !== "_self") return;
      if (anchor.hasAttribute("download")) return;
      const next = new URL(anchor.href, window.location.href);
      if (next.origin !== window.location.origin) return; // beforeunload covers leaving the site
      const here = window.location;
      if (next.pathname === here.pathname && next.search === here.search) return;
      if (window.confirm(message)) return;
      event.preventDefault();
      event.stopPropagation();
    };

    window.addEventListener("beforeunload", onBeforeUnload);
    document.addEventListener("click", onClick, true);
    return () => {
      window.removeEventListener("beforeunload", onBeforeUnload);
      document.removeEventListener("click", onClick, true);
    };
  }, [dirty, message]);
}
