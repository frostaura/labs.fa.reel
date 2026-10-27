import { test, expect, type Page } from "@playwright/test";
import { SESSION_FOUNDER, mockJson, stubEventSource } from "./helpers";

/**
 * The taste narrative on Taste DNA — what Reel believes about you, and your correction to it.
 *
 * Editable is not decoration here. This text is the personalisation context every downstream model
 * call rides on, so a wrong inference the user cannot reach becomes a standing bias. These specs
 * pin the three things that make it correctable: the edit round-trips, the edit is visibly the
 * user's own, and regenerating warns that it clears the edit rather than doing so silently.
 */

const NARRATIVE = {
  text:
    "You reach for bleak, procedural crime before anything else. You are comfortable sitting with "
    + "something slow, and you would rather be made to work than carried. You are harder on what you "
    + "watch than most people are. You have a real track record with Denis Villeneuve. You rate films "
    + "far more often than series. Lately you have been drifting toward drama.",
  isEdited: false,
  version: 3,
  ratingsCount: 2044,
  generatedAt: "2026-09-08T03:00:00Z",
  isStale: false,
  regenerating: false,
  writerAvailable: true,
};

const DNA = {
  ratingsCount: 2044,
  titlesWatched: 3353,
  hoursWatched: 4210,
  showCompletionRate: 0.62,
  contrarianScore: 0.4,
  userMean: 6.7,
  topGenres: [{ genre: "thriller", affinity: 8.6, count: 214 }],
  eras: [{ decade: 2010, affinity: 8.1, count: 412 }],
  drift: [],
  creators: [],
  topActors: [],
  histogram: [{ rating: 8, count: 300 }],
};

/**
 * A STATEFUL narrative route: GET serves whatever the last PUT left behind.
 *
 * A static GET mock cannot test an edit at all. The panel invalidates its own cache on save and
 * immediately refetches — and it polls while a rewrite is queued — so a fixed GET body would
 * overwrite the saved text a moment after it appeared, and the test would be asserting the mock
 * rather than the component. Returns the recorded PUT bodies so the request itself can be checked.
 *
 * Registered AFTER bootDna, deliberately: Playwright gives the most recently registered handler
 * precedence, so installing this first would leave bootDna's static mock in charge and the test
 * would fail for a reason that has nothing to do with the component.
 */
async function statefulNarrative(page: Page, initial: Record<string, unknown>) {
  const state = { current: initial, puts: [] as (string | null)[] };
  await page.route(
    (u: URL) => u.pathname === "/api/taste/narrative",
    async (route) => {
      if (route.request().method() === "PUT") {
        const body = route.request().postData();
        state.puts.push(body);
        const parsed = JSON.parse(body ?? "{}") as { text: string | null };
        state.current = parsed.text
          ? { ...state.current, text: parsed.text, isEdited: true }
          : { ...state.current, text: NARRATIVE.text, isEdited: false };
      }

      return route.fulfill({
        status: 200,
        contentType: "application/json",
        body: JSON.stringify(state.current),
      });
    },
  );
  return state;
}

async function bootDna(page: Page, narrative: unknown = NARRATIVE) {
  await stubEventSource(page);
  await mockJson(page, "**/api/auth/me", SESSION_FOUNDER);
  await mockJson(page, "**/api/sync/status", {
    pipelineStage: "FeedReady", connectionStatus: "Active", lastDeltaSyncAt: null,
    lastFullReconcileAt: null, activeJob: null, recentJobs: [], outboxPending: 0, outboxDeadLetters: 0,
  });
  await mockJson(page, "**/api/taste", DNA);
  await mockJson(page, "**/api/taste/dislikes", {
    total: 0, windowed: 0, items: [], profile: { genres: [], tones: [], seenEnoughCount: 0 },
  });
  await mockJson(page, "**/api/taste/narrative", narrative);
  await page.goto("/dna");
}

