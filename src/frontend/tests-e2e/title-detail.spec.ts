import { test, expect, type Page } from "@playwright/test";
import { SESSION_FOUNDER, mockJson, stubEventSource } from "./helpers";

/**
 * The title detail P1 surface (O-13, F-24, F-14, F-25, F-15, F-16): a full-screen sheet on a phone,
 * the trailer as an action rather than a disc on the title, one pitch line with the lens behind
 * "More", the prediction as a walk from a baseline, no noise bars, creators for a show, a Similar
 * rail that holds a row of skeletons and gives up into a Retry, and the region on where-to-watch
 * with a way to change it. Plus person pages that say what the person did on each title.
 */

const TITLE = {
  titleId: "00000000-0000-0000-0000-0000000000d1",
  mediaType: "Movie",
  tmdbId: 2001,
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
  crowdRating: 7.6,
  crowdVotes: 12000,
  posterPath: null,
  backdropPath: null,
  trailerUrl: "https://www.youtube.com/watch?v=dQw4w9WgXcQ",
  cast: [],
  directors: [],
  writers: [],
  prediction: {
    predictedRating: 8.2,
    scoredAt: "2026-10-25T10:00:00Z",
    contributions: [
      { feature: "directorAffinity", value: 0.8 },
      { feature: "genreAffinity", value: 0.4 },
      { feature: "eraAffinity", value: 0.03 },
      { feature: "runtime", value: -0.2 },
    ],
  },
  contributionDetails: [],
  userState: { isFullyWatched: false, plays: 0, userRating: null, savedForLater: false, notInterested: false, dropped: false },
};

const LENS = {
  source: "fresh",
  lens: "A slow tightening of the same dread you rate Prisoners for.",
  hook: "One more secret than the harbour can hold.",
  caution: null,
  anchors: [{ title: "Prisoners", relation: "same dread" }],
  mood: "tense",
  whenToWatch: null,
  generatedAt: "2026-10-25T10:00:00Z",
};

const similar = (n: number) => ({
  titleId: `00000000-0000-0000-0000-0000000005${n}0`,
  mediaType: "Movie",
  tmdbId: 3000 + n,
  name: `Similar ${n}`,
  year: 2020,
  runtimeMinutes: 100,
  posterPath: null,
  backdropPath: null,
  genres: ["drama"],
  predictedRating: 7.4,
  crowdRating: 7.0,
  crowdVotes: 800,
  whyThis: "Same director.",
  isReleased: true,
  evidence: [{ kind: "person", label: "Denis Villeneuve", role: "director" }],
});

async function bootTitle(
  page: Page,
  overrides: Record<string, unknown> = {},
  options: { more?: () => { hang: boolean; body: unknown } } = {}
) {
  await stubEventSource(page);
  await mockJson(page, "**/api/auth/me", SESSION_FOUNDER);
  await mockJson(page, "**/api/sync/status", {
    pipelineStage: "FeedReady", connectionStatus: "Active", lastDeltaSyncAt: null,
    lastFullReconcileAt: null, activeJob: null, recentJobs: [], outboxPending: 0, outboxDeadLetters: 0,
  });
  const title = { ...TITLE, ...overrides };
  const path = `/api/titles/${String(title.mediaType).toLowerCase()}/${title.tmdbId}`;
  await mockJson(page, `**${path}`, title);
  await mockJson(page, `**${path}/lens`, LENS);
  await mockJson(page, `**${path}/providers`, {
    region: "US", attribution: "Data from JustWatch via TMDB", tmdbWatchPage: "https://www.themoviedb.org/movie/2001/watch",
    providers: [
      { provider: "Netflix", logoPath: null, kinds: ["Flatrate"], linkKind: "direct", url: "https://www.netflix.com/search?q=x", displayPriority: 1 },
      { provider: "Philo", logoPath: null, kinds: ["Flatrate"], linkKind: "tmdb", url: "", displayPriority: 2 },
    ],
  });
  // The Similar rail's /feed/more. A "hang" answer is never fulfilled: the request stays out until
  // the client abandons it, exactly as a stuck rerank does.
  await page.route(
    (u) => u.pathname === "/api/feed/more",
    async (route) => {
      const answer = options.more?.() ?? { hang: false, body: { items: [], exhausted: true } };
      if (answer.hang) return;
      await route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify(answer.body) });
    }
  );
  await page.goto(`/title/${String(title.mediaType).toLowerCase()}/${title.tmdbId}`);
  await expect(page.getByTestId("title-detail")).toBeVisible();
}

