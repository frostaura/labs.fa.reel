import { test, expect } from "@playwright/test";
import { SESSION_FOUNDER, emit, mockJson, stubEventSource, syncStatus, waitForStream } from "./helpers";

/**
 * A new account mid-onboarding: no model exists yet. The shared idle payload says there is one
 * (`retrain.hasActiveModel: true`), which made the floor-met wait read as a rebuild of a model the
 * account never had — the copy decides "first" or "next" from this field.
 */
const EMPTY_SYNC = syncStatus({
  pipelineStage: "Ingesting",
  retrain: { minNewRatings: 10, newRatingsSinceTrain: 0, hasActiveModel: false, lastTrainedAt: null },
});

/**
 * The live build-up show against a fully mocked backend + scripted SSE — pins the staged
 * advance, counter rendering, and the poll-overrides-stalled-stream contract.
 */
test.describe("onboarding build-up", () => {
  test("stages advance through the scripted pipeline to the reveal", async ({ page }) => {
    await stubEventSource(page);
    await mockJson(page, "**/api/auth/me", { ...SESSION_FOUNDER, onboarded: false, pipelineStage: "Ingesting" });
    await mockJson(page, "**/api/sync/status", EMPTY_SYNC);
    await mockJson(page, "**/api/feed", { generatedAt: null, hero: [], rows: [], lockedRowCount: 0 });

    await page.goto("/onboarding");
    // The mount-time status poll (authoritative) may already advance past "connecting".
    await waitForStream(page);

    await emit(page, "connected", { traktUser: "test-founder" });
    await emit(page, "ingest-progress", { kind: "movies", found: 2842 });
    await expect(page.getByTestId("stage-copy")).toContainText("Pulling in everything");
    await expect(page.getByTestId("tickers")).toContainText("2,842");

    await emit(page, "ingest-progress", { kind: "ratings", found: 3484 });
    await emit(page, "insight", { id: "i1", kind: "genre", text: "You rate crime and thriller highest" });
    await expect(page.getByTestId("stage-copy")).toContainText("Reading your taste");
    await expect(page.getByTestId("insights")).toContainText("crime and thriller");

    await emit(page, "model-progress", { phase: "Teaching your model", processed: 1400, total: 1782 });
    await expect(page.getByTestId("stage-copy")).toContainText("Teaching your model");

    await emit(page, "feed-ready", {});
    await expect(page.getByTestId("reveal")).toBeVisible();
    await expect(page.getByTestId("enter-app")).toBeVisible();
  });

  test("a late ingest event never regresses the stage", async ({ page }) => {
    await stubEventSource(page);
    await mockJson(page, "**/api/auth/me", { ...SESSION_FOUNDER, onboarded: false, pipelineStage: "Ingesting" });
    await mockJson(page, "**/api/sync/status", EMPTY_SYNC);

    await page.goto("/onboarding");
    await waitForStream(page);
    await emit(page, "insight", { id: "i1", kind: "stat", text: "Profiling started" });
    await expect(page.getByTestId("stage-copy")).toContainText("Reading your taste");

    // Out-of-order ingest frame: counters update, stage must hold.
    await emit(page, "ingest-progress", { kind: "shows", found: 249 });
    await expect(page.getByTestId("tickers")).toContainText("249");
    await expect(page.getByTestId("stage-copy")).toContainText("Reading your taste");
  });

  test("the authoritative status poll advances a silent stream", async ({ page }) => {
    await stubEventSource(page);
    await mockJson(page, "**/api/auth/me", { ...SESSION_FOUNDER, onboarded: false, pipelineStage: "Training" });
    await mockJson(page, "**/api/sync/status", { ...EMPTY_SYNC, pipelineStage: "Training" });

    await page.goto("/onboarding");
    // No SSE events at all — the first poll lands within its interval and advances the show.
    await expect(page.getByTestId("stage-copy")).toContainText("Teaching your model", { timeout: 20_000 });
  });

  test("greets by display name, not the Trakt slug", async ({ page }) => {
    await stubEventSource(page);
    await mockJson(page, "**/api/auth/me", { ...SESSION_FOUNDER, onboarded: false, pipelineStage: "Ingesting" });
    await mockJson(page, "**/api/sync/status", EMPTY_SYNC);
    await page.goto("/onboarding");
    await waitForStream(page);
    // The stream's `connected` frame carries the slug; the greeting must still use the name.
    await emit(page, "connected", { traktUser: "test-founder" });
    await expect(page.getByTestId("onboarding-greeting")).toHaveText("Hello, Test Founder");
  });

  test("an account below the training floor is told the counts and where to rate — never 'Something went wrong'", async ({ page }) => {
    await stubEventSource(page);
    await mockJson(page, "**/api/auth/me", { ...SESSION_FOUNDER, onboarded: false, pipelineStage: "Degraded" });
    await mockJson(page, "**/api/sync/status", {
      ...EMPTY_SYNC,
      contractVersion: 2,
      pipelineStage: "NotEnoughRatings",
      degradedReason: "NotEnoughRatings",
      notEnoughRatings: { usableRatings: 4, required: 10 },
    });
    await page.goto("/onboarding");

    await expect(page.getByTestId("needs-ratings-copy")).toHaveText("4 of 10 ratings — rate 6 more and Reel builds your model.");
    await expect(page.getByTestId("needs-ratings-rate")).toHaveAttribute("href", "/rate");
    await expect(page.getByText("Something went wrong")).toHaveCount(0);
    await expect(page.getByTestId("onboarding-failed")).toHaveCount(0);
  });

  test("the trainer's Degraded frame for too few ratings becomes the counts once the poll lands", async ({ page }) => {
    await stubEventSource(page);
    await mockJson(page, "**/api/auth/me", { ...SESSION_FOUNDER, onboarded: false, pipelineStage: "Training" });
    let parked = false;
    await page.route(
      (u) => u.pathname === "/api/sync/status",
      (route) =>
        route.fulfill({
          status: 200,
          contentType: "application/json",
          body: JSON.stringify(
            parked
              ? { ...EMPTY_SYNC, pipelineStage: "NotEnoughRatings", degradedReason: "NotEnoughRatings", notEnoughRatings: { usableRatings: 9, required: 10 } }
              : { ...EMPTY_SYNC, pipelineStage: "Training" }
          ),
        })
    );
    await page.goto("/onboarding");
    await waitForStream(page);
    // Let the first (Training) poll land first: the poll is authoritative and may lift the show off
    // needsRatings, so a stale in-flight response would otherwise race the frame under test.
    await expect(page.getByTestId("stage-copy")).toContainText("Teaching your model");

    parked = true;
    await emit(page, "stage-changed", { stage: "Degraded", reason: "insufficient-ratings", message: "Not enough ratings to train yet" });
    await expect(page.getByTestId("needs-ratings")).toBeVisible();
    await expect(page.getByText("Something went wrong")).toHaveCount(0);
    // The copy needs no counts to be useful, and gains them when the authoritative poll lands.
    await expect(page.getByTestId("needs-ratings-copy")).toHaveText("9 of 10 ratings — rate one more and Reel builds your model.", { timeout: 20_000 });
  });

  test("a revoked Trakt link says so and offers a reconnect, not a reload", async ({ page }) => {
    await stubEventSource(page);
    await mockJson(page, "**/api/auth/me", { ...SESSION_FOUNDER, onboarded: false, pipelineStage: "Degraded" });
    await mockJson(page, "**/api/sync/status", {
      ...EMPTY_SYNC,
      pipelineStage: "Degraded",
      connectionStatus: "Revoked",
      degradedReason: "TraktRevoked",
      notEnoughRatings: null,
    });
    await page.goto("/onboarding");

    await expect(page.getByTestId("onboarding-failed-reason")).toContainText("no longer has access to your Trakt account");
    await expect(page.getByTestId("onboarding-reconnect")).toBeVisible();
    await expect(page.getByTestId("onboarding-retry")).toHaveCount(0);
  });

  test("a revoked link below the floor offers the reconnect, not the rate link", async ({ page }) => {
    await stubEventSource(page);
    await mockJson(page, "**/api/auth/me", { ...SESSION_FOUNDER, onboarded: false, pipelineStage: "NotEnoughRatings" });
    // What the server sends: it computes the floor whatever the link's state, and ranks the link first.
    await mockJson(page, "**/api/sync/status", {
      ...EMPTY_SYNC,
      pipelineStage: "NotEnoughRatings",
      connectionStatus: "Revoked",
      degradedReason: "TraktRevoked",
      notEnoughRatings: { usableRatings: 4, required: 10 },
    });
    await page.goto("/onboarding");

    await expect(page.getByTestId("onboarding-reconnect")).toBeVisible();
    await expect(page.getByTestId("onboarding-failed-reason")).toContainText("no longer has access to your Trakt account");
    await expect(page.getByTestId("needs-ratings")).toHaveCount(0);
  });

  test("rating up to the floor turns 'needs ratings' into 'model next', never into a failure", async ({ page }) => {
    await stubEventSource(page);
    await mockJson(page, "**/api/auth/me", { ...SESSION_FOUNDER, onboarded: false, pipelineStage: "Degraded" });
    let floorMet = false;
    let trained = false;
    await page.route(
      (u) => u.pathname === "/api/sync/status",
      (route) =>
        route.fulfill({
          status: 200,
          contentType: "application/json",
          body: JSON.stringify(
            trained
              ? { ...EMPTY_SYNC, contractVersion: 2, pipelineStage: "Training", degradedReason: null, notEnoughRatings: null, lastFailures: [] }
              : floorMet
                ? // The real payload once usable ratings reach the floor and no Train has run since
                  // the park (SyncStatusContractTests): stored Degraded, reason PipelineFailed, no counts.
                  { ...EMPTY_SYNC, contractVersion: 2, pipelineStage: "Degraded", degradedReason: "PipelineFailed", notEnoughRatings: null, lastFailures: [] }
                : { ...EMPTY_SYNC, contractVersion: 2, pipelineStage: "NotEnoughRatings", degradedReason: "NotEnoughRatings", notEnoughRatings: { usableRatings: 9, required: 10 }, lastFailures: [] }
          ),
        })
    );
    await page.clock.install();
    await page.goto("/onboarding");
    await expect(page.getByTestId("needs-ratings-copy")).toHaveText("9 of 10 ratings — rate one more and Reel builds your model.");
    await expect(page.getByTestId("needs-ratings")).toContainText("tonight's refresh at the latest");
    await expect(page.getByTestId("needs-ratings")).not.toContainText("as soon as there are enough");

    floorMet = true;
    await page.clock.fastForward(16_000);
    await expect(page.getByTestId("awaiting-model")).toBeVisible();
    await expect(page.getByTestId("awaiting-model-copy")).toHaveText("Reel builds your first model in tonight's refresh at the latest.");
    await expect(page.getByTestId("onboarding-failed")).toHaveCount(0);
    await expect(page.getByText("stopped on Reel's side")).toHaveCount(0);

    // Several more polls of the same payload change nothing.
    await page.clock.fastForward(46_000);
    await expect(page.getByTestId("awaiting-model")).toBeVisible();
    await expect(page.getByTestId("onboarding-failed")).toHaveCount(0);

    // The nightly train runs: the page moves on by itself.
    trained = true;
    await page.clock.fastForward(16_000);
    await expect(page.getByTestId("stage-copy")).toContainText("Teaching your model");
  });

  // A genuine stop: the trainer itself failed. (Degraded with no failed Train is the floor-met wait above.)
  const STOPPED = {
    ...EMPTY_SYNC,
    contractVersion: 2,
    pipelineStage: "Degraded",
    degradedReason: "PipelineFailed",
    notEnoughRatings: null,
    lastFailures: [{ kind: "Train", sseKind: "train", visibility: "UserVisible", failedAt: "2026-10-20T10:00:00Z", consecutiveFailures: 3, attempts: 3, reasonCode: "Unknown" }],
  };

  test("a refused 'Try again' keeps the stop on screen and says the sync did not start", async ({ page }) => {
    await stubEventSource(page);
    await mockJson(page, "**/api/auth/me", { ...SESSION_FOUNDER, onboarded: false, pipelineStage: "Degraded" });
    await mockJson(page, "**/api/sync/status", STOPPED);
    let syncs = 0;
    await page.route(
      (u) => u.pathname === "/api/sync/now",
      (route) => {
        syncs += 1;
        return route.fulfill({ status: 500, contentType: "application/json", body: "{}" });
      }
    );
    await page.goto("/onboarding");

    await expect(page.getByTestId("onboarding-failed-reason")).toContainText("stopped on Reel's side");
    await page.getByTestId("onboarding-retry").click();
    await expect.poll(() => syncs).toBe(1);
    await expect(page.getByTestId("onboarding-retry-refused")).toHaveText("Couldn't start a sync — nothing changed. Try again in a moment.");
    await expect(page.getByTestId("onboarding-failed")).toBeVisible();
    await expect(page.getByTestId("stage-copy")).not.toContainText("Linking your Trakt profile");
  });

  test("an accepted 'Try again' on an unchanged stop shows the stop again with what happened, then follows the run", async ({ page }) => {
    await stubEventSource(page);
    await mockJson(page, "**/api/auth/me", { ...SESSION_FOUNDER, onboarded: false, pipelineStage: "Degraded" });
    let recovered = false;
    await page.route(
      (u) => u.pathname === "/api/sync/status",
      (route) =>
        route.fulfill({
          status: 200,
          contentType: "application/json",
          body: JSON.stringify(recovered ? { ...EMPTY_SYNC, pipelineStage: "Training", degradedReason: null, lastFailures: [] } : STOPPED),
        })
    );
    let syncs = 0;
    await page.route(
      (u) => u.pathname === "/api/sync/now",
      (route) => {
        syncs += 1;
        return route.fulfill({ status: 202, contentType: "application/json", body: "{}" });
      }
    );
    await page.clock.install();
    await page.goto("/onboarding");

    await expect(page.getByTestId("onboarding-failed")).toBeVisible();
    await page.getByTestId("onboarding-retry").click();
    await expect.poll(() => syncs).toBe(1);
    // Not "Linking your Trakt profile…" for ever: the server's unchanged answer is applied again at once.
    await expect(page.getByTestId("onboarding-failed")).toBeVisible();
    await expect(page.getByTestId("onboarding-retry-queued")).toBeVisible();
    await expect(page.getByTestId("stage-copy")).toHaveText("Building your profile stopped.");

    // The page keeps watching after an accepted retry, and moves on when the run gets further.
    recovered = true;
    await page.clock.fastForward(16_000);
    await expect(page.getByTestId("stage-copy")).toContainText("Teaching your model");
    await expect(page.getByTestId("onboarding-retry-queued")).toHaveCount(0);
  });
});
