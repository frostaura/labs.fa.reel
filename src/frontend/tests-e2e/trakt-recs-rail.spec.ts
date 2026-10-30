import { test, expect, type Locator, type Page, type TestInfo } from "@playwright/test";
import { SESSION_FOUNDER, mockJson, stubEventSource } from "./helpers";
import { TRAKT_RECS_NONE, mockHomeChrome } from "./home-chrome";

/**
 * Home's "Trakt recommends" rail: Trakt's own recommendations, two rows deep.
 *
 * What these specs defend:
 * - Two rows are ONE scroller (a column-flowed grid), so the rows cannot drift apart: card 2 sits
 *   under card 1, card 3 opens the next column, and scrolling moves both rows by the same amount.
 * - It uses the width it has — many columns at 1440 — and never widens the page at 390.
 * - Keyboard: Tab walks the cards in rail order and the scroller follows focus.
 * - The placeholders are the rail's real height, so the answer landing moves nothing.
 * - Every empty state is said in words (Trakt down + Retry, no link), never an empty rail, and
 *   acting on a card takes it off the rail.
 */

const card = (n: number) => ({
  titleId: `00000000-0000-0000-0000-${String(5000 + n).padStart(12, "0")}`,
  mediaType: n % 2 === 0 ? "Show" : "Movie",
  tmdbId: 5000 + n,
  name: `Trakt pick ${n}`,
  year: 2024,
  runtimeMinutes: 105,
  posterPath: null,
  backdropPath: null,
  genres: ["drama"],
  predictedRating: n % 3 === 0 ? 7.6 : null,
  crowdRating: 7.1,
  crowdVotes: 12000,
  whyThis: "",
  isReleased: true,
  providers: [],
  traktRank: Math.ceil(n / 2),
});

const LIST = {
  ...TRAKT_RECS_NONE,
  returnedByTrakt: 30,
  hiddenAsSeen: 4,
  hiddenAsSetAside: 2,
  items: Array.from({ length: 24 }, (_, i) => card(i + 1)),
};

/** Home with no picks of its own, so the rail is on the first screen and asks at once. */
type RecsAnswer = Record<string, unknown> | ((url: URL) => Promise<Record<string, unknown>>);

async function bootHome(page: Page, recs: RecsAnswer): Promise<void> {
  await stubEventSource(page);
  await mockHomeChrome(page);
  await mockJson(page, "**/api/auth/me", SESSION_FOUNDER);
  await mockJson(page, "**/api/sync/status", {
    pipelineStage: "FeedReady", connectionStatus: "Active", lastDeltaSyncAt: null,
    lastFullReconcileAt: null, activeJob: null, recentJobs: [], outboxPending: 0, outboxDeadLetters: 0,
  });
  await mockJson(page, "**/api/feed", { generatedAt: new Date().toISOString(), hero: [], rows: [], lockedRowCount: 0 });
  await page.route(
    (url) => url.pathname === "/api/feed/trakt-recommendations",
    async (route) => {
      const body = typeof recs === "function" ? await recs(new URL(route.request().url())) : recs;
      await route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify(body) });
    }
  );
  await page.goto("/home");
}

const rail = (page: Page) => page.getByTestId("trakt-recs-rail");
const railCard = (page: Page, index: number) => rail(page).getByTestId("rec-card").nth(index);
const scroller = (page: Page) => rail(page).locator("div.grid-flow-col");

async function box(locator: Locator): Promise<{ x: number; y: number; width: number; height: number }> {
  const b = await locator.boundingBox();
  if (!b) throw new Error("not laid out");
  return b;
}

async function shot(page: Page, info: TestInfo, name: string): Promise<void> {
  // The heading clear of the sticky header, so the capture shows the rail as a reader meets it.
  await rail(page).evaluate((el) => window.scrollTo({ top: el.getBoundingClientRect().top + window.scrollY - 120, behavior: "instant" as ScrollBehavior }));
  await page.screenshot({ path: info.outputPath(`${name}-${info.project.name}.png`), fullPage: false });
}

