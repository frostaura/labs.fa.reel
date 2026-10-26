import { useEffect } from "react";
import { useBlocker } from "react-router";
import { departureNeedsAsking, pendingLeaveQuestion } from "../lib/navigationGuard";

/**
 * The single `useBlocker` (IREV3S-04). When a navigation would unmount the page while a registered
 * question has something at stake, it asks with `window.confirm` — once, whichever way the user is
 * leaving: a link, the Back or Forward button, or a programmatic `navigate()`. OK proceeds, Cancel
 * keeps the page, its URL and its drafts. Reload, close and leaving the site are `beforeunload`'s.
 */
export default function NavigationGuard() {
  const blocker = useBlocker((navigation) => departureNeedsAsking(navigation) && pendingLeaveQuestion() !== null);

  useEffect(() => {
    if (blocker.state !== "blocked") return;
    const question = pendingLeaveQuestion();
    // React Router's own usePrompt defers proceed() a tick, so the confirm dialog has fully closed
    // before the router replays a Back or Forward.
    if (question === null || window.confirm(question)) setTimeout(blocker.proceed, 0);
    else blocker.reset();
  }, [blocker]);

  return null;
}
