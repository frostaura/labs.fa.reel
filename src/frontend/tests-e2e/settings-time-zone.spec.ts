import AxeBuilder from "@axe-core/playwright";
import { test, expect, type Page, type Route } from "@playwright/test";
import { SESSION_FOUNDER, mockJson, stubEventSource } from "./helpers";
import { mockFilterChips } from "./filterChips.fixture";

/**
 * The account time zone on Settings (R1-17). The server stores an IANA zone, but nothing on screen
 * could set one. Nothing reads it yet either, so the card claims no effect of it (see UNPROMISED).
 *
 * What these pin, against a mocked API in a browser whose own zone is emulated: an account that
 * never chose is told so and offered this device's zone; any zone can be searched and picked; the
 * PUT carries exactly the zone; a 400 `time_zone_invalid` shows the server's reason and changes
 * nothing; the card is axe-clean (full rule set, contrast included) in each state; and it fits a
 * phone with 44 px targets.
 */

const FILTERS = {
  excludeGenres: [], includeGenres: [], excludeKeywords: [], maturityCeiling: null, minPredictedRating: null,
  languages: ["en"], minCrowdRating: null, minYear: null, maxYear: null,
};
const SYNC = {
  pipelineStage: "FeedReady", connectionStatus: "Active", lastDeltaSyncAt: null,
  lastFullReconcileAt: null, activeJob: null, recentJobs: [], outboxPending: 0, outboxDeadLetters: 0,
};
const BASE = { region: "ZA", onboarded: true, email: null, eveningBudgetMinutes: 150 };

interface Stored {
  timeZone: string | null;
  effectiveTimeZone: string;
}

/** Boots /settings with `stored` on the account; PUTs go through `onPut`, and each body is kept. */
async function boot(page: Page, stored: Stored, onPut?: (route: Route, body: { timeZone?: string }) => Promise<void>) {
  const puts: { timeZone?: string }[] = [];
  let current = { ...BASE, ...stored };
  await stubEventSource(page);
  await mockJson(page, "**/api/auth/me", SESSION_FOUNDER);
  await mockJson(page, "**/api/sync/status", SYNC);
  await mockJson(page, "**/api/billing/status", { tier: "Founder", configured: false });
  await mockJson(page, "**/api/preferences/tags", []);
  await mockJson(page, "**/api/settings/filters/cost", { total: 5000, eligible: 400, byRule: [], suggestions: [] });
  await mockFilterChips(page);
  await mockJson(page, "**/api/settings/filters", FILTERS);
  await page.route(
    (u) => u.pathname === "/api/settings",
    async (route) => {
      if (route.request().method() !== "PUT") {
        await route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify(current) });
        return;
      }
      const body = route.request().postDataJSON() as { timeZone?: string };
      puts.push(body);
      if (onPut) {
        await onPut(route, body);
        return;
      }
      current = { ...current, timeZone: body.timeZone ?? null, effectiveTimeZone: body.timeZone ?? "UTC" };
      await route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify(current) });
    }
  );
  await page.goto("/settings");
  await expect(page.getByTestId("time-zone-current")).toBeVisible();
  return puts;
}

/**
 * What the card must not claim (R1-17 follow-on): the Ask allowance's day and the feed's day are still
 * UTC days, so no sentence may say picks or allowances turn over in the chosen zone.
 */
const UNPROMISED = /picks|allowance|midnight|counting|counts your day|turns? over|ask reel/i;

async function axeOnCard(page: Page) {
  const result = await new AxeBuilder({ page }).include('[data-testid="settings-time-zone"]').analyze();
  return result.violations.map((v) => `${v.id}: ${v.nodes.map((n) => n.target.join(" ")).join(", ")}`);
}

