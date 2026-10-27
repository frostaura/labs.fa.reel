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
