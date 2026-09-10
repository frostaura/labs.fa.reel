import { test, expect, type Page } from "@playwright/test";
import { SESSION_FOUNDER, mockJson, stubEventSource } from "./helpers";

/**
 * Title-detail reactions: the one-tap rate popover ("Seen it — you rated N"), the clear-rating
 * escape hatch the remediation added, and the not-interested reason menu — asserting what actually
 * goes over the wire, not just what the optimistic UI paints. Plus the why-line, which used to
 * VANISH on tap — rendered on every card, absent from the title DTO, so the modal replaced the one
 * personalised sentence with the studio's own blurb.
 */

const TITLE = {
  titleId: "00000000-0000-0000-0000-0000000000t1",
  mediaType: "Movie",
  tmdbId: 1001,
  name: "Quiet Harbor",
  year: 2024,
  overview: "A harbormaster keeps one last secret.",
  whyThis: "Shares the slow-burn dread you love, and you rate Denis Villeneuve 8.4.",
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

async function bootTitle(page: Page, titleOverrides: Record<string, unknown> = {}) {
  await stubEventSource(page);
  await mockJson(page, "**/api/auth/me", SESSION_FOUNDER);
  await mockJson(page, "**/api/sync/status", {
    pipelineStage: "FeedReady", connectionStatus: "Active", lastDeltaSyncAt: null,
    lastFullReconcileAt: null, activeJob: null, recentJobs: [], outboxPending: 0, outboxDeadLetters: 0,
  });
  await mockJson(page, "**/api/titles/movie/1001", { ...TITLE, ...titleOverrides });
  await mockJson(page, "**/api/titles/movie/1001/providers", {
    region: "US", attribution: "JustWatch", tmdbWatchPage: "https://www.themoviedb.org/movie/1001/watch", providers: [],
  });
  // The "More like this" rail self-loads a page on mount; keep it deterministic and empty.
  await mockJson(page, "**/api/feed/more", { items: [], exhausted: true });
  await page.goto("/title/movie/1001");
  await expect(page.getByTestId("title-detail")).toBeVisible();
}

/**
 * Captures every call to `pattern`, answering 200 with `response` so optimistic updates settle as
 * successes. The body matters for the rating endpoint specifically: its response now carries the
 * after-rating verdict, and a bare `{}` is the "Reel never said anything about this" case.
 */
async function captureCalls(page: Page, pattern: string, response: unknown = {}): Promise<CapturedCall[]> {
  const calls: CapturedCall[] = [];
  await page.route(pattern, (route) => {
    const request = route.request();
    calls.push({
      method: request.method(),
      body: request.postData() ? request.postDataJSON() : undefined,
    });
    return route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify(response) });
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

  test("the modal carries the why-line, above the synopsis", async ({ page }) => {
    await bootTitle(page);

    const why = page.getByTestId("why-this");
    await expect(why).toHaveText("Shares the slow-burn dread you love, and you rate Denis Villeneuve 8.4.");

    // Above the synopsis, not below it: the personal reason leads, the studio blurb follows.
    const order = await page.evaluate(() => {
      const el = document.querySelector('[data-testid="why-this"]')!;
      const overview = Array.from(document.querySelectorAll("p")).find((p) =>
        p.textContent?.includes("A harbormaster keeps one last secret.")
      )!;
      return el.compareDocumentPosition(overview) & Node.DOCUMENT_POSITION_FOLLOWING ? "after" : "before";
    });
    expect(order).toBe("after");
  });

  test("no why-line renders nothing rather than boilerplate", async ({ page }) => {
    await bootTitle(page, { whyThis: null });
    await expect(page.getByTestId("why-this")).toHaveCount(0);
  });
});

/**
 * The after-rating moment. This is the emotional payload of the whole closed-loop programme and
 * also its biggest reputational risk: on the founder's live data most of these read "Reel said 9.8
 * · you said 3". The point of shipping it anyway is that a product which shows a confident number
 * on every card and never says how often that number was right is the version nobody should trust.
 */
test.describe("the after-rating verdict", () => {
  test("an over-promise names both numbers, when it was shown, and what happens next", async ({ page }) => {
    await bootTitle(page);
    const shownAt = new Date(Date.now() - 12 * 86_400_000).toISOString();
    await captureCalls(page, "**/api/titles/movie/1001/rating", {
      userRating: 5,
      watchedLogged: true,
      verdict: { predictedBefore: 8.4, surface: "FeedHero", shownAt, delta: 3.4 },
    });

    await page.getByRole("button", { name: /seen it \+ rate/i }).click();
    await page.getByTestId("rate-5").click();

    const toast = page.getByTestId("verdict-toast");
    await expect(toast).toBeVisible();
    await expect(toast).toContainText("Reel said 8.4 · you said 5");
    await expect(toast).toContainText("shown 12 days ago on tonight's picks");
    await expect(toast).toContainText("that goes into tonight's retrain");
    // It routes to the full scorecard rather than being the whole story.
    await expect(toast.getByRole("link", { name: /see the track record/i })).toBeVisible();
  });

  test("a near hit says so and promises no retrain it is not going to do", async ({ page }) => {
    await bootTitle(page);
    await captureCalls(page, "**/api/titles/movie/1001/rating", {
      userRating: 9,
      watchedLogged: true,
      verdict: { predictedBefore: 8.4, surface: null, shownAt: null, delta: -0.6 },
    });

    await page.getByRole("button", { name: /seen it \+ rate/i }).click();
    await page.getByTestId("rate-9").click();

    const toast = page.getByTestId("verdict-toast");
    await expect(toast).toBeVisible();
    await expect(toast).toContainText("Reel said 8.4 · you said 9");
    await expect(toast).toContainText("close enough to count");
    await expect(toast).not.toContainText("retrain");
    // No exposure behind it → no claim about when or where it was seen.
    await expect(toast).not.toContainText("shown");
  });

  test("no toast at all when Reel never committed to a number", async ({ page }) => {
    // The alternative would be a toast reading "Reel said — · you said 7", which is worse than
    // silence: it advertises that the product has no opinion at the exact moment it asked for one.
    await bootTitle(page);
    await captureCalls(page, "**/api/titles/movie/1001/rating", {
      userRating: 7,
      watchedLogged: true,
      verdict: null,
    });

    await page.getByRole("button", { name: /seen it \+ rate/i }).click();
    await page.getByTestId("rate-7").click();

    await expect(page.getByRole("button", { name: /seen it — you rated 7/i })).toBeVisible();
    await expect(page.getByTestId("verdict-toast")).toHaveCount(0);
  });

  test("the toast can be dismissed by hand", async ({ page }) => {
    await bootTitle(page);
    await captureCalls(page, "**/api/titles/movie/1001/rating", {
      userRating: 4,
      watchedLogged: true,
      verdict: { predictedBefore: 9.1, surface: "Digest", shownAt: null, delta: 5.1 },
    });

    await page.getByRole("button", { name: /seen it \+ rate/i }).click();
    await page.getByTestId("rate-4").click();

    const toast = page.getByTestId("verdict-toast");
    await expect(toast).toBeVisible();
    await toast.getByRole("button", { name: /dismiss/i }).click();
    await expect(toast).toHaveCount(0);
  });
});
