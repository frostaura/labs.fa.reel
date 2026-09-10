import { test, expect, type Locator, type Page } from "@playwright/test";
import { SESSION_FOUNDER, mockJson, stubEventSource } from "./helpers";

/**
 * Two rows of tonight's picks.
 *
 * The founder asked to see more of his picks at once, and the honest way to give him that is more
 * rows rather than more cards — the build hands out five hero picks and raising that number is a
 * ranking change, not a layout one. So the hero becomes a column-flowed grid: rank 1 top-left, rank
 * 2 directly under it, rank 3 opening the next column.
 *
 * What these specs defend is the ONE thing that shape can get wrong. Two rows must not be two
 * carousels. They are a single scroll container laid out as a grid, which is why they cannot drift
 * apart — there is nothing to keep in sync. A JS-synced pair would pass a screenshot and fail the
 * moment someone flicked it, so the scroll assertion below is the load-bearing one.
 *
 * The other half is that nothing regresses for anyone who cannot use two rows: a phone has no room
 * for them, and the free tier is capped at three hero cards server-side with a `/more` that returns
 * exhausted, so a second row there would be a half-empty grid advertising the cap.
 */

const card = (n: number) => ({
  titleId: `00000000-0000-0000-0000-00000000000${n}`,
  mediaType: "Movie",
  tmdbId: 7000 + n,
  name: `Pick ${n}`,
  year: 2026,
  runtimeMinutes: 110,
  posterPath: null,
  backdropPath: null,
  genres: ["drama"],
  predictedRating: 8.1,
  crowdRating: 7.9,
  crowdVotes: 40000,
  whyThis: `Why ${n}`,
  isReleased: true,
});

async function bootHome(page: Page, hero: unknown[], tier = "Founder"): Promise<void> {
  await stubEventSource(page);
  await mockJson(page, "**/api/auth/me", { ...SESSION_FOUNDER, tier });
  await mockJson(page, "**/api/sync/status", {
    pipelineStage: "FeedReady", connectionStatus: "Active", lastDeltaSyncAt: null,
    lastFullReconcileAt: null, activeJob: null, recentJobs: [], outboxPending: 0, outboxDeadLetters: 0,
  });
  await mockJson(page, "**/api/feed/continue-watching", []);
  // Pinned to exhausted so the row is exactly the seed: the geometry assertions below count
  // columns, and a top-up landing mid-measurement would move the cards under them.
  await mockJson(page, "**/api/feed/more", { items: [], exhausted: true });
  await mockJson(page, "**/api/feed/genres", { genres: [] });
  await mockJson(page, "**/api/digest/current", { weekStart: null, generatedAt: null, items: [] });
  await mockJson(page, "**/api/settings/filters", {
    excludeGenres: [], includeGenres: [], excludeKeywords: [], maturityCeiling: null,
    minPredictedRating: 6, languages: ["en"], minCrowdRating: null, minYear: null, maxYear: null,
  });
  await mockJson(page, "**/api/feed", {
    generatedAt: new Date().toISOString(),
    hero,
    rows: [],
    lockedRowCount: 0,
  });
  await page.goto("/home");
  await expect(page.getByTestId("hero-card")).toHaveCount(hero.length);
}

const FIVE = [card(1), card(2), card(3), card(4), card(5)];

function heroCard(page: Page, index: number): Locator {
  return page.getByTestId("hero-card").nth(index);
}

/** A card's viewport box, as numbers the assertions can compare. */
async function box(locator: Locator): Promise<{ x: number; y: number; width: number; height: number }> {
  const b = await locator.boundingBox();
  if (!b) throw new Error("hero card has no box — it is not laid out");
  return b;
}

