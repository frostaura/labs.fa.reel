import { expect, test, type Page, type Route } from "@playwright/test";
import { SESSION_FOUNDER, mockJson, stubEventSource, syncStatus } from "./helpers";

/**
 * Session, error boundaries, deploy safety and sign-out (WS-9 P0 tasks 2–5; H+1, H-15, I-3, I-4,
 * I-11, G-14). Everything is mocked at the route layer; no backend.
 */

async function bootQuiet(page: Page) {
  await stubEventSource(page);
  await mockJson(page, "**/api/sync/status", syncStatus());
  await page.route(
    (url) =>
      url.pathname.startsWith("/api/") &&
      !["/api/sync/status", "/api/auth/me", "/api/auth/refresh", "/api/saved", "/api/client-errors", "/api/auth/logout"].includes(
        url.pathname
      ),
    (route) => route.fulfill({ status: 204, body: "" })
  );
}

const SAVED_A = {
  managedListUrl: null,
  sort: "tonight",
  eveningBudgetMinutes: 150,
  expectedAbsoluteError: 1.9,
  unscoredRemaining: 0,
  items: [
    {
      titleId: "a-1",
      tmdbId: 1001,
      mediaType: "Movie",
      name: "Account A Secret Pick",
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

test.describe("session", () => {
  test("an expired access cookie is refreshed once on reload and the URL is kept (H+1)", async ({ page }) => {
    await bootQuiet(page);
    await mockJson(page, "**/api/saved", { ...SAVED_A, items: [] });
    let refreshed = false;
    let refreshCalls = 0;
    await page.route("**/api/auth/refresh", (route) => {
      refreshCalls += 1;
      refreshed = true;
      return route.fulfill({ status: 200, contentType: "application/json", body: "{}" });
    });
    // Before the refresh the access cookie is gone (401); after it, the probe answers.
    await page.route("**/api/auth/me", (route) =>
      refreshed
        ? route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify(SESSION_FOUNDER) })
        : route.fulfill({ status: 401, contentType: "application/json", body: "{}" })
    );

    await page.goto("/saved");
    await expect(page.getByRole("heading", { name: "Saved" })).toBeVisible();
    await expect(page).toHaveURL(/\/saved$/);
    expect(refreshCalls).toBe(1);
  });

  test("a 401 that survives the refresh goes to the landing page", async ({ page }) => {
    await bootQuiet(page);
    await mockJson(page, "**/api/auth/refresh", {}, 401);
    await mockJson(page, "**/api/auth/me", {}, 401);
    await page.goto("/saved");
    await expect(page).toHaveURL(/\/$/);
  });

  // H-15 again, one hop later: the expired access cookie is refused statelessly, but the refresh
  // needs the database. A refresh that gets no answer on its merits is not a sign-out.
  for (const [name, answer] of [
    ["a 503", (route: Route) => route.fulfill({ status: 503, contentType: "text/html", body: "Service Unavailable" })],
    ["a dropped connection", (route: Route) => route.abort("connectionreset")],
  ] as const) {
    test(`an expired access cookie whose refresh meets ${name} keeps the URL and offers a retry`, async ({ page }) => {
      await bootQuiet(page);
      await mockJson(page, "**/api/saved", { ...SAVED_A, items: [] });
      let databaseUp = false;
      let refreshed = false;
      await page.route("**/api/auth/refresh", (route) => {
        if (!databaseUp) return answer(route);
        refreshed = true;
        return route.fulfill({ status: 200, contentType: "application/json", body: "{}" });
      });
      await page.route("**/api/auth/me", (route) =>
        refreshed
          ? route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify(SESSION_FOUNDER) })
          : route.fulfill({ status: 401, contentType: "application/json", body: "{}" })
      );

      await page.goto("/saved");
      const shell = page.getByTestId("session-retry");
      await expect(shell).toContainText("Reel couldn't reach its server.");
      await expect(page).toHaveURL(/\/saved$/);

      databaseUp = true;
      await shell.getByRole("button", { name: "Try again" }).click();
      await expect(page.getByRole("heading", { name: "Saved" })).toBeVisible();
      await expect(page).toHaveURL(/\/saved$/);
    });
  }

  test("a 502 on /auth/me keeps the URL and offers a retry (H-15)", async ({ page }) => {
    await bootQuiet(page);
    await mockJson(page, "**/api/saved", { ...SAVED_A, items: [] });
    let up = false;
    await page.route("**/api/auth/me", (route) =>
      up
        ? route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify(SESSION_FOUNDER) })
        : route.fulfill({ status: 502, contentType: "text/html", body: "Bad Gateway" })
    );

    await page.goto("/saved");
    const shell = page.getByTestId("session-retry");
    await expect(shell).toContainText("Reel couldn't reach its server.");
    await expect(page).toHaveURL(/\/saved$/);

    up = true;
    await shell.getByRole("button", { name: "Try again" }).click();
    await expect(page.getByRole("heading", { name: "Saved" })).toBeVisible();
    await expect(page).toHaveURL(/\/saved$/);
  });
});

