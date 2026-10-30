import { test, expect, type Page } from "@playwright/test";
import { SESSION_FOUNDER, mockJson, stubEventSource } from "./helpers";
import { mockHomeChrome } from "./home-chrome";

/**
 * Home holds its layout while the rails load UNDER A SCROLL (HREV8-DESK-01).
 *
 * At 1440×900, scrolling 600 px every 250 ms, total CLS reached 0.21 on a cold Home: a genre rail's
 * placeholder was a bare poster, a loaded card is a poster plus its title, meta and why-lines, so
 * each rail that landed inside the viewport grew by 52–165 px and shoved everything under it down;
 * and the "Because you loved" rows grew again each time a page brought a card whose evidence chips
 * wrapped onto another line. Load-time CLS (home-cls.spec.ts) never saw it: nothing had loaded yet.
 *
 * `/api/feed/more` answers slowly, so every rail's first page lands while the user is scrolling past
 * it — the order that produced the shifts. The fixture is varied across what changes a card's
 * height: a card with no year or runtime (an empty meta line), a one-line and a two-line why, a lens
 * clause, and anchored pages whose evidence runs from no chip to three long ones.
 */

type Variant = "lean" | "full";

const LONG = "A slow-burn study of a family business coming apart over one long, unforgiving winter.";

function card(n: number, variant: Variant, evidence: number) {
  // "lean": every optional line empty or short — the card at its shortest. "full": every line
  // filled, the why wrapping, the chips long enough to wrap. Odd cards flip, so a page mixes both.
  const lean = (variant === "lean") === (n % 2 === 0);
  return {
    titleId: `00000000-0000-0000-0000-${String(n).padStart(12, "0")}`,
    mediaType: "Movie",
    tmdbId: 5000 + n,
    name: `Title ${n}`,
    year: lean ? null : 2019,
    runtimeMinutes: lean ? null : 128,
    posterPath: null,
    backdropPath: null,
    genres: ["drama"],
    predictedRating: 7.8,
    crowdRating: 7.1,
    crowdVotes: 4200,
    whyThis: lean ? "Tense." : LONG,
    lensClause: !lean && n % 3 === 0 ? LONG : null,
    isReleased: true,
    evidence: Array.from({ length: evidence }, (_, i) => ({
      kind: i === 0 ? "person" : "keyword",
      label: i === 0 ? "Christopher Alexander McQuarrie" : `a rather long shared keyword ${i}`,
      role: i === 0 ? "director" : null,
    })),
  };
}

const GENRES = ["drama", "thriller", "comedy", "crime", "horror", "sci-fi", "romance", "war"].map((slug) => ({
  slug,
  label: slug[0].toUpperCase() + slug.slice(1),
}));

let serial = 100;

async function bootHome(page: Page, variant: Variant, moreDelayMs: number) {
  await page.addInitScript(() => {
    const w = window as unknown as { __cls: number; __shifts: { value: number; at: number }[] };
    w.__cls = 0;
    w.__shifts = [];
    new PerformanceObserver((list) => {
      for (const entry of list.getEntries() as (PerformanceEntry & { value: number; hadRecentInput: boolean })[]) {
        if (entry.hadRecentInput) continue;
        w.__cls += entry.value;
        w.__shifts.push({ value: entry.value, at: Math.round(entry.startTime) });
      }
    }).observe({ type: "layout-shift", buffered: true });
  });
  await stubEventSource(page);
  await mockHomeChrome(page);
  await mockJson(page, "**/api/auth/me", SESSION_FOUNDER);
  await mockJson(page, "**/api/sync/status", {
    pipelineStage: "FeedReady", connectionStatus: "Active", lastDeltaSyncAt: null,
    lastFullReconcileAt: null, activeJob: null, recentJobs: [], outboxPending: 0, outboxDeadLetters: 0,
  });
  await mockJson(page, "**/api/feed/genres", { genres: GENRES });
  // Two anchored rows seeded with the SHORTEST evidence (one chip, or none); their later pages bring
  // the longest, so a row that grows with its chips is caught growing.
  await mockJson(page, "**/api/feed", {
    generatedAt: new Date().toISOString(),
    hero: [1, 2, 3, 4, 5, 6, 7, 8].map((n) => card(n, variant, 0)),
    rows: ["aa", "bb"].map((anchor, r) => ({
      kind: "because-you-loved",
      anchorTitleId: `00000000-0000-0000-0000-0000000000${anchor}`,
      anchorName: `Anchor ${r + 1}`,
      title: null,
      items: [10, 11, 12, 13, 14, 15, 16, 17].map((n) => card(n + r * 10, "lean", r === 0 ? 1 : 0)),
    })),
    lockedRowCount: 0,
  });
  const pagesByRail = new Map<string, number>();
  await page.route(
    (u) => u.pathname === "/api/feed/more",
    async (route) => {
      const body = route.request().postDataJSON() as { row: string; genre?: string | null; anchorTitleId?: string | null };
      const rail = `${body.row}:${body.genre ?? body.anchorTitleId ?? ""}`;
      const page = (pagesByRail.get(rail) ?? 0) + 1;
      pagesByRail.set(rail, page);
      await new Promise((r) => setTimeout(r, moreDelayMs));
      // The hero pages nothing (it is a fixed grid, and not what this spec is about).
      const items =
        body.row === "hero" || page > 2
          ? []
          : Array.from({ length: 8 }, () => card(serial++, variant, body.row === "because-you-loved" ? 3 : 0));
      await route
        .fulfill({ status: 200, contentType: "application/json", body: JSON.stringify({ items, exhausted: items.length === 0 }) })
        .catch(() => undefined);
    }
  );
  await page.goto("/home");
  await expect(page.getByTestId("hero-card").first()).toBeVisible();
}

