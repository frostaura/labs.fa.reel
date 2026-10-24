import { test, expect, type Page } from "@playwright/test";
import { SESSION_FOUNDER, mockJson, stubEventSource } from "./helpers";

/**
 * The hero fits its card at every width (F-4, O-1, I-10) and its title reads over any art (O-2).
 *
 * The defect this pins: a fixed 16:8.5 box with every line of copy pinned to the bottom and growing
 * upward, so on phones and tablets the title and badge were cut off the TOP of the card and the
 * quick actions sat on the text. The fixtures are the worst case on purpose — long names, a long
 * hook, a lens clause, a reaching label and both badges — because the bug only shows when the copy
 * is longer than the box.
 *
 * Art is served as a flat near-white image for every backdrop, the worst background a light title
 * can sit on, so the contrast assertion measures the panel and not the luck of the artwork.
 */

const LONG_NAMES = [
  "The Girl in the Spider's Web: A New Dragon Tattoo Story",
  "Ninja: Shadow of a Tear",
  "Everything Everywhere All at Once",
  "Crouching Tiger, Hidden Dragon: Sword of Destiny",
  "Mortal Kombat II",
];

const heroCard = (n: number) => ({
  titleId: `00000000-0000-0000-0000-0000000001${String(n).padStart(2, "0")}`,
  mediaType: "Movie",
  tmdbId: 9100 + n,
  name: LONG_NAMES[n % LONG_NAMES.length]!,
  year: 2018,
  runtimeMinutes: 115,
  posterPath: null,
  backdropPath: `/bright-${n}.jpg`,
  genres: ["action", "thriller", "crime"],
  predictedRating: 6.6,
  crowdRating: 7.1,
  crowdVotes: 120000,
  whyThis: "A action pick aligned with your taste.",
  hook: "A hacker with a past she cannot outrun is pulled into a conspiracy that reaches the top of the intelligence world, and the only way out is through it.",
  lensClause: "The pacing is relentless in the way your nine-rated thrillers are, and the third act lands the way they do.",
  isReleased: true,
  confidence: "reaching",
  floorUsed: 7.5,
});

/** A 64×36 near-white PNG, so every backdrop is the brightest art a title can be laid over. */
async function serveBrightArt(page: Page): Promise<void> {
  const png = await page.evaluate(() => {
    const canvas = document.createElement("canvas");
    canvas.width = 64;
    canvas.height = 36;
    const ctx = canvas.getContext("2d")!;
    ctx.fillStyle = "rgb(250,250,245)";
    ctx.fillRect(0, 0, 64, 36);
    return canvas.toDataURL("image/png").split(",")[1]!;
  });
  const body = Buffer.from(png, "base64");
  await page.route(/image\.tmdb\.org/, (route) => route.fulfill({ status: 200, contentType: "image/png", body }));
}

/** The founder's own saved conditions on the eval copy: eight genre exclusions and four floors. */
const REAL_FILTERS = {
  excludeGenres: ["animation", "anime", "comedy", "documentary", "history", "music", "musical", "romance"],
  includeGenres: [], excludeKeywords: [], maturityCeiling: null,
  minPredictedRating: 6, languages: ["en"], minCrowdRating: 6, minYear: 2000, maxYear: null,
};

/** An account with a track record, so the accuracy segment of the status line is on screen. */
const ACCURACY = {
  window: "365d", from: "2025-10-24T00:00:00Z", to: "2026-10-24T00:00:00Z", source: "exposures",
  headline: { withinOnePoint: 0.26, n: 116, meanAbsoluteError: 1.94, bias: 0.37, correlation: -0.004, meanPredicted: 6.81, meanActual: 5.78 },
  calibration: [], moments: [],
  conversion: { shown: 0, saved: 0, watched: 0, rated: 0, shownToSaved: null, savedToWatched: null, shownToRated: null, medianDaysToWatch: null },
  counterfactual: [], learnedThisWeek: null,
};

