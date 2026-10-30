import { test, expect, type Page } from "@playwright/test";
import { SESSION_FOUNDER, askEnd, askPush, mockJson, stubAskReel, stubEventSource, waitForAsk } from "./helpers";

/**
 * Ask Reel on a phone.
 *
 * The composer is `fixed` and so is the mobile tab bar, and for months they occupied the same
 * strip of screen: the tab bar sits at `z-40`, so on every phone width the Send button and the
 * text input were *behind* it — a tap on Send navigated to /dna instead. A visibility assertion
 * cannot see this (the composer is perfectly visible, just covered), so these tests hit-test
 * instead: `document.elementFromPoint` at the centre of each control must resolve back INSIDE
 * that control. Three widths, because the offending overlap is a function of the tab bar's
 * height plus the safe-area inset, not of the layout breakpoint.
 *
 * Also locks the transcript's `log` landmark: Ask Reel's replies arrive asynchronously, so a
 * screen-reader user hears them only if the region announces itself.
 *
 * …and the two controls the 44 px sweep missed. `dca1ed3` raised every Ask Reel control this page
 * owns to 44 px and deliberately excluded the "foreign" ones — the card overlay and
 * `BrowseFilters` — as other people's files. They are still real tap targets on a phone, and they
 * measured 27 x 27 and 23 px tall. This file asserts them because it is the phone file: the
 * exclusion was about who edits the component, and the user cannot tell the difference.
 */

// The reference phone. `test.use` per file rather than a second Playwright project, so CI's
// matrix stays at one browser (the precedent is `mobile-nav.spec.ts`).
test.use({ viewport: { width: 375, height: 812 } });

async function bootSearch(page: Page) {
  await stubEventSource(page);
  await stubAskReel(page);
  await mockJson(page, "**/api/auth/me", SESSION_FOUNDER);
  await mockJson(page, "**/api/sync/status", {
    pipelineStage: "FeedReady", connectionStatus: "Active", lastDeltaSyncAt: null,
    lastFullReconcileAt: null, activeJob: null, recentJobs: [], outboxPending: 0, outboxDeadLetters: 0,
  });
  // A cold /search resumes the latest conversation; 404 = there is none, so the page stays fresh.
  await mockJson(page, "**/api/search/conversations/latest", {}, 404);
  await page.goto("/search");
}

/**
 * Hit-tests one element: is the topmost node at its centre point the element itself (or something
 * inside it)? Returns what was actually hit so a failure names the covering element rather than
 * just reporting `false`.
 */
async function hitTest(page: Page, testId: string): Promise<{ ok: boolean; hit: string }> {
  return page.evaluate((id) => {
    const el = document.querySelector(`[data-testid="${id}"]`);
    if (!el) return { ok: false, hit: `no element with data-testid="${id}"` };
    const box = el.getBoundingClientRect();
    const top = document.elementFromPoint(box.left + box.width / 2, box.top + box.height / 2);
    if (!top) return { ok: false, hit: "nothing (point is outside the viewport)" };
    const named = top.closest("[data-testid]")?.getAttribute("data-testid");
    const href = top.closest("a")?.getAttribute("href");
    return {
      ok: el.contains(top) || el === top,
      hit: named ?? href ?? top.tagName.toLowerCase(),
    };
  }, testId);
}

/** Both composer controls must be reachable by a real tap at whatever width is current. */
async function expectComposerReachable(page: Page, label: string) {
  await expect(page.getByTestId("askreel-composer")).toBeVisible();
  const send = await hitTest(page, "askreel-send");
  expect(send.ok, `${label}: a tap on Send lands on "${send.hit}"`).toBe(true);
  const input = await hitTest(page, "askreel-composer");
  expect(input.ok, `${label}: a tap on the input lands on "${input.hit}"`).toBe(true);
}