test.describe("error boundaries", () => {
  test("a render throw shows the route boundary with the header mounted and sends one report (I-4)", async ({ page }) => {
    await bootQuiet(page);
    await mockJson(page, "**/api/auth/me", SESSION_FOUNDER);
    // A malformed answer — `items` is not a list — makes the Saved page throw while rendering.
    await mockJson(page, "**/api/saved", { ...SAVED_A, items: "not-a-list" });
    const reports: Record<string, unknown>[] = [];
    await page.route("**/api/client-errors", (route) => {
      reports.push(route.request().postDataJSON());
      return route.fulfill({ status: 202, body: "" });
    });

    await page.goto("/saved?q=private");
    const boundary = page.getByTestId("route-error-boundary");
    await expect(boundary).toContainText("Something went wrong on this page");
    // It cannot know the report landed or that nothing was affected, so it says neither.
    await expect(boundary).not.toContainText(/reported|affected/i);
    await expect(page.getByRole("link", { name: "Reel home" })).toBeVisible();
    await expect(page.getByTestId("sync-health")).toBeVisible();

    await expect.poll(() => reports.length).toBe(1);
    expect(reports[0]).toMatchObject({ origin: "caught", path: "/saved" });
    expect(JSON.stringify(reports[0])).not.toContain("private");

    // Navigating away recovers without a reload.
    await mockJson(page, "**/api/saved", { ...SAVED_A, items: [] });
    await page.getByRole("link", { name: "Reel home" }).click();
    await expect(page.getByTestId("route-error-boundary")).toHaveCount(0);
  });

  test("a lazy chunk that fails to load after a deploy reloads once and recovers (I-3)", async ({ page }) => {
    await bootQuiet(page);
    await mockJson(page, "**/api/auth/me", SESSION_FOUNDER);
    await mockJson(page, "**/api/saved", { ...SAVED_A, items: [] });
    await page.goto("/home");
    await expect(page.getByRole("link", { name: "Reel home" })).toBeVisible();

    // The previous build's Saved chunk is gone: the first request for it fails, as a removed
    // hashed asset does after a redeploy.
    let blocked = 0;
    await page.route(/\/src\/pages\/Saved\.tsx/, (route) => {
      if (blocked === 0) {
        blocked += 1;
        return route.fulfill({ status: 404, body: "" });
      }
      return route.continue();
    });

    const reloaded = page.waitForEvent("load");
    await page.getByRole("link", { name: "Saved" }).first().click();
    await reloaded;
    await expect(page.getByRole("heading", { name: "Saved" })).toBeVisible();
    expect(blocked).toBe(1);
  });
});

test.describe("sign-out", () => {
  test("signing out forgets account A — worker cache, reel.* keys — and refetches nothing on the way out (I-11)", async ({ page }) => {
    await bootQuiet(page);
    let session: Record<string, unknown> | null = SESSION_FOUNDER;
    await page.route("**/api/auth/me", (route) =>
      session
        ? route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify(session) })
        : route.fulfill({ status: 401, contentType: "application/json", body: "{}" })
    );
    await mockJson(page, "**/api/auth/refresh", {}, 401);
    // Logout clears the cookies: from here the probe answers 401 until B signs in.
    await page.route("**/api/auth/logout", (route) => {
      session = null;
      return route.fulfill({ status: 200, contentType: "application/json", body: "{}" });
    });
    await mockJson(page, "**/api/saved", SAVED_A);

    await page.goto("/saved");
    await expect(page.getByText("Account A Secret Pick").first()).toBeVisible();

    // What A's session leaves in the browser: the worker's API cache and reel.* keys.
    await page.evaluate(async () => {
      const cache = await caches.open("reel-api");
      await cache.put("/api/saved", new Response(JSON.stringify({ items: ["A"] })));
      localStorage.setItem("reel.ask.conversationId", "conv-of-A");
      localStorage.setItem("unrelated.key", "kept");
    });

    // Every API request from the moment the user signs out: the signed-in pages must be gone
    // before the cache resets, or each of their queries refetches with no session.
    const afterSignOut: string[] = [];
    let signingOut = false;
    page.on("request", (request) => {
      const path = new URL(request.url()).pathname;
      if (signingOut && path.startsWith("/api/") && path !== "/api/auth/logout") afterSignOut.push(path);
    });

    await page.getByText(SESSION_FOUNDER.displayName).click();
    signingOut = true;
    await page.getByRole("menuitem", { name: "Sign out" }).click();
    await expect(page).toHaveURL(/\/$/);

    // The cleanup runs once the landing page has mounted.
    await expect
      .poll(() =>
        page.evaluate(async () => ({
          cache: await caches.has("reel-api"),
          keys: Object.keys(localStorage).filter((k) => k.startsWith("reel.")),
          unrelated: localStorage.getItem("unrelated.key"),
        }))
      )
      .toEqual({ cache: false, keys: [], unrelated: "kept" });
    await page.waitForTimeout(500);
    expect(afterSignOut).toEqual([]);

    // What account B could still be shown from this browser after A signed out is exactly what the
    // asserts above cover: linking B is a full page load (Trakt redirects back), so the store
    // starts empty, and the only carriers left are the worker's API cache (deleted; the worker's
    // own guard is unit-tested in lib/swApiCache.test.ts) and reel.* storage (cleared).
  });
});

