import { expect, test, type Page } from "@playwright/test";
import { SESSION_FOUNDER, mockJson, stubEventSource, syncStatus } from "./helpers";

/**
 * The shell's phone targets (integrator CR to WS-9, IREV3S-08, O-15): on a touch device at 390×844,
 * no interactive element in the header, the bottom tab bar, the footer or the toast column is
 * smaller than 44 × 44, and a mouse still gets the compact controls. phone-targets.spec.ts probes
 * WS-10A's page content; this probes what wraps every page.
 *
 * The 44 px floor comes from the `coarse:` variant on the ui/ primitives, so the same probe also
 * proves the variant compiles: a `coarse:min-h-11` Tailwind failed to emit would leave the
 * 28 px icon buttons here exactly as they were.
 */

const PHONE = { viewport: { width: 390, height: 844 }, hasTouch: true, isMobile: true };

const SHELL = 'header, [data-testid="mobile-tabbar"], footer, [data-testid="toast-stack"]';

async function smallTargets(page: Page): Promise<string[]> {
  return page.evaluate((rootSelector) => {
    const selector = 'a[href], button, input, select, textarea, [role="button"], [tabindex]:not([tabindex="-1"])';
    const out: string[] = [];
    for (const root of Array.from(document.querySelectorAll(rootSelector))) {
      for (const el of Array.from(root.querySelectorAll<HTMLElement>(selector))) {
        const r = el.getBoundingClientRect();
        const style = getComputedStyle(el);
        if (r.width === 0 || r.height === 0 || style.visibility === "hidden" || style.display === "none") continue;
        if (el.closest('[aria-hidden="true"]')) continue;
        // A visually-hidden skip link is a 1 px box until focused.
        if (el.classList.contains("sr-only")) continue;
        if (r.width >= 44 && r.height >= 44) continue;
        const label = el.getAttribute("aria-label") ?? el.getAttribute("data-testid") ?? el.textContent?.trim().slice(0, 30) ?? el.tagName;
        out.push(`${el.tagName.toLowerCase()} "${label}" ${Math.round(r.width)}×${Math.round(r.height)}`);
      }
    }
    return out;
  }, SHELL);
}

/** The shell on /saved, with a failing sync so the pill shows its Reconnect action and details icon. */
async function boot(page: Page) {
  await stubEventSource(page);
  await mockJson(page, "**/api/auth/me", SESSION_FOUNDER);
  await mockJson(page, "**/api/sync/status", syncStatus({ connectionStatus: "RefreshFailed", degradedReason: "TraktRefreshFailed" }));
  await mockJson(page, "**/api/saved", { managedListUrl: null, sort: "tonight", eveningBudgetMinutes: 150, expectedAbsoluteError: null, unscoredRemaining: 0, items: [] });
  await mockJson(page, "**/api/saved/recently-watched", { expectedAbsoluteError: null, items: [] });
}

test("the shell has no target under 44 px on a touch phone", async ({ browser }) => {
  const context = await browser.newContext(PHONE);
  const page = await context.newPage();
  await boot(page);
  await page.goto("/saved");
  await expect(page.getByRole("heading", { name: "Saved" })).toBeVisible();
  await expect(page.getByTestId("sync-reconnect")).toBeVisible();
  await expect(page.getByTestId("mobile-tabbar")).toBeVisible();

  expect(await smallTargets(page)).toEqual([]);

  // The open account menu's items are targets too.
  await page.getByRole("button", { name: /account menu/i }).tap();
  const item = page.getByRole("menuitem", { name: "Settings" });
  await expect(item).toBeVisible();
  expect((await item.boundingBox())?.height ?? 0).toBeGreaterThanOrEqual(44);
  await context.close();
});

test("a mouse keeps the compact header controls", async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 900 });
  await boot(page);
  await page.goto("/saved");
  const details = page.getByTestId("sync-details");
  await expect(details).toBeVisible();
  // `h-7 w-7`: 28 px under a fine pointer.
  expect(Math.round((await details.boundingBox())?.height ?? 0)).toBe(28);
});

/**
 * R1-38: a failed read's Retry is `size="sm"` (30 px tall under a mouse, as WS-10B measured it) and
 * still a 44 × 44 target on a phone, because the floor lives in Button itself. Two pages, so a
 * page-specific wrapper cannot be what makes it pass.
 */
for (const { route, testId } of [
  { route: "/dna", testId: "dna-error-retry" },
  { route: "/track-record", testId: "track-record-error-retry" },
]) {
  test(`a failed read's Retry on ${route} is 44 px on a touch phone and compact under a mouse`, async ({ browser }) => {
    for (const touch of [true, false]) {
      const context = await browser.newContext(touch ? PHONE : { viewport: { width: 1440, height: 900 } });
      const page = await context.newPage();
      await stubEventSource(page);
      // Every read fails, except the two the shell needs to render at all.
      await page.route(
        (u) => u.pathname.startsWith("/api/"),
        (r) => r.fulfill({ status: 500, contentType: "application/json", body: "{}" })
      );
      await mockJson(page, "**/api/auth/me", SESSION_FOUNDER);
      await mockJson(page, "**/api/sync/status", syncStatus());
      await page.goto(route);
      const retry = page.getByTestId(testId);
      await expect(retry).toBeVisible();
      const box = await retry.boundingBox();
      if (touch) {
        expect(box?.height ?? 0).toBeGreaterThanOrEqual(44);
        expect(box?.width ?? 0).toBeGreaterThanOrEqual(44);
      } else {
        expect(box?.height ?? 99).toBeLessThan(44);
      }
      await context.close();
    }
  });
}
