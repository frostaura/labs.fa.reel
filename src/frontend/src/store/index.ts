import { configureStore } from "@reduxjs/toolkit";
import { setupListeners } from "@reduxjs/toolkit/query";
import { api } from "./api";
import onboarding from "./onboardingSlice";
import syncUi from "./syncSlice";
import removedTitles from "./removedTitlesSlice";
import scoreOverrides from "./scoreOverridesSlice";
import verdict from "./verdictSlice";

export const store = configureStore({
  reducer: {
    [api.reducerPath]: api.reducer,
    onboarding,
    syncUi,
    removedTitles,
    scoreOverrides,
    verdict,
  },
  middleware: (gDM) => gDM().concat(api.middleware),
});

setupListeners(store.dispatch);

export type RootState = ReturnType<typeof store.getState>;
export type AppDispatch = typeof store.dispatch;
