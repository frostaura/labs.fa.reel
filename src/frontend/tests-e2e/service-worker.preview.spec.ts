import { readFileSync, writeFileSync } from "node:fs";
import http from "node:http";
import type { AddressInfo } from "node:net";
import path from "node:path";
import { expect, test, type Page } from "@playwright/test";
import { SESSION_FOUNDER, stubEventSource, syncStatus } from "./helpers";

/**
 * The real service worker, on the production build (WS-9 P0 task 4; I-11). Every other spec blocks
 * workers, so these are the only tests in which the built `sw.js` answers requests:
 *
 * - an answer the worker serves from its offline cache reaches the page marked as such, and the
 *   page says so instead of presenting it as live;
 * - signing out deletes that cache in the worker, so account B on the same browser, offline, is
 *   never shown account A's Saved;
 * - a redeploy's worker waits behind the "new version" prompt, and Reload hands the page over to it
 *   with exactly one reload.
 *
 * Workers fetch on their own, past `page.route` — and in WebKit past `context.route` too — so the
 * API is mocked at the server layer (see `mockApi`), and every test runs in Chromium and WebKit
 * (PH-12). Anything the mock server does not answer still reaches the e2e tripwire and fails the run.
 */
test.use({ serviceWorkers: "allow" });
// A network-down step waits out the worker's network timeout (see DROP_AFTER_MS).
test.describe.configure({ timeout: 60_000 });

const SESSION_B = { ...SESSION_FOUNDER, accountId: "acc-b", traktUsername: "account-b", displayName: "Account B" };

function saved(name: string) {
  return {
    managedListUrl: null,
    sort: "tonight",
    eveningBudgetMinutes: 150,
    expectedAbsoluteError: 1.9,
    unscoredRemaining: 0,
    items: [
      {
        titleId: `${name}-1`,
        tmdbId: 1001,
        mediaType: "Movie",
        name,
        year: 2021,
        posterPath: null,
        runtimeMinutes: 100,
        predictedRating: 7,
        crowdRating: 7.4,
        crowdVotes: 12000,
        providers: [],
        availableNow: false,
        savedAt: "2026-08-01T00:00:00Z",
        daysSaved: 39,
        fitsEveningBudget: true,
        onManagedList: false,
        watchTonight: false,
        watchTonightReason: null,
      },
    ],
  };
}

const A_PICK = "Account A Secret Pick";
const B_PICK = "Account B Own Pick";

interface Api {
  signIn(session: Record<string, unknown>, savedName: string): void;
  /** The network goes (or comes back), for the page and its worker alike. */
  setNetwork(up: boolean): void;
  /** Requests for /api/saved the network (not the worker's cache) answered. */
  savedFetches(): number;
}

const TRIPWIRE = process.env.REEL_E2E_TRIPWIRE;
/**
 * How long a request hangs while the network is down before its connection drops: past the
 * worker's 8 s `networkTimeoutSeconds` (sw.ts), so the worker gives up on the network and answers
 * from its cache first, exactly as on a phone that has lost signal.
 */
const DROP_AFTER_MS = 10_000;
let closeApi: (() => Promise<void>) | null = null;

/**
 * Mocks the whole API at the SERVER layer (PH-12): a mock server on a loopback port, with the e2e
 * tripwire — Vite's /api proxy target — forwarding to it. Route mocks cannot do this job: WebKit's
 * `context.route` never sees a service worker's own fetches, so in WebKit the worker's /api/saved
 * went past every mock to the tripwire and the cache under test never filled. Through the proxy,
 * the page's fetches and the worker's are the same real HTTP requests in both engines.
 *
 * The network going down is the server going silent, not `context.setOffline`: WebKit's offline
 * emulation fails the page's request before the worker sees it ("Load failed" with the answer in
 * the cache), so it tests the emulator rather than the worker. A silent server is the same in both
 * engines and is what a phone that lost signal sees: the worker's network timeout fires and it
 * falls back to its cache; with nothing cached the connection drops and the page shows its error.
 */