test.describe("two-row hero at desktop width", () => {
  test("rank 2 sits under rank 1 and rank 3 opens the next column", async ({ page }) => {
    await bootHome(page, FIVE);

    // The grid shape itself, so a future refactor that quietly reverts to a flex row fails loudly
    // rather than passing on a coincidence of positions.
    await expect(page.getByTestId("hero-carousel")).toHaveClass(/md:grid-rows-2/);
    await expect(page.getByTestId("hero-carousel")).toHaveClass(/grid-flow-col/);

    const [one, two, three] = [await box(heroCard(page, 0)), await box(heroCard(page, 1)), await box(heroCard(page, 2))];

    // Column-major: same column, different row.
    expect(Math.abs(two.x - one.x)).toBeLessThanOrEqual(1);
    expect(two.y).toBeGreaterThan(one.y + one.height / 2);

    // ...and the third pick starts the second column, level with the first.
    expect(Math.abs(three.y - one.y)).toBeLessThanOrEqual(1);
    expect(three.x).toBeGreaterThan(one.x + one.width / 2);
  });

  test("DOM order is still rank order, so the row reads and tabs top-to-bottom by rank", async ({ page }) => {
    await bootHome(page, FIVE);

    // The grid places column-major; the DOM does not know that. Rank order, DOM order and Tab
    // order stay the same thing — which is what lets tonight-mood.spec assert order the way it does.
    await expect(page.locator('[data-testid="hero-card"] h2')).toHaveText([
      "Pick 1", "Pick 2", "Pick 3", "Pick 4", "Pick 5",
    ]);
  });

  test("the dots count columns, not cards — five picks in two rows are three dots", async ({ page }) => {
    await bootHome(page, FIVE);

    await expect(page.getByTestId("hero-dots").locator("span")).toHaveCount(3);
  });

  test("one scroll container: a horizontal scroll moves both rows by the same delta", async ({ page }) => {
    await bootHome(page, FIVE);

    const carousel = page.getByTestId("hero-carousel");
    const before = { one: await box(heroCard(page, 0)), two: await box(heroCard(page, 1)) };

    await carousel.hover();
    await page.mouse.wheel(600, 0);
    // Drop the hover before measuring: a hovered card carries a scale transform, which would move
    // its box for a reason that has nothing to do with scrolling.
    await page.mouse.move(2, 2);

    // Polled rather than waited on: `scroll-smooth` animates, and so does the hover transform
    // unwinding. Both settle; the assertion is what they settle to.
    await expect
      .poll(async () => Math.round(before.one.x - (await box(heroCard(page, 0))).x))
      .toBeGreaterThan(100);

    const after = { one: await box(heroCard(page, 0)), two: await box(heroCard(page, 1)) };
    const deltaTop = before.one.x - after.one.x;
    const deltaBottom = before.two.x - after.two.x;

    // The whole design claim in one line. Two scrollers synced by JS drift here; one container cannot.
    expect(Math.abs(deltaTop - deltaBottom)).toBeLessThanOrEqual(1);
    // And the rows did not swap places while moving.
    expect(after.two.y).toBeGreaterThan(after.one.y + after.one.height / 2);
  });
});

test.describe("one row where two rows would be wrong", () => {
  test("a phone gets a single row — rank 2 is to the right of rank 1, not under it", async ({ page }) => {
    await page.setViewportSize({ width: 390, height: 844 });
    await bootHome(page, FIVE);

    const [one, two] = [await box(heroCard(page, 0)), await box(heroCard(page, 1))];

    expect(Math.abs(two.y - one.y)).toBeLessThanOrEqual(1);
    expect(two.x).toBeGreaterThan(one.x + one.width / 2);
    // One row means one column per card, and the dots follow.
    await expect(page.getByTestId("hero-dots").locator("span")).toHaveCount(5);
  });

  test("the free tier keeps its three-card single row", async ({ page }) => {
    await bootHome(page, [card(1), card(2), card(3)], "Free");

    await expect(page.getByTestId("hero-card")).toHaveCount(3);
    // Not the grid at all: free never pages, so a second row could only ever be half empty.
    await expect(page.getByTestId("hero-carousel")).not.toHaveClass(/grid-rows-2/);
    await expect(page.getByTestId("hero-carousel")).toHaveClass(/^flex /);

    const [one, two] = [await box(heroCard(page, 0)), await box(heroCard(page, 1))];
    expect(Math.abs(two.y - one.y)).toBeLessThanOrEqual(1);
    expect(two.x).toBeGreaterThan(one.x + one.width / 2);
  });
});