test.describe("taste narrative", () => {
  test("shows the narrative above the charts with what it was written from", async ({ page }) => {
    await bootDna(page);

    await expect(page.getByTestId("taste-narrative")).toBeVisible();
    await expect(page.getByTestId("narrative-text")).toContainText("bleak, procedural crime");
    // The provenance line is the honesty: how many ratings, and when.
    await expect(page.getByTestId("narrative-provenance")).toContainText("2,044 ratings");
    await expect(page.getByTestId("narrative-provenance")).not.toContainText("your words");
  });

  test("a stale narrative says so rather than quietly describing someone you have stopped being", async ({ page }) => {
    await bootDna(page, { ...NARRATIVE, isStale: true });

    await expect(page.getByTestId("narrative-provenance")).toContainText("your taste has moved since");
    // Still shown — old prose beats no prose, and it is still the best description Reel has.
    await expect(page.getByTestId("narrative-text")).toBeVisible();
  });

  test("an edit round-trips and the saved text is what renders afterwards", async ({ page }) => {
    await bootDna(page);
    const state = await statefulNarrative(page, NARRATIVE);

    const edited = "You only ever watch heist films, and you know it.";
    await page.getByTestId("narrative-edit").click();
    // R1-43: the scoring model never reads the narrative (ClaimsAuditTests), so the note names the
    // written passes that do, and says the predicted ratings do not.
    const note = page.getByTestId("narrative-editor-note");
    await expect(note).toContainText("Reel's written passes read this when they run");
    await expect(note).toContainText("the predicted ratings never read it");
    await expect(note).not.toContainText("before every pick");
    await page.getByTestId("narrative-editor").fill(edited);
    await page.getByTestId("narrative-save").click();

    // Survives the panel's own invalidate-and-refetch, which is the point of the round trip.
    await expect(page.getByTestId("narrative-text")).toHaveText(edited);
    // Marked as the user's own — the product must not pass an edit off as its own judgement.
    await expect(page.getByTestId("narrative-provenance")).toContainText("your words");
    expect(state.puts.join()).toContain("heist films");
  });

  test("cancelling an edit discards the draft and keeps the generated text", async ({ page }) => {
    await bootDna(page);

    await page.getByTestId("narrative-edit").click();
    await page.getByTestId("narrative-editor").fill("nonsense that must not be saved");
    await page.getByTestId("narrative-cancel").click();

    await expect(page.getByTestId("narrative-text")).toContainText("bleak, procedural crime");
    await expect(page.getByTestId("narrative-editor")).toHaveCount(0);
  });

  test("an edited narrative can be reverted to what Reel wrote", async ({ page }) => {
    const edited = { ...NARRATIVE, isEdited: true, text: "You only watch heists." };
    await bootDna(page, edited);
    const state = await statefulNarrative(page, edited);

    await page.getByTestId("narrative-edit").click();
    await page.getByTestId("narrative-revert").click();

    await expect(page.getByTestId("narrative-text")).toContainText("bleak, procedural crime");
    await expect(page.getByTestId("narrative-provenance")).not.toContainText("your words");
    // Reverting sends an explicit null — the server reads that as "drop my edit", not as an
    // empty narrative.
    expect(state.puts.join()).toContain("null");
  });

  test("a failed save keeps the editor open with the words, says so, and Retry saves them", async ({ page }) => {
    await bootDna(page);
    const puts: string[] = [];
    let fail = true;
    await page.route(
      (u: URL) => u.pathname === "/api/taste/narrative",
      (route) => {
        if (route.request().method() !== "PUT") return route.fallback();
        puts.push(route.request().postData() ?? "");
        return fail
          ? route.fulfill({ status: 500, contentType: "application/json", body: "{}" })
          : route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify({ ...NARRATIVE, text: "Heists only.", isEdited: true }) });
      },
    );

    await page.getByTestId("narrative-edit").click();
    await page.getByTestId("narrative-editor").fill("Heists only.");
    await page.getByTestId("narrative-save").click();

    const status = page.getByTestId("narrative-save-status");
    await expect(status).toContainText("Couldn't save your words — nothing changed.");
    await expect(status).toHaveAttribute("data-tone", "danger");
    // The correction is not lost: the editor is still open with it.
    await expect(page.getByTestId("narrative-editor")).toHaveValue("Heists only.");

    fail = false;
    await page.getByTestId("narrative-save-status-retry").click();
    await expect(page.getByTestId("narrative-editor")).toHaveCount(0);
    await expect(page.getByTestId("narrative-save-status")).toHaveCount(0);
    expect(puts).toHaveLength(2);
    expect(puts[1]).toContain("Heists only.");
  });

  test("a failed revert keeps the editor open and says so", async ({ page }) => {
    const edited = { ...NARRATIVE, isEdited: true, text: "You only watch heists." };
    await bootDna(page, edited);
    await page.route(
      (u: URL) => u.pathname === "/api/taste/narrative",
      (route) =>
        route.request().method() === "PUT"
          ? route.fulfill({ status: 503, contentType: "application/json", body: "{}" })
          : route.fallback(),
    );

    await page.getByTestId("narrative-edit").click();
    await page.getByTestId("narrative-revert").click();
    await expect(page.getByTestId("narrative-save-status")).toContainText("Couldn't go back to Reel's version — nothing changed.");
    await expect(page.getByTestId("narrative-editor")).toBeVisible();
  });

  test("regenerate queues a rewrite and warns that it clears the edit", async ({ page }) => {
    await bootDna(page);

    let queued = false;
    await page.route("**/api/taste/narrative/regenerate", async (route) => {
      queued = true;
      return route.fulfill({
        status: 202, contentType: "application/json", body: JSON.stringify({ queued: true }),
      });
    });

    const button = page.getByTestId("narrative-regenerate");
    // The warning lives on the control, not in a dialog nobody reads — but it IS stated.
    await expect(button).toHaveAttribute("title", /clears your edits/i);
    await button.click();

    await expect.poll(() => queued).toBe(true);
  });

  test("with no narrative and no writer, it says why rather than showing an empty panel", async ({ page }) => {
    await bootDna(page, {
      text: null, isEdited: false, version: null, ratingsCount: 0, generatedAt: null,
      isStale: false, regenerating: false, writerAvailable: false,
    });

    await expect(page.getByTestId("taste-narrative")).toContainText("without a language model");
    await expect(page.getByTestId("narrative-regenerate")).toHaveCount(0);
  });

  test("it does not poll while nothing is pending (I*2)", async ({ page }) => {
    await page.clock.install();
    await bootDna(page);
    await expect(page.getByTestId("narrative-text")).toBeVisible();

    let gets = 0;
    await page.route(
      (u: URL) => u.pathname === "/api/taste/narrative",
      (route) => {
        gets += 1;
        return route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify(NARRATIVE) });
      },
    );
    // Three of the old 4 s polls' worth of time.
    await page.clock.runFor(13_000);
    expect(gets).toBe(0);
  });

  test("a pending rewrite is polled until it lands, then polling stops", async ({ page }) => {
    await page.clock.install();
    await bootDna(page, { ...NARRATIVE, regenerating: true });
    await expect(page.getByTestId("narrative-regenerate")).toContainText("Writing…");

    const REWRITTEN = "You have been drifting toward slow drama.";
    let gets = 0;
    await page.route(
      (u: URL) => u.pathname === "/api/taste/narrative",
      (route) => {
        gets += 1;
        const landed = gets >= 2;
        return route.fulfill({
          status: 200,
          contentType: "application/json",
          body: JSON.stringify(landed ? { ...NARRATIVE, text: REWRITTEN, regenerating: false } : { ...NARRATIVE, regenerating: true }),
        });
      },
    );

    await page.clock.runFor(4_100);
    await expect.poll(() => gets).toBe(1);
    await page.clock.runFor(4_100);
    await expect(page.getByTestId("narrative-text")).toHaveText(REWRITTEN);
    const afterLanding = gets;

    await page.clock.runFor(13_000);
    expect(gets).toBe(afterLanding);
  });

  test("a queued rewrite shows as in progress and the button is not clickable twice", async ({ page }) => {
    await bootDna(page, { ...NARRATIVE, regenerating: true });

    await expect(page.getByTestId("narrative-regenerate")).toContainText("Writing…");
    await expect(page.getByTestId("narrative-regenerate")).toBeDisabled();
  });
});
