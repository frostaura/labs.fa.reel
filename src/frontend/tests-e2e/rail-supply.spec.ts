import { test, expect, type Page } from "@playwright/test";
import { SESSION_FOUNDER, mockJson, stubEventSource } from "./helpers";
import { mockHomeChrome } from "./home-chrome";

/**
 * Rails that come up short or empty say why (HREV1-RET-07, WS-5 task 9).
 *
 * Live: a Thriller rail under "Shows" returned 0 cards with `supply.hiddenByConditions` 251, Romance 0
 * with 5,386, and "More like this" was empty (with no supply) for 27 of 50 anchors — and every one of
 * them vanished without a word, because the rail returned nothing once it was empty and no component
 * read `supply` at all.
 */

const card = (n: number) => ({
  titleId: `00000000-0000-0000-0000-0000000009${String(n).padStart(2, "0")}`, mediaType: "Movie", tmdbId: 9000 + n,
  name: `Card ${n}`, year: 2020, runtimeMinutes: 100, posterPath: null, backdropPath: null, genres: ["romance"],
  predictedRating: 7.1, crowdRating: 7.0, crowdVotes: 900, whyThis: "Because of what you rate highly.", isReleased: true,
});

const FILTERS = {
  excludeGenres: ["documentary"], includeGenres: [], excludeKeywords: [], maturityCeiling: null,
  minPredictedRating: 6, languages: ["en"], minCrowdRating: null, minYear: null, maxYear: null,
};

async function bootHome(page: Page, answer: (body: { row: string; genre?: string | null; excludeTitleIds?: string[] }) => unknown) {
  await stubEventSource(page);
  await mockHomeChrome(page, { filters: FILTERS });
  await mockJson(page, "**/api/auth/me", SESSION_FOUNDER);
  await mockJson(page, "**/api/sync/status", {
    pipelineStage: "FeedReady", connectionStatus: "Active", lastDeltaSyncAt: null,
    lastFullReconcileAt: null, activeJob: null, recentJobs: [], outboxPending: 0, outboxDeadLetters: 0,
  });
  await mockJson(page, "**/api/feed/genres", { genres: [{ slug: "thriller", label: "Thriller" }, { slug: "romance", label: "Romance" }, { slug: "western", label: "Western" }] });
  await mockJson(page, "**/api/feed", { generatedAt: new Date().toISOString(), hero: [], rows: [], lockedRowCount: 0 });
  await page.route(
    (u) => u.pathname === "/api/feed/more",
    (route) => route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify(answer(route.request().postDataJSON())) })
  );
  await page.goto("/home");
}

test.describe("a short or empty rail says why (RET-07)", () => {
  test("an empty rail names what the conditions hide, and See why opens the editor", async ({ page }) => {
    await bootHome(page, (body) => {
      if (body.row !== "genre") return { items: [], exhausted: true };
      if (body.genre === "thriller") return { items: [], exhausted: true, supply: { hiddenByConditions: 251, alreadySeen: 48, clearingFloor: 44 } };
      if (body.genre === "romance") {
        // A short rail: three cards, then dry — with what the conditions held back.
        const first = (body.excludeTitleIds ?? []).length === 0;
        return first
          ? { items: [card(1), card(2), card(3)], exhausted: false, supply: { hiddenByConditions: 5386, alreadySeen: 0, clearingFloor: 3 } }
          : { items: [], exhausted: true, supply: { hiddenByConditions: 5386, alreadySeen: 0, clearingFloor: 3 } };
      }
      // No count from the server: the rail still says it is empty, and claims no cause.
      return { items: [], exhausted: true, supply: null };
    });

    const thriller = page.locator('[data-testid="genre-rail"][data-genre="thriller"]');
    await thriller.scrollIntoViewIfNeeded();
    await expect(thriller.getByTestId("rail-supply")).toHaveText("Nothing left here — your conditions hide 251 titles · you've seen 48See why");
    await expect(thriller.getByTestId("rail-skeleton")).toHaveCount(0);

    const romance = page.locator('[data-testid="genre-rail"][data-genre="romance"]');
    await romance.scrollIntoViewIfNeeded();
    await expect(romance.getByTestId("rec-card")).toHaveCount(3);
    await expect(romance.getByTestId("rail-supply")).toContainText("Your conditions hide 5,386 more titles");

    const western = page.locator('[data-testid="genre-rail"][data-genre="western"]');
    await western.scrollIntoViewIfNeeded();
    await expect(western.getByTestId("rail-supply")).toContainText("Nothing left here that you haven't seen and your conditions allow");

    await thriller.getByTestId("rail-supply-see-why").click();
    await expect(page.getByTestId("conditions-scope")).toBeVisible();
  });
});

