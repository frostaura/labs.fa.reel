import { test, expect, type Page } from "@playwright/test";
import { SESSION_FOUNDER, mockJson, stubEventSource } from "./helpers";

/**
 * The title sheet's heading reads over any artwork (PH2-01).
 *
 * The defect this pins: on a phone the <h1> sat on the backdrop under a gradient that only darkened
 * the bottom edge, so light art — the founder's first hero, The Girl in the Spider's Web, is mostly
 * pale sky — put the title at 2.04:1 behind its median pixel, and 8 of the first 20 feed titles under
 * 3:1 behind their brightest pixels. Axe cannot see it: the background is an image.
 *
 * The probe is the one that found it. For each text line of the heading it hides the line, shoots
 * exactly the area behind it, and holds the line's own colour to 4.5:1 against both the median
 * pixel and the brightest 5% of pixels there — a title is unreadable where its brightest
 * background is, not on average.
 *
 * Twenty generated backdrops stand in for the twenty real ones, chosen to be worse: flat
 * near-whites, pale skies lit from the top and from the bottom, pastels, high-contrast stripes and
 * speckle, and a bright patch exactly where the text sits. Names and rating states vary too, because
 * a title that wraps to three lines climbs further up the art, and a rated title carries one more
 * line (the prediction note) than an unrated one.
 */

const ART_COUNT = 20;

const NAMES = [
  "The Girl in the Spider's Web",
  "Where the Wild Things Are",
  "Every Secret Thing",
  "The Girl in the Spider's Web: A New Dragon Tattoo Story Told Again",
  "Up",
];

/** Twenty light or busy backdrops, drawn in the page so no image library is needed on the Node side. */
async function makeArt(page: Page): Promise<Buffer[]> {
  const encoded = await page.evaluate((count) => {
    const pale: [number, number, number][] = [
      [250, 250, 245], [235, 245, 255], [255, 240, 180], [255, 225, 235], [225, 250, 225],
      [215, 215, 215], [200, 230, 255], [255, 255, 255], [245, 235, 210], [190, 220, 245],
    ];
    const out: string[] = [];
    for (let n = 0; n < count; n++) {
      const canvas = document.createElement("canvas");
      canvas.width = 128;
      canvas.height = 64;
      const ctx = canvas.getContext("2d")!;
      const [r, g, b] = pale[n % pale.length]!;
      const flat = `rgb(${r},${g},${b})`;
      switch (Math.floor(n / pale.length)) {
        case 0:
          if (n % 3 === 0) {
            ctx.fillStyle = flat;
            ctx.fillRect(0, 0, 128, 64);
          } else {
            // Sky lit from the top (n % 3 === 1) or from the bottom, where the text sits.
            const sky = ctx.createLinearGradient(0, 0, 0, 64);
            sky.addColorStop(n % 3 === 1 ? 0 : 1, "rgb(255,255,255)");
            sky.addColorStop(n % 3 === 1 ? 1 : 0, flat);
            ctx.fillStyle = sky;
            ctx.fillRect(0, 0, 128, 64);
          }
          break;
        default:
          // Busy art: dark ground with white stripes, speckle, or a white blaze in the lower left.
          ctx.fillStyle = "rgb(30,30,30)";
          ctx.fillRect(0, 0, 128, 64);
          ctx.fillStyle = flat;
          if (n % 3 === 0) for (let y = 0; y < 64; y += 4) ctx.fillRect(0, y, 128, 2);
          else if (n % 3 === 1) {
            for (let y = 0; y < 64; y += 2) for (let x = (y / 2) % 2; x < 128; x += 2) ctx.fillRect(x, y, 1, 1);
          } else ctx.fillRect(0, 28, 96, 36);
      }
      out.push(canvas.toDataURL("image/png").split(",")[1]!);
    }
    return out;
  }, ART_COUNT);
  return encoded.map((data) => Buffer.from(data, "base64"));
}

