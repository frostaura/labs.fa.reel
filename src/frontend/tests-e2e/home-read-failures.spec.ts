import { test, expect, type Page, type Route } from "@playwright/test";
import { SESSION_FOUNDER, mockJson, stubEventSource } from "./helpers";
import { mockHomeChrome } from "./home-chrome";

/**
 * A failed read on Home says so, retries, and offers Retry — it never collapses to "nothing here"
 * (HREV8-DESK-02).
 *
 * One 503 on /api/feed/genres used to leave Home with no genre rails at all: no error, no Retry, and
 * no second request. A failed Continue watching read removed that row the same way, and a failed
 * /api/settings/filters made the conditions line read as if none were saved while the server applied
 * all twelve. Each read now retries on the rails' schedule (1, 2, 4 s) and then shows a one-line
 * "Couldn't load … · Retry". Varied: the read that fails, how it fails (503, 500, a dropped
 * connection), failing once against failing every time, and desktop against a phone.
 */

const card = (n: number) => ({
  titleId: `00000000-0000-0000-0000-${String(n).padStart(12, "0")}`, mediaType: "Movie", tmdbId: 7000 + n, name: `Pick ${n}`,
  year: 2024, runtimeMinutes: 101, posterPath: null, backdropPath: null, genres: ["drama"], predictedRating: 8, crowdRating: 7.4,
  crowdVotes: 3000, whyThis: "Because the founder likes it.", isReleased: true,
});

const GENRES = { genres: [{ slug: "drama", label: "Drama" }, { slug: "crime", label: "Crime" }, { slug: "war", label: "War" }] };
const CONTINUE = [{
  titleId: "00000000-0000-0000-0000-0000000000c1", mediaType: "Show", tmdbId: 9001, name: "Slow Horses", posterPath: null,
  backdropPath: null, nextEpisodeSeason: 2, nextEpisodeNumber: 3, watchedEpisodes: 8, totalAired: 18, completionPct: 0.44,
}];
const FILTERS = {
  excludeGenres: ["documentary", "horror"], includeGenres: [], excludeKeywords: ["zombie"], maturityCeiling: "R",
  minPredictedRating: 6, languages: ["en"], minCrowdRating: 6, minYear: 2000, maxYear: null,
};
const DIGEST = { weekStart: "2026-10-26", generatedAt: "2026-10-30T06:00:00Z", items: [card(50)], degradedReason: null };

type Failure = "503" | "500" | "abort";

/** Answers `path` with `body`, failing the first `failures` calls (Infinity: every call until healed). */
async function flaky(page: Page, path: string, body: unknown, failures: number, how: Failure) {
  const state = { calls: 0, failures };
  await page.route(
    (u) => u.pathname === path,
    async (route: Route) => {
      state.calls += 1;
      if (state.calls <= state.failures) {
        if (how === "abort") return route.abort("connectionreset");
        return route.fulfill({ status: Number(how), contentType: "application/json", body: "{}" });
      }
      return route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify(body) });
    }
  );
  return state;
}

async function bootHome(page: Page) {
  await stubEventSource(page);
  await mockHomeChrome(page);
  await mockJson(page, "**/api/auth/me", SESSION_FOUNDER);
  await mockJson(page, "**/api/sync/status", {
    pipelineStage: "FeedReady", connectionStatus: "Active", lastDeltaSyncAt: null,
    lastFullReconcileAt: null, activeJob: null, recentJobs: [], outboxPending: 0, outboxDeadLetters: 0,
  });
  await mockJson(page, "**/api/feed", { generatedAt: new Date().toISOString(), hero: [1, 2, 3].map(card), rows: [], lockedRowCount: 0 });
}

const READS = [
  {
    name: "the genre list",
    path: "/api/feed/genres",
    body: GENRES,
    line: "home-genres-failed",
    copy: "Couldn't load your genre rows",
    loaded: (page: Page) => expect(page.getByTestId("genre-rail")).toHaveCount(3),
  },
  {
    name: "Continue watching",
    path: "/api/feed/continue-watching",
    body: CONTINUE,
    line: "home-continue-failed",
    copy: "Couldn't load Continue watching",
    loaded: (page: Page) => expect(page.getByTestId("continue-card")).toHaveCount(1),
  },
  {
    name: "the weekly digest",
    path: "/api/digest/current",
    body: DIGEST,
    line: "home-digest-failed",
    copy: "Couldn't load this week's digest",
    loaded: (page: Page) => expect(page.getByTestId("rec-card").filter({ hasText: "Pick 50" })).toHaveCount(1),
  },
] as const;