test("an empty 'More like this' says so on the title page, and opens the conditions from there (RET-07)", async ({ page }) => {
  await stubEventSource(page);
  await mockHomeChrome(page, { filters: FILTERS });
  await mockJson(page, "**/api/auth/me", SESSION_FOUNDER);
  await mockJson(page, "**/api/sync/status", {
    pipelineStage: "FeedReady", connectionStatus: "Active", lastDeltaSyncAt: null,
    lastFullReconcileAt: null, activeJob: null, recentJobs: [], outboxPending: 0, outboxDeadLetters: 0,
  });
  await mockJson(page, "**/api/titles/movie/155", {
    titleId: "00000000-0000-0000-0000-000000000155", mediaType: "Movie", tmdbId: 155, name: "The Dark Knight", year: 2008,
    overview: "Batman raises the stakes.", runtimeMinutes: 152, genres: ["action"], status: "Released", traktRating: 8.5, traktVotes: 90000,
    crowdRating: 8.5, crowdVotes: 90000, posterPath: null, backdropPath: null, trailerUrl: null, cast: [], directors: [], writers: [],
    whyThis: null, prediction: null, contributionDetails: [],
    userState: { isFullyWatched: false, plays: 0, userRating: null, savedForLater: false, notInterested: false, dropped: false },
  });
  await mockJson(page, "**/api/titles/movie/155/lens", { source: "none", lens: null, hook: null, caution: null, anchors: [], mood: null, whenToWatch: null, generatedAt: null });
  await mockJson(page, "**/api/titles/movie/155/providers", { region: "US", attribution: "x", tmdbWatchPage: "https://www.themoviedb.org/movie/155/watch", providers: [] });
  // The Similar rail today: empty, and no supply.
  await mockJson(page, "**/api/feed/more", { items: [], exhausted: true, supply: null });
  await page.goto("/title/movie/155");
  await expect(page.getByTestId("title-detail")).toBeVisible();

  const note = page.getByTestId("rail-supply");
  await note.scrollIntoViewIfNeeded();
  await expect(note).toContainText("Nothing left here that you haven't seen and your conditions allow");
  await expect(page.getByText("More like this")).toBeVisible();
  await page.getByTestId("rail-supply-see-why").click();
  await expect(page.getByTestId("conditions-scope")).toBeVisible();
});

test("a Free account's title page shows no 'More like this' — its tier pages no rails, so no cause is offered (RET-07)", async ({ page }) => {
  // Free's POST /feed/more is always { items: [], exhausted: true } with no supply (FeedEndpoints:
  // the tier has no paginated rows). The rail must not blame seen titles or conditions for that.
  await stubEventSource(page);
  await mockHomeChrome(page, { filters: FILTERS });
  await mockJson(page, "**/api/auth/me", { ...SESSION_FOUNDER, tier: "Free" });
  await mockJson(page, "**/api/sync/status", {
    pipelineStage: "FeedReady", connectionStatus: "Active", lastDeltaSyncAt: null,
    lastFullReconcileAt: null, activeJob: null, recentJobs: [], outboxPending: 0, outboxDeadLetters: 0,
  });
  await mockJson(page, "**/api/titles/movie/155", {
    titleId: "00000000-0000-0000-0000-000000000155", mediaType: "Movie", tmdbId: 155, name: "The Dark Knight", year: 2008,
    overview: "Batman raises the stakes.", runtimeMinutes: 152, genres: ["action"], status: "Released", traktRating: 8.5, traktVotes: 90000,
    crowdRating: 8.5, crowdVotes: 90000, posterPath: null, backdropPath: null, trailerUrl: null, cast: [], directors: [], writers: [],
    whyThis: null, prediction: null, contributionDetails: [],
    userState: { isFullyWatched: false, plays: 0, userRating: null, savedForLater: false, notInterested: false, dropped: false },
  });
  await mockJson(page, "**/api/titles/movie/155/lens", { source: "none", lens: null, hook: null, caution: null, anchors: [], mood: null, whenToWatch: null, generatedAt: null });
  await mockJson(page, "**/api/titles/movie/155/providers", { region: "US", attribution: "x", tmdbWatchPage: "https://www.themoviedb.org/movie/155/watch", providers: [] });
  let moreCalls = 0;
  await page.route(
    (u) => u.pathname === "/api/feed/more",
    (route) => {
      moreCalls += 1;
      return route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify({ items: [], exhausted: true }) });
    }
  );
  await page.goto("/title/movie/155");
  await expect(page.getByTestId("title-detail")).toBeVisible();
  await page.getByTestId("where-to-watch").scrollIntoViewIfNeeded();
  // Let the rail reach the viewport and its first page come back.
  await expect.poll(() => moreCalls).toBeGreaterThan(0);

  await expect(page.getByTestId("rail-supply")).toHaveCount(0);
  await expect(page.getByText("Nothing left here that you haven't seen and your conditions allow")).toHaveCount(0);
  await expect(page.getByText("More like this", { exact: true })).toHaveCount(0);
});

