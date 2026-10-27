import { test, expect, type Page } from "@playwright/test";
import { SESSION_FOUNDER, mockJson, stubEventSource, syncStatus } from "./helpers";
import { mockFilterChips } from "./filterChips.fixture";

/**
 * The phone-target probe for WS-10B's routes (P1 acceptance "0 interactive elements under 44 px on
 * phone routes"; IREV3S-08 measured Settings at 56 and Taste DNA at 48): at 390×844 on a touch
 * device, no interactive element inside the page's own content is smaller than 44 × 44.
 *
 * The same rule as WS-10A's probe (phone-targets.spec.ts): scoped to `main`, because the header pill
 * and the tab bar are the shell's; a link set inside a paragraph of running text is the one
 * exception WCAG 2.5.8 makes, and the one exception here.
 */

const PHONE = { viewport: { width: 390, height: 844 }, hasTouch: true, isMobile: true };

async function smallTargets(page: Page, rootSelector = "main"): Promise<string[]> {
  return page.evaluate((rootSelector) => {
    const main = document.querySelector(rootSelector);
    if (!main) return [`<no ${rootSelector}>`];
    const selector = 'a[href], button, input, select, textarea, [role="button"], [tabindex]:not([tabindex="-1"])';
    const out: string[] = [];
    for (const el of Array.from(main.querySelectorAll<HTMLElement>(selector))) {
      const r = el.getBoundingClientRect();
      const style = getComputedStyle(el);
      if (r.width === 0 || r.height === 0 || style.visibility === "hidden" || style.display === "none") continue;
      if (el.closest('[aria-hidden="true"]')) continue;
      if (el.tagName === "A" && el.parentElement?.tagName === "P" && style.display === "inline") continue;
      if (r.width >= 44 && r.height >= 44) continue;
      const label = el.getAttribute("aria-label") ?? el.getAttribute("data-testid") ?? el.textContent?.trim().slice(0, 30) ?? el.tagName;
      out.push(`${el.tagName.toLowerCase()} "${label}" ${Math.round(r.width)}×${Math.round(r.height)}`);
    }
    return out;
  }, rootSelector);
}

/** Every /api call answered: a 404 for anything a test does not mock, then the shell's own calls. */
async function boot(page: Page, session: Record<string, unknown> = SESSION_FOUNDER) {
  await stubEventSource(page);
  await page.route(
    (u) => u.pathname.startsWith("/api/"),
    (route) => route.fulfill({ status: 404, contentType: "application/json", body: "{}" })
  );
  await mockJson(page, "**/api/auth/me", session);
  await mockJson(page, "**/api/sync/status", syncStatus());
}

const FILTERS = {
  excludeGenres: ["horror"], includeGenres: ["drama"], excludeKeywords: ["gore"], maturityCeiling: null,
  minPredictedRating: 6, languages: ["en"], minCrowdRating: 6, minYear: 1990, maxYear: null,
};

const ACCURACY = {
  window: "90d", from: "2026-06-12T00:00:00Z", to: "2026-09-10T00:00:00Z", source: "exposures",
  windows: [
    { key: "90d", pairs: 116, sameAs: null },
    { key: "365d", pairs: 240, sameAs: null },
    { key: "all", pairs: 300, sameAs: null },
  ],
  headline: { withinOnePoint: 0.26, n: 116, meanAbsoluteError: 1.94, bias: 0.37, correlation: -0.004, meanPredicted: 6.81, meanActual: 5.78 },
  calibration: [{ bucket: 6, low: 6, high: 7, n: 61, meanPredicted: 6.7, meanActual: 5.9 }],
  moments: [
    {
      titleId: "00000000-0000-0000-0000-000000000301", mediaType: "Movie", tmdbId: 301, name: "Quiet Harbor", posterPath: null,
      predicted: 8.1, actual: 6, ratedAt: "2026-09-01T00:00:00Z", surface: "feed", daysToOutcome: 4,
    },
  ],
  conversion: { shown: 40, saved: 6, watched: 3, rated: 3, shownToSaved: 0.15, savedToWatched: 0.5, shownToRated: 0.075, medianDaysToWatch: 4 },
  counterfactual: [],
  learnedThisWeek: null,
};

const TASTE = {
  ratingsCount: 2044, titlesWatched: 3353, hoursWatched: 4210, showCompletionRate: 0.62, contrarianScore: -0.2, userMean: 6.8,
  topGenres: [
    { genre: "drama", affinity: 7.2, count: 980 },
    { genre: "comedy", affinity: 6.5, count: 455 },
  ],
  eras: [{ decade: 2000, affinity: 6.9, count: 520 }],
  creators: [], topActors: [], histogram: [{ rating: 8, count: 300 }], drift: [], clusters: [],
};