test.describe("title detail", () => {
  test("on a phone it is a full-screen sheet, and the trailer is an action, not a disc on the title", async ({ browser }) => {
    const context = await browser.newContext({ viewport: { width: 390, height: 844 }, hasTouch: true, isMobile: true });
    const page = await context.newPage();
    await bootTitle(page);
    // Measured once the rise animation has settled (it animates `transform`).
    await expect
      .poll(async () => {
        const box = (await page.getByTestId("title-detail").boundingBox())!;
        return [Math.round(box.x), Math.round(box.y), Math.round(box.width), Math.round(box.height)];
      })
      .toEqual([0, 0, 390, 844]);
    await expect(page.getByRole("button", { name: "Play trailer" })).toHaveCount(0);
    const trailer = page.getByTestId("play-trailer");
    await expect(trailer).toBeVisible();
    const trailerBox = (await trailer.boundingBox())!;
    const titleBox = (await page.locator('[data-testid="title-detail"] h1').boundingBox())!;
    expect(trailerBox.y).toBeGreaterThanOrEqual(titleBox.y + titleBox.height);
    // Focus is trapped in the sheet: Tab never leaves it.
    for (let i = 0; i < 12; i++) {
      await page.keyboard.press("Tab");
      expect(await page.evaluate(() => Boolean(document.activeElement?.closest('[data-testid="title-detail"]')))).toBe(true);
    }
    await context.close();
  });

  test("one pitch line leads, and the lens is behind “More”", async ({ page }) => {
    await bootTitle(page);
    await expect(page.getByTestId("why-this")).toHaveText(TITLE.whyThis);
    await expect(page.getByTestId("lens-text")).toHaveCount(0);
    await expect(page.getByTestId("lens-more")).toHaveAttribute("aria-expanded", "false");
    await page.getByTestId("lens-more").click();
    await expect(page.getByTestId("lens-text")).toContainText("slow tightening");
    await expect(page.getByTestId("lens-hook")).toContainText("One more secret");
    await expect(page.getByTestId("lens-anchors")).toContainText("Prisoners");
  });

  test("with no why-line the lens's hook is the pitch line", async ({ page }) => {
    await bootTitle(page, { whyThis: null });
    await expect(page.getByTestId("why-this")).toHaveCount(0);
    await expect(page.getByTestId("lens-pitch")).toHaveText(LENS.hook);
    await page.getByTestId("lens-more").click();
    await expect(page.getByTestId("lens-hook")).toHaveCount(0);
  });

  test("bars under 0.05 are not drawn, and the walk from a baseline appears when the server sends one that adds up", async ({ page }) => {
    await bootTitle(page);
    await expect(page.getByText("+0.0")).toHaveCount(0);
    await expect(page.getByTestId("prediction-waterfall")).toHaveCount(0);

    await bootTitle(page, {
      prediction: {
        ...TITLE.prediction,
        waterfall: {
          baseline: 6.7,
          baselineLabel: "your average",
          steps: [
            { feature: "directorAffinity", value: 0.9 },
            { feature: "genreAffinity", value: 0.8 },
            { feature: "runtime", value: -0.2 },
          ],
        },
      },
    });
    const walk = page.getByTestId("prediction-waterfall");
    await expect(walk).toContainText("your average 6.7");
    await expect(walk).toContainText("8.2 for you");
    await expect(walk.getByTestId("waterfall-step")).toHaveCount(3);
    await expect(walk.getByTestId("waterfall-step").last()).toContainText("−0.2");
    // The bars are each push scaled to the largest one, not rating points: a signed decimal on a
    // bar read as points and contradicted the walk above it ("+1.0" beside a walk step of +0.9).
    const bars = page.getByTestId("contribution-bar");
    await expect(bars.first()).toBeVisible();
    for (const text of await bars.allInnerTexts()) expect(text).not.toMatch(/[+−-]\d/);
  });

  // A deep-linked title has no feed or Saved read in the cache to borrow the band from, so it
  // carries its own (prediction.expectedAbsoluteError, change request to WS-5).
  test("a deep-linked title's chip carries the band the title payload sends", async ({ page }) => {
    await bootTitle(page, { prediction: { ...TITLE.prediction, expectedAbsoluteError: 1.3 } });
    await expect(page.getByTestId("predicted-band").first()).toHaveText("±1.3");

    await bootTitle(page, { prediction: { ...TITLE.prediction, expectedAbsoluteError: null } });
    await expect(page.getByTestId("predicted-rating").first()).toBeVisible();
    await expect(page.getByTestId("predicted-band")).toHaveCount(0);
  });

  test("a show lists its creators", async ({ page }) => {
    await bootTitle(page, {
      mediaType: "Show",
      tmdbId: 2002,
      creators: [{ personId: "00000000-0000-0000-0000-00000000c0de", name: "Vince Gilligan", profilePath: null, userRating: null }],
    });
    await expect(page.getByText("Creator", { exact: true })).toBeVisible();
    await expect(page.getByText("Vince Gilligan")).toBeVisible();
  });

  test("the Similar rail holds a row of skeletons, gives up into a Retry after its time limit, and its cards carry evidence", async ({ page }) => {
    test.setTimeout(45_000);
    let hang = true;
    let asked = 0;
    await bootTitle(page, {}, {
      more: () => {
        asked += 1;
        return { hang, body: { items: [similar(1), similar(2)], exhausted: true } };
      },
    });
    await expect(page.getByTestId("rail-skeleton")).toHaveCount(5);
    // The rail loads as it nears the viewport (it sits under the synopsis), then its clock starts.
    await page.getByTestId("rail-skeleton").first().scrollIntoViewIfNeeded();
    await expect.poll(() => asked).toBe(1);
    // 8 s limit, then the Retry tile — and no automatic retries behind it.
    await expect(page.getByTestId("rail-retry")).toBeVisible({ timeout: 12_000 });
    await expect(page.getByTestId("rail-skeleton")).toHaveCount(0);
    expect(asked).toBe(1);
    hang = false;
    await page.getByTestId("rail-retry-button").click();
    await expect(page.getByTestId("rec-card")).toHaveCount(2);
    await expect(page.getByTestId("rec-card").first().getByTestId("card-evidence-chip")).toContainText("Denis Villeneuve");
  });

  test("where-to-watch states the region with a way to change it, and a subscription with no link is still a chip", async ({ page }) => {
    await bootTitle(page);
    await expect(page.getByTestId("where-to-watch-region")).toContainText("in US");
    await expect(page.getByTestId("where-to-watch-region-edit")).toHaveAttribute("href", "/settings#region");
    const philo = page.getByTestId("provider-chip").filter({ hasText: "Philo" });
    await expect(philo).toHaveAttribute("data-link-kind", "none");
    await expect(philo.locator("a")).toHaveCount(0);
    expect(await philo.evaluate((el) => el.tagName)).toBe("DIV");
    await expect(page.getByTestId("provider-chip").first()).toContainText("Netflix");
  });
});

