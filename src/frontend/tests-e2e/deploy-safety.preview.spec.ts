import { readdirSync, readFileSync } from "node:fs";
import path from "node:path";
import { expect, test, type Page } from "@playwright/test";
import { SESSION_FOUNDER, mockJson, stubEventSource, syncStatus } from "./helpers";

/**
 * Deploy safety on the production build (WS-9 P0 task 4; I-3). A tab opened on the previous build
 * asks for a lazy chunk whose hash the redeploy removed. It must reload once onto the new build
 * and land on the page it asked for — not go blank, and not loop.
 *
 * The redeploy is simulated as one really happens (PH-12): from the moment it lands, every asset
 * URL the old build named is gone (404), and the server hands out a NEW build — an index.html that
 * names a new entry chunk, whose chunks name each other by new hashes. The new build is the old
 * one's bytes with every asset file renamed, so it runs the same code. The earlier version 404'd
 * the Saved chunk once and then served it again at the SAME URL; WebKit, rightly, reuses the failed
 * response for an unchanged module URL, so the spec failed there while the app was fine — and a
 * real redeploy never serves a new chunk at an old hash anyway.
 */

const DIST = path.resolve(path.dirname(new URL(import.meta.url).pathname), "../dist");
const NEW = "-redeployed";

/** Every hashed asset of the build, old name → the new build's name for the same bytes. */
function renames(): Map<string, string> {
  const map = new Map<string, string>();
  for (const file of readdirSync(path.join(DIST, "assets"))) {
    const ext = path.extname(file);
    if (ext === ".js" || ext === ".css") map.set(file, `${file.slice(0, -ext.length)}${NEW}${ext}`);
  }
  return map;
}

/** Rewrites every old asset name in `text` to its new-build name, in one pass. */
function toNewBuild(text: string, names: Map<string, string>): string {
  const pattern = new RegExp([...names.keys()].map((n) => n.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")).join("|"), "g");
  return text.replace(pattern, (old) => names.get(old) ?? old);
}

/** From now on the server is the new build: old assets 404, new ones serve, documents are the new index.html. */
async function redeploy(page: Page): Promise<{ oldSavedRequests: () => number; newSavedRequests: () => number }> {
  const names = renames();
  const byNewName = new Map([...names].map(([old, renamed]) => [renamed, old]));
  const index = toNewBuild(readFileSync(path.join(DIST, "index.html"), "utf8"), names);
  let oldSaved = 0;
  let newSaved = 0;

  await page.route(
    (url) => url.pathname.startsWith("/assets/") && /\.(js|css)$/.test(url.pathname),
    (route) => {
      const file = path.basename(new URL(route.request().url()).pathname);
      const original = byNewName.get(file);
      if (!original) {
        if (/^Saved-/.test(file)) oldSaved += 1;
        return route.fulfill({ status: 404, body: "" });
      }
      if (/^Saved-/.test(original)) newSaved += 1;
      const body = toNewBuild(readFileSync(path.join(DIST, "assets", original), "utf8"), names);
      return route.fulfill({
        status: 200,
        contentType: file.endsWith(".css") ? "text/css" : "text/javascript",
        body,
      });
    }
  );
  await page.route(
    (url) => !url.pathname.startsWith("/api/") && !url.pathname.startsWith("/assets/") && !path.extname(url.pathname),
    (route) =>
      route.request().resourceType() === "document"
        ? route.fulfill({ status: 200, contentType: "text/html", body: index })
        : route.fallback()
  );
  return { oldSavedRequests: () => oldSaved, newSavedRequests: () => newSaved };
}

test("an old tab navigating after a redeploy reloads cleanly", async ({ page }) => {
  await stubEventSource(page);
  await page.route(
    (url) => url.pathname.startsWith("/api/"),
    (route) => route.fulfill({ status: 200, contentType: "application/json", body: "{}" })
  );
  await mockJson(page, "**/api/auth/me", SESSION_FOUNDER);
  await mockJson(page, "**/api/sync/status", syncStatus());
  await mockJson(page, "**/api/saved", {
    managedListUrl: null, sort: "tonight", eveningBudgetMinutes: 150, expectedAbsoluteError: null, unscoredRemaining: 0, items: [],
  });

  await page.goto("/home");
  await expect(page.getByRole("link", { name: "Reel home" })).toBeVisible();
  // The old tab has not loaded Saved yet: its chunk is still to be fetched, by its old hash.
  const entry = await page.evaluate(() => (document.querySelector('script[type="module"][src*="/assets/index-"]') as HTMLScriptElement | null)?.src ?? "");
  expect(entry).not.toContain(NEW);

  const server = await redeploy(page);

  let loads = 0;
  page.on("load", () => (loads += 1));
  await page.getByRole("link", { name: "Saved" }).first().click();

  await expect(page.getByRole("heading", { name: "Saved" })).toBeVisible();
  await expect(page).toHaveURL(/\/saved$/);
  // One reload, onto the new build: the old Saved chunk was asked for once and missing, the new one served.
  expect(loads).toBe(1);
  expect(server.oldSavedRequests()).toBe(1);
  expect(server.newSavedRequests()).toBe(1);
  expect(
    await page.evaluate(() => (document.querySelector('script[type="module"][src*="/assets/index-"]') as HTMLScriptElement | null)?.src ?? "")
  ).toContain(NEW);
  await expect(page.getByTestId("route-error-boundary")).toHaveCount(0);
  // Settled: no second reload follows.
  await page.waitForLoadState("networkidle");
  expect(loads).toBe(1);
});
