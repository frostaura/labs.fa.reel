import { combineReducers, configureStore, type UnknownAction } from "@reduxjs/toolkit";
import { setupListeners } from "@reduxjs/toolkit/query";
import { api } from "./api";
import onboarding from "./onboardingSlice";
import syncUi from "./syncSlice";
import removedTitles from "./removedTitlesSlice";
import scoreOverrides from "./scoreOverridesSlice";
import verdict from "./verdictSlice";
import connectivity from "./connectivitySlice";
import { sessionReset } from "./sessionReset";

const appReducer = combineReducers({
  [api.reducerPath]: api.reducer,
  onboarding,
  syncUi,
  removedTitles,
  scoreOverrides,
  verdict,
  connectivity,
});

type AppState = ReturnType<typeof appReducer>;

/**
 * `sessionReset` returns every slice except the RTK Query cache to its initial state. The cache is
 * kept out on purpose: it is reset through `api.util.resetApiState()`, the only path that also
 * clears the middleware's own subscription bookkeeping.
 */
export function rootReducer(state: AppState | undefined, action: UnknownAction): AppState {
  if (state && sessionReset.match(action)) {
    return appReducer({ [api.reducerPath]: state[api.reducerPath] } as AppState, action);
  }
  return appReducer(state, action);
}

export const store = configureStore({
  reducer: rootReducer,
  middleware: (gDM) => gDM().concat(api.middleware),
});

setupListeners(store.dispatch);

export type RootState = ReturnType<typeof store.getState>;
export type AppDispatch = typeof store.dispatch;
