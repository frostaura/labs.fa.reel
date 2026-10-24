import { test, expect, type Page } from "@playwright/test";
import { SESSION_FOUNDER, mockJson, stubEventSource } from "./helpers";

/**
 * /rate, rebuilt around watched-but-unrated titles.
 *
 * The old screen offered the catalogue's most popular unrated titles under "Sharpen your taste",
 * with one unfalsifiable payoff line ("Your model sharpens on the next refresh"). Most of those
 * cards are titles you have never seen, so every one is a recall test — while the titles you HAVE
 * watched and never scored, the free labels, were invisible to it.
 *
 * What these pin: the watched half leads and is headlined as a memory; the popularity pool is
 * clearly the "or anything else" fallback behind it; and the payoff is a countable promise read
 * from the real retrain gate, which moves as you rate.
 */

const suggestion = (n: number, source: "watched" | "popular") => ({
  titleId: `00000000-0000-0000-0000-0000000000${String(n).padStart(2, "0")}`,
  mediaType: "Movie",
  tmdbId: 2000 + n,
  name: `${source === "watched" ? "Seen" : "Popular"} ${n}`,
  year: 2020,
  posterPath: null,
  crowdRating: 7.2,
  crowdVotes: 4000,
  source,
});

const SYNC = (retrain: Record<string, unknown> | null) => ({
  pipelineStage: "FeedReady",
  connectionStatus: "Active",
  lastDeltaSyncAt: null,
  lastFullReconcileAt: null,
  activeJob: null,
  recentJobs: [],
  outboxPending: 0,
  outboxDeadLetters: 0,
  lastListSyncAt: null,
  outboxLastError: null,
  ...(retrain ? { retrain } : {}),
});

async function bootRate(
  page: Page,
  suggestions: unknown[],
  // `null` means the field is absent from the response; omitting the argument uses the default.
  retrain: Record<string, unknown> | null = {
    minNewRatings: 10,
    newRatingsSinceTrain: 3,
    hasActiveModel: true,
    lastTrainedAt: "2026-09-08T00:00:00Z",
  }
) {
  await stubEventSource(page);
  await mockJson(page, "**/api/auth/me", SESSION_FOUNDER);
  await mockJson(page, "**/api/sync/status", SYNC(retrain));
  await mockJson(page, "**/api/titles/rate-suggestions", suggestions);
  await page.goto("/rate");
}

