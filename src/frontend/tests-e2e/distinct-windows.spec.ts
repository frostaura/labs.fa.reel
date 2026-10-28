import { test, expect, type Page } from "@playwright/test";
import { SESSION_FOUNDER, mockJson, stubEventSource, syncStatus } from "./helpers";
import { LAB_CROSS_REFERENCE, LAB_MODEL_METRICS } from "./labPopulated.fixture";

/**
 * Round-6 copy agreement on /track-record, /lab and /dna.
 *
 * HREV5-DESK-04 (G-9): with every rating inside the last year, the counterfactual printed "last 365
 * days" and "all time" with the same figures on both pages, while the accuracy windows above it were
 * already de-duplicated. A matching wider window now prints once under the widest name.
 *
 * HREV5-DESK-07: /dna captioned a contrarian score of -0.2 "with the crowd" a few hundred pixels
 * below a narrative saying "You rate below the crowd overall". The caption now follows the sign.
 */

const YEAR = {
  window: "last365Days", days: 365, viaReelCount: 120, viaReelMeanRating: 6.07, otherCount: 152,
  otherMeanRating: 6.48, delta: -0.41, viaReelUnmatchedCount: 0, matchDays: 14,
};
const SAME_WINDOWS = [YEAR, { ...YEAR, window: "allTime", days: null }];
const DIFFERENT_WINDOWS = [YEAR, { ...YEAR, window: "allTime", days: null, otherCount: 2022, otherMeanRating: 6.77, delta: -0.71 }];

const ACCURACY = {
  window: "90d",
  from: "2026-07-30T00:00:00Z",
  to: "2026-10-28T00:00:00Z",
  source: "exposures",
  headline: { withinOnePoint: 0.31, n: 65, meanAbsoluteError: 1.6, bias: 0.2, correlation: 0.1, meanPredicted: 6.5, meanActual: 6.3 },
  calibration: [],
  moments: [],
  conversion: { shown: 0, saved: 10, watched: 6, rated: 6, shownToSaved: null, savedToWatched: 0.6, shownToRated: null, medianDaysToWatch: 4 },
  counterfactual: SAME_WINDOWS,
  learnedThisWeek: null,
  crossReference: { online: [], backtests: [], minimumOutcomes: 20 },
};

const XREF_EMPTY = {
  window: "90d",
  population: { name: "Titles served under a cross-reference standing that you have since rated", selectionNote: "It corroborates the offline backtest; it never gates anything." },
  crossReference: { online: [], backtests: [], minimumOutcomes: 20 },
};

const INTERLEAVE_NEVER_RUN = {
  computedAt: "2026-10-27T06:00:00Z", enabled: false, policyA: "CalibratedModel", policyB: "PopularityBaseline",
  tau: 0.15, alpha: 0.05, slates: 0, decidedSlates: 0, ties: 0, winsA: 0, winsB: 0, alwaysValidP: 1,
  significant: false, preferenceRateA: null, ciLow: 0, ciHigh: 1, signTestP: 1, firstSlateAt: null, lastSlateAt: null,
  decidedSlatesPerDay: 0, slatesToDecisionAt65: null, estimatedDaysToDecision: null, standings: [],
};

async function shell(page: Page) {
  await stubEventSource(page);
  await mockJson(page, "**/api/auth/me", SESSION_FOUNDER);
  await mockJson(page, "**/api/sync/status", syncStatus());
}

async function trackRecord(page: Page, counterfactual: unknown) {
  await shell(page);
  await mockJson(page, "**/api/accuracy", { ...ACCURACY, counterfactual });
  await mockJson(page, "**/api/metrics/cross-reference", XREF_EMPTY);
  // The band's own status: 204 until a pass has judged anything.
  await page.route("**/api/feed/cross-reference", (route) => route.fulfill({ status: 204 }));
  await page.goto("/track-record");
  await expect(page.getByTestId("track-record")).toBeVisible();
  return page.getByTestId("counterfactual");
}

