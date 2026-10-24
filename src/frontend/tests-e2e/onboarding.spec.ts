import { test, expect } from "@playwright/test";
import { EMPTY_SYNC, SESSION_FOUNDER, emit, mockJson, stubEventSource, waitForStream } from "./helpers";

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

  test("any other stop names its reason and 'Try again' asks the server for a sync", async ({ page }) => {
    await stubEventSource(page);
    await mockJson(page, "**/api/auth/me", { ...SESSION_FOUNDER, onboarded: false, pipelineStage: "Degraded" });
    await mockJson(page, "**/api/sync/status", { ...EMPTY_SYNC, pipelineStage: "Degraded", degradedReason: "PipelineFailed" });
    let syncs = 0;
    await page.route(
      (u) => u.pathname === "/api/sync/now",
      (route) => {
        syncs += 1;
        return route.fulfill({ status: 202, contentType: "application/json", body: "{}" });
      }
    );
    await page.goto("/onboarding");

    await expect(page.getByTestId("onboarding-failed-reason")).toContainText("stopped on Reel's side");
    await page.getByTestId("onboarding-retry").click();
    await expect.poll(() => syncs).toBe(1);
  });
});