test.describe("Home says when a read failed, and retries (HREV8-DESK-02)", () => {
  test.use({ viewport: { width: 1440, height: 900 } });

  for (const read of READS) {
    for (const how of ["503", "abort"] as const) {
      test(`${read.name} failing once (${how}) retries on its own and shows the section`, async ({ page }) => {
        await bootHome(page);
        const state = await flaky(page, read.path, read.body, 1, how);
        await page.goto("/home");
        await read.loaded(page);
        expect(state.calls).toBe(2);
        await expect(page.getByTestId(read.line)).toHaveCount(0);
      });
    }

    test(`${read.name} failing every time says so with Retry, and Retry brings it back`, async ({ page }) => {
      await bootHome(page);
      const state = await flaky(page, read.path, read.body, Infinity, "500");
      await page.goto("/home");
      const line = page.getByTestId(read.line);
      // Said from the first failure, while the automatic retries run…
      await expect(line).toContainText(read.copy);
      // …then, with them spent (1 + 2 + 4 s), the line offers Retry and nothing more is sent.
      await expect(line).toHaveAttribute("data-phase", "failed", { timeout: 15_000 });
      await expect(line).toContainText(`${read.copy} ·`);
      expect(state.calls).toBe(4);
      await page.waitForTimeout(2500);
      expect(state.calls).toBe(4);

      state.failures = 0;
      await page.getByTestId(`${read.line}-retry`).click();
      await read.loaded(page);
      expect(state.calls).toBe(5);
      await expect(line).toHaveCount(0);
    });
  }

  for (const how of ["500", "abort"] as const) {
    test(`a failed conditions read (${how}) never reads as "none saved", and Retry restores the chips`, async ({ page }) => {
      await bootHome(page);
      const state = await flaky(page, "/api/settings/filters", FILTERS, Infinity, how);
      await page.goto("/home");
      const line = page.getByTestId("home-conditions-failed");
      await expect(line).toContainText("Couldn't load your conditions — they still apply");
      await expect(page.getByTestId("summary-none")).toHaveCount(0);
      await expect(line).toHaveAttribute("data-phase", "failed", { timeout: 15_000 });

      state.failures = 0;
      await page.getByTestId("home-conditions-failed-retry").click();
      await expect(page.getByTestId("condition-chip").first()).toBeVisible();
      await expect(line).toHaveCount(0);
    });
  }

  test("the conditions line holds its one-line height while it reports the failure", async ({ page }) => {
    await bootHome(page);
    await flaky(page, "/api/settings/filters", FILTERS, Infinity, "503");
    await page.goto("/home");
    const line = page.getByTestId("home-conditions-failed");
    await expect(line).toBeVisible();
    const hero = page.getByTestId("hero-card").first();
    const before = (await hero.boundingBox())!.y;
    await expect(line).toHaveAttribute("data-phase", "failed", { timeout: 15_000 });
    expect(Math.abs((await hero.boundingBox())!.y - before)).toBeLessThanOrEqual(1);
  });
});

test.describe("on a phone (HREV8-DESK-02)", () => {
  test.use({ viewport: { width: 390, height: 844 }, hasTouch: true, isMobile: true });

  test("a failed conditions read is said in the control line, with Retry", async ({ page, browserName }) => {
    test.skip(browserName !== "chromium", "isMobile is a Chromium emulation");
    await bootHome(page);
    const state = await flaky(page, "/api/settings/filters", FILTERS, Infinity, "503");
    await page.goto("/home");
    const line = page.getByTestId("home-conditions-failed");
    await expect(line).toHaveAttribute("data-phase", "failed", { timeout: 15_000 });
    await expect(line).toContainText("Conditions didn't load");
    state.failures = 0;
    await page.getByTestId("home-conditions-failed-retry").tap();
    await expect(page.getByTestId("condition-chip").first()).toBeAttached();
    await expect(line).toHaveCount(0);
  });
});
