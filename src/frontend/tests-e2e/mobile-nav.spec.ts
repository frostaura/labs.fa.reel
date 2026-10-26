import { test, expect } from "@playwright/test";
import { SESSION_FOUNDER, mockJson, stubEventSource } from "./helpers";

const FEED = {
  generatedAt: new Date().toISOString(),
  hero: [
    {
      titleId: "00000000-0000-0000-0000-000000000001",
      mediaType: "Movie",
      tmdbId: 1001,
      name: "Hero Pick",
      year: 2026,
      runtimeMinutes: 110,
      posterPath: null,
      backdropPath: null,
      genres: ["drama"],
      predictedRating: 8.4,
      whyThis: "Strong fit with your taste profile.",
    },
  ],
  rows: [],
  lockedRowCount: 0,
};

/**
 * The media feed wants full-width rows, so phones navigate via a fixed bottom tab bar instead
 * of the desktop inline nav — and search, which is desktop-only in the header, must stay
 * reachable through that bar. This locks both in: the search box was once hidden on mobile
 * with no alternative, stranding Ask Reel.
 */
test.describe("mobile navigation", () => {
  test.use({ viewport: { width: 390, height: 844 } });

  async function bootHome(page: import("@playwright/test").Page) {
    await stubEventSource(page);
    // Every call this spec does not answer below is answered empty: no request leaves for a real
    // API (the e2e tripwire fails the run otherwise). Registered first, so the mocks below win.
    await page.route(
      (url) => url.pathname.startsWith("/api/"),
      (route) => route.fulfill({ status: 204, body: "" })
    );
    await mockJson(page, "**/api/auth/me", SESSION_FOUNDER);
    await mockJson(page, "**/api/sync/status", {
      pipelineStage: "FeedReady", connectionStatus: "Active", lastDeltaSyncAt: null,
      lastFullReconcileAt: null, activeJob: null, recentJobs: [], outboxPending: 0, outboxDeadLetters: 0,
    });
    await mockJson(page, "**/api/feed/continue-watching", []);
    await mockJson(page, "**/api/feed", FEED);
    await page.goto("/home");
  }

  test("bottom tab bar replaces the hidden inline nav on phones", async ({ page }) => {
    await bootHome(page);

    await expect(page.getByTestId("mobile-tabbar")).toBeVisible();
    // The desktop inline nav is hidden at this width.
    await expect(page.locator("header nav.hidden")).toBeHidden();
  });

  // Previously known-failing, fixed 2026-08-10 — it was a MOCK problem, not a product bug, and it
  // had TWO stacked causes:
  //  1. Playwright glob patterns match the full URL including the query string, so the original
  //     `**/api/search/typeahead` glob never matched `?q=medieval`. mockJson now matches string
  //     patterns against the pathname, which closes this for every parameterised endpoint.
  //  2. The RegExp retry failed anyway because the local run was reusing whatever listened on
  //     :5173 — the *production* container, whose service worker seizes control shortly after
  //     first paint (skipWaiting + clientsClaim). Late fetches like typeahead were then issued by
  //     the worker, which page.route cannot see; the first-load mocks (auth/feed) fired before the
  //     worker took control, which is why only this one "randomly" missed. The config now blocks
  //     service workers and makes the port overridable (PW_PORT) so a busy :5173 is never reused.
  test("the search tab opens a working search panel (Ask Reel reachable)", async ({ page }) => {
    await bootHome(page);
    // Also answers the sparse-shortlist `?global=1` follow-up (same pathname); the payload has no
    // `global` array so the Beyond-your-catalogue group simply stays hidden here.
    await mockJson(page, "**/api/search/typeahead", {
      titles: [{
        titleId: "00000000-0000-0000-0000-0000000000aa",
        mediaType: "Movie", tmdbId: 2002, name: "Medieval Quest", year: 2019,
        posterPath: null, isFullyWatched: false, predictedRating: 7.5,
      }],
      people: [],
    });

    // Search lives behind a tab, not the header, on mobile.
    const panel = page.getByTestId("mobile-search-panel");
    await expect(panel).toBeHidden();
    await page.getByTestId("mobile-search-tab").click();
    await expect(panel).toBeVisible();

    await panel.getByTestId("search-input").fill("medieval");
    await expect(panel.getByTestId("typeahead-result")).toBeVisible();

    // Ask Reel now CONSUMES the ?q= seed — /search starts the turn and immediately strips the
    // param (a reload three refinements in used to re-fire it), so the URL to assert is /search
    // and the proof of arrival is the seeded question sitting in the transcript. The stream is
    // answered minimally so the turn settles instead of 401ing against the real proxy.
    await page.route("**/api/search/ask", (route) =>
      route.fulfill({
        status: 200,
        contentType: "text/event-stream",
        body: 'event: done\ndata: {"results":[],"reason":null}\n\n',
      })
    );
    await panel.getByTestId("ask-reel-row").click();
    await expect(page).toHaveURL(/\/search(?:$|\?)/);
    await expect(page.getByText("medieval").first()).toBeVisible();
    // Navigating closes the panel.
    await expect(panel).toBeHidden();
  });
});
