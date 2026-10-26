import { readFileSync, writeFileSync } from "node:fs";
import path from "node:path";
import { expect, test, type BrowserContext, type Page } from "@playwright/test";
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
 * Workers fetch on their own, past `page.route`, so the API is mocked on the context, which in
 * Chromium also intercepts the worker's requests. Anything the mocks miss reaches the e2e tripwire
 * and fails the run, so a mock the worker bypassed cannot pass silently.
 */
test.use({ serviceWorkers: "allow" });

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
  /**
   * The network goes (or comes back). `context.setOffline` does not stop a context route from
   * answering the worker's own fetch, so the route itself fails every request the way a dropped
   * connection does; the page is set offline as well.
   */
  setNetwork(up: boolean, options?: { keepSession: boolean }): Promise<void>;
  /** Requests for /api/saved the network (not the worker's cache) answered. */
  savedFetches(): number;
}

/** Mocks the whole API on the context, so the worker's own fetches are answered too. */
async function mockApi(context: BrowserContext): Promise<Api> {
  let networkUp = true;
  let keepSession = false;
  let session: Record<string, unknown> | null = null;
  let savedName = A_PICK;
  let savedFetches = 0;
  const json = (body: unknown, status = 200) => ({ status, contentType: "application/json", body: JSON.stringify(body) });

  await context.route(
    (url) => url.pathname.startsWith("/api/"),
    (route) => {
      const path = new URL(route.request().url()).pathname;
      if (!networkUp && !(keepSession && path === "/api/auth/me")) return route.abort("internetdisconnected");
      if (path === "/api/auth/me") return route.fulfill(session ? json(session) : json({}, 401));
      if (path === "/api/auth/refresh") return route.fulfill(json({}, 401));
      if (path === "/api/auth/logout") {
        session = null;
        return route.fulfill(json({}));
      }
      if (path === "/api/sync/status") return route.fulfill(json(syncStatus()));
      if (path === "/api/saved") {
        savedFetches += 1;
        return route.fulfill({ ...json(saved(savedName)), headers: { Date: new Date().toUTCString() } });
      }
      return route.fulfill({ status: 204, body: "" });
    }
  );
  return {
    signIn(next, name) {
      session = next;
      savedName = name;
    },
    async setNetwork(up, options) {
      networkUp = up;
      keepSession = options?.keepSession ?? false;
      await context.setOffline(!up);
    },
    savedFetches: () => savedFetches,
  };
}

/** The built worker controls the page (it claims clients on its first activation). */
async function workerInControl(page: Page): Promise<void> {
  await expect
    .poll(() => page.evaluate(() => navigator.serviceWorker?.controller?.state ?? null), { timeout: 15_000 })
    .toBe("activated");
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

test("an answer the worker serves from its cache is marked, and the page says so (I-11)", async ({ page, context }) => {
  await stubEventSource(page);
  const api = await mockApi(context);
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
  await api.setNetwork(false);
  await openSavedFromTheShell(page);

  await expect(page.getByText(A_PICK).first()).toBeVisible();
  await expect(page.getByTestId("offline-banner")).toContainText("Couldn't reach Reel — showing what was saved");
  expect(api.savedFetches()).toBe(fetchesOnline);
  await api.setNetwork(true);
});

test("after A signs out, B on the same browser offline is never shown A's Saved (I-11)", async ({ page, context }) => {
  await stubEventSource(page);
  const api = await mockApi(context);
  api.signIn(SESSION_FOUNDER, A_PICK);

  await page.goto("/home");
  await workerInControl(page);
  await openSavedFromTheShell(page);
  await expect(page.getByText(A_PICK).first()).toBeVisible();
  // Not vacuous: the worker really holds A's Saved before the sign-out.
  await expect.poll(() => cachedSaved(page)).toEqual([A_PICK]);

  await page.getByText(String(SESSION_FOUNDER.displayName)).click();
  await page.getByRole("menuitem", { name: "Sign out" }).click();
  await expect(page).toHaveURL(/\/$/);
  await expect.poll(() => cachedSaved(page)).toBeNull();

  // B links (Trakt redirects back: a full page load), then the network goes.
  api.signIn(SESSION_B, B_PICK);
  await page.goto("/home");
  await expect(page.getByRole("link", { name: "Reel home" })).toBeVisible();
  await api.setNetwork(false);
  await openSavedFromTheShell(page);

  // The worker has nothing for B, and must not fall back to A.
  await expect(page.getByRole("heading", { name: "Saved" })).toBeVisible();
  // The read settles as a failure (the queue's own error state), not as A's list.
  await expect(page.getByTestId("saved-error")).toBeVisible();
  await expect(page.getByText(A_PICK)).toHaveCount(0);
  expect(await cachedSaved(page)).toBeNull();
  await api.setNetwork(true);
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
  test(`a redeploy's worker waits for the prompt, and Reload hands over to it with one reload, on ${visit} (I-3)`, async ({ page, context }) => {
    await stubEventSource(page);
    const api = await mockApi(context);
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

    // The handover runs with the data API down and only the session probe answering. Answering the
    // worker's fetches while it activates makes it hand control back and forth hundreds of times a
    // second (a Playwright interception artifact: with no routes at all it activates once, in about
    // a second), and with no routes the reloaded page's calls would reach the tripwire. What is
    // under test is the handover and the reload, not the data.
    await api.setNetwork(false, { keepSession: true });

    let loads = 0;
    page.on("load", () => (loads += 1));
    await prompt.getByRole("button", { name: "Reload" }).click();

    await expect.poll(() => loads).toBe(1);
    await expect(page.getByRole("link", { name: "Reel home" })).toBeVisible();
    await workerInControl(page);
    expect(await page.evaluate(async () => (await navigator.serviceWorker.getRegistration())?.waiting ?? null)).toBeNull();
    await expect(prompt).toHaveCount(0);
    // Settled: no second reload follows.
    await page.waitForLoadState("networkidle");
    expect(loads).toBe(1);
    // The network stays down: back online, every query would refetch past the removed mocks.
  });
}
