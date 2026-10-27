import { test, expect, type Page } from "@playwright/test";
import { SESSION_FOUNDER, mockJson, stubEventSource, syncStatus } from "./helpers";
import { mockFilterChips } from "./filterChips.fixture";

/**
 * Claims family failure-states-change-nothing (R1-46): a failed action says it changed nothing —
 * still on the list, still on, kept, nothing charged — and the page agrees with the sentence. Each
 * case here is a variant the owning surface's own spec did not cover; the covered ones are named
 * in the WS-9 report for the claims register:
 *   - a NotInterested revoke (taste-dna-dislikes.spec.ts) → here, a Dropped revoke;
 *   - a failed notifications ENABLE (settings-errors.spec.ts, copy only) → here, a failed DISABLE,
 *     and the refused enable again with a browser that remembers its subscription across a reload;
 *   - a 423 on an account with no PIN (settings-errors.spec.ts) → here, an unlock that lapses
 *     mid-edit on a PIN account, where the draft must survive the lock screen;
 *   - a refused checkout with the server's reason (settings-errors.spec.ts) → here, a checkout
 *     request that never gets an answer.
 * Every /api call is answered: a 404 for anything a case does not mock.
 */

async function shell(page: Page, session: Record<string, unknown> = SESSION_FOUNDER) {
  await stubEventSource(page);
  await page.route(
    (u) => u.pathname.startsWith("/api/"),
    (route) => route.fulfill({ status: 404, contentType: "application/json", body: "{}" })
  );
  await mockJson(page, "**/api/auth/me", session);
  await mockJson(page, "**/api/sync/status", syncStatus());
}

test("a Dropped title whose revoke fails stays on the list, and the panel says so", async ({ page }) => {
  await shell(page);
  const dropped = {
    titleId: "00000000-0000-0000-0000-000000000202",
    mediaType: "Show",
    tmdbId: 3002,
    name: "Dropped Show",
    year: 2019,
    posterPath: null,
    kind: "Dropped",
    reason: null,
    createdAt: "2026-09-01T12:00:00Z",
  };
  await mockJson(page, "**/api/taste", {
    userMean: 6.7, contrarianScore: 0.2, ratingsCount: 2044, hoursWatched: 4000, titlesWatched: 3353,
    showCompletionRate: 0.7, topGenres: [], eras: [], creators: [], topActors: [],
    histogram: [{ rating: 7, count: 10 }], drift: [],
  });
  await mockJson(page, "**/api/taste/dislikes", {
    total: 1,
    offset: 0,
    limit: 20,
    hasMore: false,
    items: [dropped],
    profile: { windowDays: 180, activeCount: 1, genres: [], tones: [] },
  });
  let revokes = 0;
  await page.route(
    (u) => u.pathname === "/api/titles/show/3002/reactions/dropped",
    (route) => {
      revokes += 1;
      return route.fulfill({ status: 500, contentType: "application/json", body: "{}" });
    }
  );
  await page.goto("/dna");
  await page.getByTestId("dislike-toggle").click();
  await expect(page.getByTestId("dislike-row")).toHaveCount(1);

  await page.getByTestId("dislike-row").getByTestId("dislike-revoke").click();
  await expect(page.getByTestId("dislike-revoke-status")).toContainText("Couldn't undo Dropped Show — it's still on the list.");
  expect(revokes).toBe(1);
  await expect(page.getByTestId("dislike-row")).toHaveCount(1);
  await expect(page.getByTestId("dislike-row")).toContainText("Dropped Show");
});

/** Settings with push supported and a live subscription whose browser unsubscribe throws. */
async function bootPushOn(page: Page) {
  await page.addInitScript(() => {
    const subscription = {
      endpoint: "https://push.example/abc",
      toJSON: () => ({ keys: { p256dh: "p", auth: "a" } }),
      unsubscribe: async () => {
        throw new Error("push service unreachable");
      },
    };
    const registration = {
      pushManager: { getSubscription: async () => subscription, subscribe: async () => subscription },
    };
    Object.defineProperty(window, "PushManager", { value: function PushManager() {}, configurable: true });
    Object.defineProperty(window, "Notification", {
      value: { permission: "granted", requestPermission: async () => "granted" },
      configurable: true,
    });
    Object.defineProperty(navigator, "serviceWorker", {
      value: { getRegistration: async () => registration, getRegistrations: async () => [], ready: Promise.resolve(registration) },
      configurable: true,
    });
  });
  await shell(page);
  await mockJson(page, "**/api/settings", { region: "US", onboarded: true, email: null, eveningBudgetMinutes: 150 });
  await mockJson(page, "**/api/preferences/tags", []);
}

