import { test, expect, type Page } from "@playwright/test";
import { SESSION_FOUNDER, mockJson, stubEventSource, syncStatus } from "./helpers";
import { CONDITIONS_SCOPE_COPY } from "../src/lib/conditionsCopy";

/**
 * "Enough ratings, no model yet" on every WS-10B surface (R1-57, CR-6).
 *
 * An account that crosses the training floor sits with no model until the nightly refresh trains
 * one. The server reports that as the trainer's Degraded park + `PipelineFailed` + no counts + no
 * failed Train (SyncStatusContractTests), and — once CR-6 lands — as `AwaitingFirstModel`. Both are
 * a wait, never "Something went wrong": onboarding says when the model comes, Track record stops
 * asking for scored titles that cannot exist yet, Taste DNA says nothing on it waits for the model,
 * and Settings stops printing "Degraded".
 *
 * Every status fixture is `syncStatus()` (the full v2 payload) varied one field at a time.
 */

const NO_MODEL = { minNewRatings: 10, newRatingsSinceTrain: 0, hasActiveModel: false, lastTrainedAt: null };

/** What the server sends today for the floor-met wait. */
const AWAITING_TODAY = syncStatus({ pipelineStage: "Degraded", degradedReason: "PipelineFailed", retrain: NO_MODEL });

/** The same state under the CR-6 contract. */
const AWAITING_CR6 = syncStatus({ pipelineStage: "Degraded", degradedReason: "AwaitingFirstModel", retrain: NO_MODEL });

/** A real stop: the first Train itself failed. */
const TRAIN_FAILED = syncStatus({
  pipelineStage: "Degraded",
  degradedReason: "PipelineFailed",
  retrain: NO_MODEL,
  lastFailures: [
    { kind: "Train", sseKind: "train", visibility: "UserVisible", failedAt: "2026-10-24T03:00:00Z", consecutiveFailures: 1, attempts: 3, reasonCode: "Unknown" },
  ],
});

const WAIT_SENTENCE = "You've rated enough — Reel builds your first model in tonight's refresh at the latest.";

const ACCURACY_EMPTY = {
  window: "90d",
  from: "2026-07-27T00:00:00Z",
  to: "2026-10-25T00:00:00Z",
  source: "none",
  headline: { withinOnePoint: 0, n: 0, meanAbsoluteError: 0, bias: 0, correlation: null, meanPredicted: 0, meanActual: 0 },
  calibration: [],
  moments: [],
  conversion: { shown: 0, saved: 0, watched: 0, rated: 0, shownToSaved: null, savedToWatched: null, shownToRated: null, medianDaysToWatch: null },
  counterfactual: [],
  learnedThisWeek: null,
};

const DNA = {
  userMean: 6.7,
  contrarianScore: 0.2,
  ratingsCount: 14,
  hoursWatched: 40,
  titlesWatched: 33,
  showCompletionRate: 0.7,
  topGenres: [{ genre: "drama", affinity: 7.4, count: 9 }],
  eras: [],
  creators: [],
  topActors: [],
  histogram: [{ rating: 7, count: 10 }],
  drift: [],
  clusters: [],
};

async function shell(page: Page, status: unknown, session: Record<string, unknown> = {}) {
  await stubEventSource(page);
  await mockJson(page, "**/api/auth/me", { ...SESSION_FOUNDER, ...session });
  await mockJson(page, "**/api/sync/status", status);
}

/**
 * The two panels Taste DNA loads beside the charts, answered as an account with nothing in them yet
 * would see them: no dislikes and no narrative written. Unanswered, they reach the e2e tripwire.
 */
async function tastePanels(page: Page) {
  await mockJson(page, "**/api/taste/dislikes", {
    total: 0, windowed: 0, items: [], profile: { genres: [], tones: [], seenEnoughCount: 0 },
  });
  await mockJson(page, "**/api/taste/narrative", {
    text: null, isEdited: false, version: null, ratingsCount: 0, generatedAt: null,
    isStale: false, regenerating: false, writerAvailable: true,
  });
}