/** WCAG 2.1 AA, success criterion 2.5.5 target size. Not a house style — the published minimum. */
const TOUCH_MIN = 44;
/**
 * `getBoundingClientRect` on a box inside a transformed ancestor (the poster wrapper carries a
 * `group-hover:scale-[1.03]` transition) comes back through a float matrix, and a `min-h-11` box
 * that IS 44 px measured 43.999969482421875 on the CI runner — once in ten local runs, twice on
 * GitHub. A target-size rule is about CSS pixels a thumb can hit, and 3e-5 of a pixel is not a
 * smaller target, so the floor tolerates float noise below a hundredth of a pixel and nothing
 * more: a genuine 43.5 px box still fails.
 */
const TOUCH_TOLERANCE = 0.01;

const CONVERSATION = "5c0a1b2c-0000-4000-8000-000000000001";

function answerCard(n: number) {
  return {
    titleId: `00000000-0000-0000-0000-00000000000${n}`,
    mediaType: "Movie",
    tmdbId: 900 + n,
    name: `Title ${n}`,
    year: 2021,
    posterPath: null,
    genres: ["thriller"],
    similarity: 0.5,
    predictedRating: 7.2,
    why: "Because.",
    seen: false,
  };
}

/**
 * /search with an ANSWER on it — the only state in which either offender exists. The card overlay
 * needs cards, and the media segment is gated on `turns.length > 0`, so a cold page can measure
 * neither. Resuming a stored thread reaches both in one fixture.
 */
async function bootAnswered(page: Page) {
  await stubEventSource(page);
  await stubAskReel(page);
  await mockJson(page, "**/api/auth/me", SESSION_FOUNDER);
  await mockJson(page, "**/api/sync/status", {
    pipelineStage: "FeedReady", connectionStatus: "Active", lastDeltaSyncAt: null,
    lastFullReconcileAt: null, activeJob: null, recentJobs: [], outboxPending: 0, outboxDeadLetters: 0,
  });
  const resume = {
    id: CONVERSATION,
    title: "something tense",
    lastCardsAt: new Date(Date.now() - 60_000).toISOString(),
    turns: [
      { sequence: 0, role: "user", text: "something tense", kind: "ask", status: "Completed" },
      { sequence: 1, role: "assistant", text: "Two that fit.", kind: "ask", status: "Completed" },
    ],
    cards: [answerCard(1), answerCard(2)],
    reason: "2 picks ranked for you, from 40 pulled.",
    removed: 0,
    lastQuery: "something tense",
    mediaTypes: [],
    shown: [],
  };
  await page.route(
    (u) => u.pathname === "/api/search/conversations",
    (route) => route.fulfill({ status: 200, contentType: "application/json", body: "[]" }),
  );
  await page.route(
    (u) => u.pathname.startsWith("/api/search/conversations/"),
    (route) => {
      const id = decodeURIComponent(new URL(route.request().url()).pathname.split("/").pop()!);
      return id === CONVERSATION
        ? route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify(resume) })
        : route.fulfill({ status: 404, contentType: "application/json", body: "{}" });
    },
  );
  await page.addInitScript((id) => localStorage.setItem("reel.ask.conversationId", id as string), CONVERSATION);
  await page.goto("/search");
  await expect(page.getByTestId("semantic-results")).toBeVisible();
}

/** Every box, named, so a failure reports the control and its measured size rather than `false`. */
async function boxesOf(page: Page, selector: string) {
  return page.evaluate((sel) => {
    return Array.from(document.querySelectorAll(sel)).map((el) => {
      const r = el.getBoundingClientRect();
      return {
        label: el.getAttribute("data-testid") ?? el.getAttribute("aria-label") ?? el.tagName.toLowerCase(),
        x: r.x, y: r.y, width: r.width, height: r.height,
      };
    });
  }, selector);
}

