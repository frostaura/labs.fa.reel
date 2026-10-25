import { existsSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { expect, test } from "@playwright/test";
import { mockJson, stubEventSource } from "./helpers";

/**
 * The privacy and terms routes (WS-9 P0 task 6; H-14). The pages are another lane's files and the
 * routes appear in the same build as they do, so this spec reads the build's own source tree and
 * asserts whichever case is true: every page present is routed, readable signed out inside the
 * public shell and linked from the footer; every page absent has no route and no link — never a
 * link to a page that is not there.
 */
const PAGES = [
  { file: "../src/pages/Privacy.tsx", path: "/privacy", label: "Privacy" },
  { file: "../src/pages/Terms.tsx", path: "/terms", label: "Terms" },
].map((p) => ({ ...p, present: existsSync(fileURLToPath(new URL(p.file, import.meta.url))) }));

test.beforeEach(async ({ page }) => {
  await stubEventSource(page);
  // Signed out: the legal pages must be readable before anyone links Trakt.
  await mockJson(page, "**/api/auth/me", {}, 401);
  await mockJson(page, "**/api/auth/refresh", {}, 401);
  await page.route(
    (url) => url.pathname.startsWith("/api/") && !["/api/auth/me", "/api/auth/refresh"].includes(url.pathname),
    (route) => route.fulfill({ status: 204, body: "" })
  );
});

for (const legal of PAGES) {
  test(`${legal.path}: ${legal.present ? "routed, readable signed out, linked from the footer" : "no route and no footer link until the page exists"}`, async ({ page }) => {
    await page.goto(legal.path);
    const footerLink = page.locator("footer").getByRole("link", { name: legal.label, exact: true });

    if (legal.present) {
      await expect(page).toHaveURL(new RegExp(`${legal.path}$`));
      await expect(page.getByRole("link", { name: "Reel home" })).toBeVisible();
      await expect(page.locator("main")).not.toBeEmpty();
      await expect(footerLink).toHaveAttribute("href", legal.path);
    } else {
      // Falls through to the catch-all and, signed out, to the landing page.
      await expect(page).not.toHaveURL(new RegExp(`${legal.path}$`));
      await expect(page.locator("footer").first()).toBeVisible();
      await expect(footerLink).toHaveCount(0);
    }
  });
}