test.describe("two rows at desktop width", () => {
  test("card 2 sits under card 1, card 3 opens the next column, and both rows scroll as one", async ({ page }, info) => {
    await bootHome(page, LIST);
    await expect(rail(page).getByTestId("rec-card")).toHaveCount(24);
    await expect(rail(page).getByRole("heading", { name: "Trakt recommends" })).toBeVisible();
    await expect(rail(page).getByTestId("rail-caption")).toHaveText(
      "Trakt's own picks for you, in Trakt's order. Not shown: 4 you've already seen and 2 you've saved or turned down."
    );

    const [one, two, three] = [await box(railCard(page, 0)), await box(railCard(page, 1)), await box(railCard(page, 2))];
    expect(Math.abs(two.x - one.x)).toBeLessThan(1);
    expect(two.y).toBeGreaterThan(one.y + one.height - 1);
    expect(three.x).toBeGreaterThan(one.x + one.width - 1);
    expect(Math.abs(three.y - one.y)).toBeLessThan(1);

    // It uses the width: at 1440 well over a phone's two columns are on screen at once.
    const view = await box(scroller(page));
    const columnsInView = (await rail(page).getByTestId("rec-card").evaluateAll((els, right) =>
      els.filter((el, i) => i % 2 === 0 && el.getBoundingClientRect().right <= (right as number) + 1).length, view.x + view.width));
    expect(columnsInView).toBeGreaterThanOrEqual(5);
    await shot(page, info, "desktop-1440");

    // One scroller: moving it moves the top and bottom rows by exactly the same amount.
    await scroller(page).evaluate((el) => el.scrollTo({ left: 600, behavior: "instant" as ScrollBehavior }));
    await expect.poll(() => scroller(page).evaluate((el) => el.scrollLeft)).toBeGreaterThan(0);
    const [oneAfter, twoAfter] = [await box(railCard(page, 0)), await box(railCard(page, 1))];
    expect(Math.abs(oneAfter.x - one.x - (twoAfter.x - two.x))).toBeLessThan(1);
    expect(oneAfter.x).toBeLessThan(one.x);
  });

  test("Tab walks the cards in rail order and the scroller follows focus", async ({ page }) => {
    await bootHome(page, LIST);
    await expect(rail(page).getByTestId("rec-card")).toHaveCount(24);

    await railCard(page, 0).getByTestId("rec-card-link").focus();
    const start = await scroller(page).evaluate((el) => el.scrollLeft);
    // Walk forward until focus is on a card well past the first screen (each card is a link plus its
    // actions, so this is a handful of presses per card); the cap keeps a broken order from spinning.
    let reached = -1;
    for (let i = 0; i < 120 && reached < 16; i++) {
      await page.keyboard.press("Tab");
      reached = await page.evaluate(() => {
        const cards = [...document.querySelectorAll('[data-testid="trakt-recs-rail"] [data-testid="rec-card"]')];
        return cards.findIndex((c) => c.contains(document.activeElement));
      });
      if (reached < 0) break;
    }
    expect(reached).toBeGreaterThanOrEqual(16);
    expect(await scroller(page).evaluate((el) => el.scrollLeft)).toBeGreaterThan(start);
    const focused = await box(railCard(page, reached));
    const view = await box(scroller(page));
    expect(focused.x).toBeGreaterThanOrEqual(view.x - 1);
    expect(focused.x + focused.width).toBeLessThanOrEqual(view.x + view.width + 1);
  });

  test("the placeholders are the rail's real height, so the answer lands without moving anything", async ({ page }) => {
    let release: () => void = () => {};
    const gate = new Promise<void>((resolve) => (release = resolve));
    await bootHome(page, async () => {
      await gate;
      return LIST;
    });

    await expect(rail(page).getByTestId("rail-skeleton").first()).toBeVisible();
    const loading = await box(rail(page));
    release();
    await expect(rail(page).getByTestId("rec-card")).toHaveCount(24);
    await expect(rail(page).getByTestId("rail-skeleton")).toHaveCount(0);
    const loaded = await box(rail(page));

    // Heading, caption line and two rows were all held: at 1440 the counts extend the caption's one
    // line, so the rail is the same box before and after.
    expect(Math.abs(loaded.height - loading.height)).toBeLessThan(2);
    expect(Math.abs(loaded.y - loading.y)).toBeLessThan(1);
  });

  test("Not for me takes the card off the rail and leaves the others where they are", async ({ page }) => {
    await bootHome(page, LIST);
    await expect(rail(page).getByTestId("rec-card")).toHaveCount(24);
    const posted: string[] = [];
    await page.route("**/api/titles/movie/5001/reactions/not_interested", (route) => {
      posted.push(route.request().url());
      return route.fulfill({ status: 200, contentType: "application/json", body: "{}" });
    });

    const target = railCard(page, 0);
    await target.hover();
    await target.getByTestId("quick-not-interested").click();

    await expect(rail(page).getByText("Trakt pick 1", { exact: true })).toHaveCount(0);
    await expect(rail(page).getByTestId("rec-card")).toHaveCount(23);
    expect(posted).toHaveLength(1);
  });
});

