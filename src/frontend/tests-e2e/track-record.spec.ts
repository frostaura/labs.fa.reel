import { test, expect, type Page } from "@playwright/test";
import { SESSION_FOUNDER, mockJson, stubEventSource } from "./helpers";

/**
 * The track record page — the founder's decision "honest from day one", rendered.
 *
 * These are page-level tests in Playwright rather than component tests in vitest because the repo
 * carries no @testing-library and this wave adds no dependency to get one. The pure copy helpers
 * are covered in `src/lib/trackRecordCopy.test.ts`; what is asserted here is the thing only a
 * browser can show — that the page renders the unflattering number, says which evidence base it
 * came from, and degrades to a useful empty state rather than a wall of zeros.
 */

const ACCURACY_EXPOSURES = {
  window: "90d",
  from: "2026-06-12T00:00:00Z",
  to: "2026-09-10T00:00:00Z",
  source: "exposures",
  headline: {
    withinOnePoint: 0.26,
    n: 116,
    meanAbsoluteError: 1.94,
    bias: 0.37,
    correlation: -0.004,
    meanPredicted: 6.81,
    meanActual: 5.78,
  },
  calibration: [
    { bucket: 5, low: 5, high: 6, n: 12, meanPredicted: 5.6, meanActual: 6.4 },
    { bucket: 6, low: 6, high: 7, n: 61, meanPredicted: 6.7, meanActual: 5.9 },
    { bucket: 7, low: 7, high: 8, n: 31, meanPredicted: 7.4, meanActual: 5.2 },
    { bucket: 8, low: 8, high: 9, n: 12, meanPredicted: 8.6, meanActual: 4.4 },
    { bucket: 9, low: 9, high: 10, n: 0, meanPredicted: null, meanActual: null },
  ],
  moments: [
    {
      titleId: "00000000-0000-0000-0000-0000000000m1",
      name: "Succubus",
      year: 2024,
      posterPath: null,
      mediaType: "Movie",
      tmdbId: 9001,
      predicted: 9.78,
      actual: 3,
      surface: "FeedHero",
      shownAt: "2026-08-29T20:00:00Z",
      ratedAt: "2026-09-09T20:00:00Z",
      daysToOutcome: 11,
    },
    {
      titleId: "00000000-0000-0000-0000-0000000000m2",
      name: "Quiet Harbor",
      year: 2024,
      posterPath: null,
      mediaType: "Movie",
      tmdbId: 9002,
      predicted: 7.1,
      actual: 7,
      surface: "Digest",
      shownAt: "2026-09-01T20:00:00Z",
      ratedAt: "2026-09-08T20:00:00Z",
      daysToOutcome: 7,
    },
  ],
  conversion: {
    shown: 470,
    saved: 104,
    watched: 73,
    rated: 73,
    shownToSaved: 0.2213,
    savedToWatched: 0.7019,
    shownToRated: 0.1553,
    medianDaysToWatch: 11,
  },
  counterfactual: [
    { window: "last365Days", days: 365, viaReelCount: 73, viaReelMeanRating: 5.78, otherCount: 900, otherMeanRating: 6.74, delta: -0.96 },
    { window: "allTime", days: null, viaReelCount: 73, viaReelMeanRating: 5.78, otherCount: 1971, otherMeanRating: 6.74, delta: -0.96 },
  ],
  learnedThisWeek: {
    trainedAt: "2026-09-05T03:00:00Z",
    newRatings: 14,
    calibrationBias: 0.37,
    calibrationMae: 1.94,
    spearman: 0.18,
    picksChanged: 3,
  },
};

/** GET /api/metrics/cross-reference — where the cross-reference block lives now (WS-5 task 11). */
const XREF_EMPTY = {
  window: "90d",
  population: { name: "Titles served under a cross-reference standing that you have since rated", selectionNote: "It corroborates the offline backtest; it never gates anything." },
  crossReference: { online: [], backtests: [], minimumOutcomes: 20 },
};

const POPULATIONS = {
  headline: {
    name: "Titles Reel showed you that you went on to rate",
    selectionNote: "You picked which of the shown titles to watch, so these lean towards the ones that looked best to you.",
  },
  counterfactual: {
    name: "Your ratings of titles that reached you through Reel, beside everything else you rated in the same weeks",
    selectionNote: "Both groups are titles you chose to watch.",
  },
  conversion: {
    name: "Cards Reel logged as shown to you",
    selectionNote: "Counts only what the exposure log recorded since it began.",
  },
  crossReference: XREF_EMPTY.population,
};

async function mockXref(page: Page, body: unknown = XREF_EMPTY) {
  await mockJson(page, "**/api/metrics/cross-reference", body);
}

