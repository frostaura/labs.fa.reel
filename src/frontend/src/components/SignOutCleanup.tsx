import { useEffect } from "react";
import { useDispatch } from "react-redux";
import type { AppDispatch } from "../store";
import { clearClientState, consumeSignOutPending } from "../lib/signOut";

/**
 * Runs the sign-out cleanup on the landing page, after the signed-in shell has unmounted. React runs
 * every unmount effect of a commit before any mount effect, so by the time this effect runs the old
 * pages' query subscriptions are gone and resetting the cache refetches nothing (I-11).
 */
export default function SignOutCleanup() {
  const dispatch = useDispatch<AppDispatch>();
  useEffect(() => {
    if (consumeSignOutPending()) void clearClientState(dispatch);
  }, [dispatch]);
  return null;
}
