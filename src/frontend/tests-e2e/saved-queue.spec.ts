import { test, expect, type Page } from "@playwright/test";
import { SESSION_FOUNDER, mockJson, stubEventSource } from "./helpers";

/**
 * The Saved shelf as a queue: the sort control, the pinned "watch tonight" card and its provider
 * ladder, "not tonight", and the loop-closing post-watch prompt settling a promise out loud.
 *
 * Fully API-mocked, so it pins the CLIENT's behaviour — which order it asks the server for, which
 * card it pins, what it sends when the user answers the prompt — while the server's own comparator
 * and unrated guard are pinned by the backend suites.
 */

const EVENING_BUDGET = 150;

function savedItem(overrides: Record<string, unknown> & { titleId: string; tmdbId: number; name: string }) {
  return {
    mediaType: "Movie",
    year: 2021,
    posterPath: null,
    runtimeMinutes: 100,
    predictedRating: 7.0,
    crowdRating: 7.4,
    crowdVotes: 12000,
    providers: [],
    availableNow: true,
    savedAt: "2026-08-01T00:00:00Z",
    daysSaved: 39,
    fitsEveningBudget: true,
    onManagedList: true,
    watchTonight: false,
    watchTonightReason: null,
    ...overrides,
  };
}

// streaming, 8.4, 1h58 → the pinned pick
const PINNED = savedItem({
  titleId: "00000000-0000-0000-0000-0000000000a1",
  tmdbId: 1001,
  name: "Sicario",
  predictedRating: 8.4,
  runtimeMinutes: 118,
  providers: [{ name: "Netflix", logoPath: null }],
  watchTonight: true,
  watchTonightReason:
    "Highest-predicted thing in your queue that's on Netflix and fits tonight — 8.4 for you (±1.9), 1h 58m against your 2h 30m.",
});

// streaming, 7.6, 1h32 → the runner-up "Not tonight" advances to
const RUNNER_UP = savedItem({
  titleId: "00000000-0000-0000-0000-0000000000a2",
  tmdbId: 1002,
  name: "Coherence",
  predictedRating: 7.6,
  runtimeMinutes: 92,
  providers: [{ name: "Prime Video", logoPath: null }],
});

// the best number in the queue and unwatchable tonight — must never be pinned
const UNAVAILABLE = savedItem({
  titleId: "00000000-0000-0000-0000-0000000000a3",
  tmdbId: 1003,
  name: "Le Samourai",
  predictedRating: 9.2,
  runtimeMinutes: 105,
  availableNow: false,
  daysSaved: 120,
  listSync: "onList",
});

const TONIGHT_ORDER = [PINNED, RUNNER_UP, UNAVAILABLE];
const PREDICTED_ORDER = [UNAVAILABLE, PINNED, RUNNER_UP];

/** Records the `sort` each /api/saved request asked for and answers with that order. */
async function mockSaved(page: Page): Promise<string[]> {
  const sorts: string[] = [];
  await page.route(
    (url) => url.pathname === "/api/saved",
    (route) => {
      const sort = new URL(route.request().url()).searchParams.get("sort") ?? "tonight";
      sorts.push(sort);
      return route.fulfill({
        status: 200,
        contentType: "application/json",
        body: JSON.stringify({
          managedListUrl: "https://trakt.tv/lists/42",
          sort,
          eveningBudgetMinutes: EVENING_BUDGET,
          expectedAbsoluteError: 1.9,
          unscoredRemaining: 0,
          items: sort === "predicted" ? PREDICTED_ORDER : TONIGHT_ORDER,
        }),
      });
    }
  );
  return sorts;
}

async function bootSaved(page: Page, recentlyWatched: unknown[] = []) {
  await stubEventSource(page);
  await mockJson(page, "**/api/auth/me", SESSION_FOUNDER);
  await mockJson(page, "**/api/sync/status", {
    pipelineStage: "FeedReady", connectionStatus: "Active", lastDeltaSyncAt: null,
    lastFullReconcileAt: null, activeJob: null, recentJobs: [], outboxPending: 0, outboxDeadLetters: 0,
  });
  await mockJson(page, "**/api/saved/recently-watched", {
    expectedAbsoluteError: 1.9,
    items: recentlyWatched,
  });
  // The pinned card's provider ladder loads on mount, from the same component the title page uses.
  await mockJson(page, "**/api/titles/movie/1001/providers", {
    region: "US",
    attribution: "Streaming data by JustWatch",
    tmdbWatchPage: "https://www.themoviedb.org/movie/1001/watch",
    providers: [{ provider: "Netflix", logoPath: null, kinds: ["Flatrate"], url: "https://netflix.com/search?q=Sicario", linkKind: "direct" }],
  });
  await mockJson(page, "**/api/titles/movie/1002/providers", {
    region: "US",
    attribution: "Streaming data by JustWatch",
    tmdbWatchPage: "https://www.themoviedb.org/movie/1002/watch",
    providers: [{ provider: "Prime Video", logoPath: null, kinds: ["Flatrate"], url: "https://www.themoviedb.org/movie/1002/watch", linkKind: "tmdb" }],
  });
}

