import { createAction } from "@reduxjs/toolkit";

/**
 * Returns every client slice to its initial state. Dispatched on sign-out (lib/signOut.ts), after
 * `api.util.resetApiState()` has cleared the RTK Query cache, so nothing account A put in the
 * store — removed titles, score overrides, a pending verdict, onboarding progress — survives into
 * account B's session on the same device (I-11).
 */
export const sessionReset = createAction("session/reset");