test.describe("person page", () => {
  test("says what the person did on each title, and only what the API says", async ({ page }) => {
    await stubEventSource(page);
    await mockJson(page, "**/api/auth/me", SESSION_FOUNDER);
    await mockJson(page, "**/api/sync/status", {
      pipelineStage: "FeedReady", connectionStatus: "Active", lastDeltaSyncAt: null,
      lastFullReconcileAt: null, activeJob: null, recentJobs: [], outboxPending: 0, outboxDeadLetters: 0,
    });
    const film = (n: number, extra: Record<string, unknown>) => ({
      titleId: `00000000-0000-0000-0000-0000000007${n}0`, mediaType: "Movie", tmdbId: 4000 + n, name: `Film ${n}`,
      year: 2010 + n, posterPath: null, userRating: null, predictedRating: 7.1, ...extra,
    });
    await mockJson(page, "**/api/people/00000000-0000-0000-0000-0000000000aa", {
      id: "00000000-0000-0000-0000-0000000000aa", name: "Michael Mann", department: "Directing", profilePath: null,
      userRating: null, derivedAffinity: null, ratedTitleCount: 0,
      filmography: [
        film(1, { roles: ["Director", "Writer"] }),
        film(2, { roles: ["Actor"], character: "Himself" }),
        film(3, {}),
      ],
    });
    await page.goto("/person/00000000-0000-0000-0000-0000000000aa");
    const roles = page.getByTestId("filmography-role");
    await expect(roles).toHaveText(["Director · Writer", "as Himself"]);
  });

  // R1-41: PersonEndpoints holds out credits the conditions exclude and counts them; the page used
  // to drop the count, so a director's horror films simply vanished. The line names the count and
  // opens the conditions editor right there; a person whose every credit is held out still says so.
  async function bootPerson(page: Page, filmography: unknown[], hiddenByConditions: number) {
    await stubEventSource(page);
    await mockJson(page, "**/api/auth/me", SESSION_FOUNDER);
    await mockJson(page, "**/api/sync/status", {
      pipelineStage: "FeedReady", connectionStatus: "Active", lastDeltaSyncAt: null,
      lastFullReconcileAt: null, activeJob: null, recentJobs: [], outboxPending: 0, outboxDeadLetters: 0,
    });
    await mockJson(page, "**/api/settings/filters", { excludeGenres: ["horror"], languages: ["en"] });
    await mockJson(page, "**/api/search/filter-chips", { minTitles: 50, chips: [] });
    await mockJson(page, "**/api/people/00000000-0000-0000-0000-0000000000bb", {
      id: "00000000-0000-0000-0000-0000000000bb", name: "John Carpenter", department: "Directing", profilePath: null,
      userRating: null, derivedAffinity: null, ratedTitleCount: 0, hiddenByConditions, filmography,
    });
    await page.goto("/person/00000000-0000-0000-0000-0000000000bb");
    await expect(page.getByRole("heading", { level: 1, name: "John Carpenter" })).toBeVisible();
  }

  const credit = (n: number) => ({
    titleId: `00000000-0000-0000-0000-0000000008${n}0`, mediaType: "Movie", tmdbId: 5000 + n, name: `Credit ${n}`,
    year: 2001 + n, posterPath: null, userRating: null, predictedRating: 6.8, roles: ["Director"],
  });

  test("counts the credits the conditions hide and opens the conditions from that line", async ({ page }) => {
    await bootPerson(page, [credit(1), credit(2)], 3);
    await expect(page.getByTestId("person-hidden")).toContainText("3 more of their titles are hidden by your conditions.");
    await page.getByTestId("person-hidden-change").click();
    await expect(page.getByTestId("conditions-editor")).toBeVisible();
  });

  test("a person whose every credit is held out says so instead of showing nothing", async ({ page }) => {
    await bootPerson(page, [], 1);
    await expect(page.getByTestId("person-hidden")).toContainText("Their 1 title in Reel is hidden by your conditions.");
  });

  test("no hidden line when the conditions hide nothing", async ({ page }) => {
    await bootPerson(page, [credit(1)], 0);
    await expect(page.getByText("Credit 1")).toBeVisible();
    await expect(page.getByTestId("person-hidden")).toHaveCount(0);
  });
});