test.describe("time zone, on a device in Johannesburg", () => {
  test.use({ timezoneId: "Africa/Johannesburg" });

  test("an account that never chose is told so, and one tap stores this device's zone", async ({ page }) => {
    const puts = await boot(page, { timeZone: null, effectiveTimeZone: "UTC" });

    await expect(page.getByTestId("time-zone-current")).toHaveText("Not set — your account's zone is UTC until you choose one.");
    expect(await axeOnCard(page)).toEqual([]);

    const use = page.getByTestId("time-zone-use-device");
    await expect(use).toHaveText("Use this device's zone — Africa / Johannesburg");
    await use.click();

    await expect(page.getByTestId("time-zone-save-status")).toHaveText("Saved.");
    await expect(page.getByTestId("time-zone-save-status")).toHaveAttribute("data-tone", "success");
    expect(puts).toEqual([{ timeZone: "Africa/Johannesburg" }]);
    await expect(page.getByTestId("time-zone-current")).toHaveText("Your zone: Africa / Johannesburg (GMT+2).");
    await expect(page.getByTestId("time-zone-use-device")).toHaveCount(0);
    await expect(page.getByTestId("time-zone-select")).toHaveValue("Africa / Johannesburg");
    // R1-17 follow-on: nothing reads the zone yet, so the card claims no effect of it.
    await expect(page.getByTestId("settings-time-zone")).not.toContainText(UNPROMISED);
    expect(await axeOnCard(page)).toEqual([]);
  });

  test("any zone can be searched for and picked, and the PUT carries exactly it", async ({ page }) => {
    const puts = await boot(page, { timeZone: null, effectiveTimeZone: "UTC" });

    const box = page.getByTestId("time-zone-select");
    await box.click();
    await box.fill("new york");
    const option = page.getByTestId("time-zone-select-option").first();
    await expect(option).toHaveAttribute("data-code", "America/New_York");
    await expect(page.getByTestId("time-zone-select-options")).toBeVisible();
    expect(await axeOnCard(page)).toEqual([]);
    await option.click();

    await expect(page.getByTestId("time-zone-save-status")).toHaveText("Saved.");
    expect(puts).toEqual([{ timeZone: "America/New_York" }]);
    await expect(page.getByTestId("time-zone-current")).toContainText("Your zone: America / New York (GMT-");
    // The device is elsewhere now, so its zone is offered back — quietly.
    await expect(page.getByTestId("time-zone-use-device")).toHaveText("Use this device's zone — Africa / Johannesburg");
  });

  test("a refused zone shows the server's reason in danger tone and changes nothing", async ({ page }) => {
    const puts = await boot(page, { timeZone: null, effectiveTimeZone: "UTC" }, (route) =>
      route.fulfill({
        status: 400,
        contentType: "application/json",
        body: JSON.stringify({ error: "timeZone must be an IANA time zone such as Africa/Johannesburg", code: "time_zone_invalid" }),
      })
    );
    await page.clock.install();

    await page.getByTestId("time-zone-use-device").click();
    const status = page.getByTestId("time-zone-save-status");
    await expect(status).toHaveText("Time zone must be an IANA time zone such as Africa/Johannesburg.");
    await expect(status).toHaveAttribute("data-tone", "danger");
    await expect(status).toHaveAttribute("role", "alert");
    await expect(page.getByTestId("time-zone-save-status-retry")).toHaveCount(0);
    expect(puts).toHaveLength(1);
    await expect(page.getByTestId("time-zone-current")).toHaveText("Not set — your account's zone is UTC until you choose one.");
    expect(await axeOnCard(page)).toEqual([]);

    // A failure is not a toast: it is still up 10 s later.
    await page.clock.fastForward(10_000);
    await expect(status).toBeVisible();
  });

  test("a failed read says so with a Retry instead of an empty card", async ({ page }) => {
    let fail = true;
    await stubEventSource(page);
    await mockJson(page, "**/api/auth/me", SESSION_FOUNDER);
    await mockJson(page, "**/api/sync/status", SYNC);
    await mockJson(page, "**/api/billing/status", { tier: "Founder", configured: false });
    await mockJson(page, "**/api/preferences/tags", []);
    await mockJson(page, "**/api/settings/filters/cost", { total: 5000, eligible: 400, byRule: [], suggestions: [] });
    await mockFilterChips(page);
    await mockJson(page, "**/api/settings/filters", FILTERS);
    await page.route(
      (u) => u.pathname === "/api/settings",
      (route) =>
        fail
          ? route.fulfill({ status: 500, contentType: "application/json", body: "{}" })
          : route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify({ ...BASE, timeZone: "Africa/Johannesburg", effectiveTimeZone: "Africa/Johannesburg" }) })
    );
    await page.goto("/settings");

    const error = page.getByTestId("time-zone-load-error");
    await expect(error).toContainText("Couldn't load your time zone.");
    fail = false;
    await error.getByRole("button").click();
    await expect(page.getByTestId("time-zone-current")).toHaveText("Your zone: Africa / Johannesburg (GMT+2).");
  });
});

test.describe("time zone, on a device in London", () => {
  test.use({ timezoneId: "Europe/London" });

  test("a chosen zone stays chosen; the device's differing zone is offered, not applied", async ({ page }) => {
    const puts = await boot(page, { timeZone: "Africa/Johannesburg", effectiveTimeZone: "Africa/Johannesburg" });

    await expect(page.getByTestId("time-zone-current")).toHaveText("Your zone: Africa / Johannesburg (GMT+2).");
    await expect(page.getByTestId("time-zone-use-device")).toHaveText("Use this device's zone — Europe / London");
    await expect(page.getByTestId("settings-time-zone")).not.toContainText(UNPROMISED);
    await page.waitForTimeout(300);
    expect(puts).toEqual([]);
  });
});

test.describe("time zone on a phone", () => {
  test.use({ timezoneId: "Africa/Johannesburg", viewport: { width: 390, height: 844 }, hasTouch: true, isMobile: true });

  test("fits the width, and its controls are 44 px targets", async ({ page }) => {
    await boot(page, { timeZone: null, effectiveTimeZone: "UTC" });
    const card = page.getByTestId("settings-time-zone");
    await card.scrollIntoViewIfNeeded();
    const { scrollWidth, innerWidth } = await page.evaluate(() => ({
      scrollWidth: document.documentElement.scrollWidth,
      innerWidth: window.innerWidth,
    }));
    expect(scrollWidth).toBe(innerWidth);
    const cardBox = await card.boundingBox();
    for (const id of ["time-zone-use-device", "time-zone-select"]) {
      const box = await page.getByTestId(id).boundingBox();
      expect(box?.height ?? 0, id).toBeGreaterThanOrEqual(44);
      // Inside the card's padding (p-5, 20 px), not running into or past its edge — a long zone name
      // on one line is wider than a phone's card, and the page itself does not scroll sideways
      // either way, so the page-width check above cannot see it.
      expect((box?.x ?? 0) + (box?.width ?? 0), id).toBeLessThanOrEqual((cardBox?.x ?? 0) + (cardBox?.width ?? 0) - 20);
    }
  });
});
