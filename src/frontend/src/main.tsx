import React from "react";
import ReactDOM from "react-dom/client";
import { Provider } from "react-redux";
import App from "./App";
import { store } from "./store";
// Figtree, self-hosted (@fontsource) instead of the old Google Fonts <link>: the woff2 files
// ship same-origin (so the share-card exporter can embed them and the console stays free of
// font CORS errors) and land in the Workbox precache, making the PWA genuinely offline-capable.
import "@fontsource/figtree/300.css";
import "@fontsource/figtree/400.css";
import "@fontsource/figtree/500.css";
import "@fontsource/figtree/600.css";
import "@fontsource/figtree/700.css";
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

ReactDOM.createRoot(document.getElementById("root")!).render(
  <React.StrictMode>
    <Provider store={store}>
      <App />
    </Provider>
  </React.StrictMode>
);
