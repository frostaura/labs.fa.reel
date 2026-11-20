import React from "react";
import ReactDOM from "react-dom/client";
import { Provider } from "react-redux";
import App from "./App";
import ErrorBoundary from "./components/ErrorBoundary";
import { store } from "./store";
import { reportClientError } from "./lib/clientErrors";
import { reloadOnceForNewBuild } from "./lib/deployReload";
import { startPwaUpdates } from "./lib/pwaUpdate";
// Figtree, self-hosted (@fontsource) instead of the old Google Fonts <link>: the woff2 files
// ship same-origin (so the share-card exporter can embed them and the console stays free of
// font CORS errors) and land in the Workbox precache, making the PWA genuinely offline-capable.
// ONE variable font (I-17) carries every weight from 300 to 900, where five static weights were
// five downloads. Its latin-ext half is fetched only for a character that needs it (unicode-range).
import "@fontsource-variable/figtree/wght.css";
import "./index.css";

// Dev-only: unregister any stale service worker from previous sessions so cached app shells or
// API responses never shadow the dev server (Safari is the offender).
if (import.meta.env.DEV && "serviceWorker" in navigator) {
  navigator.serviceWorker.getRegistrations().then((regs) => {
    if (regs.length > 0) {
      Promise.all(regs.map((r) => r.unregister()))
        .then(() => caches.keys())
        .then((keys) => Promise.all(keys.map((k) => caches.delete(k))))
        .then(() => location.reload());
    }
  });
}

// A tab still running the previous build asks for a lazy chunk the redeploy removed. Reload once
// for the new build instead of letting the import throw; if that reload already happened, the
// error goes on to the route boundary, which offers a manual reload (I-3).
window.addEventListener("vite:preloadError", (event) => {
  if (reloadOnceForNewBuild()) event.preventDefault();
});

startPwaUpdates();

// Every render error leaves the browser through these two hooks — once each, whichever boundary
// (route, root, or a surface's own) caught it. React's default console logging is kept.
ReactDOM.createRoot(document.getElementById("root")!, {
  onCaughtError(error, info) {
    console.error(error);
    reportClientError(error, "caught", info.componentStack);
  },
  onUncaughtError(error, info) {
    console.error(error);
    reportClientError(error, "uncaught", info.componentStack);
  },
}).render(
  <React.StrictMode>
    <ErrorBoundary scope="root">
      <Provider store={store}>
        <App />
      </Provider>
    </ErrorBoundary>
  </React.StrictMode>
);
