/// <reference lib="webworker" />
/**
 * Reel's service worker — hand-written (injectManifest) because Web Push needs real `push` /
 * `notificationclick` listeners, which generateSW cannot express. Every caching rule that used
 * to live in vite.config.ts's `workbox` block lives HERE now, semantics preserved verbatim.
 * The dev server never serves this file: vite.config.ts's kill-switch owns /sw.js in dev.
 */
import { clientsClaim } from "workbox-core";
import { createHandlerBoundToURL, precacheAndRoute } from "workbox-precaching";
import { NavigationRoute, registerRoute } from "workbox-routing";
import { CacheFirst, NetworkFirst } from "workbox-strategies";
import { ExpirationPlugin } from "workbox-expiration";
import { CacheableResponsePlugin } from "workbox-cacheable-response";
import { API_CACHE, SIGNED_OUT, SKIP_WAITING } from "./lib/swContract";
import { PROMPT_AWARE_MARKER, createSignOutGuard, markFromCache, shouldTakeOverOnInstall } from "./lib/swApiCache";

declare let self: ServiceWorkerGlobalScope;

// `registerType: "prompt"` semantics. A new worker WAITS: taking over the moment a deploy installed
// it deleted the previous build's precached chunks out from under every open tab, and the next lazy
// route in those tabs went blank (I-3). The page shows "New version — reload" (lib/pwaUpdate.ts)
// and, on the user's go-ahead, posts SKIP_WAITING. A first install has no previous build to protect,
// so `clientsClaim` still lets it control the page at once.
//
// The one exception is the first deploy of this prompt: tabs on the previous build never post
// SKIP_WAITING, so the new worker takes over once, as the old one always did (lib/swApiCache.ts).
const signOutGuard = createSignOutGuard();

self.addEventListener("message", (event) => {
  const type = (event.data as { type?: string } | null)?.type;
  if (type === SKIP_WAITING) void self.skipWaiting();
  // Sign-out (lib/signOut.ts): forget the account's API answers, including any still in flight.
  if (type === SIGNED_OUT) {
    signOutGuard.signedOut();
    event.waitUntil(caches.delete(API_CACHE));
  }
});
self.addEventListener("install", (event) => {
  event.waitUntil(
    (async () => {
      const takeOver = shouldTakeOverOnInstall({
        hasActiveWorker: self.registration.active != null,
        promptAwareMarker: await caches.has(PROMPT_AWARE_MARKER),
      });
      if (takeOver) await self.skipWaiting();
    })()
  );
});
self.addEventListener("activate", (event) => {
  event.waitUntil(caches.open(PROMPT_AWARE_MARKER).then(() => undefined));
});
clientsClaim();

precacheAndRoute(self.__WB_MANIFEST);

// SPA shell fallback. SSE must never be answered by the service worker, and /api is data, not
// navigation — both are denylisted, matching the old navigateFallbackDenylist.
registerRoute(
  new NavigationRoute(createHandlerBoundToURL("/index.html"), {
    denylist: [/^\/sse\//, /^\/api\//]
  })
);

// All GET /api/* is mutable, per-user data (feed, sync status). NetworkFirst keeps RTK Query's
// post-mutation refetches honest; the cache is only an offline fallback. SSE (/sse/*) is excluded
// entirely (it never matches /api/, and navigations to it are denylisted above).
//
// TWO exclusions are load-bearing, both learned the hard way:
//
// /api/auth/* is NEVER cached. A cached session response makes a signed-out user look signed in
// until the entry expires, and the app cannot tell the difference — which is the same class of
// bug as the cached app shell rendering perfectly over a completely dead backend and reporting
// it as "couldn't reach the sign-in service".
//
// maxAgeSeconds is ONE HOUR, not the seven days this used to hold. Seven days of fallback for
// "mutable, per-user data" means a network blip can serve a week-old feed, watch state or
// entitlement with no visual difference from live data. An offline fallback should be recent
// enough to be recognisably yours and short enough that anything genuinely stale simply fails
// and surfaces as an error the UI can report.
//
// A response answered FROM the cache is marked (`X-Reel-From-Cache: 1`), so the app can say "offline —
// showing what Reel saved at 4:17 PM" instead of presenting an hour-old answer as live (I-11). The
// server's own `Date` header travels with it and says when it was saved. Sign-out deletes this cache
// (lib/signOut.ts posts SIGNED_OUT; the guard refuses answers to requests already in flight), so a
// second account on the same device never falls back to the first one's data.
registerRoute(
  ({ url, request }) =>
    request.method === "GET" &&
    url.pathname.startsWith("/api/") &&
    !url.pathname.startsWith("/api/auth/") &&
    !url.pathname.includes("/stream"),
  new NetworkFirst({
    cacheName: API_CACHE,
    networkTimeoutSeconds: 8,
    plugins: [
      new ExpirationPlugin({ maxEntries: 300, maxAgeSeconds: 60 * 60 }),
      new CacheableResponsePlugin({ statuses: [0, 200] }),
      {
        requestWillFetch: async (param) => signOutGuard.requestWillFetch(param),
        cacheWillUpdate: async (param) => signOutGuard.cacheWillUpdate(param),
        cachedResponseWillBeUsed: async ({ cachedResponse }) => (await markFromCache(cachedResponse)) ?? undefined
      }
    ]
  })
);

// TMDB artwork is immutable per path — cache hard. Repeat visits paint posters instantly.
registerRoute(
  ({ url }) => url.hostname === "image.tmdb.org",
  new CacheFirst({
    cacheName: "reel-posters",
    plugins: [
      new ExpirationPlugin({ maxEntries: 500, maxAgeSeconds: 60 * 60 * 24 * 30 }),
      new CacheableResponsePlugin({ statuses: [0, 200] })
    ]
  })
);

/** Payload shape the backend's digest job sends — see BuildDigestJobHandler.NotifyAsync. */
interface PushPayload {
  title?: string;
  body?: string;
  url?: string;
}

// Weekly-digest Web Push. The payload is JSON ({title, body, url}); a malformed or absent
// payload still shows a generic notification — a push the user granted permission for must
// never be silently dropped (browsers may penalise the subscription for it).
self.addEventListener("push", (event) => {
  let payload: PushPayload = {};
  try {
    payload = (event.data?.json() as PushPayload) ?? {};
  } catch {
    // Non-JSON payload — fall through to the defaults.
  }
  const title = payload.title ?? "Reel";
  const options: NotificationOptions = {
    body: payload.body ?? "Your weekly picks are ready.",
    icon: "/favicon.svg",
    data: { url: payload.url ?? "/" }
  };
  event.waitUntil(self.registration.showNotification(title, options));
});

// Clicking the notification focuses an open Reel tab (navigating it to the target), or opens a
// new one. `focus` before `navigate`: an already-visible tab should not lose its place unless
// the notification actually points somewhere else.
self.addEventListener("notificationclick", (event) => {
  event.notification.close();
  const targetUrl = (event.notification.data as { url?: string } | undefined)?.url ?? "/";
  event.waitUntil(
    (async () => {
      const clientList = (await self.clients.matchAll({
        type: "window",
        includeUncontrolled: true
      })) as WindowClient[];
      const existing = clientList.find((c) => typeof c.focus === "function");
      if (existing) {
        await existing.focus();
        if (new URL(existing.url).pathname !== targetUrl) {
          await existing.navigate(targetUrl);
        }
        return;
      }
      await self.clients.openWindow(targetUrl);
    })()
  );
});