test.describe("phone targets on WS-10B routes", () => {
  test("Settings has no interactive element under 44 px", async ({ browser }) => {
    const context = await browser.newContext(PHONE);
    const page = await context.newPage();
    await boot(page);
    await mockJson(page, "**/api/billing/status", { tier: "Founder", configured: false });
    await mockJson(page, "**/api/preferences/tags", [{ id: "t1", text: "slow-burn mysteries" }]);
    await mockJson(page, "**/api/settings/filters/cost", { total: 5000, eligible: 400, byRule: [], suggestions: [] });
    await mockJson(page, "**/api/settings", { region: "US", onboarded: true, email: null, eveningBudgetMinutes: 150 });
    await mockJson(page, "**/api/settings/filters", FILTERS);
    await mockFilterChips(page);
    await page.goto("/settings");
    await expect(page.getByTestId("genre-chips")).toBeVisible();
    await expect(page.getByTestId("genre-chips-loading")).toHaveCount(0);
    expect(await smallTargets(page)).toEqual([]);
    await context.close();
  });

  test("Taste DNA has no interactive element under 44 px", async ({ browser }) => {
    const context = await browser.newContext(PHONE);
    const page = await context.newPage();
    await boot(page);
    await mockJson(page, "**/api/taste", TASTE);
    await mockJson(page, "**/api/taste/dislikes", {
      total: 0, offset: 0, limit: 20, hasMore: false, items: [],
      profile: { windowDays: 180, activeCount: 0, genres: [], tones: [] },
    });
    await mockJson(page, "**/api/taste/narrative", {
      text: "You like slow, careful dramas.", isEdited: false, version: 1, ratingsCount: 2044,
      generatedAt: "2026-10-20T00:00:00Z", isStale: false, regenerating: false, writerAvailable: true,
    });
    await page.goto("/dna");
    await expect(page.getByTestId("genre-fingerprint")).toBeVisible();
    expect(await smallTargets(page)).toEqual([]);
    await context.close();
  });

  test("Track record has no interactive element under 44 px", async ({ browser }) => {
    const context = await browser.newContext(PHONE);
    const page = await context.newPage();
    await boot(page);
    await mockJson(page, "**/api/accuracy", ACCURACY);
    await mockJson(page, "**/api/metrics/cross-reference", {
      window: "90d",
      population: { name: "Titles served under a cross-reference standing that you have since rated", selectionNote: "" },
      crossReference: { online: [], backtests: [], minimumOutcomes: 20 },
    });
    await page.goto("/track-record");
    await expect(page.getByTestId("track-record")).toBeVisible();
    expect(await smallTargets(page)).toEqual([]);
    await context.close();
  });

  test("Rate has no interactive element under 44 px, the value picker included", async ({ browser }) => {
    const context = await browser.newContext(PHONE);
    const page = await context.newPage();
    await boot(page);
    await mockJson(page, "**/api/titles/rate-suggestions", [1, 2, 3].map((n) => ({
      titleId: `00000000-0000-0000-0000-0000000004${n}0`, mediaType: "Movie", tmdbId: 4000 + n, name: `Seen ${n}`,
      year: 2020, posterPath: null, crowdRating: 7.2, crowdVotes: 4000, source: "watched",
    })));
    await page.goto("/rate");
    await expect(page.getByTestId("rate-card").first()).toBeVisible();
    expect(await smallTargets(page)).toEqual([]);

    // The picker is a popover outside `main`: ten values, each a 44 px target, all on screen.
    await page.getByTestId("rate-card").first().click();
    await expect(page.getByTestId("rate-value")).toHaveCount(10);
    expect(await smallTargets(page, "[data-radix-popper-content-wrapper]")).toEqual([]);
    for (const value of await page.getByTestId("rate-value").all()) await expect(value).toBeInViewport();
    await context.close();
  });

  test("Onboarding has no interactive element under 44 px", async ({ browser }) => {
    const context = await browser.newContext(PHONE);
    const page = await context.newPage();
    await boot(page, { ...SESSION_FOUNDER, onboarded: false });
    await page.route("**/api/sync/status", (route) =>
      route.fulfill({
        status: 200,
        contentType: "application/json",
        body: JSON.stringify(
          syncStatus({ pipelineStage: "NotEnoughRatings", degradedReason: "NotEnoughRatings", notEnoughRatings: { usableRatings: 4, required: 10 } })
        ),
      })
    );
    await page.goto("/onboarding");
    await expect(page.getByTestId("needs-ratings-rate")).toBeVisible();
    expect(await smallTargets(page)).toEqual([]);
    await context.close();
  });
});
