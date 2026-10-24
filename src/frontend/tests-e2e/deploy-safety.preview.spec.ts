import { expect, test } from "@playwright/test";
import { SESSION_FOUNDER, mockJson, stubEventSource, syncStatus } from "./helpers";

/**
 * Deploy safety on the production build (WS-9 P0 task 4; I-3). A tab opened on the previous build
 * asks for a lazy chunk whose hash the redeploy removed. It must reload once onto the new build
 * and land on the page it asked for — not go blank, and not loop.
 */
test("an old tab navigating after a redeploy reloads cleanly", async ({ page }) => {
  await stubEventSource(page);
  await mockJson(page, "**/api/auth/me", SESSION_FOUNDER);
  await mockJson(page, "**/api/sync/status", syncStatus());
  await page.route(
    (url) => url.pathname === "/api/saved",
    (route) =>
      route.fulfill({
        status: 200,
        contentType: "application/json",
        body: JSON.stringify({ managedListUrl: null, sort: "tonight", eveningBudgetMinutes: 150, expectedAbsoluteError: null, unscoredRemaining: 0, items: [] }),
      })
  );
  await page.route(
    (url) => url.pathname.startsWith("/api/") && !["/api/auth/me", "/api/sync/status", "/api/saved"].includes(url.pathname),
    (route) => route.fulfill({ status: 200, contentType: "application/json", body: "{}" })
  );

  await page.goto("/home");
  await expect(page.getByRole("link", { name: "Reel home" })).toBeVisible();

  // The redeploy: the Saved chunk this tab knows by its old hash is gone (404 the first time).
  let missing = 0;
  await page.route(/\/assets\/Saved-[^/]+\.js$/, (route) => {
    if (missing === 0) {
      missing += 1;
      return route.fulfill({ status: 404, body: "" });
    }
    return route.continue();
  });

  let loads = 0;
  page.on("load", () => (loads += 1));
  await page.getByRole("link", { name: "Saved" }).first().click();

  await expect(page.getByRole("heading", { name: "Saved" })).toBeVisible();
  await expect(page).toHaveURL(/\/saved$/);
  expect(missing).toBe(1);
  expect(loads).toBe(1);
  await expect(page.getByTestId("route-error-boundary")).toHaveCount(0);
});