test.describe("states that are not a list", () => {
  test("Trakt down says so with Retry, and Retry asks Trakt again", async ({ page }, info) => {
    const asked: string[] = [];
    await bootHome(page, async (url) => {
      asked.push(url.search);
      return url.searchParams.get("retry") === "true"
        ? LIST
        : { ...TRAKT_RECS_NONE, state: "Unavailable", fetchedAt: null };
    });

    const notice = rail(page).getByTestId("trakt-recs-notice");
    await expect(notice).toHaveText(/Trakt isn't answering right now, so its recommendations can't be shown\./);
    await expect(notice).toHaveAttribute("data-state", "Unavailable");
    await expect(rail(page).getByTestId("rail-skeleton")).toHaveCount(0);
    await shot(page, info, "unavailable");

    await rail(page).getByTestId("trakt-recs-retry").click();
    await expect(rail(page).getByTestId("rec-card")).toHaveCount(24);
    expect(asked).toEqual(["", "?retry=true"]);
  });

  test("no Trakt link is said in words, with the heading kept", async ({ page }) => {
    await bootHome(page, { ...TRAKT_RECS_NONE, state: "NotLinked", fetchedAt: null });
    await expect(rail(page).getByTestId("trakt-recs-notice")).toHaveText(
      "This account has no Trakt link, so there are no Trakt recommendations to show."
    );
    await expect(rail(page).getByRole("heading", { name: "Trakt recommends" })).toBeVisible();
    await expect(rail(page).getByTestId("trakt-recs-retry")).toHaveCount(0);
  });

  test("an answer Reel filtered to nothing says what it filtered", async ({ page }) => {
    await bootHome(page, { ...TRAKT_RECS_NONE, returnedByTrakt: 12, hiddenAsSeen: 10, hiddenByConditions: 2 });
    await expect(rail(page).getByTestId("trakt-recs-notice")).toHaveText(
      "Nothing left to show from Trakt's 12: 10 you've already seen and 2 your conditions rule out."
    );
  });
});

test.describe("two rows on a phone", () => {
  test.use({ viewport: { width: 390, height: 844 } });

  test("still two rows, scrolling sideways inside the rail, and the page never scrolls sideways", async ({ page }, info) => {
    await bootHome(page, LIST);
    await expect(rail(page).getByTestId("rec-card")).toHaveCount(24);

    const [one, two] = [await box(railCard(page, 0)), await box(railCard(page, 1))];
    expect(Math.abs(two.x - one.x)).toBeLessThan(1);
    expect(two.y).toBeGreaterThan(one.y + one.height - 1);

    const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
    expect(overflow).toBeLessThanOrEqual(0);
    expect(await scroller(page).evaluate((el) => el.scrollWidth > el.clientWidth)).toBe(true);
    await shot(page, info, "phone-390");
  });
});
