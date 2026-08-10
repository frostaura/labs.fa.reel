import { test, expect, type Page } from "@playwright/test";
import { SESSION_FOUNDER, mockJson, stubEventSource } from "./helpers";

/**
 * Title-detail reactions: the one-tap rate popover ("Seen it — you rated N"), the clear-rating
 * escape hatch the remediation added, and the not-interested reason menu — asserting what actually
 * goes over the wire, not just what the optimistic UI paints.
 */

const TITLE = {
  titleId: "00000000-0000-0000-0000-0000000000t1",
  mediaType: "Movie",
  tmdbId: 1001,
  name: "Quiet Harbor",
  year: 2024,
  overview: "A harbormaster keeps one last secret.",
  tagline: null,
  runtimeMinutes: 112,
  certification: "PG-13",
  genres: ["drama"],
  network: null,
  status: "Released",
  traktRating: 7.8,
  traktVotes: 12000,
  posterPath: null,
  backdropPath: null,
  trailerUrl: null,
  cast: [],
  directors: [],
  writers: [],
  prediction: null,
  contributionDetails: [],
  userState: {
    isFullyWatched: false,
    plays: 0,
    userRating: null,
    savedForLater: false,
    notInterested: false,
    dropped: false,
  },
};

interface CapturedCall {
  method: string;
  body: unknown;
}

async function bootTitle(page: Page) {
  await stubEventSource(page);
  await mockJson(page, "**/api/auth/me", SESSION_FOUNDER);
  await mockJson(page, "**/api/sync/status", {
    pipelineStage: "FeedReady", connectionStatus: "Active", lastDeltaSyncAt: null,
    lastFullReconcileAt: null, activeJob: null, recentJobs: [], outboxPending: 0, outboxDeadLetters: 0,
  });
  await mockJson(page, "**/api/titles/movie/1001", TITLE);
  await mockJson(page, "**/api/titles/movie/1001/providers", {
    region: "US", attribution: "JustWatch", tmdbWatchPage: "https://www.themoviedb.org/movie/1001/watch", providers: [],
  });
  // The "More like this" rail self-loads a page on mount; keep it deterministic and empty.
  await mockJson(page, "**/api/feed/more", { items: [], exhausted: true });
  await page.goto("/title/movie/1001");
  await expect(page.getByTestId("title-detail")).toBeVisible();
}

/** Captures every call to `pattern`, answering 200 {} so optimistic updates settle as successes. */
async function captureCalls(page: Page, pattern: string): Promise<CapturedCall[]> {
  const calls: CapturedCall[] = [];
  await page.route(pattern, (route) => {
    const request = route.request();
    calls.push({
      method: request.method(),
      body: request.postData() ? request.postDataJSON() : undefined,
    });
    return route.fulfill({ status: 200, contentType: "application/json", body: "{}" });
  });
  return calls;
}

test.describe("title detail actions", () => {
  test("rating flips the button to 'Seen it — you rated N' and Clear rating reverts it", async ({ page }) => {
    await bootTitle(page);
    const ratingCalls = await captureCalls(page, "**/api/titles/movie/1001/rating");

    await page.getByRole("button", { name: /seen it \+ rate/i }).click();
    await page.getByTestId("rate-7").click();

    await expect(page.getByRole("button", { name: /seen it — you rated 7/i })).toBeVisible();
    expect(ratingCalls).toHaveLength(1);
    expect(ratingCalls[0]).toEqual({ method: "POST", body: { rating: 7 } });

    // Clear rating only exists once a rating does — reopen the popover and take it back.
    await page.getByRole("button", { name: /seen it — you rated 7/i }).click();
    const clear = page.getByTestId("clear-rating");
    await expect(clear).toBeVisible();
    await clear.click();

    await expect(page.getByRole("button", { name: /seen it \+ rate/i })).toBeVisible();
    expect(ratingCalls).toHaveLength(2);
    expect(ratingCalls[1].method).toBe("DELETE");
  });

  test("the Not interested menu sends the chosen reason on the wire", async ({ page }) => {
    await bootTitle(page);
    const reactionCalls = await captureCalls(page, "**/api/titles/movie/1001/reactions/not_interested");

    await page.getByTestId("not-interested").click();
    await page.getByRole("menuitem", { name: /just not for me/i }).click();

    // Optimistic state flips to the undo affordance…
    await expect(page.getByRole("button", { name: /not interested — undo/i })).toBeVisible();
    // …and the wire carried the specific reason, not a bare reaction.
    expect(reactionCalls).toHaveLength(1);
    expect(reactionCalls[0]).toEqual({ method: "POST", body: { reason: "not-for-me" } });
  });
});