test.describe("saved queue", () => {
  test("pins tonight's pick, shows why, and opens its provider ladder", async ({ page }) => {
    await bootSaved(page);
    await mockSaved(page);
    await page.goto("/saved");

    const pinned = page.getByTestId("watch-tonight");
    await expect(pinned).toBeVisible();
    await expect(pinned).toContainText("Sicario");
    // Not the 9.2 — the best number in the queue is not streaming, and the pin's claim is that you
    // can actually watch it tonight.
    await expect(pinned).not.toContainText("Le Samourai");
    await expect(page.getByTestId("watch-tonight-reason")).toContainText("8.4 for you (±1.9)");
    await expect(page.getByTestId("watch-tonight-reason")).toContainText("1h 58m against your 2h 30m");

    // The provider ladder is the real WhereToWatch, fed by the title's own providers call.
    await expect(pinned.getByTestId("where-to-watch")).toBeVisible();
    await expect(pinned.getByTestId("provider-chip").filter({ hasText: "Netflix" })).toHaveCount(1);
    await expect(pinned).toContainText("Streaming data by JustWatch");

    // The pinned title is not repeated in the grid below it; the other two are.
    await expect(page.getByTestId("saved-card")).toHaveCount(2);
    await expect(page.getByTestId("saved-card").filter({ hasText: "Sicario" })).toHaveCount(0);

    // Cards carry the band, the runtime and the availability the old poster grid never showed.
    const unavailableCard = page.getByTestId("saved-card").filter({ hasText: "Le Samourai" });
    await expect(unavailableCard).toContainText("9.2 ± 1.9");
    await expect(unavailableCard).toContainText("1h 45m");
    await expect(unavailableCard).toContainText("not streaming");
    await expect(unavailableCard).toContainText("saved 120 days ago");
    await expect(unavailableCard).toContainText("on Up Next");
  });

  test("switching the sort re-asks the server and re-orders the queue", async ({ page }) => {
    await bootSaved(page);
    const sorts = await mockSaved(page);
    await page.goto("/saved");
    await expect(page.getByTestId("watch-tonight")).toBeVisible();

    await page.getByTestId("saved-sort-predicted").click();

    // The order is the server's to decide, so the client must ASK for it rather than re-sorting.
    await expect.poll(() => sorts).toContain("predicted");
    expect(sorts[0]).toBe("tonight");
    await expect(page.getByTestId("saved-sort-predicted")).toHaveAttribute("aria-pressed", "true");

    // Predicted order leads with the 9.2; the pin is unchanged, because it is a property of the
    // queue rather than of the order being looked at.
    // Auto-retrying, because the re-render lands after the request the poll above observed.
    await expect(page.getByTestId("saved-card").first()).toContainText("Le Samourai");
    await expect(page.getByTestId("watch-tonight")).toContainText("Sicario");
  });

  test("'Not tonight' advances to the next candidate and then stops", async ({ page }) => {
    await bootSaved(page);
    await mockSaved(page);
    await page.goto("/saved");
    await expect(page.getByTestId("watch-tonight")).toContainText("Sicario");

    await page.getByTestId("not-tonight").click();
    await expect(page.getByTestId("watch-tonight")).toContainText("Coherence");

    // Two candidates, both declined: the shelf says so rather than re-offering the first.
    await expect(page.getByTestId("not-tonight")).toHaveCount(0);
  });

  test("the post-watch prompt settles the promise and states the delta", async ({ page }) => {
    await bootSaved(page, [
      {
        titleId: "00000000-0000-0000-0000-0000000000b1",
        mediaType: "Movie",
        tmdbId: 2001,
        name: "Sicario",
        year: 2015,
        posterPath: null,
        predictedRating: 8.4,
        watchedAt: "2026-09-06T00:00:00Z",
      },
    ]);
    await mockSaved(page);

    const ratingCalls: unknown[] = [];
    await page.route("**/api/titles/movie/2001/rating", (route) => {
      ratingCalls.push(route.request().postDataJSON());
      return route.fulfill({
        status: 200,
        contentType: "application/json",
        body: JSON.stringify({ userRating: 6, watchedLogged: false }),
      });
    });

    await page.goto("/saved");

    const strip = page.getByTestId("post-watch-strip");
    await expect(strip).toBeVisible();
    await expect(strip).toContainText("You watched Sicario. Reel guessed 8.4 — what did you actually think?");

    await page.getByTestId("post-watch-rate-6").click();

    // The number it committed to, the number it got, and no softening in between.
    await expect(page.getByTestId("post-watch-settled")).toContainText("Reel said 8.4 · you said 6 — noted");
    // Already watched — Reel must not log a second play back to Trakt from this prompt.
    expect(ratingCalls).toEqual([{ rating: 6, markWatched: false }]);
  });

  test("a dismissed prompt stays dismissed across a reload", async ({ page }) => {
    const watched = {
      titleId: "00000000-0000-0000-0000-0000000000b2",
      mediaType: "Movie",
      tmdbId: 2002,
      name: "Coherence",
      year: 2013,
      posterPath: null,
      predictedRating: 7.6,
      watchedAt: "2026-09-06T00:00:00Z",
    };
    await bootSaved(page, [watched]);
    await mockSaved(page);
    await page.goto("/saved");

    await expect(page.getByTestId("post-watch-strip")).toBeVisible();
    await page.getByTestId("post-watch-dismiss").click();
    await expect(page.getByTestId("post-watch-strip")).toHaveCount(0);

    await page.reload();
    await expect(page.getByTestId("watch-tonight")).toBeVisible();
    await expect(page.getByTestId("post-watch-strip")).toHaveCount(0);
  });

  test("an empty queue explains itself instead of rendering a blank grid", async ({ page }) => {
    await bootSaved(page);
    await page.route(
      (url) => url.pathname === "/api/saved",
      (route) =>
        route.fulfill({
          status: 200,
          contentType: "application/json",
          body: JSON.stringify({
            managedListUrl: null,
            sort: "tonight",
            eveningBudgetMinutes: EVENING_BUDGET,
            expectedAbsoluteError: null,
            unscoredRemaining: 0,
            items: [],
          }),
        })
    );
    await page.goto("/saved");

    await expect(page.getByTestId("saved-empty")).toContainText("Nothing in the queue yet");
    await expect(page.getByTestId("watch-tonight")).toHaveCount(0);
  });
});

