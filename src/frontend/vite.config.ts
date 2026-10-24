import { defineConfig, type PluginOption } from "vite";
import react from "@vitejs/plugin-react";
import { VitePWA } from "vite-plugin-pwa";

// Dev-only kill switch for a stale production service worker (fa.foresight precedent). In dev the
// PWA SW is disabled, so a browser that once registered the prod `sw.js` (Safari especially) keeps
// serving its old Workbox precache forever. Serving a self-destroying SW at /sw.js means that
// browser's next update check fetches this tombstone, unregisters, purges caches, and reloads.
function devSwKillSwitch(): PluginOption {
  return {
    name: "dev-sw-kill-switch",
    apply: "serve",
    configureServer(server) {
      server.middlewares.use((req, res, next) => {
        if (req.url === "/sw.js" || req.url?.startsWith("/sw.js?")) {
          res.setHeader("Content-Type", "text/javascript");
          res.setHeader("Cache-Control", "no-cache, no-store, must-revalidate");
          res.end(
            "self.addEventListener('install',()=>self.skipWaiting());\n" +
              "self.addEventListener('activate',(e)=>e.waitUntil((async()=>{\n" +
              "  try{await self.registration.unregister();}catch(_){}\n" +
              "  try{const ks=await caches.keys();await Promise.all(ks.map(k=>caches.delete(k)));}catch(_){}\n" +
              "  const cs=await self.clients.matchAll({type:'window'});\n" +
              "  cs.forEach(c=>{try{c.navigate(c.url);}catch(_){}});\n" +
              "})()));\n"
          );
          return;
        }
        next();
      });
    }
  };
}

export default defineConfig({
  resolve: { dedupe: ["react", "react-dom"] },
  plugins: [
    react(),
    devSwKillSwitch(),
    VitePWA({
      // injectManifest (not generateSW): Web Push needs first-class `push` / `notificationclick`
      // listeners, so the worker is now hand-written in src/sw.ts. ALL caching semantics —
      // NetworkFirst /api with its two load-bearing exclusions, the poster CacheFirst, the
      // navigation-fallback denylist — moved there verbatim; sw.ts is the single place those
      // rules live now. The dev kill-switch above still owns /sw.js during `vite dev`.
      strategies: "injectManifest",
      srcDir: "src",
      filename: "sw.ts",
      // "prompt": a new worker waits for the user's go-ahead instead of taking over mid-session and
      // deleting the chunks open tabs still need (I-3). Registration is done by hand in
      // src/lib/pwaUpdate.ts (`virtual:pwa-register`), which raises the "New version" toast.
      registerType: "prompt",
      injectRegister: false,
      devOptions: { enabled: false, type: "module", navigateFallback: "index.html" },
      manifest: {
        name: "Reel by FrostAura",
        short_name: "Reel",
        theme_color: "#0B1B30",
        background_color: "#06121F",
        display: "standalone"
      },
      injectManifest: {
        globPatterns: ["**/*.{js,css,html,svg,png,ico,webp,woff2}"]
      }
    })
  ],
  server: {
    port: 5173,
    host: "0.0.0.0",
    watch: { usePolling: true, interval: 500 },
    proxy: {
      "/api": {
        // 127.0.0.1 (not "localhost") on purpose: on macOS "localhost" resolves to IPv6 ::1 first,
        // where the AirPlay Receiver squats on port 5000 and returns 403. Forcing IPv4 hits the
        // backend's 127.0.0.1 bind. 5054 = the API's launchSettings port, so a bare `npm run dev`
        // just works. Override with VITE_API_TARGET for non-local backends.
        target: process.env.VITE_API_TARGET ?? "http://127.0.0.1:5054",
        changeOrigin: true
      },
      "/sse": {
        target: process.env.VITE_API_TARGET ?? "http://127.0.0.1:5054",
        changeOrigin: true
      }
    }
  },
  build: { outDir: "dist", sourcemap: true }
});