const ACCURACY_EMPTY = {
  ...ACCURACY_EXPOSURES,
  source: "none",
  headline: { withinOnePoint: 0, n: 0, meanAbsoluteError: 0, bias: 0, correlation: null, meanPredicted: 0, meanActual: 0 },
  calibration: ACCURACY_EXPOSURES.calibration.map((b) => ({ ...b, n: 0, meanPredicted: null, meanActual: null })),
  moments: [],
  conversion: { shown: 0, saved: 0, watched: 0, rated: 0, shownToSaved: null, savedToWatched: null, shownToRated: null, medianDaysToWatch: null },
  counterfactual: [],
  learnedThisWeek: null,
};

async function boot(page: Page, accuracy: unknown) {
  await stubEventSource(page);
  await mockJson(page, "**/api/auth/me", SESSION_FOUNDER);
  await mockJson(page, "**/api/sync/status", {
    pipelineStage: "FeedReady", connectionStatus: "Active", lastDeltaSyncAt: null,
    lastFullReconcileAt: null, activeJob: null, recentJobs: [], outboxPending: 0, outboxDeadLetters: 0,
  });
  await mockJson(page, "**/api/accuracy", accuracy);
  await mockXref(page);
  await page.goto("/track-record");
  await expect(page.getByTestId("track-record")).toBeVisible();
}