test.describe("rate picker", () => {
  test("leads with what you watched and never rated, and files the popularity pool behind it", async ({ page }) => {
    await bootRate(page, [
      suggestion(1, "watched"),
      suggestion(2, "watched"),
      suggestion(3, "popular"),
    ]);

    await expect(page.getByTestId("rate-heading")).toHaveText(
      "You watched these and never told me what you thought"
    );
    await expect(page.getByTestId("rate-picker").getByTestId("rate-card")).toHaveCount(2);
    await expect(page.getByTestId("rate-picker-popular").getByTestId("rate-card")).toHaveCount(1);
    await expect(page.getByText("Or rate anything else you remember")).toBeVisible();
  });

  test("with nothing watched-but-unrated left it is the old cold-start grid, honestly headlined", async ({ page }) => {
    await bootRate(page, [suggestion(3, "popular"), suggestion(4, "popular")]);

    await expect(page.getByTestId("rate-heading")).toHaveText("Sharpen your taste");
    await expect(page.getByTestId("rate-picker").getByTestId("rate-card")).toHaveCount(2);
    await expect(page.getByText("Or rate anything else you remember")).toHaveCount(0);
  });

  test("the payoff is a countable promise that moves as you rate", async ({ page }) => {
    await bootRate(page, [suggestion(1, "watched"), suggestion(2, "watched")]);

    // 10 needed, 3 already banked server-side ⇒ 7 to go.
    await expect(page.getByTestId("retrain-progress")).toHaveText(
      "7 more ratings and Reel retrains tonight."
    );

    await page.route(/\/api\/titles\/[Mm]ovie\/2001\/rating$/, (route) =>
      route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify({ userRating: 8 }) })
    );

    await page.getByTestId("rate-card").filter({ hasText: "Seen 1" }).click();
    await page.getByTestId("rate-value").nth(7).click();

    await expect(page.getByTestId("retrain-progress")).toHaveText(
      "6 more ratings and Reel retrains tonight."
    );
    // And the card leaves the grid, so the next ask is never one already answered.
    await expect(page.getByTestId("rate-card")).toHaveCount(1);
  });

  test("the last rating before the gate says the retrain is on, not 'nearly'", async ({ page }) => {
    await bootRate(page, [suggestion(1, "watched")], {
      minNewRatings: 10,
      newRatingsSinceTrain: 10,
      hasActiveModel: true,
      lastTrainedAt: "2026-09-08T00:00:00Z",
    });

    await expect(page.getByTestId("retrain-progress")).toHaveText(
      "That's enough — Reel retrains tonight on 10 new ratings."
    );
  });

  test("before the first model exists it promises a first training, not a retrain", async ({ page }) => {
    await bootRate(page, [suggestion(3, "popular")], {
      minNewRatings: 10,
      newRatingsSinceTrain: 0,
      hasActiveModel: false,
      lastTrainedAt: null,
    });

    await expect(page.getByTestId("retrain-progress")).toHaveText(
      "0 ratings in — Reel trains your first model tonight."
    );
  });

  test("a backend with no retrain field renders no promise at all rather than a made-up one", async ({ page }) => {
    await bootRate(page, [suggestion(1, "watched")], null);

    await expect(page.getByTestId("rate-card")).toHaveCount(1);
    await expect(page.getByTestId("retrain-progress")).toHaveCount(0);
  });

  test("a failed read is an error with a Retry, never an empty grid", async ({ page }) => {
    await stubEventSource(page);
    await mockJson(page, "**/api/auth/me", SESSION_FOUNDER);
    await mockJson(page, "**/api/sync/status", SYNC(null));
    let fail = true;
    await page.route(
      (u) => u.pathname === "/api/titles/rate-suggestions",
      (route) =>
        fail
          ? route.fulfill({ status: 500, contentType: "application/json", body: "{}" })
          : route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify([suggestion(1, "watched")]) })
    );
    await page.goto("/rate");

    await expect(page.getByTestId("rate-error")).toBeVisible();
    await expect(page.getByTestId("rate-error")).toContainText("Couldn't load titles to rate");
    await expect(page.getByTestId("rate-empty")).toHaveCount(0);
    await expect(page.getByTestId("rate-loading")).toHaveCount(0);

    fail = false;
    await page.getByTestId("rate-error-retry").click();
    await expect(page.getByTestId("rate-card")).toHaveCount(1);
    await expect(page.getByTestId("rate-error")).toHaveCount(0);
  });

  test("a truly empty pool says so, distinct from a failure", async ({ page }) => {
    await bootRate(page, []);
    await expect(page.getByTestId("rate-empty")).toBeVisible();
    await expect(page.getByTestId("rate-error")).toHaveCount(0);
    await expect(page.getByTestId("rate-card")).toHaveCount(0);
  });

  test("while the read is in flight it shows a skeleton, not an empty page", async ({ page }) => {
    await stubEventSource(page);
    await mockJson(page, "**/api/auth/me", SESSION_FOUNDER);
    await mockJson(page, "**/api/sync/status", SYNC(null));
    let release: () => void = () => undefined;
    const gate = new Promise<void>((resolve) => (release = resolve));
    await page.route(
      (u) => u.pathname === "/api/titles/rate-suggestions",
      async (route) => {
        await gate;
        await route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify([suggestion(1, "watched")]) });
      }
    );
    await page.goto("/rate");
    await expect(page.getByTestId("rate-loading")).toBeVisible();
    await expect(page.getByTestId("rate-empty")).toHaveCount(0);
    release();
    await expect(page.getByTestId("rate-card")).toHaveCount(1);
  });

  for (const status of [500, 400, 0]) {
    test(`a rating that fails (${status === 0 ? "network drop" : status}) puts the card back and leaves the retrain counter alone`, async ({ page }) => {
      await bootRate(page, [suggestion(1, "watched"), suggestion(2, "watched")]);
      await expect(page.getByTestId("retrain-progress")).toHaveText("7 more ratings and Reel retrains tonight.");

      await page.route(/\/api\/titles\/[Mm]ovie\/2001\/rating$/, (route) =>
        status === 0
          ? route.abort("connectionreset")
          : route.fulfill({ status, contentType: "application/json", body: JSON.stringify({ error: "nope" }) })
      );

      await page.getByTestId("rate-card").filter({ hasText: "Seen 1" }).click();
      await page.getByTestId("rate-value").nth(7).click();

      await expect(page.getByTestId("rate-failure")).toContainText("Couldn't save your 8 for Seen 1");
      // The card is back, the counter never moved, and nothing claims a rating was made.
      await expect(page.getByTestId("rate-card")).toHaveCount(2);
      await expect(page.getByTestId("retrain-progress")).toHaveText("7 more ratings and Reel retrains tonight.");
      await expect(page.getByTestId("rate-session-count")).toHaveCount(0);
    });
  }

  test("a failure, then a success on the same card: only the success counts", async ({ page }) => {
    await bootRate(page, [suggestion(1, "watched"), suggestion(2, "watched")]);
    let fail = true;
    await page.route(/\/api\/titles\/[Mm]ovie\/2001\/rating$/, (route) =>
      fail
        ? route.fulfill({ status: 500, contentType: "application/json", body: "{}" })
        : route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify({ userRating: 8 }) })
    );

    await page.getByTestId("rate-card").filter({ hasText: "Seen 1" }).click();
    await page.getByTestId("rate-value").nth(7).click();
    await expect(page.getByTestId("rate-failure")).toBeVisible();

    fail = false;
    await page.getByTestId("rate-card").filter({ hasText: "Seen 1" }).click();
    await page.getByTestId("rate-value").nth(7).click();

    await expect(page.getByTestId("rate-failure")).toHaveCount(0);
    await expect(page.getByTestId("rate-card")).toHaveCount(1);
    await expect(page.getByTestId("retrain-progress")).toHaveText("6 more ratings and Reel retrains tonight.");
    await expect(page.getByTestId("rate-session-count")).toHaveText("1 rated in this session");
  });
});