test("a failed notifications disable says they are still on, and the toggle still offers Turn off", async ({ page }) => {
  await bootPushOn(page);
  let serverUnsubscribes = 0;
  await page.route(
    (u) => u.pathname.startsWith("/api/push/") && u.pathname !== "/api/push/vapid-public-key",
    (route) => {
      serverUnsubscribes += 1;
      return route.fulfill({ status: 204, body: "" });
    }
  );
  await page.goto("/settings");

  const toggle = page.getByTestId("push-toggle");
  await expect(toggle).toContainText("Turn off");
  await toggle.click();
  await expect(page.getByTestId("push-failure")).toHaveText("Couldn't turn notifications off — they're still on. Try again in a moment.");
  await expect(toggle).toContainText("Turn off");
  // The browser kept its subscription, so the server's row was left alone too.
  expect(serverUnsubscribes).toBe(0);
});

/**
 * Settings with push supported, permission granted and NO subscription yet. The browser's
 * subscription lives in sessionStorage, so it outlives a reload the way a real one does:
 * subscribe() creates it, unsubscribe() drops it, getSubscription() reports what is there. The
 * settings-errors enable case keeps getSubscription() at null forever, which is how it could pass
 * while a refused enable left the browser subscribed.
 */
async function bootPushOff(page: Page, subscribeStatus: 200 | 500) {
  await page.addInitScript(() => {
    const KEY = "e2e-browser-push-subscription";
    const subscription = {
      endpoint: "https://fcm.googleapis.com/fcm/send/e2e",
      toJSON: () => ({ keys: { p256dh: "p", auth: "a" } }),
      unsubscribe: async () => {
        sessionStorage.removeItem(KEY);
        return true;
      },
    };
    const registration = {
      pushManager: {
        getSubscription: async () => (sessionStorage.getItem(KEY) ? subscription : null),
        subscribe: async () => {
          sessionStorage.setItem(KEY, "1");
          return subscription;
        },
      },
    };
    Object.defineProperty(window, "PushManager", { value: function PushManager() {}, configurable: true });
    Object.defineProperty(window, "Notification", {
      value: { permission: "granted", requestPermission: async () => "granted" },
      configurable: true,
    });
    Object.defineProperty(navigator, "serviceWorker", {
      value: { getRegistration: async () => registration, getRegistrations: async () => [], ready: Promise.resolve(registration) },
      configurable: true,
    });
  });
  await shell(page);
  await mockJson(page, "**/api/settings", { region: "US", onboarded: true, email: null, eveningBudgetMinutes: 150 });
  await mockJson(page, "**/api/preferences/tags", []);
  await mockJson(page, "**/api/push/vapid-public-key", { key: "BEl62iUYgUivxIkv69yViEuiBIa-Ib9-SkvMeAtA3LFgDzkrxZJjSgSnfckjBJuBkr3qBUYIHBQFLXYp5Nksh8U" });
  // The server's rows, keyed by endpoint: a POST it accepts adds one, a DELETE removes it.
  const serverRows = new Set<string>();
  await page.route(
    (u) => u.pathname === "/api/push/subscribe",
    async (route) => {
      const { endpoint } = route.request().postDataJSON() as { endpoint: string };
      if (route.request().method() === "DELETE") {
        serverRows.delete(endpoint);
        return route.fulfill({ status: 204, body: "" });
      }
      if (subscribeStatus === 200) serverRows.add(endpoint);
      return route.fulfill({ status: subscribeStatus, contentType: "application/json", body: subscribeStatus === 200 ? '{"ok":true}' : "{}" });
    }
  );
  const browserSubscribed = () => page.evaluate(() => sessionStorage.getItem("e2e-browser-push-subscription") !== null);
  return { serverRows, browserSubscribed };
}

test("an accepted notifications enable is still on after a reload (the fixture remembers the browser half)", async ({ page }) => {
  const { serverRows, browserSubscribed } = await bootPushOff(page, 200);
  await page.goto("/settings");
  const toggle = page.getByTestId("push-toggle");
  await expect(toggle).toContainText("Turn on");
  await toggle.click();
  await expect(toggle).toContainText("Turn off");
  expect(serverRows.size).toBe(1);
  expect(await browserSubscribed()).toBe(true);

  await page.reload();
  await expect(page.getByTestId("push-toggle")).toContainText("Turn off");
});

test("a notifications enable the server refuses leaves them off in the browser too, so a reload still says off", async ({ page }) => {
  // KNOWN DEFECT, change request CR-11 → WS-10B (PushOptIn.tsx is theirs): enable()'s catch never
  // unsubscribes the browser subscription it just created, so the next visit reads "Turn off" while
  // the server has no row and no digest can arrive. Remove this line when CR-11 lands — Playwright
  // reports the test as an unexpected pass until then.
  test.fail(true, "CR-11 → WS-10B: PushOptIn enable() leaves the browser subscribed after the server refuses");
  const { serverRows, browserSubscribed } = await bootPushOff(page, 500);
  await page.goto("/settings");
  const toggle = page.getByTestId("push-toggle");
  await expect(toggle).toContainText("Turn on");
  await toggle.click();
  await expect(page.getByTestId("push-failure")).toHaveText("Couldn't turn notifications on — they're still off. Try again in a moment.");
  await expect(toggle).toContainText("Turn on");
  expect(serverRows.size).toBe(0);
  // "Still off" has to be true of the browser as well as of the server.
  expect(await browserSubscribed()).toBe(false);

  await page.reload();
  await expect(page.getByTestId("push-toggle")).toContainText("Turn on");
  await expect(page.getByTestId("push-failure")).toHaveCount(0);
});