const titleFor = (n: number) => ({
  titleId: `00000000-0000-0000-0000-0000000007${String(n).padStart(2, "0")}`,
  mediaType: "Movie",
  tmdbId: 7000 + n,
  name: NAMES[n % NAMES.length]!,
  year: 2018,
  overview: "A hacker with a past she cannot outrun.",
  whyThis: "Shares the cold dread you rate highly.",
  tagline: null,
  runtimeMinutes: 115,
  certification: "R",
  genres: ["action", "thriller", "crime"],
  network: null,
  status: "Released",
  traktRating: 6.1,
  traktVotes: 12000,
  crowdRating: 6.3,
  crowdVotes: 12000,
  posterPath: null,
  backdropPath: `/art-${n}.png`,
  trailerUrl: null,
  cast: [],
  directors: [],
  writers: [],
  prediction: { predictedRating: 6.6, scoredAt: "2026-10-25T10:00:00Z", contributions: [] },
  contributionDetails: [],
  userState: {
    isFullyWatched: false, plays: 0,
    // Every other title is rated, which puts the prediction note under the heading.
    userRating: n % 2 === 0 ? null : 7,
    savedForLater: false, notInterested: false, dropped: false,
  },
});

async function bootSheets(page: Page): Promise<void> {
  await stubEventSource(page);
  await page.goto("about:blank");
  const art = await makeArt(page);
  await page.route(/image\.tmdb\.org/, (route) => {
    const n = Number(/art-(\d+)\.png/.exec(route.request().url())?.[1] ?? 0);
    return route.fulfill({ status: 200, contentType: "image/png", body: art[n]! });
  });
  await mockJson(page, "**/api/auth/me", SESSION_FOUNDER);
  await mockJson(page, "**/api/sync/status", {
    pipelineStage: "FeedReady", connectionStatus: "Active", lastDeltaSyncAt: null,
    lastFullReconcileAt: null, activeJob: null, recentJobs: [], outboxPending: 0, outboxDeadLetters: 0,
  });
  await page.route(
    (u) => /^\/api\/titles\/movie\/\d+(\/(lens|providers))?$/.test(u.pathname),
    (route) => {
      const [, id, tail] = /\/api\/titles\/movie\/(\d+)(?:\/(\w+))?$/.exec(new URL(route.request().url()).pathname)!;
      const body =
        tail === "lens"
          ? { source: "fresh", lens: null, hook: null, caution: null, anchors: [], mood: null, whenToWatch: null, generatedAt: null }
          : tail === "providers"
            ? { region: "US", attribution: "Data from JustWatch via TMDB", tmdbWatchPage: null, providers: [] }
            : titleFor(Number(id) - 7000);
      return route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify(body) });
    }
  );
  await mockJson(page, "**/api/feed/more", { items: [], exhausted: true });
}

async function openSheet(page: Page, n: number): Promise<void> {
  await page.goto(`/title/movie/${7000 + n}`);
  const sheet = page.getByTestId("title-detail");
  await expect(sheet.locator("h1")).toHaveText(titleFor(n).name);
  // The art has decoded, or the reading could be over the dark placeholder and prove nothing.
  await expect
    .poll(() => sheet.locator("img").first().evaluate((img: HTMLImageElement) => img.complete && img.naturalWidth > 0))
    .toBe(true);
  // The sheet's rise animation (it animates `transform`) has settled.
  await expect
    .poll(async () => {
      const a = await sheet.boundingBox();
      await page.waitForTimeout(50);
      const b = await sheet.boundingBox();
      return a && b && a.y === b.y;
    })
    .toBe(true);
}

/** Every text line of the heading block: the title, the prediction note when rated, and the meta line. */
const LINES = ['h1', '[data-testid="title-prediction-note"]', '[data-testid="title-meta"]'];

/**
 * The line's own colour against the median and the 95th-percentile background pixel behind it,
 * shot with the line hidden so its glyphs are not part of its own background.
 */
