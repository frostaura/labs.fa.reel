import { test, expect, type Page } from "@playwright/test";
import { SESSION_FOUNDER, mockJson, stubEventSource } from "./helpers";

/**
 * The interleaving panel on /lab.
 *
 * The numbers are mocked, so what is under test is not the statistics — those are simulated in the
 * backend suite — but the three things only a browser can show, and the three the founder is most
 * able to misread. That a decided-row count far below the drafted-row count reads as normal rather
 * than broken. That the cadence is stated in months on the page rather than inferred from a moving
 * p-value. And that an experiment which has never run says so plainly instead of rendering a wall
 * of confident zeros.
 */

const RUNNING = {
  computedAt: "2026-09-10T06:00:00Z",
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
  lastSlateAt: "2026-09-10T05:00:00Z",
  decidedSlatesPerDay: 0.71,
  slatesToDecisionAt65: 103,
  estimatedDaysToDecision: 87.3,
  standings: [
    {
      policyId: "CalibratedModel",
      description: "Calibrated prediction × freshness × preference boost — today's served order.",
      team: "A",
      slates: 148,
      cards: 1032,
      credits: 61,
      slateWins: 24,
    },
    {
      policyId: "PopularityBaseline",
      description: "Crowd popularity within the eligible pool — the eval harness's baseline comparator.",
      team: "B",
      slates: 148,
      cards: 1027,
      credits: 49,
      slateWins: 17,
    },
  ],
};

const NEVER_RUN = {
  ...RUNNING,
  enabled: false,
  slates: 0,
  decidedSlates: 0,
  ties: 0,
  winsA: 0,
  winsB: 0,
  alwaysValidP: 1,
  preferenceRateA: null,
  ciLow: 0,
  ciHigh: 1,
  signTestP: 1,
  firstSlateAt: null,
  lastSlateAt: null,
  decidedSlatesPerDay: null,
  estimatedDaysToDecision: null,
  standings: [],
};

const MODEL_METRICS = {
  gate: {
    threshold: 0.2,
    passed: false,
    latestLift: -0.8,
    ndcgModel: 0.21,
    ndcgBaseline: 0.34,
    ndcgLiftCiLow: -0.31,
    ndcgLiftCiHigh: 0.04,
    ciExcludesZero: false,
    legacyThreshold: 0.2,
    legacyPassed: false,
    iterationsUsed: 12,
    killCriterionAt: 3,
    criterionExceeded: true,
  },
  efficiency: {
    computedAt: "2026-09-10T06:00:00Z",
    conversions: {
      count: 0,
      ratedCount: 0,
      scoredCount: 0,
      accuracy: {
        count: 0, meanPredicted: null, meanActual: null, meanAbsoluteError: null,
        bias: null, withinOnePointRate: null, pearsonCorrelation: null,
      },
      ratingDistribution: [],
      worstMisses: [],
    },
    predictedVsActual: {
      pairs: 0,
      accuracy: {
        count: 0, meanPredicted: null, meanActual: null, meanAbsoluteError: null,
        bias: null, withinOnePointRate: null, pearsonCorrelation: null,
      },
      worstMisses: [],
    },
    counterfactual: [],
  },
  activeArtifact: null,
  runs: [],
};

async function boot(page: Page, interleave: unknown): Promise<void> {
  await stubEventSource(page);
  await mockJson(page, "**/api/auth/me", SESSION_FOUNDER);
  await mockJson(page, "**/api/sync/status", {
    pipelineStage: "FeedReady", connectionStatus: "Active", lastDeltaSyncAt: null,
    lastFullReconcileAt: null, activeJob: null, recentJobs: [], outboxPending: 0, outboxDeadLetters: 0,
  });
  await mockJson(page, "**/api/metrics/model", MODEL_METRICS);
  await mockJson(page, "**/api/metrics/interleave", interleave);
  await page.goto("/lab");
  await expect(page.getByTestId("gate-badge")).toBeVisible();
}

test.describe("interleaving panel", () => {
  test("shows the standings, and reads a mostly-tied ledger as normal", async ({ page }) => {
    await boot(page, RUNNING);

    const panel = page.getByTestId("interleave-panel");
    await expect(panel).toBeVisible();
    await expect(panel).toContainText("CalibratedModel vs PopularityBaseline");
    await expect(page.getByTestId("interleave-state")).toHaveText("running");

    // 148 drafted, only 41 decided — the ties must be explained on the page, because a reader who
    // does not see them accounted for concludes the log is dropping rows.
    await expect(page.getByTestId("interleave-slates")).toHaveText("148");
    await expect(page.getByTestId("interleave-decided")).toHaveText("41");
    await expect(panel).toContainText("107 tied");

    await expect(page.getByTestId("interleave-p")).toHaveText("0.412");
    await expect(page.getByTestId("interleave-rate")).toHaveText("59%");
    await expect(panel).toContainText("range 39–78%");

    const standings = page.getByTestId("interleave-standings");
    await expect(standings).toContainText("CalibratedModel");
    await expect(standings).toContainText("PopularityBaseline");
    await expect(standings).toContainText("team A");
  });

  test("states the cadence in months and that peeking is free", async ({ page }) => {
    await boot(page, RUNNING);

    const cadence = page.getByTestId("interleave-cadence");
    await expect(cadence).toContainText("103 decided rows");
    await expect(cadence).toContainText("two to three months");
    await expect(cadence).toContainText("0.71 decided rows a day");
    await expect(cadence).toContainText("87 days left");
    await expect(cadence).toContainText("as often as you like");
  });

  test("does not claim a result while the evidence is short of one", async ({ page }) => {
    await boot(page, RUNNING);

    const panel = page.getByTestId("interleave-panel");
    await expect(panel).toContainText("needs ≤ 0.05");
    // A leader is named, and named as not yet a verdict — the distinction the whole always-valid
    // apparatus exists to preserve.
    await expect(panel).toContainText("CalibratedModel is ahead, but not yet by enough to call");
    await expect(panel).toContainText("a sanity check only");
  });

  test("says plainly when the experiment has never run", async ({ page }) => {
    await boot(page, NEVER_RUN);

    await expect(page.getByTestId("interleave-state")).toHaveText("not running");
    await expect(page.getByTestId("interleave-empty")).toContainText("INTERLEAVE_ENABLED=true");
    await expect(page.getByTestId("interleave-standings")).toHaveCount(0);
  });
});