/** Text no surface may show for the wait. */
async function expectNoFailure(page: Page) {
  await expect(page.getByText(/something went wrong/i)).toHaveCount(0);
  await expect(page.getByText(/\bDegraded\b/)).toHaveCount(0);
}

test.describe("track record with no model yet", () => {
  for (const [label, status] of [["today's payload", AWAITING_TODAY], ["the CR-6 reason", AWAITING_CR6]] as const) {
    test(`says the model comes tonight and stops asking for scored titles (${label})`, async ({ page }) => {
      await shell(page, status);
      await mockJson(page, "**/api/accuracy", ACCURACY_EMPTY);
      await page.goto("/track-record");

      await expect(page.getByTestId("track-record-no-model")).toHaveText(WAIT_SENTENCE);
      await expect(page.getByTestId("source-note")).toHaveText(
        "Reel puts a number on titles once it has a model; every one it scores that you then rate lands here."
      );
      await expect(page.getByText("it has already put a number on")).toHaveCount(0);
      await expect(page.getByTestId("track-record-empty-rate")).toHaveAttribute("href", "/rate");
      await expectNoFailure(page);
    });
  }

  test("a failed first Train is a stop, not a wait with a promised time", async ({ page }) => {
    await shell(page, TRAIN_FAILED);
    await mockJson(page, "**/api/accuracy", ACCURACY_EMPTY);
    await page.goto("/track-record");

    await expect(page.getByTestId("track-record-no-model")).toHaveText(
      "Building your model stopped on Reel's side. Nothing you've rated is lost."
    );
    await expect(page.getByTestId("track-record-no-model")).not.toContainText("tonight");
  });

  test("scored numbers already on the page are never contradicted by a no-model line", async ({ page }) => {
    await shell(page, AWAITING_TODAY);
    await mockJson(page, "**/api/accuracy", {
      ...ACCURACY_EMPTY,
      source: "exposures",
      headline: { ...ACCURACY_EMPTY.headline, n: 65, withinOnePoint: 0.3, meanAbsoluteError: 1.8, bias: 0.4, correlation: 0.05 },
    });
    await page.goto("/track-record");

    await expect(page.getByTestId("source-note")).toHaveText("From 65 cards Reel actually put in front of you and you later rated.");
    await expect(page.getByTestId("track-record-no-model")).toHaveCount(0);
  });

  test("an account with a model keeps the usual empty-state line", async ({ page }) => {
    await shell(page, syncStatus());
    await mockJson(page, "**/api/accuracy", ACCURACY_EMPTY);
    await page.goto("/track-record");

    await expect(page.getByTestId("source-note")).toContainText("Rate a few titles it has already put a number on");
    await expect(page.getByTestId("track-record-no-model")).toHaveCount(0);
  });
});

test.describe("taste DNA with no model yet", () => {
  test("renders in full and says none of it waits for the model", async ({ page }) => {
    await shell(page, AWAITING_TODAY);
    await mockJson(page, "**/api/taste", DNA);
    await tastePanels(page);
    await page.goto("/dna");

    await expect(page.getByTestId("dna-no-model")).toHaveText(
      `${WAIT_SENTENCE} Everything below is read straight from your ratings, so none of it waits for the model.`
    );
    await expect(page.getByRole("heading", { name: "Genre fingerprint" })).toBeVisible();
    await expect(page.getByTestId("dna-error")).toHaveCount(0);
    await expectNoFailure(page);
  });

  test("below the floor it gives the counts instead", async ({ page }) => {
    await shell(
      page,
      syncStatus({
        pipelineStage: "NotEnoughRatings",
        degradedReason: "NotEnoughRatings",
        notEnoughRatings: { usableRatings: 4, required: 10 },
        retrain: NO_MODEL,
      })
    );
    await mockJson(page, "**/api/taste", DNA);
    await tastePanels(page);
    await page.goto("/dna");

    await expect(page.getByTestId("dna-no-model")).toContainText(
      "Reel hasn't built your model yet. 4 of 10 ratings — rate 6 more and Reel builds your model."
    );
  });

  test("an account with a model sees no line about it", async ({ page }) => {
    await shell(page, syncStatus());
    await mockJson(page, "**/api/taste", DNA);
    await tastePanels(page);
    await page.goto("/dna");

    await expect(page.getByRole("heading", { name: "Genre fingerprint" })).toBeVisible();
    await expect(page.getByTestId("dna-no-model")).toHaveCount(0);
  });
});