async function mockApi(): Promise<Api> {
  if (!TRIPWIRE) throw new Error("REEL_E2E_TRIPWIRE is unset: run under playwright.preview.config.ts");
  let networkUp = true;
  let session: Record<string, unknown> | null = null;
  let savedName = A_PICK;
  let savedFetches = 0;
  const held = new Set<http.ServerResponse>();

  const server = http.createServer((req, res) => {
    req.resume();
    if (!networkUp) {
      held.add(res);
      setTimeout(() => {
        held.delete(res);
        res.socket?.destroy();
      }, DROP_AFTER_MS).unref();
      return;
    }
    const path = new URL(req.url ?? "/", "http://mock.invalid").pathname;
    const json = (body: unknown, status = 200, headers: Record<string, string> = {}) =>
      res.writeHead(status, { "Content-Type": "application/json", ...headers }).end(JSON.stringify(body));
    if (path === "/api/auth/me") return session ? json(session) : json({}, 401);
    if (path === "/api/auth/refresh") return json({}, 401);
    if (path === "/api/auth/logout") {
      session = null;
      return json({});
    }
    if (path === "/api/sync/status") return json(syncStatus());
    if (path === "/api/saved") {
      savedFetches += 1;
      return json(saved(savedName), 200, { Date: new Date().toUTCString() });
    }
    res.writeHead(204).end();
  });
  await new Promise<void>((resolve) => server.listen(0, "127.0.0.1", resolve));
  const { port } = server.address() as AddressInfo;
  const handOver = await fetch(`${TRIPWIRE}/__tripwire/forward`, {
    method: "PUT",
    body: JSON.stringify({ target: `http://127.0.0.1:${port}` }),
  });
  expect(handOver.status, "the tripwire refused the mock server").toBe(204);
  closeApi = async () => {
    await fetch(`${TRIPWIRE}/__tripwire/forward`, { method: "DELETE" });
    server.closeAllConnections();
    await new Promise<void>((resolve) => server.close(() => resolve()));
  };

  return {
    signIn(next, name) {
      session = next;
      savedName = name;
    },
    setNetwork(up) {
      networkUp = up;
      if (up) for (const res of held) res.socket?.destroy();
      if (up) held.clear();
    },
    savedFetches: () => savedFetches,
  };
}

test.afterEach(async () => {
  await closeApi?.();
  closeApi = null;
});

/** The built worker controls the page (it claims clients on its first activation). */
async function workerInControl(page: Page): Promise<void> {
  await expect
    .poll(() => page.evaluate(() => navigator.serviceWorker?.controller?.state ?? null), { timeout: 15_000 })
    .toBe("activated");
}

/**
 * After the handover: the new worker controls the reloaded page and nothing waits. The page's own
 * view of the worker may read "activating" for good in WebKit — a document that loads while its
 * worker activates never gets the statechange (measured: still "activating" after 60 s, while its
 * fetches go through that worker and a fresh document reads "activated"). So the reloaded page is
 * held to "controlled, nothing waiting", and a second tab in the same browser to "activated".
 */
async function handedOver(page: Page): Promise<void> {
  await expect
    .poll(() =>
      page.evaluate(async () => ({
        controller: ["activating", "activated"].includes(navigator.serviceWorker?.controller?.state ?? ""),
        waiting: (await navigator.serviceWorker.getRegistration())?.waiting?.state ?? null,
      }))
    )
    .toEqual({ controller: true, waiting: null });
  const tab = await page.context().newPage();
  await tab.goto("/home");
  await workerInControl(tab);
  await tab.close();
}

/** The names in every Saved answer the worker's API cache holds (any query string), or null. */
const cachedSaved = (page: Page) =>
  page.evaluate(async () => {
    if (!(await caches.has("reel-api"))) return null;
    const cache = await caches.open("reel-api");
    const keys = (await cache.keys()).filter((request) => new URL(request.url).pathname === "/api/saved");
    if (!keys.length) return null;
    const names: string[] = [];
    for (const key of keys) {
      const hit = await cache.match(key);
      if (hit) names.push(...((await hit.json()) as { items: { name: string }[] }).items.map((i) => i.name));
    }
    return names;
  });

async function openSavedFromTheShell(page: Page): Promise<void> {
  await page.getByRole("link", { name: "Saved" }).first().click();
  await expect(page).toHaveURL(/\/saved$/);
}

test("an answer the worker serves from its cache is marked, and the page says so (I-11)", async ({ page }) => {
  await stubEventSource(page);
  const api = await mockApi();
  api.signIn(SESSION_FOUNDER, A_PICK);

  await page.goto("/home");
  await workerInControl(page);

  // Online, through the worker: Saved answers live and the worker keeps a copy.
  const viaWorker = page.waitForResponse((r) => new URL(r.url()).pathname === "/api/saved");
  await openSavedFromTheShell(page);
  expect((await viaWorker).fromServiceWorker()).toBe(true);
  await expect(page.getByText(A_PICK).first()).toBeVisible();
  await expect(page.getByTestId("offline-banner")).toHaveCount(0);
  await expect.poll(() => cachedSaved(page)).toEqual([A_PICK]);

  // A fresh page (the store starts empty), then the network goes. The worker has the shell and
  // the Saved chunk precached, and the Saved answer in its API cache.
  await page.goto("/home");
  await expect(page.getByRole("link", { name: "Reel home" })).toBeVisible();
  const fetchesOnline = api.savedFetches();
  api.setNetwork(false);
  await openSavedFromTheShell(page);

  // The worker waits out its 8 s network timeout before it falls back to the cache.
  await expect(page.getByText(A_PICK).first()).toBeVisible({ timeout: 15_000 });
  await expect(page.getByTestId("offline-banner")).toContainText("Couldn't reach Reel — showing what was saved");
  expect(api.savedFetches()).toBe(fetchesOnline);
  api.setNetwork(true);
});

