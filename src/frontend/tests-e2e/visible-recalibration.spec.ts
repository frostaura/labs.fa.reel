import { test, expect, type Page } from "@playwright/test";
import { SESSION_FOUNDER, mockJson, stubEventSource } from "./helpers";
import { mockHomeChrome } from "./home-chrome";

/**
 * Visible recalibration, end to end in the browser.
 *
 * The founder's second observation was "the sync happens instantly and nothing changes". A
 * Preserve-mode build can now recalibrate tonight's picks without re-dealing them: it updates the
 * Active snapshot's rows in place and publishes `feed-scores-updated` carrying the snapshot's
 * UNCHANGED `generatedAt`.
 *
 * Unchanged is the whole contract, and it is what makes this spec necessary rather than nice to
 * have. Every row on Home is keyed on `generatedAt`, so the one thing the client must NOT do is
 * treat the frame like `feed-ready` — invalidating the Feed tag would remount every row and throw
 * away the scroll the mechanism exists to protect. What the numbers change, and what stays put,
 * are only observable together.
 */

const HERO_ID = "00000000-0000-0000-0000-0000000000h1".replace("h", "a");
const GENERATED_AT = "2026-09-09T18:30:00.000Z";

const card = (n: number, predictedRating = 8.1) => ({
  titleId: `00000000-0000-0000-0000-00000000000${n}`,
  mediaType: "Movie",
  tmdbId: 1000 + n,
  name: `Pick ${n}`,
  year: 2026,
  runtimeMinutes: 110,
  posterPath: null,
  backdropPath: null,
  genres: ["drama"],
  predictedRating,
  crowdRating: 7.5,
  crowdVotes: 9000,
  whyThis: "Explores a heist crew coming apart, like Heat.",
  isReleased: true,
});

const SYNC_IDLE = {
  pipelineStage: "FeedReady",
  connectionStatus: "Active",
  lastDeltaSyncAt: "2026-09-09T18:25:00.000Z",
  lastFullReconcileAt: null,
  activeJob: null,
  recentJobs: [],
  outboxPending: 0,
  outboxDeadLetters: 0,
};

async function bootHome(page: Page): Promise<{ feedRequests: () => number }> {
  await stubEventSource(page);
  await mockHomeChrome(page);
  await mockJson(page, "**/api/auth/me", SESSION_FOUNDER);
  await mockJson(page, "**/api/sync/status", SYNC_IDLE);
  await mockJson(page, "**/api/feed/continue-watching", []);
  await mockJson(page, "**/api/feed/more", { items: [], exhausted: true });
  await mockJson(page, "**/api/feed/genres", { genres: [] });
  await mockJson(page, "**/api/digest/current", { weekStart: null, generatedAt: null, items: [] });
  // 204: nothing honest to say about a retrain in this fixture, so no band.
  await page.route(
    (url) => url.pathname === "/api/feed/learned",
    (route) => route.fulfill({ status: 204, body: "" })
  );

  let feedRequests = 0;
  await page.route(
    (url) => url.pathname === "/api/feed",
    (route) => {
      feedRequests += 1;
      return route.fulfill({
        status: 200,
        contentType: "application/json",
        body: JSON.stringify({
          generatedAt: GENERATED_AT,
          hero: [card(1)],
          rows: [
            {
              kind: "because-you-loved",
              anchorTitleId: HERO_ID,
              anchorName: "Anchor Film",
              title: null,
              items: [card(6), card(7), card(8), card(9)],
            },
          ],
          lockedRowCount: 0,
        }),
      });
    }
  );

  await page.goto("/home");
  await expect(page.getByTestId("rec-card")).toHaveCount(4);
  return { feedRequests: () => feedRequests };
}