async function lineContrast(page: Page, selector: string): Promise<{ median: number; brightest: number } | null> {
  const line = page.getByTestId("title-detail").locator(selector);
  if ((await line.count()) === 0) return null;
  const colour = await line.evaluate((el) => getComputedStyle(el).color);
  const box = (await line.boundingBox())!;
  await line.evaluate((el) => ((el as HTMLElement).style.visibility = "hidden"));
  const shot = (await page.screenshot({ clip: box, animations: "disabled" })).toString("base64");
  await line.evaluate((el) => ((el as HTMLElement).style.visibility = ""));
  return page.evaluate(
    async ({ data, colour }) => {
      const channel = (c: number) => {
        const s = c / 255;
        return s <= 0.03928 ? s / 12.92 : ((s + 0.055) / 1.055) ** 2.4;
      };
      const lum = (r: number, g: number, b: number) => 0.2126 * channel(r) + 0.7152 * channel(g) + 0.0722 * channel(b);
      const [tr, tg, tb] = colour.match(/\d+(\.\d+)?/g)!.map(Number) as [number, number, number];
      const text = lum(tr, tg, tb);
      const img = new Image();
      img.src = `data:image/png;base64,${data}`;
      await img.decode();
      const canvas = document.createElement("canvas");
      canvas.width = img.width;
      canvas.height = img.height;
      const ctx = canvas.getContext("2d")!;
      ctx.drawImage(img, 0, 0);
      const { data: px } = ctx.getImageData(0, 0, img.width, img.height);
      const lums: number[] = [];
      for (let i = 0; i < px.length; i += 4) lums.push(lum(px[i]!, px[i + 1]!, px[i + 2]!));
      lums.sort((a, b) => a - b);
      const ratio = (bg: number) => (Math.max(text, bg) + 0.05) / (Math.min(text, bg) + 0.05);
      return {
        median: ratio(lums[Math.floor(lums.length / 2)]!),
        brightest: ratio(lums[Math.floor(lums.length * 0.95)]!),
      };
    },
    { data: shot, colour }
  );
}

for (const viewport of [
  { width: 390, height: 844 },
  { width: 834, height: 1194 },
  { width: 1440, height: 900 },
]) {
  test.describe(`title sheet heading at ${viewport.width}×${viewport.height}`, () => {
    test.use({ viewport });

    test("every heading line is at least 4.5:1 behind its brightest 5% over twenty light or busy backdrops", async ({ page }) => {
      test.setTimeout(240_000);
      await bootSheets(page);
      const failures: string[] = [];
      for (let n = 0; n < ART_COUNT; n++) {
        await openSheet(page, n);
        for (const selector of LINES) {
          const result = await lineContrast(page, selector);
          if (!result) continue;
          if (result.median < 4.5 || result.brightest < 4.5) {
            failures.push(`art ${n} ${selector}: median ${result.median.toFixed(2)}:1, brightest 5% ${result.brightest.toFixed(2)}:1`);
          }
        }
      }
      expect(failures).toEqual([]);
    });
  });
}

test.describe("title sheet heading on a phone", () => {
  test.use({ viewport: { width: 390, height: 844 } });

  test("the heading stacks under the art on the sheet's ink, the way the Home hero stacks", async ({ page }) => {
    await bootSheets(page);
    await openSheet(page, 3);
    const sheet = page.getByTestId("title-detail");
    const art = (await sheet.locator("img").first().boundingBox())!;
    const heading = (await sheet.locator("h1").boundingBox())!;
    expect(heading.y).toBeGreaterThanOrEqual(art.y + art.height - 1);
    // No scrim darkens art that carries no text.
    await expect(sheet.locator(".reel-scrim")).toBeHidden();
  });
});

test.describe("title sheet heading from sm", () => {
  test.use({ viewport: { width: 834, height: 1194 } });

  test("the heading still overlays the art", async ({ page }) => {
    await bootSheets(page);
    await openSheet(page, 3);
    const sheet = page.getByTestId("title-detail");
    const art = (await sheet.locator("img").first().boundingBox())!;
    const heading = (await sheet.locator("h1").boundingBox())!;
    expect(heading.y).toBeGreaterThanOrEqual(art.y);
    expect(heading.y + heading.height).toBeLessThanOrEqual(art.y + art.height + 0.5);
  });
});