test.describe("onboarding with the CR-6 reason", () => {
  test("AwaitingFirstModel is the wait — even where an older reading would see a failed Train", async ({ page }) => {
    await shell(
      page,
      { ...AWAITING_CR6, lastFailures: (TRAIN_FAILED as { lastFailures: unknown[] }).lastFailures },
      { onboarded: false, pipelineStage: "Degraded" }
    );
    await page.goto("/onboarding");

    await expect(page.getByTestId("awaiting-model-copy")).toHaveText("Reel builds your first model in tonight's refresh at the latest.");
    await expect(page.getByTestId("onboarding-failed")).toHaveCount(0);
    await expectNoFailure(page);
  });

  test("an account that already has a model is told about its next build, not its first", async ({ page }) => {
    await shell(page, { ...AWAITING_TODAY, retrain: { ...NO_MODEL, hasActiveModel: true } }, { onboarded: false, pipelineStage: "Degraded" });
    await page.goto("/onboarding");

    await expect(page.getByTestId("awaiting-model-copy")).toHaveText("Reel rebuilds your model in tonight's refresh at the latest.");
    await expect(page.getByTestId("awaiting-model")).not.toContainText("first model");
  });
});

async function bootSettings(page: Page, status: unknown) {
  await shell(page, status);
  await mockJson(page, "**/api/billing/status", { tier: "Founder", configured: false });
  await mockJson(page, "**/api/preferences/tags", []);
  await mockJson(page, "**/api/settings/filters/cost", { total: 5000, eligible: 400, byRule: [], suggestions: [] });
  await mockJson(page, "**/api/settings/filters", {
    excludeGenres: [],
    includeGenres: [],
    excludeKeywords: [],
    maturityCeiling: null,
    minPredictedRating: 6,
    languages: ["en"],
    minCrowdRating: null,
    minYear: null,
    maxYear: null,
  });
  await mockJson(page, "**/api/settings", { region: "US", onboarded: true, email: null, eveningBudgetMinutes: 150 });
  await page.goto("/settings");
}

test.describe("settings", () => {
  test("the pipeline line says the account is waiting, never 'Degraded'", async ({ page }) => {
    await bootSettings(page, AWAITING_TODAY);

    await expect(page.getByTestId("settings-pipeline")).toHaveText(
      "Pipeline: waiting for your first model — tonight's refresh at the latest"
    );
    await expectNoFailure(page);
  });

  test("a failed first Train reads as stopped", async ({ page }) => {
    await bootSettings(page, TRAIN_FAILED);

    await expect(page.getByTestId("settings-pipeline")).toHaveText("Pipeline: stopped");
  });

  // R1-26, IR1S-06: Settings renders the one shared scope sentence. The typeahead does not apply the
  // "for you" floor, its global leg applies no rule, and Ask Reel keeps under-minimum titles behind a
  // tray — so the sentence never claims "airtight" or "all search".
  test("the conditions say where they apply, and do not claim all search", async ({ page }) => {
    await bootSettings(page, syncStatus());

    const scope = page.getByTestId("conditions-scope");
    await expect(scope).toHaveText(CONDITIONS_SCOPE_COPY);
    await expect(scope).not.toContainText("airtight");
    await expect(scope).not.toContainText("all search");
    await expect(page.getByTestId("settings-pipeline")).toHaveText("Pipeline: ready");
  });
});