async function bootHome(
  page: Page,
  count: number,
  overrides: Record<string, unknown> = {},
  chrome: { filters?: unknown; accuracy?: unknown } = {}
): Promise<void> {
  await stubEventSource(page);
  await page.goto("about:blank");
  await serveBrightArt(page);
  await mockJson(page, "**/api/auth/me", SESSION_FOUNDER);
  await mockJson(page, "**/api/sync/status", {
    pipelineStage: "FeedReady", connectionStatus: "Active", lastDeltaSyncAt: null,
    lastFullReconcileAt: null, activeJob: null, recentJobs: [], outboxPending: 0, outboxDeadLetters: 0,
  });
  await mockJson(page, "**/api/feed/continue-watching", []);
  await mockJson(page, "**/api/feed/more", { items: [], exhausted: true });
  await mockJson(page, "**/api/feed/genres", { genres: [] });
  await mockJson(page, "**/api/digest/current", { weekStart: null, generatedAt: null, items: [] });
  if (chrome.filters) await mockJson(page, "**/api/settings/filters", chrome.filters);
  if (chrome.accuracy) await mockJson(page, "**/api/accuracy", chrome.accuracy);
  await mockJson(page, "**/api/feed", {
    generatedAt: new Date().toISOString(),
    hero: Array.from({ length: count }, (_, i) => ({ ...heroCard(i + 1), ...overrides })),
    rows: [],
    lockedRowCount: 0,
  });
  await page.goto("/home");
  await expect(page.getByTestId("hero-card")).toHaveCount(count);
}

type Box = { x: number; y: number; width: number; height: number };

function inside(inner: Box, outer: Box, slack = 0.5): boolean {
  return (
    inner.y >= outer.y - slack &&
    inner.y + inner.height <= outer.y + outer.height + slack &&
    inner.x >= outer.x - slack &&
    inner.x + inner.width <= outer.x + outer.width + slack
  );
}

function intersects(a: Box, b: Box): boolean {
  return a.x < b.x + b.width && b.x < a.x + a.width && a.y < b.y + b.height && b.y < a.y + a.height;
}

/** Every hero card's title, badges, text block and quick-action buttons, as boxes. */
async function heroGeometry(page: Page) {
  return page.evaluate(() => {
    const box = (el: Element) => {
      const r = el.getBoundingClientRect();
      return { x: r.x, y: r.y, width: r.width, height: r.height };
    };
    return Array.from(document.querySelectorAll('[data-testid="hero-card"]')).map((card) => ({
      card: box(card),
      title: box(card.querySelector("h2")!),
      badges: Array.from(card.querySelectorAll('[data-testid="hero-text"] h2 ~ *')).map(box),
      textLines: Array.from(card.querySelectorAll('[data-testid="hero-text"] h2, [data-testid="hero-text"] p')).map(box),
      actions: Array.from(card.querySelectorAll('[data-testid="card-quick-actions"] button')).map(box),
    }));
  });
}