test.describe("track record", () => {
  test("leads with the unflattering headline and says the evidence is exposures", async ({ page }) => {
    await boot(page, ACCURACY_EXPOSURES);

    await expect(page.getByTestId("headline-sentence")).toHaveText("Within one point on 26% of what you rate");
    await expect(page.getByTestId("source-note")).toContainText("actually put in front of you");

    const headline = page.getByTestId("headline");
    // The average miss, the direction of the miss, and the fact that the ordering carries no
    // signal — all three stated, none softened.
    await expect(headline).toContainText("1.94");
    await expect(headline).toContainText("+0.37");
    await expect(headline).toContainText("Reel promises more than you give");
    await expect(headline).toContainText("no relationship worth the name");
    await expect(headline).toContainText("116");
  });

  test("says out loud when the numbers come from the weaker retro evidence", async ({ page }) => {
    // Day one: the exposure log is empty, so the page falls back to scores that predate a rating —
    // which proves Reel committed to a number, not that anyone saw it. Presenting that as the
    // stronger evidence is the one dishonesty this page exists against.
    await boot(page, { ...ACCURACY_EXPOSURES, source: "retro" });

    await expect(page.getByTestId("source-note")).toContainText("not that you saw it");
    await expect(page.getByTestId("source-note")).not.toContainText("put in front of you");
  });

  test("draws the calibration chart from the bands that have data", async ({ page }) => {
    await boot(page, ACCURACY_EXPOSURES);

    const chart = page.getByTestId("calibration");
    await expect(chart).toBeVisible();
    await expect(chart.getByTestId("calibration-empty")).toHaveCount(0);
    // Both series are named, so the gap between promise and outcome is legible without a legend key.
    await expect(chart).toContainText("Reel said");
    await expect(chart).toContainText("you said");
  });

  test("shows the recent calls with the predicted-versus-actual pair on each", async ({ page }) => {
    await boot(page, ACCURACY_EXPOSURES);

    const moments = page.getByTestId("moment-card");
    await expect(moments).toHaveCount(2);
    await expect(moments.first()).toContainText("Succubus");
    await expect(moments.first()).toContainText("9.8 → 3");
    await expect(moments.first()).toContainText("tonight's picks");
  });

  test("shows the funnel and the counterfactual, including a negative gap", async ({ page }) => {
    await boot(page, ACCURACY_EXPOSURES);

    const conversion = page.getByTestId("conversion");
    await expect(conversion).toContainText("70.2%");
    await expect(conversion).toContainText("11.0");

    // The number that says whether Reel helped at all. It is negative on this data and renders as
    // negative — a page that hid this would be worthless.
    const counterfactual = page.getByTestId("counterfactual");
    await expect(counterfactual).toContainText("-0.96");
    await expect(counterfactual).toContainText("5.78");
    await expect(counterfactual).toContainText("6.74");
  });

  test("the retrain line claims nothing the server no longer measures (R1-56)", async ({ page }) => {
    await boot(page, ACCURACY_EXPOSURES);

    await expect(page.getByTestId("learned-sentence")).toHaveText("Learned from 14 new ratings. 3 picks changed.");
    await expect(page.getByTestId("learned")).not.toContainText("closer");
  });

  test("offers only the windows that differ, and says so when there is just one (G-9)", async ({ page }) => {
    await boot(page, {
      ...ACCURACY_EXPOSURES,
      windows: [
        { key: "90d", pairs: 116, sameAs: null },
        { key: "365d", pairs: 116, sameAs: "90d" },
        { key: "all", pairs: 116, sameAs: "90d" },
      ],
    });
    await expect(page.getByTestId("window-switch")).toHaveCount(0);
    await expect(page.getByTestId("single-window-note")).toHaveText(
      "All 116 scored calls fall in the last 90 days, so a longer window would show the same."
    );
  });

  test("two windows that differ get a two-button switch", async ({ page }) => {
    await boot(page, {
      ...ACCURACY_EXPOSURES,
      windows: [
        { key: "90d", pairs: 40, sameAs: null },
        { key: "365d", pairs: 116, sameAs: null },
        { key: "all", pairs: 116, sameAs: "365d" },
      ],
    });
    const buttons = page.getByTestId("window-switch").getByRole("button");
    await expect(buttons).toHaveText(["90 days", "a year"]);
  });

  test("each block names who its numbers are about and how they were selected (G-8)", async ({ page }) => {
    await boot(page, { ...ACCURACY_EXPOSURES, populations: POPULATIONS });
    await expect(page.getByTestId("headline-population")).toContainText("Titles Reel showed you that you went on to rate");
    await expect(page.getByTestId("headline-population")).toContainText("lean towards the ones that looked best to you");
    await expect(page.getByTestId("conversion-population")).toContainText("Cards Reel logged as shown to you");
    await expect(page.getByTestId("counterfactual-population")).toContainText("Both groups are titles you chose to watch");
  });

  test("a scored page still links to Rate (G-19)", async ({ page }) => {
    await boot(page, ACCURACY_EXPOSURES);
    await expect(page.getByTestId("track-record-rate")).toHaveAttribute("href", "/rate");
  });

  test("the cross-reference block reads in words; its statistics live in the lab (G-10)", async ({ page }) => {
    await stubEventSource(page);
    await mockJson(page, "**/api/auth/me", SESSION_FOUNDER);
    await mockJson(page, "**/api/sync/status", {
      pipelineStage: "FeedReady", connectionStatus: "Active", lastDeltaSyncAt: null,
      lastFullReconcileAt: null, activeJob: null, recentJobs: [], outboxPending: 0, outboxDeadLetters: 0,
    });
    await mockJson(page, "**/api/accuracy", ACCURACY_EXPOSURES);
    await mockXref(page, {
      ...XREF_EMPTY,
      crossReference: {
        minimumOutcomes: 20,
        online: [{ verdict: "ProvisionalKeep", n: 31, meanLaterRating: 7.1, lovedRate: 0.35, dislikedRate: 0.1, answerable: true }],
        backtests: [
          {
            promptVersion: 2, evaluatedAt: "2026-09-11T02:14:00Z", judged: 1194, auc: 0.655,
            meanLaterRatingKeep: 6.83, meanLaterRatingOut: 6.16, keepLovedRate: 0.332, keepDislikedRate: 0.081,
            falseRuleOutRate: 0.162, falseRuleOutLow: 0.123, falseRuleOutHigh: 0.21, trueRuleOutRate: 0.228,
            unsureRate: 0.46, gating: "demote-only", caveat: "", current: true,
          },
        ],
      },
    });
    await page.goto("/track-record");

    const panel = page.getByTestId("cross-reference");
    await expect(page.getByTestId("cross-reference-backtest")).toContainText(
      "titles it kept went on to average 6.83; titles it ruled out averaged 6.16"
    );
    await expect(page.getByTestId("cross-reference-population")).toContainText("never gates anything");
    await expect(page.getByTestId("cross-reference-online-row")).toContainText("31 rated since");
    // The engineering readouts are gone from the consumer page (KILL-11).
    for (const jargon of ["AUC", "Separation", "prompt v", "95% confidence", "version running now"]) {
      await expect(panel).not.toContainText(jargon);
    }
  });

  test("a new account gets a useful empty state, not a wall of zeros", async ({ page }) => {
    await boot(page, ACCURACY_EMPTY);

    await expect(page.getByTestId("headline-sentence")).toHaveText("Nothing to score yet.");
    await expect(page.getByTestId("source-note")).toContainText("Rate a few titles");
    await expect(page.getByTestId("calibration-empty")).toBeVisible();
    await expect(page.getByTestId("moments-empty")).toBeVisible();
    // No stat tiles claiming a 0% accuracy the data cannot support.
    await expect(page.getByTestId("headline")).not.toContainText("0%");
    await expect(page.getByTestId("learned")).toHaveCount(0);
    // And it says where the ratings that fill it come from.
    await expect(page.getByTestId("track-record-empty-rate")).toHaveAttribute("href", "/rate");
    await expect(page.getByTestId("track-record-empty-rate")).toHaveText("Rate what you've watched");
  });

  test("a failed read is an error with a Retry, not a shimmer that never ends", async ({ page }) => {
    await stubEventSource(page);
    await mockJson(page, "**/api/auth/me", SESSION_FOUNDER);
    await mockJson(page, "**/api/sync/status", {
      pipelineStage: "FeedReady", connectionStatus: "Active", lastDeltaSyncAt: null,
      lastFullReconcileAt: null, activeJob: null, recentJobs: [], outboxPending: 0, outboxDeadLetters: 0,
    });
    await mockXref(page);
    let fail = true;
    await page.route(
      (url) => url.pathname === "/api/accuracy",
      (route) =>
        fail
          ? route.fulfill({ status: 500, contentType: "application/json", body: "{}" })
          : route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify(ACCURACY_EXPOSURES) })
    );

    await page.goto("/track-record");
    await expect(page.getByTestId("track-record-error")).toBeVisible();
    await expect(page.getByTestId("track-record-loading")).toHaveCount(0);
    // The window switch survives the failure, so another window is still one tap away.
    await expect(page.getByRole("button", { name: "all time" })).toBeVisible();

    fail = false;
    await page.getByTestId("track-record-error-retry").click();
    await expect(page.getByTestId("headline-sentence")).toHaveText("Within one point on 26% of what you rate");
  });

  test("a window that fails never shows the previous window's numbers under its button", async ({ page }) => {
    await stubEventSource(page);
    await mockJson(page, "**/api/auth/me", SESSION_FOUNDER);
    await mockJson(page, "**/api/sync/status", {
      pipelineStage: "FeedReady", connectionStatus: "Active", lastDeltaSyncAt: null,
      lastFullReconcileAt: null, activeJob: null, recentJobs: [], outboxPending: 0, outboxDeadLetters: 0,
    });
    await mockXref(page);
    await page.route(
      (url) => url.pathname === "/api/accuracy",
      (route) =>
        new URL(route.request().url()).searchParams.get("window") === "all"
          ? route.fulfill({ status: 500, contentType: "application/json", body: "{}" })
          : route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify(ACCURACY_EXPOSURES) })
    );

    await page.goto("/track-record");
    await expect(page.getByTestId("headline-sentence")).toBeVisible();
    await page.getByRole("button", { name: "all time" }).click();
    await expect(page.getByTestId("track-record-error")).toBeVisible();
    await expect(page.getByTestId("headline-sentence")).toHaveCount(0);

    // Stepping back to the window that loaded shows it again.
    await page.getByRole("button", { name: "90 days" }).click();
    await expect(page.getByTestId("headline-sentence")).toHaveText("Within one point on 26% of what you rate");
  });

  test("switching the window re-asks the server for that window", async ({ page }) => {
    const windows: string[] = [];
    await stubEventSource(page);
    await mockJson(page, "**/api/auth/me", SESSION_FOUNDER);
    await mockJson(page, "**/api/sync/status", {
      pipelineStage: "FeedReady", connectionStatus: "Active", lastDeltaSyncAt: null,
      lastFullReconcileAt: null, activeJob: null, recentJobs: [], outboxPending: 0, outboxDeadLetters: 0,
    });
    await mockXref(page);
    await page.route(
      (url) => url.pathname === "/api/accuracy",
      (route) => {
        windows.push(new URL(route.request().url()).searchParams.get("window") ?? "");
        return route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify(ACCURACY_EXPOSURES) });
      }
    );

    await page.goto("/track-record");
    await expect(page.getByTestId("track-record")).toBeVisible();
    await page.getByRole("button", { name: "all time" }).click();
    await expect(page.getByTestId("track-record")).toBeVisible();

    // The label and the data must agree: a chart headed "all time" drawn from 90 days of rows is
    // a mislabelled chart, which is a quieter version of the same dishonesty.
    await expect.poll(() => windows).toContain("all");
    expect(windows[0]).toBe("90d");
  });

  test("the avatar menu offers the track record to every tier", async ({ page }) => {
    await boot(page, ACCURACY_EXPOSURES);

    await page.goto("/home");
    await page.getByRole("button", { name: /test founder/i }).click();
    await page.getByRole("menuitem", { name: /track record/i }).click();
    await expect(page.getByTestId("track-record")).toBeVisible();
  });
});