test.describe("visible recalibration", () => {
  test("a feed-scores-updated frame changes the numbers without remounting a single row", async ({ page }) => {
    const { feedRequests } = await bootHome(page);
    const requestsBefore = feedRequests();

    const pick6 = page.getByTestId("rec-card").filter({ hasText: "Pick 6" });
    const pick7 = page.getByTestId("rec-card").filter({ hasText: "Pick 7" });
    await expect(pick6.getByTestId("predicted-rating")).toHaveText("8.1for you");

    // Scroll away from the top so a remount would be unmistakable — the reset is the damage, not
    // the number.
    await page.evaluate(() => window.scrollTo(0, 240));
    const scrollBefore = await page.evaluate(() => window.scrollY);

    // Tag every live card node. A remount replaces these nodes and the marks vanish with them.
    await page.evaluate(() => {
      document
        .querySelectorAll('[data-testid="rec-card"], [data-testid="hero-card"]')
        .forEach((el) => ((el as HTMLElement).dataset.persisted = "yes"));
    });

    await page.evaluate(
      ([generatedAt, sixth, seventh]) => {
        (window as unknown as { __sse: { emit: (t: string, d: Record<string, unknown>) => void } }).__sse.emit(
          "feed-scores-updated",
          {
            generatedAt,
            changed: [
              { titleId: sixth, predictedRating: 6.4 },
              { titleId: seventh, predictedRating: 9.2 },
            ],
          }
        );
      },
      [GENERATED_AT, card(6).titleId, card(7).titleId] as const
    );

    // The badges move...
    await expect(pick6.getByTestId("predicted-rating")).toHaveText("6.4for you");
    await expect(pick7.getByTestId("predicted-rating")).toHaveText("9.2for you");

    // ...and nothing else does. Same nodes, same order, same scroll position.
    const persisted = await page.evaluate(() =>
      Array.from(document.querySelectorAll('[data-testid="rec-card"]')).map(
        (el) => (el as HTMLElement).dataset.persisted ?? "no"
      )
    );
    expect(persisted).toEqual(["yes", "yes", "yes", "yes"]);
    expect(await page.evaluate(() => window.scrollY)).toBe(scrollBefore);
    await expect(page.getByTestId("rec-card")).toHaveCount(4);

    // The Feed tag was NOT invalidated: a refetch here would return the same generatedAt while the
    // invalidation itself remounted the page. This assertion is the one that catches a future
    // "just treat it like feed-ready".
    expect(feedRequests()).toBe(requestsBefore);

    // Untouched cards keep the number they had.
    await expect(
      page.getByTestId("rec-card").filter({ hasText: "Pick 8" }).getByTestId("predicted-rating")
    ).toHaveText("8.1for you");
  });

  test("a stale or malformed frame leaves the page alone", async ({ page }) => {
    await bootHome(page);
    const pick6 = page.getByTestId("rec-card").filter({ hasText: "Pick 6" });

    await page.evaluate(() => {
      const sse = (window as unknown as { __sse: { emit: (t: string, d: Record<string, unknown>) => void } }).__sse;
      sse.emit("feed-scores-updated", { generatedAt: "2026-09-09T18:30:00.000Z", changed: [] });
      sse.emit("feed-scores-updated", { generatedAt: "2026-09-09T18:30:00.000Z" });
    });

    await expect(pick6.getByTestId("predicted-rating")).toHaveText("8.1for you");
    await expect(page.getByTestId("rec-card")).toHaveCount(4);
  });

  test("the sync pill says what the sync did, then what it did to the page", async ({ page }) => {
    await bootHome(page);
    const label = page.getByTestId("sync-health-label");
    await expect(label).toHaveText(/^Synced/);

    // The delta finishes. It knows what it ingested and whether a retrain is owed — but not yet how
    // much of the page moved, because the rebuild is enqueued, never awaited.
    await page.evaluate(() => {
      (window as unknown as { __sse: { emit: (t: string, d: Record<string, unknown>) => void } }).__sse.emit(
        "job-completed",
        {
          kind: "delta",
          ratingsAdded: 12,
          ratingsChanged: 0,
          watchedAdded: 0,
          retrainQueued: true,
          retrainAt: "2026-09-10T03:00:00Z",
          picksChanged: null,
        }
      );
    });

    await expect(label).toHaveText("Pulled 12 new ratings from Trakt — retraining tonight at 03:00 UTC");

    // The rebuild lands and reports the number the sync could not know. A concrete change to the
    // page outranks a promise about tonight.
    await page.evaluate(() => {
      (window as unknown as { __sse: { emit: (t: string, d: Record<string, unknown>) => void } }).__sse.emit(
        "feed-ready",
        { snapshotId: "s1", generatedAt: "2026-09-09T18:30:00.000Z", picksChanged: 3 }
      );
    });

    await expect(label).toHaveText("Pulled 12 new ratings from Trakt — 3 picks changed");
  });

  test("a sync that found nothing says so, rather than reverting to a timestamp", async ({ page }) => {
    await bootHome(page);
    const label = page.getByTestId("sync-health-label");

    await page.evaluate(() => {
      (window as unknown as { __sse: { emit: (t: string, d: Record<string, unknown>) => void } }).__sse.emit(
        "job-completed",
        {
          kind: "delta",
          ratingsAdded: 0,
          ratingsChanged: 0,
          watchedAdded: 0,
          retrainQueued: false,
          retrainAt: null,
          picksChanged: null,
        }
      );
    });

    await expect(label).toHaveText("Already up to date");
  });

  test("a fresh retrain is one segment of Home's status line, and ages out (FD-15)", async ({ page }) => {
    const boot = async (trainedAt: string) => {
      await stubEventSource(page);
      await mockHomeChrome(page);
      await mockJson(page, "**/api/auth/me", SESSION_FOUNDER);
      await mockJson(page, "**/api/sync/status", SYNC_IDLE);
      await mockJson(page, "**/api/feed/continue-watching", []);
      await mockJson(page, "**/api/feed/more", { items: [], exhausted: true });
      await mockJson(page, "**/api/feed/genres", { genres: [] });
      await mockJson(page, "**/api/digest/current", { weekStart: null, generatedAt: null, items: [] });
      await mockJson(page, "**/api/feed/learned", {
        trainedAt,
        newRatingsSinceLast: 14,
        calibrationBias: 0.2,
        calibrationMae: 1.1,
        spearman: 0.31,
        picksChanged: 3,
      });
      await mockJson(page, "**/api/feed", { generatedAt: GENERATED_AT, hero: [card(1)], rows: [], lockedRowCount: 0 });
      await page.goto("/home");
      await expect(page.getByTestId("hero-card")).toHaveCount(1);
    };

    // Three hours ago: news. Said once, on the status line — no band, no dismiss control.
    await boot(new Date(Date.now() - 3 * 3_600_000).toISOString());
    await expect(page.getByTestId("status-retrained")).toHaveText("Retrained 3h ago on 14 new ratings, 3 picks changed");
    await expect(page.getByTestId("model-learned-band")).toHaveCount(0);

    // Three days ago: no longer news, so the segment is gone rather than waiting to be dismissed.
    await boot(new Date(Date.now() - 3 * 86_400_000).toISOString());
    await expect(page.getByTestId("status-retrained")).toHaveCount(0);
  });
});