test.describe("offline fallback", () => {
  test("a cached answer stays flagged while unrelated queries answer live (I-11)", async ({ page }) => {
    await bootQuiet(page);
    await mockJson(page, "**/api/auth/me", SESSION_FOUNDER);
    let savedFromCache = true;
    // What the worker sends when the network failed or timed out: the saved answer, marked, with
    // the server's Date from when it was saved. (Workers are blocked here, so the route stands in
    // for it; the worker's marking itself is unit-tested in lib/swApiCache.test.ts.)
    await page.route(
      (url) => url.pathname === "/api/saved",
      (route) =>
        route.fulfill({
          status: 200,
          contentType: "application/json",
          headers: savedFromCache
            ? { "X-Reel-From-Cache": "1", Date: new Date(Date.now() - 40 * 60_000).toUTCString() }
            : {},
          body: JSON.stringify(SAVED_A),
        })
    );
    let statusAnswers = 0;
    await page.route((url) => url.pathname === "/api/sync/status", (route) => {
      statusAnswers += 1;
      return route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify(syncStatus()) });
    });

    await page.goto("/saved");
    const banner = page.getByTestId("offline-banner");
    await expect(banner).toContainText("Couldn't reach Reel — showing what was saved");
    await expect(banner).not.toContainText("Offline");

    // The status query answers live again; the Saved list on screen is still the cached one.
    const before = statusAnswers;
    await page.evaluate(() =>
      (window as unknown as { __sse: { emit: (t: string, d: unknown) => void } }).__sse.emit("failed", {})
    );
    await expect.poll(() => statusAnswers).toBeGreaterThan(before);
    await page.waitForTimeout(200);
    await expect(banner).toBeVisible();

    // Leaving the page leaves its cached answer behind: Home's answers are live.
    await page.getByRole("link", { name: "Reel home" }).click();
    await expect(page).toHaveURL(/\/home$/);
    await expect(banner).toHaveCount(0);

    // Back on Saved, the list on screen is still the cached one, so the banner is back.
    await page.getByRole("link", { name: "Saved" }).first().click();
    await expect(banner).toBeVisible();

    // Saved answers live: now the banner goes.
    savedFromCache = false;
    await page.reload();
    await expect(page.getByRole("heading", { name: "Saved" })).toBeVisible();
    await expect(page.getByText("Account A Secret Pick").first()).toBeVisible();
    await expect(banner).toHaveCount(0);
  });
});

test.describe("shell layout", () => {
  test("the footer stays below the first viewport while a page loads (G-14, I-5)", async ({ page }) => {
    await bootQuiet(page);
    await mockJson(page, "**/api/auth/me", SESSION_FOUNDER);
    // Saved never answers: the page stays in its loading state.
    await page.route((url) => url.pathname === "/api/saved", () => undefined);
    for (const viewport of [
      { width: 390, height: 844 },
      { width: 1440, height: 900 },
    ]) {
      await page.setViewportSize(viewport);
      await page.goto("/saved");
      await expect(page.getByRole("link", { name: "Reel home" })).toBeVisible();
      const footerTop = await page.locator("footer").evaluate((el) => el.getBoundingClientRect().top);
      expect(footerTop).toBeGreaterThanOrEqual(viewport.height);
    }
  });
});
