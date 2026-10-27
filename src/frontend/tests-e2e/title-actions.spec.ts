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
  // No lens for this title: the on-demand read answers "none" instead of reaching the tripwire.
  await mockJson(page, "**/api/titles/movie/1001/lens", {
    source: "none", lens: null, hook: null, caution: null, anchors: [], mood: null, whenToWatch: null, generatedAt: null,
  });
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

  // R1-40: ReactionEndpoints logs a play only for a movie with no play yet; an already-watched movie
  // gets the rating alone, so the popover may promise the play only when it will happen.
  for (const { plays, logs } of [{ plays: 0, logs: true }, { plays: 1, logs: false }, { plays: 4, logs: false }]) {
    test(`the rating note promises a logged play only when the movie has none (plays ${plays})`, async ({ page }) => {
      await bootTitle(page, { userState: { ...TITLE.userState, plays, isFullyWatched: plays > 0 } });
      await page.getByRole("button", { name: /seen it \+ rate/i }).click();
      const note = page.getByTestId("rating-trakt-note");
      if (logs) {
        await expect(note).toHaveText("Also logs a play on your Trakt history — cleared if you remove the rating.");
      } else {
        await expect(note).toHaveText("Ratings sync to Trakt — this movie is already in your history, so no play is added.");
      }
    });
  }

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
    // "Next", not "tonight" (R1-40): one rating does not make RetrainGate owe a retrain.
    await expect(toast).toContainText("that goes into your next retrain");
    await expect(toast).not.toContainText("tonight's retrain");
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

/**
 * The title modal's own failure and navigation states (F-8, F-9, O+3).
 */
