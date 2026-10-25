import { expect, test, type Page } from "@playwright/test";
import { SESSION_FOUNDER, mockJson, stubEventSource, syncStatus } from "./helpers";

/**
 * The data router (WS-9, R1-54). The app moved from `<BrowserRouter>` to `createBrowserRouter`, so
 * `useBlocker` can question the Back button and programmatic `navigate()` (WS-10B's leave guard).
 * These lock in that history still behaves — Back and Forward across pages, Back after a
 * `navigate()` from the avatar menu — and that a render error the page boundary does not catch
 * reaches Reel's root boundary instead of the router's built-in error screen.
 */

const SAVED_EMPTY = {
  managedListUrl: null,
  sort: "tonight",
  eveningBudgetMinutes: 150,
  expectedAbsoluteError: 1.9,
  unscoredRemaining: 0,
  items: [],
};

async function boot(page: Page, status: Record<string, unknown> = syncStatus()) {
  await stubEventSource(page);
  // Everything this spec does not answer below is answered empty; registered first so the
  // specific mocks win.
  await page.route(
    (url) => url.pathname.startsWith("/api/"),
    (route) => route.fulfill({ status: 204, body: "" })
  );
  await mockJson(page, "**/api/auth/me", SESSION_FOUNDER);
  await mockJson(page, "**/api/sync/status", status);
  await mockJson(page, "**/api/saved", SAVED_EMPTY);
}

test("Back and Forward move between pages and keep each page's URL", async ({ page }) => {
  await boot(page);
  await page.goto("/saved");
  await expect(page.getByRole("heading", { name: "Saved" })).toBeVisible();

  await page.locator("header").getByRole("link", { name: "Taste DNA" }).first().click();
  await expect(page).toHaveURL(/\/dna$/);

  await page.goBack();
  await expect(page).toHaveURL(/\/saved$/);
  await expect(page.getByRole("heading", { name: "Saved" })).toBeVisible();

  await page.goForward();
  await expect(page).toHaveURL(/\/dna$/);
});

test("Back after a programmatic navigate() from the avatar menu returns to the page it left", async ({ page }) => {
  await boot(page);
  await page.goto("/saved");
  await expect(page.getByRole("heading", { name: "Saved" })).toBeVisible();

  await page.getByRole("button", { name: SESSION_FOUNDER.displayName }).click();
  await page.getByRole("menuitem", { name: "Track record" }).click();
  await expect(page).toHaveURL(/\/track-record$/);

  await page.goBack();
  await expect(page).toHaveURL(/\/saved$/);
  await expect(page.getByRole("heading", { name: "Saved" })).toBeVisible();
});

test("a render error outside the page boundary shows Reel's root boundary, not the router's error screen", async ({ page }) => {
  // A status payload this build cannot read makes the header pill throw while rendering. The
  // header sits outside the page's route boundary, so only the root boundary can catch it.
  await boot(page, syncStatus({ lastFailures: "not-a-list" }));
  const reports: unknown[] = [];
  await page.route("**/api/client-errors", async (route) => {
    reports.push(route.request().postDataJSON());
    await route.fulfill({ status: 204, body: "" });
  });

  await page.goto("/saved");
  await expect(page.getByTestId("root-error-boundary")).toBeVisible();
  await expect(page.getByText("Unexpected Application Error")).toHaveCount(0);
  await expect.poll(() => reports.length).toBeGreaterThanOrEqual(1);
});