for (const viewport of [
  { width: 390, height: 844 },
  { width: 834, height: 1194 },
  { width: 1024, height: 768 },
  { width: 1440, height: 900 },
]) {
  test.describe(`hero at ${viewport.width}×${viewport.height}`, () => {
    test.use({ viewport });

    test("every title and badge sits inside its card, and the actions never cover text", async ({ page }) => {
      await bootHome(page, 5);
      // Off-screen columns are measured where they sit: the scroller lays every card out whether or
      // not it is in view, so its boxes are real without scrolling to it.
      const geometry = await heroGeometry(page);
      expect(geometry).toHaveLength(5);
      for (const [index, card] of geometry.entries()) {
        expect(inside(card.title, card.card), `card ${index} title ${JSON.stringify(card.title)} in ${JSON.stringify(card.card)}`).toBe(true);
        expect(card.badges.length).toBeGreaterThanOrEqual(2);
        for (const badge of card.badges) {
          expect(inside(badge, card.card), `card ${index} badge`).toBe(true);
        }
        expect(card.actions).toHaveLength(3);
        for (const action of card.actions) {
          for (const line of card.textLines) {
            expect(intersects(action, line), `card ${index}: action ${JSON.stringify(action)} over text ${JSON.stringify(line)}`).toBe(false);
          }
        }
      }
    });

    test("the lens clause is off the card and the hook is held to two lines", async ({ page }) => {
      await bootHome(page, 3);
      await expect(page.getByTestId("hero-lens-clause")).toHaveCount(0);
      const hook = page.getByTestId("hero-hook").first();
      const lines = await hook.evaluate((el) => {
        const style = getComputedStyle(el);
        return Math.round(el.getBoundingClientRect().height / parseFloat(style.lineHeight));
      });
      expect(lines).toBeLessThanOrEqual(2);
    });
  });
}

test.describe("narrow screens", () => {
  test.use({ viewport: { width: 390, height: 844 } });

  test("the card stacks: art above, text below it, actions in a visible row at the bottom", async ({ page }) => {
    await bootHome(page, 2);
    const card = page.getByTestId("hero-card").first();
    const [art, text, actions] = await Promise.all([
      card.locator("img").boundingBox(),
      card.getByTestId("hero-text").boundingBox(),
      card.getByTestId("card-quick-actions").boundingBox(),
    ]);
    expect(art && text && actions).toBeTruthy();
    expect(text!.y).toBeGreaterThanOrEqual(art!.y + art!.height - 1);
    expect(actions!.y).toBeGreaterThanOrEqual(text!.y + text!.height - 1);
    // Visible without hover: there is no hover on a phone.
    await expect(card.getByTestId("quick-save")).toHaveCSS("opacity", "1");
    // 44 px targets.
    for (const button of await card.locator('[data-testid="card-quick-actions"] button').all()) {
      const b = await button.boundingBox();
      expect(b!.width).toBeGreaterThanOrEqual(44);
      expect(b!.height).toBeGreaterThanOrEqual(44);
    }
  });
});

/**
 * WCAG relative luminance of an sRGB pixel, in the page so it can read pixels off a screenshot
 * drawn into a canvas — no image library needed on the Node side.
 */
async function titleContrast(page: Page, index: number): Promise<number> {
  const card = page.getByTestId("hero-card").nth(index);
  await card.scrollIntoViewIfNeeded();
  // Past the eager pair the art is lazy: wait until THIS card's near-white art has decoded, or the
  // reading could be over the dark placeholder and prove nothing about the panel.
  await expect
    .poll(() => card.locator("img").evaluate((img: HTMLImageElement) => img.complete && img.naturalWidth > 0))
    .toBe(true);
  // Off the card so the hover zoom is not in the picture, and the snap scroll settled before the
  // shot (a screenshot mid-scroll is what detached the element in the one flaky run).
  await page.mouse.move(1, 1);
  await card.evaluate(
    (el) =>
      new Promise<void>((resolve) => {
        let last = el.getBoundingClientRect().x;
        const tick = () => {
          const x = el.getBoundingClientRect().x;
          if (x === last) resolve();
          else {
            last = x;
            requestAnimationFrame(tick);
          }
        };
        requestAnimationFrame(tick);
      })
  );
  const title = card.locator("h2");
  const shot = (await title.screenshot({ animations: "disabled" })).toString("base64");
  return page.evaluate(async (data) => {
    const img = new Image();
    img.src = `data:image/png;base64,${data}`;
    await img.decode();
    const canvas = document.createElement("canvas");
    canvas.width = img.width;
    canvas.height = img.height;
    const ctx = canvas.getContext("2d")!;
    ctx.drawImage(img, 0, 0);
    const { data: px } = ctx.getImageData(0, 0, img.width, img.height);
    const channel = (c: number) => {
      const s = c / 255;
      return s <= 0.03928 ? s / 12.92 : ((s + 0.055) / 1.055) ** 2.4;
    };
    const lums: number[] = [];
    for (let i = 0; i < px.length; i += 4) {
      lums.push(0.2126 * channel(px[i]!) + 0.7152 * channel(px[i + 1]!) + 0.0722 * channel(px[i + 2]!));
    }
    lums.sort((a, b) => a - b);
    // Background: the median pixel of the title box (most of it is not glyph). Text: the 98th
    // percentile, i.e. the glyph cores rather than one stray anti-aliased pixel.
    const background = lums[Math.floor(lums.length / 2)]!;
    const text = lums[Math.floor(lums.length * 0.98)]!;
    return (text + 0.05) / (background + 0.05);
  }, shot);
}