const FILTERS = {
  excludeGenres: ["horror"],
  includeGenres: [],
  excludeKeywords: [],
  maturityCeiling: null,
  minPredictedRating: null,
  languages: ["en"],
  minCrowdRating: null,
  minYear: null,
  maxYear: null,
};

test("an unlock that lapses mid-edit keeps the unsaved draft, and the next unlock saves exactly it", async ({ page }) => {
  await shell(page, { ...SESSION_FOUNDER, pinConfigured: true });
  await mockJson(page, "**/api/settings", { region: "US", onboarded: true, email: null, eveningBudgetMinutes: 150 });
  await mockJson(page, "**/api/preferences/tags", []);
  await mockJson(page, "**/api/billing/status", { tier: "Founder", configured: false });
  await mockJson(page, "**/api/settings/filters/cost", { total: 5000, eligible: 400, byRule: [], suggestions: [] });
  await mockFilterChips(page);
  let unlocks = 0;
  await page.route("**/api/settings/pin/verify", (route) => {
    unlocks += 1;
    return route.fulfill({
      status: 200,
      contentType: "application/json",
      body: JSON.stringify({ valid: true, unlockToken: `token-${unlocks}`, expiresAt: new Date(Date.now() + 15 * 60_000).toISOString() }),
    });
  });
  const puts: { token: string | null; includeGenres: string[] }[] = [];
  await page.route(
    (u) => u.pathname === "/api/settings/filters",
    (route) => {
      if (route.request().method() !== "PUT") {
        return route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify(FILTERS) });
      }
      const body = route.request().postDataJSON() as typeof FILTERS & { includeGenres: string[] };
      puts.push({ token: route.request().headers()["x-settings-unlock"] ?? null, includeGenres: body.includeGenres });
      // The first save finds the unlock lapsed server-side (423); the second lands.
      return puts.length === 1
        ? route.fulfill({ status: 423, contentType: "application/json", body: "{}" })
        : route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify(body) });
    }
  );
  await page.goto("/settings");

  await page.getByTestId("pin-input").fill("2468");
  await page.getByRole("button", { name: "Unlock" }).click();
  await expect(page.getByTestId("prefs-section")).toBeVisible();

  await page.getByTestId("genre-war-include").click();
  await page.getByTestId("save-prefs").click();

  // The lock screen is back, says why, and says the draft survived it.
  await expect(page.getByTestId("prefs-locked")).toBeVisible();
  await expect(page.getByTestId("conditions-locked-reason")).toContainText("Your unsaved changes are kept — unlock to save them.");

  await page.getByTestId("pin-input").fill("2468");
  await page.getByRole("button", { name: "Unlock" }).click();
  await expect(page.getByTestId("genre-war")).toHaveAttribute("data-state", "include");
  await expect(page.getByTestId("conditions-unsaved")).toBeVisible();
  await page.getByTestId("save-prefs").click();

  await expect.poll(() => puts.length).toBe(2);
  // The same draft both times, each under the unlock it was made with.
  expect(puts).toEqual([
    { token: "token-1", includeGenres: ["war"] },
    { token: "token-2", includeGenres: ["war"] },
  ]);
});

test("a checkout request that never gets an answer says nothing was charged, and the plan is unchanged", async ({ page }) => {
  await shell(page, { ...SESSION_FOUNDER, tier: "Free" });
  await mockJson(page, "**/api/settings", { region: "US", onboarded: true, email: null, eveningBudgetMinutes: 150 });
  await mockJson(page, "**/api/preferences/tags", []);
  await mockJson(page, "**/api/billing/status", { tier: "Free", configured: true });
  await mockJson(page, "**/api/settings/filters", FILTERS);
  await mockFilterChips(page);
  await page.route("**/api/billing/checkout", (route) => route.abort("connectionrefused"));
  await page.goto("/settings");

  const upgrade = page.getByRole("button", { name: /upgrade/i });
  await upgrade.click();
  await expect(page.getByTestId("checkout-failure")).toHaveText("Couldn't open checkout — nothing was charged. Try again in a moment.");
  // Still on Settings, still Free, still offered the upgrade: nothing moved.
  await expect(page).toHaveURL(/\/settings$/);
  await expect(page.getByTestId("plan-line")).toContainText("Free includes taste DNA");
  await expect(upgrade).toBeEnabled();
});
