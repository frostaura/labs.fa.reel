import { test, expect } from "@playwright/test";
import { SESSION_FOUNDER, mockJson, stubEventSource, syncStatus } from "./helpers";
import { SAVED, mockDna, mockSaved, mockTmdbImages } from "./route-fixtures";

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
      whyThis: "Explores a heist crew coming apart, like Heat.",
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

/**
 * PH-01: the footer carries the TMDB licence line, a hard licence requirement, and on a phone the
 * fixed tab bar sat over it at every scroll position — `main` cleared the bar, the footer after it
 * did not. At the bottom of a page the footer's last pixel must sit at or above the bar's first, in
 * both engines, on a touch phone. Varied over a short page (/saved with one item) and two tall
 * ones, and over two phone heights, so a clearance that only works when `main` is padded enough to
 * push the footer down cannot pass.
 */
test.describe("the footer clears the tab bar at the bottom of the page", () => {
  for (const viewport of [{ width: 390, height: 844 }, { width: 375, height: 667 }]) {
    test.describe(`${viewport.width}×${viewport.height}`, () => {
      test.use({ viewport, hasTouch: true, isMobile: true });

      const cases: { path: string; mock: (page: import("@playwright/test").Page) => Promise<void> }[] = [
        { path: "/saved", mock: mockSaved },
        { path: "/saved?one", mock: async (page) => mockJson(page, "**/api/saved", { ...SAVED, items: SAVED.items.slice(0, 1) }) },
        { path: "/dna", mock: mockDna },
      ];

      for (const { path, mock } of cases) {
        test(`${path}`, async ({ page }) => {
          await stubEventSource(page);
          await mockTmdbImages(page);
          await page.route(
            (url) => url.pathname.startsWith("/api/"),
            (route) => route.fulfill({ status: 204, body: "" })
          );
          await mockJson(page, "**/api/auth/me", SESSION_FOUNDER);
          await mockJson(page, "**/api/sync/status", syncStatus());
          await mockSaved(page);
          await mock(page);
          await page.goto(path);
          await expect(page.getByTestId("page-fallback")).toHaveCount(0);
          await expect(page.locator("main h1").first()).toBeVisible();

          const footer = page.locator("footer");
          await expect(footer).toContainText("This product uses the TMDB API");
          // WebKit's phone emulation reports a visual viewport 10 px shorter than the layout one
          // (visualViewport 380×834 inside 390×844, scale 1), and at max scroll it anchors the fixed
          // bar 10 px above the content's end. That slack is the emulator's, not the page's, so it is
          // measured and allowed for; Chromium's is 0, and every line of the footer below is held to
          // the bar's top edge with no allowance at all.
          await expect
            .poll(async () => {
              await page.evaluate(() => window.scrollTo(0, document.documentElement.scrollHeight));
              return page.evaluate(() => {
                const bar = document.querySelector('[data-testid="mobile-tabbar"]')!.getBoundingClientRect();
                const footer = document.querySelector("footer")!;
                const slack = Math.round(window.innerHeight - (window.visualViewport?.height ?? window.innerHeight));
                const lines = [...footer.children].map((c) => c.getBoundingClientRect().bottom);
                return {
                  footer: Math.round(footer.getBoundingClientRect().bottom - bar.top) - slack <= 0,
                  lines: lines.every((b) => b <= bar.top),
                };
              });
            })
            .toEqual({ footer: true, lines: true });
          // And the licence line itself is the topmost thing at its own last row, not the bar.
          const line = footer.getByText("This product uses the TMDB API");
          const box = await line.boundingBox();
          if (!box) throw new Error("the licence line has no box");
          const hit = await page.evaluate(
            ({ x, y }) => !!document.elementFromPoint(x, y)?.closest("footer"),
            { x: box.x + box.width / 2, y: box.y + box.height - 2 }
          );
          expect(hit, "the licence line's last row is under something").toBe(true);
        });
      }
    });
  }
});