async function lab(page: Page, counterfactual: unknown) {
  await shell(page);
  await mockJson(page, "**/api/metrics/model", {
    ...LAB_MODEL_METRICS,
    efficiency: { ...LAB_MODEL_METRICS.efficiency, counterfactual },
  });
  await mockJson(page, "**/api/metrics/interleave", INTERLEAVE_NEVER_RUN);
  await mockJson(page, "**/api/metrics/cross-reference", LAB_CROSS_REFERENCE);
  await page.goto("/lab");
  const block = page.getByTestId("counterfactual");
  await expect(block).toBeVisible();
  return block;
}

test.describe("counterfactual windows print once when they match (HREV5-DESK-04)", () => {
  test("/track-record folds a matching year into all time", async ({ page }) => {
    const block = await trackRecord(page, SAME_WINDOWS);
    await expect(block.getByTestId("counterfactual-row")).toHaveCount(1);
    await expect(block.getByTestId("counterfactual-row")).toContainText("all time (the same ratings as the last 365 days):");
    await expect(block).toContainText("via Reel 6.07 (120) · found yourself 6.48 (152) · -0.41");
  });

  test("/track-record keeps both windows when one number differs", async ({ page }) => {
    const block = await trackRecord(page, DIFFERENT_WINDOWS);
    const rows = block.getByTestId("counterfactual-row");
    await expect(rows).toHaveCount(2);
    await expect(rows.nth(0)).toContainText("last 365 days:");
    await expect(rows.nth(1)).toContainText("all time:");
    await expect(rows.nth(1)).toContainText("found yourself 6.77 (2022)");
  });

  test("/lab folds a matching year into all time", async ({ page }) => {
    const block = await lab(page, SAME_WINDOWS);
    await expect(block.getByTestId("counterfactual-row")).toHaveCount(1);
    await expect(block.getByTestId("counterfactual-row")).toContainText("all time (the same ratings as the last 365 days):");
  });

  test("/lab keeps both windows when one number differs", async ({ page }) => {
    const block = await lab(page, DIFFERENT_WINDOWS);
    await expect(block.getByTestId("counterfactual-row")).toHaveCount(2);
  });
});

const DNA = {
  ratingsCount: 2044, titlesWatched: 3353, hoursWatched: 4210, showCompletionRate: 0.62, contrarianScore: -0.2, userMean: 6.8,
  topGenres: [{ genre: "drama", affinity: 7.2, count: 980 }, { genre: "mystery", affinity: 6.4, count: 300 }],
  eras: [], creators: [], topActors: [], histogram: [{ rating: 7, count: 400 }], drift: [], clusters: [],
};

async function dna(page: Page, contrarianScore: number) {
  await shell(page);
  await mockJson(page, "**/api/taste", { ...DNA, contrarianScore });
  await mockJson(page, "**/api/taste/narrative", {
    text: "You rate below the crowd overall, with the clearest gaps in drama and mystery.", isEdited: false, version: 2,
    ratingsCount: 2044, generatedAt: "2026-10-20T08:00:00Z", isStale: false, regenerating: false, writerAvailable: true,
  });
  await mockJson(page, "**/api/taste/dislikes", {
    total: 0, offset: 0, limit: 20, hasMore: false, items: [],
    profile: { windowDays: 180, activeCount: 0, genres: [], tones: [] },
  });
  await page.goto("/dna");
  return page.getByTestId("contrarian-tile");
}

test.describe("the contrarian tile agrees with the sign it prints (HREV5-DESK-07)", () => {
  const cases: [number, string, string][] = [
    [-0.2, "-0.2", "slightly harsher than the crowd"],
    [0.2, "+0.2", "slightly kinder than the crowd"],
    [-0.04, "0.0", "with the crowd"],
    [-0.6, "-0.6", "harsher than the crowd"],
  ];
  for (const [score, value, caption] of cases) {
    test(`${score} reads ${value}, "${caption}"`, async ({ page }) => {
      const tile = await dna(page, score);
      await expect(tile.getByTestId("contrarian-value")).toHaveText(value);
      await expect(tile.getByTestId("contrarian-caption")).toHaveText(caption);
    });
  }
});