test("after A signs out, B on the same browser offline is never shown A's Saved (I-11)", async ({ page }) => {
  await stubEventSource(page);
  const api = await mockApi();
  api.signIn(SESSION_FOUNDER, A_PICK);

  await page.goto("/home");
  await workerInControl(page);
  await openSavedFromTheShell(page);
  await expect(page.getByText(A_PICK).first()).toBeVisible();
  // Not vacuous: the worker really holds A's Saved before the sign-out.
  await expect.poll(() => cachedSaved(page)).toEqual([A_PICK]);

  // By role, as shell-session does: since e02672a the name is also in an sr-only span, so text matches twice.
  await page.getByRole("button", { name: `Account menu, ${SESSION_FOUNDER.displayName}` }).click();
  await page.getByRole("menuitem", { name: "Sign out" }).click();
  await expect(page).toHaveURL(/\/$/);
  await expect.poll(() => cachedSaved(page)).toBeNull();

  // B links (Trakt redirects back: a full page load), then the network goes.
  api.signIn(SESSION_B, B_PICK);
  await page.goto("/home");
  await expect(page.getByRole("link", { name: "Reel home" })).toBeVisible();
  api.setNetwork(false);
  await openSavedFromTheShell(page);

  // The worker has nothing for B, and must not fall back to A.
  await expect(page.getByRole("heading", { name: "Saved" })).toBeVisible();
  // The read settles as a failure (the queue's own error state), not as A's list.
  // The worker has no cache to fall back to, so the read fails when the connection drops.
  await expect(page.getByTestId("saved-error")).toBeVisible({ timeout: DROP_AFTER_MS + 10_000 });
  await expect(page.getByText(A_PICK)).toHaveCount(0);
  expect(await cachedSaved(page)).toBeNull();
  api.setNetwork(true);
});

// Two tabs a redeploy can find: one opened after the worker was installed (the usual case), and the
// one whose own first visit installed it. In the second, no worker controlled the page when it
// registered, which is the case workbox-window does not reload for on its own.
let restoreWorker: (() => void) | null = null;
test.afterEach(() => {
  restoreWorker?.();
  restoreWorker = null;
});

for (const visit of ["a later visit", "the visit that installed the worker"] as const) {
  test(`a redeploy's worker waits for the prompt, and Reload hands over to it with one reload, on ${visit} (I-3)`, async ({ page }) => {
    await stubEventSource(page);
    const api = await mockApi();
    api.signIn(SESSION_FOUNDER, A_PICK);

    await page.goto("/home");
    await workerInControl(page);
    if (visit === "a later visit") {
      await page.reload();
      await workerInControl(page);
    }
    await expect(page.getByTestId("update-prompt")).toHaveCount(0);

    // The redeploy: the server now has a different worker script (a new build's). The worker's
    // update check is the browser's own fetch, which no route sees, so the built file itself
    // changes (and is put back at once; every preview run rebuilds dist/ anyway).
    const builtWorker = path.join(test.info().project.testDir, "../dist/sw.js");
    const original = readFileSync(builtWorker, "utf8");
    writeFileSync(builtWorker, `${original}\n// redeploy ${Date.now()}\n`);
    restoreWorker = () => writeFileSync(builtWorker, original);
    // The app looks for a new build whenever it comes back into view; here the check runs directly.
    await page.evaluate(async () => (await navigator.serviceWorker.getRegistration())?.update());

    const prompt = page.getByTestId("update-prompt");
    await expect(prompt).toContainText("A new version of Reel is ready.");
    // The new worker waits: the page keeps running the build it loaded.
    expect(await page.evaluate(async () => (await navigator.serviceWorker.getRegistration())?.waiting?.state ?? null)).toBe("installed");

    // The handover runs with the API up. Under route mocks it had to run with the data API down:
    // Playwright answering the worker's fetches while it activated made it hand control back and
    // forth hundreds of times a second. The server-layer mock intercepts nothing, so that artifact
    // is gone and the reloaded page loads real (mocked) data, as after a real redeploy.
    let loads = 0;
    page.on("load", () => (loads += 1));
    await prompt.getByRole("button", { name: "Reload" }).click();

    await expect.poll(() => loads).toBe(1);
    await expect(page.getByRole("link", { name: "Reel home" })).toBeVisible();
    await handedOver(page);
    await expect(prompt).toHaveCount(0);
    // Settled: no second reload follows.
    await page.waitForLoadState("networkidle");
    expect(loads).toBe(1);
    await expect(page.getByTestId("route-error-boundary")).toHaveCount(0);
  });
}