/**
 * HREV5-RET-01: live, POST /feed/more {row: similar, anchor: Master and Commander} came back empty
 * with supply null and anchoredSupply {gateHeldBack: 146, belowBar: 78}. Nothing read
 * anchoredSupply, so the rail said "your conditions allow" and linked to them — when 146 unseen
 * titles inside the conditions were refused by the shared-evidence gate.
 */
test.describe("an empty 'More like this' names what its window lost (HREV5-RET-01)", () => {
  async function bootTitle(page: Page, anchoredSupply: unknown) {
    await stubEventSource(page);
    await mockHomeChrome(page, { filters: FILTERS });
    await mockJson(page, "**/api/auth/me", SESSION_FOUNDER);
    await mockJson(page, "**/api/sync/status", {
      pipelineStage: "FeedReady", connectionStatus: "Active", lastDeltaSyncAt: null,
      lastFullReconcileAt: null, activeJob: null, recentJobs: [], outboxPending: 0, outboxDeadLetters: 0,
    });
    await mockJson(page, "**/api/titles/movie/8619", {
      titleId: "00000000-0000-0000-0000-000000008619", mediaType: "Movie", tmdbId: 8619,
      name: "Master and Commander: The Far Side of the World", year: 2003, overview: "A frigate gives chase.",
      runtimeMinutes: 138, genres: ["adventure"], status: "Released", traktRating: 7.4, traktVotes: 9000,
      crowdRating: 7.4, crowdVotes: 9000, posterPath: null, backdropPath: null, trailerUrl: null, cast: [], directors: [], writers: [],
      whyThis: null, prediction: null, contributionDetails: [],
      userState: { isFullyWatched: false, plays: 0, userRating: null, savedForLater: false, notInterested: false, dropped: false },
    });
    await mockJson(page, "**/api/titles/movie/8619/lens", { source: "none", lens: null, hook: null, caution: null, anchors: [], mood: null, whenToWatch: null, generatedAt: null });
    await mockJson(page, "**/api/titles/movie/8619/providers", { region: "US", attribution: "x", tmdbWatchPage: "https://www.themoviedb.org/movie/8619/watch", providers: [] });
    await mockJson(page, "**/api/feed/more", { items: [], exhausted: true, supply: null, anchoredSupply });
    await page.goto("/title/movie/8619");
    await expect(page.getByTestId("title-detail")).toBeVisible();
    const note = page.getByTestId("rail-supply");
    await note.scrollIntoViewIfNeeded();
    return note;
  }

  test("the gate refused near matches: it says so, quotes the bar, and offers no conditions link", async ({ page }) => {
    const note = await bootTitle(page, { gateHeldBack: 146, belowBar: 78 });
    await expect(note).toHaveText(
      "Nothing else shares a person, keyword or theme with Master and Commander: The Far Side of the World closely enough (146 near matches) · 78 more are under your 6.0 bar"
    );
    await expect(page.getByText("your conditions allow")).toHaveCount(0);
    await expect(page.getByTestId("rail-supply-see-why")).toHaveCount(0);
  });

  test("only the bar emptied it: it names the bar and opens the editor that holds it", async ({ page }) => {
    const note = await bootTitle(page, { gateHeldBack: 0, belowBar: 12 });
    await expect(note).toContainText("Nothing left like Master and Commander: The Far Side of the World above your 6.0 bar — 12 close matches are under it");
    await page.getByTestId("rail-supply-see-why").click();
    await expect(page.getByTestId("conditions-scope")).toBeVisible();
  });
});