/** Scroll as the reviewer did: settle, then 600 px every 250 ms to the end, then let rails land. */
async function scrollThrough(page: Page) {
  await page.waitForTimeout(3000);
  for (let i = 0; i < 40; i++) {
    const atEnd = await page.evaluate(() => {
      window.scrollBy(0, 600);
      return window.scrollY + window.innerHeight >= document.documentElement.scrollHeight - 2;
    });
    await page.waitForTimeout(250);
    if (atEnd) break;
  }
  await page.waitForTimeout(2500);
}

test.describe("Home holds its layout while rails load under a scroll (HREV8-DESK-01)", () => {
  test.use({ viewport: { width: 1440, height: 900 } });

  for (const variant of ["lean", "full"] as const) {
    for (const delay of [400, 1200]) {
      test(`total CLS stays ≤ 0.05 scrolling a cold Home (${variant} cards, /more after ${delay} ms)`, async ({ page, browserName }) => {
        test.skip(browserName !== "chromium", "layout-shift entries are Chromium-only; the height specs below hold in every engine");
        await bootHome(page, variant, delay);
        await scrollThrough(page);
        // Every genre rail actually loaded — the scroll reached them all, so their shifts were seen.
        await expect(page.getByTestId("genre-rail")).toHaveCount(GENRES.length);
        await expect(page.getByTestId("rail-skeleton")).toHaveCount(0);
        const { cls, shifts } = await page.evaluate(() => {
          const w = window as unknown as { __cls: number; __shifts: { value: number; at: number }[] };
          return { cls: w.__cls, shifts: w.__shifts };
        });
        expect(cls, `cumulative layout shift ${cls.toFixed(4)} — ${JSON.stringify(shifts)}`).toBeLessThanOrEqual(0.05);
      });
    }
  }

  for (const variant of ["lean", "full"] as const) {
    test(`a genre rail is the same height as a placeholder and once its first page lands (${variant} cards)`, async ({ page }) => {
      await bootHome(page, variant, 1500);
      const rail = page.getByTestId("genre-rail").first();
      await rail.scrollIntoViewIfNeeded();
      await expect(rail.getByTestId("rail-skeleton").first()).toBeVisible();
      const before = (await rail.boundingBox())!.height;
      await expect(rail.getByTestId("rec-card").first()).toBeVisible({ timeout: 10_000 });
      await expect(rail.getByTestId("rail-skeleton")).toHaveCount(0);
      const after = (await rail.boundingBox())!.height;
      expect(Math.abs(after - before), `placeholder ${before}px, loaded ${after}px`).toBeLessThanOrEqual(1);
    });
  }

  test("an anchored row keeps its height as pages with longer evidence land", async ({ page }) => {
    await bootHome(page, "full", 600);
    const row = page.locator("section").filter({ hasText: "Because you loved Anchor 1" });
    await row.scrollIntoViewIfNeeded();
    const seeded = (await row.boundingBox())!.height;
    // Page the row sideways to its end, so both of its pages (three long chips a card) land.
    const scroller = row.locator(".overflow-x-auto").first();
    for (let i = 0; i < 6; i++) {
      await scroller.evaluate((el) => el.scrollBy(4000, 0));
      await page.waitForTimeout(700);
    }
    await expect.poll(() => row.getByTestId("rec-card").count()).toBeGreaterThan(8);
    const heights = await row.getByTestId("rec-card").evaluateAll((cards) => cards.map((c) => c.getBoundingClientRect().height));
    const grown = (await row.boundingBox())!.height;
    expect(Math.abs(grown - seeded), `seeded ${seeded}px, paged ${grown}px; cards ${JSON.stringify(heights)}`).toBeLessThanOrEqual(1);
  });
});