/**
 * Saved tells the truth about itself (G-3, G-15). The list line says "syncing…" only for a card
 * whose write-back the server reports pending, and a failed read is a failure with a retry — never
 * the "Nothing in the queue yet" empty state.
 */
test.describe("saved states", () => {
  async function serveSaved(page: Page, items: unknown[]) {
    await mockJson(page, "**/api/saved", {
      managedListUrl: "https://trakt.tv/lists/42",
      sort: "tonight",
      eveningBudgetMinutes: EVENING_BUDGET,
      expectedAbsoluteError: 1.9,
      unscoredRemaining: 0,
      items,
    });
  }

  const shelf = (listSync: Array<string | undefined>, onManagedList: boolean[]) =>
    listSync.map((state, i) =>
      savedItem({
        titleId: `00000000-0000-0000-0000-0000000000b${i}`,
        tmdbId: 2000 + i,
        name: `Saved ${i}`,
        // Not streaming, so nothing is pinned and every card renders in the grid.
        availableNow: false,
        onManagedList: onManagedList[i]!,
        ...(state === undefined ? {} : { listSync: state }),
      })
    );

  test("no card says syncing when no write-back is pending", async ({ page }) => {
    await bootSaved(page);
    // The founder's shelf in miniature: on the list, off the list, and an older server's card.
    await serveSaved(page, shelf(["onList", "offList", "offList", undefined, undefined], [true, false, false, true, false]));
    await page.goto("/saved");
    await expect(page.getByTestId("saved-card")).toHaveCount(5);
    await expect(page.getByText("syncing…")).toHaveCount(0);
    // The older server's cards say nothing about the list, whatever their legacy flag says.
    await expect(page.getByTestId("saved-list-sync")).toHaveText(["on Up Next", "not on Up Next", "not on Up Next"]);
  });

  test("a pending write-back is the only thing that says syncing", async ({ page }) => {
    await bootSaved(page);
    await serveSaved(page, shelf(["pending", "onList", "pending"], [true, true, true]));
    await page.goto("/saved");
    await expect(page.getByTestId("saved-card")).toHaveCount(3);
    await expect(page.getByText("syncing…")).toHaveCount(2);
  });

  test("loading holds a skeleton, and a failed read shows an error with a working retry", async ({ page }) => {
    await bootSaved(page);
    let calls = 0;
    let release: () => void = () => {};
    const held = new Promise<void>((resolve) => (release = resolve));
    await page.route(
      (url) => url.pathname === "/api/saved",
      async (route) => {
        calls += 1;
        if (calls === 1) {
          await held;
          return route.fulfill({ status: 500, contentType: "application/json", body: JSON.stringify({ error: "boom" }) });
        }
        return route.fulfill({
          status: 200,
          contentType: "application/json",
          body: JSON.stringify({
            managedListUrl: null, sort: "tonight", eveningBudgetMinutes: EVENING_BUDGET,
            expectedAbsoluteError: 1.9, unscoredRemaining: 0, items: TONIGHT_ORDER,
          }),
        });
      }
    );
    await page.goto("/saved");
    await expect(page.getByTestId("saved-loading")).toBeVisible();
    release();

    await expect(page.getByTestId("saved-error")).toBeVisible();
    await expect(page.getByTestId("saved-empty")).toHaveCount(0);
    await expect(page.getByTestId("saved-error")).toContainText("Couldn't load your queue");

    await page.getByTestId("saved-retry").click();
    await expect(page.getByTestId("watch-tonight")).toContainText("Sicario");
    await expect(page.getByTestId("saved-error")).toHaveCount(0);
  });

  test("an empty shelf is the empty state, not an error", async ({ page }) => {
    await bootSaved(page);
    await serveSaved(page, []);
    await page.goto("/saved");
    await expect(page.getByTestId("saved-empty")).toBeVisible();
    await expect(page.getByTestId("saved-error")).toHaveCount(0);
  });
});