test.describe("title detail states", () => {
  async function bootShell(page: Page) {
    await stubEventSource(page);
    await mockJson(page, "**/api/auth/me", SESSION_FOUNDER);
    await mockJson(page, "**/api/sync/status", {
      pipelineStage: "FeedReady", connectionStatus: "Active", lastDeltaSyncAt: null,
      lastFullReconcileAt: null, activeJob: null, recentJobs: [], outboxPending: 0, outboxDeadLetters: 0,
    });
    await mockJson(page, "**/api/feed/more", { items: [], exhausted: true });
    await mockJson(page, "**/api/feed", { generatedAt: new Date().toISOString(), hero: [], rows: [], lockedRowCount: 0 });
    await mockJson(page, "**/api/feed/continue-watching", []);
    await mockJson(page, "**/api/feed/genres", { genres: [] });
    await mockJson(page, "**/api/digest/current", { weekStart: null, generatedAt: null, items: [] });
  }

  test("a deep link's Close goes to Home and replaces the entry, so Back does not reopen it", async ({ page }) => {
    await bootTitle(page);
    await mockJson(page, "**/api/feed", { generatedAt: new Date().toISOString(), hero: [], rows: [], lockedRowCount: 0 });
    await page.getByRole("button", { name: "Close" }).first().click();
    await expect(page).toHaveURL(/\/home$/);
    await expect(page.getByTestId("title-detail")).toHaveCount(0);
    const historyLength = await page.evaluate(() => window.history.length);
    // Replaced, not pushed: the title entry is gone from the stack.
    expect(historyLength).toBeLessThanOrEqual(2);
  });

  test("Escape on a deep link also lands on Home", async ({ page }) => {
    await bootTitle(page);
    await page.keyboard.press("Escape");
    await expect(page).toHaveURL(/\/home$/);
  });

  test("a 404 says not found, not an endless shimmer, and offers no pointless retry", async ({ page }) => {
    await bootShell(page);
    await mockJson(page, "**/api/titles/movie/999999999", { error: "not found" }, 404);
    await page.goto("/title/movie/999999999");
    await expect(page.getByTestId("title-not-found")).toBeVisible();
    await expect(page.getByTestId("title-detail").locator(".reel-shimmer")).toHaveCount(0);
    await expect(page.getByTestId("title-retry")).toHaveCount(0);
  });

  test("a 404 renders within one second of its response", async ({ page }) => {
    await bootShell(page);
    let answeredAt = 0;
    await page.route(
      (u) => u.pathname === "/api/titles/movie/999999999",
      (route) => {
        answeredAt = Date.now();
        return route.fulfill({ status: 404, contentType: "application/json", body: "{}" });
      }
    );
    await page.goto("/title/movie/999999999");
    await expect(page.getByTestId("title-not-found")).toBeVisible();
    expect(Date.now() - answeredAt).toBeLessThan(1000);
  });

  /**
   * The acceptance is "404s show not-found within 1 s", timed from the navigation, not from the
   * response. This times the client's whole share — route change, request, render — from an
   * in-app navigation with the server answering at once. The server's share is NOT covered here:
   * a cold TMDB miss took 1.19 s on the slot, so end to end the second holds only once the server
   * negative-caches the miss (WS-5). Replace the instant answer with the measured server time when
   * that lands.
   */
  test("a 404 renders within one second of the navigation that asked for it", async ({ page }) => {
    await bootShell(page);
    await mockJson(page, "**/api/titles/movie/999999999", { error: "not found" }, 404);
    await page.goto("/home");
    await expect(page.getByRole("heading", { name: "Tonight's picks" })).toBeVisible();
    const started = await page.evaluate(() => {
      const t = performance.now();
      history.pushState({}, "", "/title/movie/999999999");
      dispatchEvent(new PopStateEvent("popstate"));
      return t;
    });
    await expect(page.getByTestId("title-not-found")).toBeVisible();
    const elapsed = await page.evaluate((t) => performance.now() - t, started);
    expect(elapsed).toBeLessThan(1000);
  });

  test("a server error offers a retry that recovers", async ({ page }) => {
    await bootShell(page);
    await mockJson(page, "**/api/titles/movie/1001/providers", {
      region: "US", attribution: "JustWatch", tmdbWatchPage: "https://www.themoviedb.org/movie/1001/watch", providers: [],
    });
    let calls = 0;
    await page.route(
      (u) => u.pathname === "/api/titles/movie/1001",
      (route) => {
        calls += 1;
        return calls === 1
          ? route.fulfill({ status: 500, contentType: "application/json", body: "{}" })
          : route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify(TITLE) });
      }
    );
    await page.goto("/title/movie/1001");
    await expect(page.getByTestId("title-error")).toBeVisible();
    await expect(page.getByTestId("title-not-found")).toHaveCount(0);
    // A 500 is an answer: the copy claims neither silence nor a duration nobody has measured.
    await expect(page.getByTestId("title-error")).toContainText("Couldn't load this title.");
    await expect(page.getByTestId("title-error")).not.toContainText("didn't answer");
    await expect(page.getByTestId("title-error")).not.toContainText("usually");
    await page.getByTestId("title-retry").click();
    await expect(page.getByRole("heading", { level: 1, name: "Quiet Harbor" })).toBeVisible();
  });

  test("a rated title's header leads with your rating and keeps the prediction as a note", async ({ page }) => {
    await bootTitle(page, {
      userState: { ...TITLE.userState, userRating: 9, isFullyWatched: true, plays: 1 },
      prediction: { predictedRating: 8.2, contributions: [], scoredAt: "2026-10-01T00:00:00Z" },
    });
    await expect(page.getByTestId("title-your-rating")).toHaveText("You rated 9");
    await expect(page.getByTestId("title-prediction-note")).toHaveText("Reel predicted 8.2 for you");
    // The big "for you" badge is the prediction speaking over you; it is gone from the header.
    await expect(page.getByTestId("title-detail").locator("h1 ~ *").filter({ hasText: /for you/i })).toHaveCount(0);
  });

  test("an unrated title keeps the prediction badge and no note", async ({ page }) => {
    await bootTitle(page, {
      prediction: { predictedRating: 8.2, contributions: [], scoredAt: "2026-10-01T00:00:00Z" },
    });
    await expect(page.getByTestId("title-your-rating")).toHaveCount(0);
    await expect(page.getByTestId("title-prediction-note")).toHaveCount(0);
    await expect(page.getByTestId("title-detail").getByText("8.2", { exact: false }).first()).toBeVisible();
  });
});

test.describe("person page states", () => {
  test("a 404 says not found instead of Loading… forever", async ({ page }) => {
    await stubEventSource(page);
    await mockJson(page, "**/api/auth/me", SESSION_FOUNDER);
    await mockJson(page, "**/api/people/00000000-0000-0000-0000-000000000000", {}, 404);
    await page.goto("/person/00000000-0000-0000-0000-000000000000");
    await expect(page.getByTestId("person-not-found")).toBeVisible();
    await expect(page.getByText("Loading…")).toHaveCount(0);
  });

  test("a server error offers a retry", async ({ page }) => {
    await stubEventSource(page);
    await mockJson(page, "**/api/auth/me", SESSION_FOUNDER);
    await mockJson(page, "**/api/people/00000000-0000-0000-0000-000000000000", {}, 503);
    await page.goto("/person/00000000-0000-0000-0000-000000000000");
    await expect(page.getByTestId("person-error")).toBeVisible();
    await expect(page.getByTestId("person-retry")).toBeVisible();
  });
});
