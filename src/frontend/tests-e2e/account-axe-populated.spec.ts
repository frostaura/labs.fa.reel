import { test, expect, type Page } from "@playwright/test";
import AxeBuilder from "@axe-core/playwright";
import { SESSION_FOUNDER, mockJson, stubEventSource, syncStatus } from "./helpers";
import { LAB_CROSS_REFERENCE, LAB_MODEL_METRICS } from "./labPopulated.fixture";

/**
 * Full axe rules, 0 violations, on /dna, /lab and /rate WITH DATA (PH-02, PH-09), in Chromium and
 * WebKit at 390×844 — where the review found them.
 *
 * The route-wide a11y run mocks these pages near-empty, so it never drew what failed on the eval
 * snapshot: the pocket summaries and the lab's stat-card captions in frost-dim on a nested glass
 * card (#5c8ab4 on #182938, 4.06:1 at 11 px — serious colour-contrast ×12 on /dna, ×7 on /lab), the
 * iteration ledger's sideways scroller with nothing focusable in it (scrollable-region-focusable),
 * and a poster on every /rate card announcing the same title as the text under it
 * (image-redundant-alt ×30). Each fixture here is populated enough to draw all of those.
 */

const PNG_1PX = Buffer.from(
  "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNkYAAAAAYAAjCB0C8AAAAASUVORK5CYII=",
  "base64",
);

const RUNNING_INTERLEAVE = {
  computedAt: "2026-10-27T06:00:00Z",
  enabled: true,
  policyA: "CalibratedModel",
  policyB: "PopularityBaseline",
  tau: 0.15,
  alpha: 0.05,
  slates: 148,
  decidedSlates: 41,
  ties: 107,
  winsA: 24,
  winsB: 17,
  alwaysValidP: 0.412,
  significant: false,
  preferenceRateA: 0.5854,
  ciLow: 0.3902,
  ciHigh: 0.7805,
  signTestP: 0.3497,
  firstSlateAt: "2026-07-14T18:00:00Z",
  lastSlateAt: "2026-10-27T05:00:00Z",
  decidedSlatesPerDay: 0.71,
  slatesToDecisionAt65: 103,
  estimatedDaysToDecision: 87.3,
  standings: [
    { policyId: "CalibratedModel", description: "Today's served order.", team: "A", slates: 148, cards: 1032, credits: 61, slateWins: 24 },
    { policyId: "PopularityBaseline", description: "Crowd popularity in the eligible pool.", team: "B", slates: 148, cards: 1027, credits: 49, slateWins: 17 },
  ],
};

const DNA = {
  ratingsCount: 2044,
  titlesWatched: 3353,
  hoursWatched: 4210,
  showCompletionRate: 0.62,
  contrarianScore: -0.2,
  userMean: 6.8,
  topGenres: [
    { genre: "drama", affinity: 7.2, count: 980 },
    { genre: "thriller", affinity: 7.0, count: 512 },
    { genre: "comedy", affinity: 6.5, count: 455 },
  ],
  eras: [
    { decade: 2000, affinity: 6.9, count: 520 },
    { decade: 2010, affinity: 6.7, count: 801 },
  ],
  creators: [{ name: "A Director", profilePath: null, affinity: 8.6 }],
  topActors: [],
  histogram: [{ rating: 7, count: 400 }, { rating: 8, count: 300 }],
  drift: [],
  clusters: Array.from({ length: 4 }, (_, i) => ({
    ordinal: i,
    share: 0.3 - i * 0.05,
    titleCount: 180 - i * 20,
    exemplars: Array.from({ length: 3 }, (__, j) => ({ name: `Pocket ${i + 1} title ${j + 1}`, year: 2010 + j, posterPath: `/p${i}${j}.jpg` })),
  })),
};

async function bootShell(page: Page) {
  await stubEventSource(page);
  await mockJson(page, "**/api/auth/me", SESSION_FOUNDER);
  await mockJson(page, "**/api/sync/status", syncStatus());
  // Real posters, so the cards draw <img> elements rather than the initials fallback.
  await page.route("https://image.tmdb.org/**", (route) => route.fulfill({ status: 200, contentType: "image/png", body: PNG_1PX }));
}

async function fullAxe(page: Page) {
  const result = await new AxeBuilder({ page }).analyze();
  return result.violations.map((v) => ({ id: v.id, impact: v.impact, nodes: v.nodes.map((n) => n.target.join(" ")) }));
}

test.describe("full axe rules on populated account pages (PH-02, PH-09)", () => {
  test.beforeEach(async ({ page }) => {
    await page.setViewportSize({ width: 390, height: 844 });
  });

  test("/dna with taste pockets", async ({ page }) => {
    await bootShell(page);
    await mockJson(page, "**/api/taste", DNA);
    await mockJson(page, "**/api/taste/narrative", {
      text: "You rate slow, deliberate thrillers above your own average.", isEdited: false, version: 2, ratingsCount: 2044,
      generatedAt: "2026-10-20T08:00:00Z", isStale: false, regenerating: false, writerAvailable: true,
    });
    await mockJson(page, "**/api/taste/dislikes", {
      total: 0, offset: 0, limit: 20, hasMore: false, items: [],
      profile: { windowDays: 180, activeCount: 0, genres: [], tones: [] },
    });
    await page.goto("/dna");
    await expect(page.getByTestId("pocket-summary")).toHaveCount(4);
    expect(await fullAxe(page)).toEqual([]);
  });

  test("/lab with a trained model, a running interleave and cross-reference outcomes", async ({ page }) => {
    await bootShell(page);
    await mockJson(page, "**/api/metrics/model", LAB_MODEL_METRICS);
    await mockJson(page, "**/api/metrics/interleave", RUNNING_INTERLEAVE);
    await mockJson(page, "**/api/metrics/cross-reference", LAB_CROSS_REFERENCE);
    await page.goto("/lab");
    await expect(page.getByTestId("interleave-panel")).toBeVisible();
    await expect(page.getByTestId("retro-loop")).toBeVisible();
    await expect(page.getByTestId("lab-xref-backtest").first()).toBeVisible();
    // The ledger's sideways scroller is a named, focusable region, so a keyboard can scroll it.
    const ledger = page.getByTestId("iteration-ledger-scroll");
    await expect(ledger).toHaveAttribute("role", "region");
    await expect(ledger).toHaveAttribute("aria-label", "Iteration ledger runs");
    await ledger.focus();
    await expect(ledger).toBeFocused();
    expect(await fullAxe(page)).toEqual([]);
  });

  test("/rate with posters on every card", async ({ page }) => {
    await bootShell(page);
    await mockJson(
      page,
      "**/api/titles/rate-suggestions",
      Array.from({ length: 12 }, (_, i) => ({
        titleId: `00000000-0000-0000-0000-0000000002${String(i).padStart(2, "0")}`,
        mediaType: "Movie",
        tmdbId: 4000 + i,
        name: `Watched title ${i + 1}`,
        year: 2020,
        posterPath: `/r${i}.jpg`,
        crowdRating: 7.2,
        crowdVotes: 4000,
        source: i < 6 ? "watched" : "popular",
      })),
    );
    await page.goto("/rate");
    await expect(page.getByTestId("rate-card")).toHaveCount(12);
    await expect(page.locator('[data-testid="rate-card"] img').first()).toBeAttached();
    // Each card is named once, by its title text — the poster says nothing to a screen reader.
    await expect(page.getByTestId("rate-card").first()).toHaveAccessibleName("Watched title 1");
    expect(await fullAxe(page)).toEqual([]);
  });
});
