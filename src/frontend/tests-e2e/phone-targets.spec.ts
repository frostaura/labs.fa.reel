import { test, expect, type Page } from "@playwright/test";
import { SESSION_FOUNDER, mockJson, stubEventSource } from "./helpers";
import { mockHomeChrome } from "./home-chrome";

/**
 * The phone-target acceptance probe for WS-10A's routes (IREV3S-08, O-15): at 390×844 on a touch
 * device, no interactive element inside the page's own content is smaller than 44 × 44.
 *
 * Scoped to `main`: the header pill and the tab bar are the shell's (WS-9), and they are probed on
 * the shell's own routes. An element counts if it is visible and focusable or clickable by role —
 * links, buttons, inputs, selects and anything with a non-negative tabindex. Inline text links
 * inside a sentence are the one exception WCAG 2.5.8 makes, and the one exception here: a link
 * whose parent is a paragraph of running text (`p`) is allowed its line height.
 */

const PHONE = { viewport: { width: 390, height: 844 }, hasTouch: true, isMobile: true };

async function smallTargets(page: Page, rootSelector = "main"): Promise<string[]> {
  return page.evaluate((rootSelector) => {
    const main = document.querySelector(rootSelector);
    if (!main) return [`<no ${rootSelector}>`];
    const selector = 'a[href], button, input, select, textarea, [role="button"], [tabindex]:not([tabindex="-1"])';
    const out: string[] = [];
    for (const el of Array.from(main.querySelectorAll<HTMLElement>(selector))) {
      const r = el.getBoundingClientRect();
      const style = getComputedStyle(el);
      if (r.width === 0 || r.height === 0 || style.visibility === "hidden" || style.display === "none") continue;
      if (el.closest('[aria-hidden="true"]')) continue;
      // The inline-link exception: a link set in a run of text, not a control of its own.
      if (el.tagName === "A" && el.parentElement?.tagName === "P" && style.display === "inline") continue;
      // A screen-reader-only overlay link (the hero card's) is covered by the card it fills.
      if (r.width >= 44 && r.height >= 44) continue;
      const label = el.getAttribute("aria-label") ?? el.getAttribute("data-testid") ?? el.textContent?.trim().slice(0, 30) ?? el.tagName;
      out.push(`${el.tagName.toLowerCase()} "${label}" ${Math.round(r.width)}×${Math.round(r.height)}`);
    }
    return out;
  }, rootSelector);
}

const card = (n: number) => ({
  titleId: `00000000-0000-0000-0000-00000000000${n}`,
  mediaType: "Movie",
  tmdbId: 1000 + n,
  name: `Pick ${n}`,
  year: 2026,
  runtimeMinutes: 110,
  posterPath: null,
  backdropPath: null,
  genres: ["drama"],
  predictedRating: 8.1,
  crowdRating: 7.5,
  crowdVotes: 9000,
  whyThis: "Explores a heist crew coming apart, like Heat.",
  isReleased: true,
  providers: [{ name: "Netflix", logoPath: null }],
});

const FILTERS = {
  excludeGenres: ["documentary", "horror", "romance"], includeGenres: [], excludeKeywords: [], maturityCeiling: null,
  minPredictedRating: 6, languages: ["en"], minCrowdRating: 6, minYear: 2000, maxYear: null,
};

async function boot(page: Page) {
  await stubEventSource(page);
  await mockHomeChrome(page, { filters: FILTERS });
  await mockJson(page, "**/api/auth/me", SESSION_FOUNDER);
  await mockJson(page, "**/api/sync/status", {
    pipelineStage: "FeedReady", connectionStatus: "Active", lastDeltaSyncAt: null,
    lastFullReconcileAt: null, activeJob: null, recentJobs: [], outboxPending: 0, outboxDeadLetters: 0,
  });
}