for (const viewport of [{ width: 1440, height: 900 }, { width: 390, height: 844 }]) {
  test.describe(`title contrast at ${viewport.width}`, () => {
    test.use({ viewport });

    test("every one of 20 hero titles is at least 4.5:1 over near-white art", async ({ page }) => {
      test.setTimeout(120_000);
      await bootHome(page, 20);
      for (let i = 0; i < 20; i++) {
        const ratio = await titleContrast(page, i);
        expect(ratio, `hero ${i} title contrast`).toBeGreaterThanOrEqual(4.5);
      }
    });
  });
}

/**
 * The first pick is above the fold on a cold Home (I-10, O-6): its top edge within 220px at
 * 1440×900 and 300px at 390×844.
 *
 * Measured with the chrome a real account has, because the earlier version of this test passed on
 * a page with no conditions line at all (the filters were never mocked) and with confident picks
 * only — the founder's eight exclusions and a reaching row were 10–40px further down (review D2).
 * So every case here carries the founder's own saved conditions, and the cases vary what the
 * status line has to say: nothing, the track record only, the reaching admission only, and both.
 *
 * The assertion keeps 10px of headroom under the acceptance number, so a slightly longer line or a
 * different engine's font metrics cannot pass by a fraction of a pixel.
 */
const HEADROOM_PX = 10;
const TOP_CASES = [
  { label: "confident, no track record", overrides: { confidence: "confident", predictedRating: 8.1 }, accuracy: undefined },
  { label: "confident, with a track record", overrides: { confidence: "confident", predictedRating: 8.1 }, accuracy: ACCURACY },
  { label: "reaching, no track record", overrides: {}, accuracy: undefined },
  { label: "reaching, with a track record", overrides: {}, accuracy: ACCURACY },
] as const;

for (const [viewport, budget] of [
  [{ width: 1440, height: 900 }, 220],
  [{ width: 390, height: 844 }, 300],
] as const) {
  for (const c of TOP_CASES) {
    test(`the first hero card starts within ${budget}px of the top at ${viewport.width}×${viewport.height} (${c.label})`, async ({ page }) => {
      await page.setViewportSize(viewport);
      await bootHome(page, 5, c.overrides, { filters: REAL_FILTERS, accuracy: c.accuracy });
      // The chrome this budget is about is really on the page — the fixture cannot drop it again.
      await expect(page.getByTestId("conditions-summary")).toBeVisible();
      await expect(page.getByTestId("browse-filters")).toBeVisible();
      await expect(page.getByTestId("tonight-mood-chips")).toBeVisible();
      if (c.accuracy) await expect(page.getByTestId("status-track-record")).toBeVisible();
      if (!("confidence" in c.overrides)) await expect(page.getByTestId("hero-reaching-note")).toBeVisible();
      const top = await page.getByTestId("hero-card").first().evaluate((el) => el.getBoundingClientRect().top);
      expect(top).toBeLessThanOrEqual(budget - HEADROOM_PX);
      // And nothing above it made the page wider than the phone.
      expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBe(viewport.width);
    });
  }
}
