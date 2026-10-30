import { test, expect, type Page } from "@playwright/test";
import { SESSION_FOUNDER, mockJson, stubEventSource } from "./helpers";
import { TRAKT_RECS_NONE } from "./home-chrome";

/**
 * Quick actions on the HERO.
 *
 * The headline pick was the one card in the app you could not act on without navigating into the
 * detail modal and back — a strange property for the card the whole screen is built around, and the
 * reason the busiest surface in the product contributed nothing to the loop. It now carries the same
 * three actions the rail cards do, at hero scale, plus the rating popover both surfaces share.
 *
 * What these pin: every action fires without navigating (the whole-card link still works everywhere
 * the buttons are not), the reason picker is the same four-way one the rails offer, and a rating
 * posts to the existing endpoint and removes the card the same way a dismissal does.
 */

const hero = (n: number) => ({
  titleId: `00000000-0000-0000-0000-00000000000${n}`,
  mediaType: "Movie",
  tmdbId: 4000 + n,
  name: `Hero ${n}`,
  year: 2026,
  runtimeMinutes: 110,
  posterPath: null,
  backdropPath: null,
  genres: ["drama"],
  predictedRating: 8.2,
  crowdRating: 7.5,
  crowdVotes: 9000,
  whyThis: "Explores a heist crew coming apart, like Heat.",
  isReleased: true,
  confidence: "confident",
  floorUsed: 7.5,
});

async function bootHome(page: Page) {
  await stubEventSource(page);
  await mockJson(page, "**/api/auth/me", SESSION_FOUNDER);
  await mockJson(page, "**/api/sync/status", {
    pipelineStage: "FeedReady", connectionStatus: "Active", lastDeltaSyncAt: null,
    lastFullReconcileAt: null, activeJob: null, recentJobs: [], outboxPending: 0, outboxDeadLetters: 0,
  });
  await mockJson(page, "**/api/feed/continue-watching", []);
  await mockJson(page, "**/api/feed/more", { items: [], exhausted: true });
  await mockJson(page, "**/api/feed/genres", { genres: [] });
  await mockJson(page, "**/api/feed/trakt-recommendations", TRAKT_RECS_NONE);
  await mockJson(page, "**/api/digest/current", { weekStart: null, generatedAt: null, items: [] });
  await mockJson(page, "**/api/feed", {
    generatedAt: new Date().toISOString(),
    hero: [hero(1), hero(2), hero(3)],
    rows: [],
    lockedRowCount: 0,
  });
  await page.goto("/home");
  await expect(page.getByTestId("hero-card")).toHaveCount(3);
}

test.describe("hero quick actions", () => {
  test("the hero carries all three actions, revealed on hover like the rail cards", async ({ page }) => {
    await bootHome(page);

    const card = page.getByTestId("hero-card").filter({ hasText: "Hero 1" });
    const notForMe = card.getByTestId("quick-not-interested");

    await expect(card.getByTestId("card-quick-actions")).toHaveCount(1);
    await expect(notForMe).toHaveCSS("opacity", "0");
    await card.hover();
    await expect(notForMe).toHaveCSS("opacity", "1");
    await expect(card.getByTestId("quick-save")).toHaveCSS("opacity", "1");
    await expect(card.getByTestId("quick-rate")).toHaveCSS("opacity", "1");
  });

  test("Not for me acts instead of navigating, and takes only that hero slide", async ({ page }) => {
    await bootHome(page);

    const calls: unknown[] = [];
    await page.route("**/api/titles/movie/4001/reactions/not_interested", (route) => {
      calls.push(route.request().postDataJSON());
      return route.fulfill({ status: 200, contentType: "application/json", body: "{}" });
    });

    const card = page.getByTestId("hero-card").filter({ hasText: "Hero 1" });
    await card.hover();
    await card.getByTestId("quick-not-interested").click();

    await expect(page).toHaveURL(/\/home/);
    await expect(card).toHaveCount(0);
    await expect(page.getByTestId("hero-card")).toHaveCount(2);
    expect(calls).toEqual([{ reason: "not-for-me" }]);
  });

  test("the four-way reason picker is the same one the rails offer", async ({ page }) => {
    await bootHome(page);

    const calls: unknown[] = [];
    await page.route("**/api/titles/movie/4002/reactions/not_interested", (route) => {
      calls.push(route.request().postDataJSON());
      return route.fulfill({ status: 200, contentType: "application/json", body: "{}" });
    });

    const card = page.getByTestId("hero-card").filter({ hasText: "Hero 2" });
    await card.hover();
    await card.getByTestId("quick-not-interested").click({ button: "right" });

    const picker = page.getByTestId("quick-reason-picker");
    await expect(picker).toBeVisible();
    await expect(picker.getByRole("button")).toHaveText([
      "Just not for me",
      "Not my genre",
      "Seen enough like this",
      "Wrong tone",
    ]);
    expect(calls).toHaveLength(0);

    await picker.getByTestId("quick-reason-tone").click();
    await expect(card).toHaveCount(0);
    expect(calls).toEqual([{ reason: "tone" }]);
  });

  test("rating from the hero posts the score, logs the watch, and retires the card", async ({ page }) => {
    await bootHome(page);

    const ratings: unknown[] = [];
    await page.route("**/api/titles/movie/4003/rating", (route) => {
      ratings.push(route.request().postDataJSON());
      return route.fulfill({
        status: 200, contentType: "application/json", body: JSON.stringify({ userRating: 5 }),
      });
    });

    const card = page.getByTestId("hero-card").filter({ hasText: "Hero 3" });
    await card.hover();
    await card.getByTestId("quick-rate").click();

    const picker = page.getByTestId("quick-rate-picker");
    await expect(picker).toBeVisible();
    await expect(picker.getByRole("button")).toHaveCount(10);

    // Reel predicted 8.2 for this one — the whole point of rating in place is that the disagreement
    // gets recorded at the moment it is felt, not hours later when Trakt syncs it back.
    await picker.getByTestId("quick-rate-5").click();

    await expect(page).toHaveURL(/\/home/);
    expect(ratings).toEqual([{ rating: 5, markWatched: true }]);
    // A rating is an interaction footprint, so the title leaves every suggestion surface.
    await expect(card).toHaveCount(0);
    await expect(page.getByTestId("hero-card")).toHaveCount(2);
  });

  test("Save takes the hero pick to the queue without navigating", async ({ page }) => {
    await bootHome(page);

    const saves: string[] = [];
    await page.route("**/api/titles/movie/4001/reactions/save_for_later", (route) => {
      saves.push(route.request().url());
      return route.fulfill({ status: 200, contentType: "application/json", body: "{}" });
    });

    const card = page.getByTestId("hero-card").filter({ hasText: "Hero 1" });
    await card.hover();
    await card.getByTestId("quick-save").click();

    await expect(page).toHaveURL(/\/home/);
    expect(saves).toHaveLength(1);
    await expect(card).toHaveCount(0);
  });
});
