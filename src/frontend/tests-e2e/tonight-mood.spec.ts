import { test, expect, type Page } from "@playwright/test";
import { SESSION_FOUNDER, mockJson, stubEventSource } from "./helpers";
import { TRAKT_RECS_NONE } from "./home-chrome";

/**
 * The tonight-mood chips.
 *
 * What these specs actually defend is the restraint. The chips re-sort tonight's picks and do
 * nothing else — they save no filter, queue no rebuild, remove no card, and tapping the active one
 * puts the page back. Tonight's picks are a contract, and the whole value of a mood control is
 * that it can be used casually; a chip that quietly re-deals the page is a chip you learn not to
 * touch.
 */

const card = (n: number, extra: Record<string, unknown>) => ({
  titleId: `00000000-0000-0000-0000-00000000000${n}`,
  mediaType: "Movie",
  tmdbId: 5000 + n,
  name: `Hero ${n}`,
  year: 2024,
  runtimeMinutes: 110,
  posterPath: null,
  backdropPath: null,
  genres: ["drama"],
  predictedRating: 8,
  crowdRating: 7.5,
  crowdVotes: 9000,
  whyThis: `Why ${n}`,
  isReleased: true,
  ...extra,
});

// Model order is Bleak, Breezy, Dense — deliberately none of the mood orders, so any correct
// re-sort is visibly a re-sort rather than the order the server happened to send.
const BLEAK = card(1, {
  name: "Bleak", texture: { darkness: 0.9, pacing: 0.3, complexity: 0.8, humor: 0.0 }, mood: "heavy",
});
const BREEZY = card(2, {
  name: "Breezy", texture: { darkness: 0.1, pacing: 0.6, complexity: 0.2, humor: 0.9 }, mood: "light",
});
const DENSE = card(3, {
  name: "Dense", texture: { darkness: 0.5, pacing: 0.2, complexity: 0.95, humor: 0.1 }, mood: "cerebral",
});
const UNREAD = card(4, { name: "Unread" });

async function bootHome(page: Page, hero: unknown[]) {
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

/** Hero card names in DOM order. */
function heroNames(page: Page) {
  return page.locator('[data-testid="hero-card"] h2');
}

test.describe("tonight-mood chips", () => {
  test("the four chips sit above the hero", async ({ page }) => {
    await bootHome(page, [BLEAK, BREEZY, DENSE]);

    await expect(page.getByTestId("tonight-mood-chips")).toBeVisible();
    for (const id of ["light", "heavy", "switch-off", "cerebral"]) {
      await expect(page.getByTestId(`mood-${id}`)).toBeVisible();
    }
  });

  test("'something light' puts the light pick first without a request", async ({ page }) => {
    await bootHome(page, [BLEAK, BREEZY, DENSE]);
    await expect(heroNames(page)).toHaveText(["Bleak", "Breezy", "Dense"]);

    // Nothing may go to the server: the card already carries the mood and the scalars, and a
    // rebuild would break the stable-tonight contract the chip is designed around.
    const requests: string[] = [];
    page.on("request", (r) => {
      if (r.url().includes("/api/")) requests.push(r.url());
    });

    await page.getByTestId("mood-light").click();

    await expect(heroNames(page)).toHaveText(["Breezy", "Dense", "Bleak"]);
    expect(requests.filter((u) => u.includes("/api/feed"))).toHaveLength(0);
  });

  test("'something heavy' and 'something cerebral' each lead with their own", async ({ page }) => {
    await bootHome(page, [BREEZY, DENSE, BLEAK]);

    await page.getByTestId("mood-heavy").click();
    await expect(heroNames(page).first()).toHaveText("Bleak");

    await page.getByTestId("mood-cerebral").click();
    await expect(heroNames(page).first()).toHaveText("Dense");
  });

  test("'switch my brain off' ranks the undemanding first and the demanding last", async ({ page }) => {
    await bootHome(page, [BLEAK, BREEZY, DENSE]);

    await page.getByTestId("mood-switch-off").click();

    await expect(heroNames(page).first()).toHaveText("Breezy");
    // Dense is the least switch-off-able thing on the row, however unbleak it is.
    await expect(heroNames(page).last()).toHaveText("Dense");
  });

  test("it re-sorts and never filters — every card is still on the page", async ({ page }) => {
    await bootHome(page, [BLEAK, BREEZY, DENSE, UNREAD]);

    await page.getByTestId("mood-light").click();

    await expect(page.getByTestId("hero-card")).toHaveCount(4);
    await expect(page.getByTestId("mood-note")).toContainText("nothing has been removed");
  });

  test("a title with no extracted texture keeps its place rather than being ranked on a guess", async ({ page }) => {
    await bootHome(page, [BLEAK, BREEZY, DENSE, UNREAD]);

    await page.getByTestId("mood-light").click();

    // Below the measured cards, and still there. "We have not read this one yet" is not a verdict
    // about it, so it is neither promoted nor removed.
    await expect(heroNames(page).last()).toHaveText("Unread");
  });

  test("tapping the active chip puts the page back exactly as it was", async ({ page }) => {
    await bootHome(page, [BLEAK, BREEZY, DENSE]);

    await page.getByTestId("mood-light").click();
    await expect(heroNames(page)).toHaveText(["Breezy", "Dense", "Bleak"]);
    await expect(page.getByTestId("mood-light")).toHaveAttribute("aria-pressed", "true");

    await page.getByTestId("mood-light").click();

    await expect(heroNames(page)).toHaveText(["Bleak", "Breezy", "Dense"]);
    await expect(page.getByTestId("mood-light")).toHaveAttribute("aria-pressed", "false");
    await expect(page.getByTestId("mood-note")).toHaveCount(0);
  });

  test("the mood does not survive a reload — it is about tonight, not a standing preference", async ({ page }) => {
    await bootHome(page, [BLEAK, BREEZY, DENSE]);
    await page.getByTestId("mood-light").click();
    await expect(heroNames(page).first()).toHaveText("Breezy");

    await page.reload();
    await expect(page.getByTestId("hero-card")).toHaveCount(3);

    await expect(heroNames(page)).toHaveText(["Bleak", "Breezy", "Dense"]);
  });
});

test.describe("lens copy on the cards", () => {
  test("the hero leads with the hook, and the lens clause stays in the detail view", async ({ page }) => {
    const lensed = card(9, {
      name: "Lensed",
      texture: { darkness: 0.5, pacing: 0.5, complexity: 0.5, humor: 0.5 },
      hook: "The bleak procedural you have been circling.",
      lensClause: "A cartel task force, told with the moral flatness you rate highly.",
    });
    await bootHome(page, [lensed]);

    await expect(page.getByTestId("hero-hook")).toContainText("been circling");
    // The clause was the line that pushed the title out of a phone-sized card (F-4); it belongs to
    // the detail view, not the card.
    await expect(page.getByTestId("hero-lens-clause")).toHaveCount(0);
    await expect(page.getByTestId("hero-card")).not.toContainText("moral flatness");
    // The templated why-line stands down when copy exists — one voice per card.
    await expect(page.getByTestId("hero-why")).toHaveCount(0);
  });

  test("a hero with no copy keeps the templated why-line it always had", async ({ page }) => {
    await bootHome(page, [BLEAK]);

    await expect(page.getByTestId("hero-why")).toContainText("Why 1");
    await expect(page.getByTestId("hero-hook")).toHaveCount(0);
  });
});