test.describe("Ask Reel on a phone", () => {
  test("the composer is tappable above the mobile tab bar", async ({ page }) => {
    await bootSearch(page);
    // Typed, so Send is enabled — a disabled button is still hit-tested, but the assertion should
    // describe the control the user actually reaches for.
    await page.getByTestId("askreel-composer").fill("something tense");

    await expect(page.getByTestId("mobile-tabbar")).toBeVisible();
    await expectComposerReachable(page, "375×812");
  });

  test("the composer stays tappable on a taller phone and on the smallest one", async ({ page }) => {
    await bootSearch(page);
    await page.getByTestId("askreel-composer").fill("something tense");

    // A modern large phone: more safe-area inset at the bottom.
    await page.setViewportSize({ width: 390, height: 844 });
    await expectComposerReachable(page, "390×844");

    // The smallest width the app claims to support — the tab bar's four labels are tightest here.
    await page.setViewportSize({ width: 320, height: 568 });
    await expectComposerReachable(page, "320×568");
  });

  test("the composer sits flush on the tab bar, with no strip of page between them", async ({ page }) => {
    // PH-06. The composer was offset by a hard-coded 3.5rem (56 px) while the bar is as tall as its
    // icon and label make it — 49 px in both engines — so a 7 px strip of page showed through
    // between them, the footer's "Reel by FrostAura" in it. Measured, never assumed: the seam is
    // asserted at every phone width, and on desktop, where the bar is gone, against the viewport.
    // The settings read the page makes once it has settled; this test stays long enough to see it.
    await mockJson(page, "**/api/settings/filters", {
      excludeGenres: [], includeGenres: [], excludeKeywords: [], maturityCeiling: null,
      minPredictedRating: null, languages: ["en"], minCrowdRating: null, minYear: null, maxYear: null,
    });
    await bootSearch(page);
    await expect(page.getByTestId("askreel-composer")).toBeVisible();
    const seam = () => page.evaluate(() => {
      const form = document.querySelector<HTMLElement>("[data-testid='askreel-composer']")!.closest("form")!;
      const bar = document.querySelector<HTMLElement>("[data-testid='mobile-tabbar']")!;
      const f = form.getBoundingClientRect();
      const b = bar.getBoundingClientRect();
      // One pixel above the bar's top edge, mid-screen: that row must be the composer's own.
      const above = document.elementFromPoint(window.innerWidth / 2, b.top - 1);
      return {
        gap: b.top - f.bottom, barVisible: b.height > 0, viewport: window.innerHeight, formBottom: f.bottom,
        aboveIsComposer: !!above && form.contains(above),
      };
    });
    for (const [width, height] of [[375, 812], [390, 844], [320, 568]] as const) {
      await page.setViewportSize({ width, height });
      await expect(page.getByTestId("mobile-tabbar")).toBeVisible();
      await expect.poll(async () => Math.abs((await seam()).gap), { message: `${width}×${height}: composer-to-bar gap` })
        .toBeLessThan(0.5);
      expect((await seam()).aboveIsComposer, `${width}×${height}: the row above the bar is the composer`).toBe(true);
    }
    // One field varied: a bar 34 px taller — an iPhone's home-indicator inset, which a desktop
    // engine reports as 0 — must carry the composer up with it. A new constant would pass the
    // widths above and fail here.
    await page.addStyleTag({ content: "[data-testid='mobile-tabbar'] { padding-bottom: 34px !important; }" });
    await expect.poll(async () => Math.abs((await seam()).gap), { message: "taller bar: composer-to-bar gap" }).toBeLessThan(0.5);
    // Desktop: no bar, so the composer is the bottom edge of the screen.
    await page.setViewportSize({ width: 1024, height: 768 });
    await expect(page.getByTestId("mobile-tabbar")).toBeHidden();
    await expect.poll(async () => { const s = await seam(); return Math.abs(s.viewport - s.formBottom); }).toBeLessThan(0.5);
  });

  test("the composer wraps and grows to four lines, and stops there", async ({ page }) => {
    // F6-18. It was a single-line `<input>`, 239 px wide at this viewport — about 36 characters of
    // a question the server accepts up to 1 000 of. A 200-character question measured
    // `scrollWidth 1241` and a 1 000-character one `scrollWidth 6101`: the user could not read
    // back what they were about to send, while the bubble AFTER sending has been readable since
    // F6-02.
    await bootSearch(page);
    const composer = page.getByTestId("askreel-composer");
    const heightOf = () => composer.evaluate((el) => el.getBoundingClientRect().height);
    const overflowsSideways = () => composer.evaluate((el) => el.scrollWidth > el.clientWidth + 1);

    // The cap: four lines. Measured, not assumed per engine — WebKit lays the field out 244 px wide
    // at 390 against Chromium's 254, so the same sentence wraps to a different number of lines.
    const cap = 116;
    const scrollsDown = () => composer.evaluate((el) => el.scrollHeight > el.clientHeight + 1);

    expect(await heightOf()).toBeGreaterThanOrEqual(44);
    const resting = await heightOf();

    // A question two lines long in both engines (IR2S-05: the old 95-character one already hit the
    // cap in WebKit, so "it grows, then it stops" could not be told apart there).
    await composer.fill("a tense thriller with a clever ending, no ghosts");
    const grown = await heightOf();
    expect(grown).toBeGreaterThan(resting);
    // Still growing, not yet at the cap — otherwise the stop below proves nothing.
    expect(grown).toBeLessThan(cap);
    expect(await scrollsDown()).toBe(false);
    // Wrapped, not scrolled sideways past the edge of the field.
    expect(await overflowsSideways()).toBe(false);

    // …and it stops. A composer that grows without limit eats the answer it is asking about.
    await composer.fill("x".repeat(1000).replace(/(.{40})/g, "$1 "));
    const full = await heightOf();
    expect(full).toBeGreaterThan(grown);
    expect(full).toBe(cap);
    // What does not fit scrolls inside the field, top to bottom — never sideways.
    expect(await scrollsDown()).toBe(true);
    expect(await overflowsSideways()).toBe(false);

    // Deleting it back shrinks the field again rather than leaving a four-line box over one word.
    await composer.fill("short");
    expect(await heightOf()).toBe(resting);
  });

  test("Enter sends and Shift+Enter breaks the line", async ({ page }) => {
    // F3-12. As an `<input>` Shift+Enter "submitted" because there was no second line to make and
    // the form's default ran — the distinction only became real once the field could hold one.
    await bootSearch(page);
    const composer = page.getByTestId("askreel-composer");

    await composer.fill("a tense heist");
    await composer.press("Shift+Enter");
    await composer.type("with a clever ending");
    expect(await composer.inputValue()).toBe("a tense heist\nwith a clever ending");
    // Nothing was sent by the newline.
    expect(await page.evaluate(() => (window as unknown as { __ask: { requests: unknown[] } }).__ask.requests.length)).toBe(0);

    await composer.press("Enter");
    await expect(page.getByTestId("askreel-banner")).toBeVisible();
    const sent = await page.evaluate(() => (window as unknown as { __ask: { requests: unknown[] } }).__ask.requests);
    expect((sent as { query: string }[])[0].query).toBe("a tense heist\nwith a clever ending");
    // …and the field is empty and back at its resting height for the next question.
    expect(await composer.inputValue()).toBe("");
    expect(await composer.evaluate((el) => el.getBoundingClientRect().height)).toBe(44);
  });

  test("the card overlay and the media segment are 44 px targets too", async ({ page }) => {
    // The two `dca1ed3` left behind, measured headed at this exact viewport: the three card quick
    // actions at 27 x 27 and each media segment button at 23 px tall. Both are foreign components
    // — which is why the sweep skipped them and why nothing has caught them since.
    await bootAnswered(page);

    const actions = await boxesOf(page, '[data-testid="semantic-results"] button');
    // The fixture is two cards x three actions; a zero here would make the loop below vacuous.
    expect(actions.length).toBe(6);
    for (const b of actions) {
      expect(b.height, `${b.label} is ${Math.round(b.width)} x ${Math.round(b.height)}`).toBeGreaterThanOrEqual(TOUCH_MIN - TOUCH_TOLERANCE);
      expect(b.width, `${b.label} is ${Math.round(b.width)} x ${Math.round(b.height)}`).toBeGreaterThanOrEqual(TOUCH_MIN - TOUCH_TOLERANCE);
    }

    const segment = await boxesOf(page, '[data-testid="browse-filters"] button');
    expect(segment.length).toBe(3);
    for (const b of segment) {
      expect(b.height, `${b.label} is ${Math.round(b.height)} px tall`).toBeGreaterThanOrEqual(TOUCH_MIN - TOUCH_TOLERANCE);
    }
  });

  test("growing the card overlay does not stack it on itself or push it off the poster", async ({ page }) => {
    // The other half of the fix: 44 px boxes in a column inside a ~163 px poster is the shape that
    // goes wrong. Three overlapping targets are worse than three small ones — the top one eats the
    // taps meant for the others — and one that hangs off the poster covers the card beneath it.
    await bootAnswered(page);

    const overlay = (await boxesOf(page, '[data-testid="card-quick-actions"]'))[0];
    const poster = (await boxesOf(page, '[data-testid="semantic-results"] > a > div'))[0];
    expect(poster, "no poster box").toBeTruthy();

    const actions = (await boxesOf(page, '[data-testid="semantic-results"] > a:first-child button'));
    expect(actions.length).toBe(3);
    for (let i = 1; i < actions.length; i++) {
      const above = actions[i - 1];
      expect(
        actions[i].y,
        `${actions[i].label} starts at ${Math.round(actions[i].y)}, above it ends at ${Math.round(above.y + above.height)}`,
      ).toBeGreaterThanOrEqual(above.y + above.height - 0.5);
    }

    const bottom = actions[actions.length - 1];
    expect(overlay.x, "overlay starts left of the poster").toBeGreaterThanOrEqual(poster.x - 0.5);
    expect(overlay.y, "overlay starts above the poster").toBeGreaterThanOrEqual(poster.y - 0.5);
    expect(
      bottom.y + bottom.height,
      `the stack ends at ${Math.round(bottom.y + bottom.height)}, the poster at ${Math.round(poster.y + poster.height)}`,
    ).toBeLessThanOrEqual(poster.y + poster.height + 0.5);
    expect(
      Math.max(...actions.map((a) => a.x + a.width)),
      "an action spills off the right edge of the poster",
    ).toBeLessThanOrEqual(poster.x + poster.width + 0.5);
  });

  test("a none-found answer with unchecked and held-back titles is announced as what is on screen", async ({ page }) => {
    // HREV4-PH-01 at 390×844. The status region said "Nothing left that matches" while the card
    // showed unchecked titles and a tray held back by the bar; both now read the same trays.
    await page.setViewportSize({ width: 390, height: 844 });
    await mockJson(page, "**/api/settings/filters", {
      excludeGenres: [], includeGenres: [], excludeKeywords: [], maturityCeiling: null,
      minPredictedRating: 6, languages: [], minCrowdRating: null, minYear: null, maxYear: null,
    });
    await bootSearch(page);
    await page.getByTestId("askreel-composer").fill("samurai films");
    await page.getByTestId("askreel-composer").press("Enter");
    await waitForAsk(page);
    await askPush(page, "assistant-message", {
      text: "I couldn't check what I found against what you asked this time.",
      conflict: false,
      degraded: false,
    });
    await askPush(page, "done", {
      results: [], streamed: 0, kept: 0, droppedByFloor: 3,
      belowFloor: [3, 4, 5].map((n) => ({ ...answerCard(n), predictedRating: 5.1 })),
      nearMiss: [],
      unchecked: [answerCard(1), answerCard(2)],
      mode: "NoneFound",
      supply: null,
      degraded: ["judge:2/2"],
      reason: "2 not checked against what you asked — shown below",
      timingsMs: null,
      debug: null,
    });
    await askEnd(page);

    await expect(page.getByTestId("askreel-none-found")).toContainText("Reel couldn’t check what it found");
    const status = page.getByTestId("askreel-status");
    await expect(status).toHaveText("Reel couldn’t check what it found — 2 unchecked titles shown, 3 held back by your 6.0 bar.");
    await expect(status).not.toContainText("Nothing left");
  });

  // HREV5-PH-01 and HREV5-PH-01-R1, at 390×844: a cold open of a stored thread with nothing left
  // in its grid. The status region names the card the strip renders. The two fixtures are one field
  // apart — the newest answer's mode — and `lastCardsAt` is set in both, as an earlier Picks turn
  // leaves it: the all-removed card is announced as all removed, the none-found card as none found.
  for (const [mode, card, said, never] of [
    [
      "Picks", "askreel-all-removed",
      "Picked up where you left off · 10 picks are gone from this answer — you’d already rated, watched, saved or dismissed them when it was given. You’ve watched or rated everything this thread found.",
      "no saved picks",
    ],
    [
      "NoneFound", "askreel-none-found",
      "Picked up where you left off · Nothing left that matches — Reel’s reply says why.",
      "You’ve watched or rated everything",
    ],
  ] as const) {
    test(`a resumed thread whose newest answer was ${mode} with nothing left is announced as the card on screen`, async ({ page }) => {
      await page.setViewportSize({ width: 390, height: 844 });
      await stubEventSource(page);
      await stubAskReel(page);
      await mockJson(page, "**/api/auth/me", SESSION_FOUNDER);
      await mockJson(page, "**/api/sync/status", {
        pipelineStage: "FeedReady", connectionStatus: "Active", lastDeltaSyncAt: null,
        lastFullReconcileAt: null, activeJob: null, recentJobs: [], outboxPending: 0, outboxDeadLetters: 0,
      });
      await mockJson(page, "**/api/settings/filters", {
        excludeGenres: [], includeGenres: [], excludeKeywords: [], maturityCeiling: null,
        minPredictedRating: null, languages: [], minCrowdRating: null, minYear: null, maxYear: null,
      });
      const stamp = new Date(Date.now() - 60_000).toISOString();
      const removed = mode === "Picks" ? 10 : 0;
      const resume = {
        id: CONVERSATION, title: "christopher nolan", lastCardsAt: stamp, lastTurnAt: stamp,
        turns: [
          { sequence: 0, role: "user", text: "christopher nolan", kind: "ask", status: "Completed" },
          { sequence: 1, role: "assistant", text: "Nolan picks.", kind: "ask", status: "Completed", mode: "Picks" },
          { sequence: 2, role: "user", text: "only the silent ones", kind: "ask", status: "Completed" },
          { sequence: 3, role: "assistant", text: "Nothing left that fits.", kind: "ask", status: "Completed", mode },
        ],
        cards: [], removed, removedEarlier: removed, mode, reason: null,
        lastQuery: "only the silent ones", mediaTypes: [], shown: [],
      };
      await page.route(
        (u) => u.pathname === "/api/search/conversations",
        (route) => route.fulfill({ status: 200, contentType: "application/json", body: "[]" }),
      );
      await page.route(
        (u) => u.pathname.startsWith("/api/search/conversations/"),
        (route) => {
          const id = decodeURIComponent(new URL(route.request().url()).pathname.split("/").pop()!);
          return id === CONVERSATION
            ? route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify(resume) })
            : route.fulfill({ status: 404, contentType: "application/json", body: "{}" });
        },
      );
      await page.addInitScript((id) => localStorage.setItem("reel.ask.conversationId", id as string), CONVERSATION);
      await page.goto("/search");

      await expect(page.getByTestId(card)).toBeVisible();
      const status = page.getByTestId("askreel-status");
      await expect(status).toHaveText(said);
      await expect(status).not.toContainText(never);
    });
  }

  test("transcript is a log landmark with speaker labels", async ({ page }) => {
    await bootSearch(page);

    const transcript = page.getByTestId("askreel-transcript");
    await expect(transcript).toHaveAttribute("role", "log");
    await expect(transcript).toHaveAttribute("aria-live", "polite");

    await page.getByTestId("askreel-composer").fill("tense small-town thriller");
    await page.getByTestId("askreel-composer").press("Enter");

    // The user's own turn, and Reel's reply, are each announced with who is speaking — without
    // the labels a screen reader reads one undifferentiated run of sentences.
    await expect(transcript.getByText("You said", { exact: true })).toHaveCount(1);
    await expect(transcript.getByText("tense small-town thriller")).toBeVisible();
  });
});