test.describe("phone targets on WS-10A routes", () => {
  test("Home has no interactive element under 44 px", async ({ browser }) => {
    const context = await browser.newContext(PHONE);
    const page = await context.newPage();
    await boot(page);
    await mockJson(page, "**/api/feed", {
      generatedAt: new Date().toISOString(),
      hero: [card(1), card(2)],
      rows: [{ kind: "because-you-loved", anchorTitleId: card(1).titleId, anchorName: "Anchor", title: null, items: [card(3), card(4), card(5)] }],
      lockedRowCount: 0,
    });
    await page.goto("/home");
    await expect(page.getByTestId("rec-card")).toHaveCount(3);
    expect(await smallTargets(page)).toEqual([]);
    await context.close();
  });

  // HREV5-PH-05: on real data at 390 px the media-type control sat at the end of the control strip's
  // horizontal scroller (clientWidth 358, scrollWidth 657): "Movies" was cut mid-word at the clip and
  // "TV" was entirely off-screen. Every browse segment must be wholly on screen at first paint —
  // inside the viewport's gutters and inside whatever scroller holds it — with the founder's long
  // conditions line and with none.
  for (const [label, filters] of [["the founder's conditions", FILTERS], ["no conditions", { languages: ["*"] }]] as const) {
    test(`Home's Movies/TV control is never clipped at 390 px (${label})`, async ({ browser }) => {
      const context = await browser.newContext(PHONE);
      const page = await context.newPage();
      await stubEventSource(page);
      await mockHomeChrome(page, { filters });
      await mockJson(page, "**/api/auth/me", SESSION_FOUNDER);
      await mockJson(page, "**/api/sync/status", {
        pipelineStage: "FeedReady", connectionStatus: "Active", lastDeltaSyncAt: null,
        lastFullReconcileAt: null, activeJob: null, recentJobs: [], outboxPending: 0, outboxDeadLetters: 0,
      });
      await mockJson(page, "**/api/feed", { generatedAt: new Date().toISOString(), hero: [card(1), card(2)], rows: [], lockedRowCount: 0 });
      await page.goto("/home");
      await expect(page.getByTestId("hero-card").first()).toBeVisible();
      await expect(page.getByTestId("browse-show")).toBeAttached();
      const clipped = await page.evaluate(() => {
        const out: string[] = [];
        for (const id of ["browse-media-all", "browse-movie", "browse-show"]) {
          const el = document.querySelector<HTMLElement>(`[data-testid="${id}"]`)!.closest("button")!;
          const r = el.getBoundingClientRect();
          const box = `${id} x${Math.round(r.left)}–${Math.round(r.right)}`;
          if (r.left < 0 || r.right > window.innerWidth) out.push(`${box} outside the ${window.innerWidth} px viewport`);
          for (let a = el.parentElement; a; a = a.parentElement) {
            const style = getComputedStyle(a);
            if (!/(auto|scroll|hidden)/.test(style.overflowX)) continue;
            const c = a.getBoundingClientRect();
            if (r.left < c.left - 0.5 || r.right > c.right + 0.5) out.push(`${box} clipped by a scroller at x${Math.round(c.left)}–${Math.round(c.right)}`);
          }
        }
        return out;
      });
      expect(clipped).toEqual([]);
      // The two groups stay told apart by name (two radio groups, each with its own "All").
      await expect(page.getByRole("radiogroup", { name: "Media type" }).getByRole("radio", { name: "All" })).toHaveCount(1);
      await expect(page.getByRole("radiogroup", { name: "Availability" }).getByRole("radio", { name: "All" })).toHaveCount(1);
      // And the first pick is still on the first screen.
      const hero = (await page.getByTestId("hero-card").first().boundingBox())!;
      expect(hero.y, "the first pick's top").toBeLessThan(844 / 2);
      await context.close();
    });
  }

  // Twice: the plain shelf, and one with saves held out by the conditions — the held-out line's
  // "Change conditions" link-button measured 101×16 on touch, and the plain fixture never drew it (PH-07).
  for (const heldOutByRules of [undefined, 4]) test(`Saved has no interactive element under 44 px${heldOutByRules ? " — with saves held out" : ""}`, async ({ browser }) => {
    const context = await browser.newContext(PHONE);
    const page = await context.newPage();
    await boot(page);
    const item = (n: number) => ({
      titleId: `00000000-0000-0000-0000-0000000001${n}0`, mediaType: "Movie", tmdbId: 2000 + n, name: `Saved ${n}`,
      year: 2001, runtimeMinutes: 100, posterPath: null, predictedRating: 7.4, crowdRating: 7.1, crowdVotes: 500,
      availableNow: true, providers: [{ name: "Netflix", logoPath: null }], savedAt: "2026-10-23T10:00:00Z",
      daysSaved: 3, fitsEveningBudget: true, onManagedList: true, watchTonight: n === 1,
      watchTonightReason: n === 1 ? "Highest-predicted thing in your queue that's streaming and fits tonight." : null,
    });
    await mockJson(page, "**/api/saved", {
      items: [item(1), item(2), item(3)], eveningBudgetMinutes: 150, expectedAbsoluteError: 1.2,
      unscoredRemaining: 0, managedListUrl: null, sort: "tonight", heldOutByRules,
    });
    // The pinned pick's where-to-watch block (R1-42: left unmocked, it hit the tripwire and sat in the
    // unmocked-API baseline). Served with a deep link and a search link, so the probe measures the
    // provider chips the founder actually taps, not an empty block.
    await mockJson(page, "**/api/titles/movie/2001/providers", {
      region: "US", attribution: "JustWatch", tmdbWatchPage: "https://www.themoviedb.org/movie/2001/watch",
      providers: [
        { provider: "Netflix", logoPath: null, kinds: ["Flatrate"], linkKind: "direct", url: "https://www.netflix.com/title/1", displayPriority: 1 },
        { provider: "Max", logoPath: null, kinds: ["Flatrate"], linkKind: "search", url: "https://play.max.com/search?q=x", displayPriority: 2 },
      ],
    });
    await page.goto("/saved");
    await expect(page.getByTestId("saved-card").first()).toBeVisible();
    await expect(page.getByTestId("provider-chip").first()).toBeVisible();
    if (heldOutByRules) await expect(page.getByTestId("saved-held-out-change")).toBeVisible();
    expect(await smallTargets(page)).toEqual([]);
    await context.close();
  });

  test("the title sheet has no interactive element under 44 px", async ({ browser }) => {
    const context = await browser.newContext(PHONE);
    const page = await context.newPage();
    await boot(page);
    await mockJson(page, "**/api/titles/movie/1001", {
      titleId: "00000000-0000-0000-0000-0000000000e1", mediaType: "Movie", tmdbId: 1001, name: "Quiet Harbor", year: 2024,
      overview: "A harbormaster keeps one last secret.", whyThis: "Shares the slow-burn dread you love.", tagline: null,
      runtimeMinutes: 112, certification: "PG-13", genres: ["drama"], network: null, status: "Released", traktRating: 7.8,
      traktVotes: 12000, crowdRating: 7.6, crowdVotes: 12000, posterPath: null, backdropPath: null,
      trailerUrl: "https://www.youtube.com/watch?v=dQw4w9WgXcQ", cast: [], directors: [], writers: [],
      prediction: { predictedRating: 8.2, scoredAt: "2026-10-25T10:00:00Z", contributions: [{ feature: "genreAffinity", value: 0.6 }] },
      contributionDetails: [],
      userState: { isFullyWatched: false, plays: 0, userRating: null, savedForLater: false, notInterested: false, dropped: false },
    });
    await mockJson(page, "**/api/titles/movie/1001/lens", {
      source: "fresh", lens: "A slow tightening.", hook: "One more secret.", caution: "Slow.", anchors: [], mood: "tense",
      whenToWatch: null, generatedAt: "2026-10-25T10:00:00Z",
    });
    await mockJson(page, "**/api/titles/movie/1001/providers", {
      region: "US", attribution: "Data from JustWatch via TMDB", tmdbWatchPage: "https://www.themoviedb.org/movie/1001/watch",
      providers: [{ provider: "Netflix", logoPath: null, kinds: ["Flatrate"], linkKind: "direct", url: "https://www.netflix.com/search?q=x", displayPriority: 1 }],
    });
    await page.goto("/title/movie/1001");
    await expect(page.getByTestId("provider-chip")).toHaveCount(1);
    await page.getByTestId("lens-more").click();
    expect(await smallTargets(page, '[data-testid="title-detail"]')).toEqual([]);
    await context.close();
  });

  // HREV5-PH-03: a failed where-to-watch read's Retry measured 29 × 44 on /title/movie/41362,
  // /title/show/246 and /saved — it had a touch height and no touch width. Probed in both places it
  // renders, on a title sheet and under Saved's pinned pick.
  for (const where of ["title", "saved"] as const) test(`the where-to-watch Retry after a failed read is 44 × 44 (${where})`, async ({ browser }) => {
    const context = await browser.newContext(PHONE);
    const page = await context.newPage();
    await boot(page);
    const tmdbId = where === "title" ? 41362 : 2001;
    await page.route(`**/api/titles/movie/${tmdbId}/providers`, (route) =>
      route.fulfill({ status: 503, contentType: "application/json", body: JSON.stringify({ error: "providers unavailable" }) })
    );
    if (where === "title") {
      await mockJson(page, "**/api/titles/movie/41362", {
        titleId: "00000000-0000-0000-0000-000000041362", mediaType: "Movie", tmdbId: 41362, name: "Rubber", year: 2010,
        overview: "A tyre comes to life.", whyThis: null, tagline: null, runtimeMinutes: 82, certification: "R", genres: ["comedy"],
        network: null, status: "Released", traktRating: 5.9, traktVotes: 3000, crowdRating: 5.9, crowdVotes: 3000, posterPath: null,
        backdropPath: null, trailerUrl: null, cast: [], directors: [], writers: [], prediction: null, contributionDetails: [],
        userState: { isFullyWatched: false, plays: 0, userRating: null, savedForLater: false, notInterested: false, dropped: false },
      });
      await mockJson(page, "**/api/titles/movie/41362/lens", { source: "none", lens: null, hook: null, caution: null, anchors: [], mood: null, whenToWatch: null, generatedAt: null });
      await page.goto("/title/movie/41362");
    } else {
      await mockJson(page, "**/api/saved", {
        items: [{
          titleId: "00000000-0000-0000-0000-000000000110", mediaType: "Movie", tmdbId: 2001, name: "Saved 1", year: 2001,
          runtimeMinutes: 100, posterPath: null, predictedRating: 7.4, crowdRating: 7.1, crowdVotes: 500, availableNow: true,
          providers: [{ name: "Netflix", logoPath: null }], savedAt: "2026-10-23T10:00:00Z", daysSaved: 3, fitsEveningBudget: true,
          onManagedList: true, watchTonight: true, watchTonightReason: "Highest-predicted thing in your queue that's streaming and fits tonight.",
        }],
        eveningBudgetMinutes: 150, expectedAbsoluteError: 1.2, unscoredRemaining: 0, managedListUrl: null, sort: "tonight",
      });
      await page.goto("/saved");
    }
    const retry = page.getByTestId("where-to-watch-retry");
    await expect(retry).toBeVisible({ timeout: 15_000 });
    const box = (await retry.boundingBox())!;
    expect(box.width, "Retry width").toBeGreaterThanOrEqual(44);
    expect(box.height, "Retry height").toBeGreaterThanOrEqual(44);
    const root = where === "title" ? '[data-testid="where-to-watch-failed"]' : "main";
    expect(await smallTargets(page, root)).toEqual([]);
    await context.close();
  });

  test("a person page has no interactive element under 44 px", async ({ browser }) => {
    const context = await browser.newContext(PHONE);
    const page = await context.newPage();
    await boot(page);
    await mockJson(page, "**/api/people/00000000-0000-0000-0000-0000000000aa", {
      id: "00000000-0000-0000-0000-0000000000aa", name: "Michael Mann", department: "Directing", profilePath: null,
      userRating: 8, derivedAffinity: 8.1, ratedTitleCount: 3,
      filmography: [{ titleId: "00000000-0000-0000-0000-000000000701", mediaType: "Movie", tmdbId: 949, name: "Heat", year: 1995, posterPath: null, userRating: 9, predictedRating: null, roles: ["Director"] }],
    });
    await page.goto("/person/00000000-0000-0000-0000-0000000000aa");
    await expect(page.getByTestId("filmography-role")).toHaveText("Director");
    expect(await smallTargets(page)).toEqual([]);
    await context.close();
  });
});
