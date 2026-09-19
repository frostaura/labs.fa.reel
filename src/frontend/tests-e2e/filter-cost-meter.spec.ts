import { test, expect, type Page } from "@playwright/test";
import { SESSION_FOUNDER, mockJson, stubEventSource } from "./helpers";

/**
 * The filter cost meter in the conditions editor.
 *
 * The founder's rules leave ~421 of 5,319 titles eligible — under eight feed-fulls — and this editor
 * used to give no feedback at all, so "the picks get worse every time I queue" had no answer on the
 * one screen where the trade is made. What these specs pin: the counts render, they follow the DRAFT
 * as chips move (not the saved conditions), the per-chip badges say what each exclusion is costing,
 * and a suggestion chip edits the draft without saving anything.
 *
 * The cost endpoint is mocked and answers from the posted body, so a count that did not follow the
 * draft is a visible failure rather than a stale-looking number.
 */

const SAVED_FILTERS = {
  excludeGenres: ["comedy", "documentary"],
  includeGenres: [],
  excludeKeywords: [],
  maturityCeiling: null,
  // The founder's real stack carries this one, and it is the rule the meter deliberately cannot
  // count — it is applied per title by the model, after scoring, not by the catalogue.
  minPredictedRating: 6,
  languages: ["en"],
  minCrowdRating: 6,
  minYear: 2000,
  maxYear: null,
};

/** Marginals the fake server hands back, keyed the way the real one keys them. */
const MARGINALS: Record<string, number> = {
  "ExcludeGenre:comedy": 180,
  "ExcludeGenre:documentary": 34,
  "ExcludeGenre:horror": 12,
  "Language:en": 640,
  "MinCrowdRating:6.0": 97,
  "MinYear:2000": 210,
};

/**
 * Stands in for FilterCostMeter: it reads the DRAFT out of the request body and answers from it, so
 * the numbers on screen are provably a function of what the editor posted.
 */
async function mockCost(page: Page): Promise<{ bodies: () => unknown[] }> {
  const bodies: unknown[] = [];
  await page.route("**/api/settings/filters/cost", (route) => {
    const draft = route.request().postDataJSON() as typeof SAVED_FILTERS;
    bodies.push(draft);

    const byRule: { kind: string; value: string; marginal: number }[] = [];
    for (const genre of draft.excludeGenres) {
      byRule.push({ kind: "ExcludeGenre", value: genre, marginal: MARGINALS[`ExcludeGenre:${genre}`] ?? 5 });
    }
    const languages = draft.languages.length > 0 ? draft.languages : ["en"];
    if (!languages.includes("*")) {
      byRule.push({ kind: "Language", value: languages.join(","), marginal: 640 });
    }
    if (draft.minCrowdRating != null) {
      byRule.push({ kind: "MinCrowdRating", value: draft.minCrowdRating.toFixed(1), marginal: 97 });
    }
    if (draft.minYear != null) {
      byRule.push({ kind: "MinYear", value: String(draft.minYear), marginal: 210 });
    }

    // Eligible falls as rules are added and rises as they are dropped — the meter's whole point.
    const eligible = 5319 - byRule.reduce((sum, r) => sum + r.marginal, 0) - 4000;
    const suggestions = [...byRule]
      .filter((r) => r.marginal > 0)
      .sort((a, b) => b.marginal - a.marginal)
      .slice(0, 3)
      .map((r) => ({ kind: r.kind, value: r.value, gain: r.marginal }));

    return route.fulfill({
      status: 200,
      contentType: "application/json",
      body: JSON.stringify({ total: 5319, eligible: Math.max(0, eligible), byRule, suggestions }),
    });
  });
  return { bodies: () => bodies };
}

async function openConditions(page: Page) {
  await stubEventSource(page);
  await mockJson(page, "**/api/auth/me", SESSION_FOUNDER);
  await mockJson(page, "**/api/sync/status", {
    pipelineStage: "FeedReady", connectionStatus: "Active", lastDeltaSyncAt: null,
    lastFullReconcileAt: null, activeJob: null, recentJobs: [], outboxPending: 0, outboxDeadLetters: 0,
  });
  await mockJson(page, "**/api/feed/continue-watching", []);
  await mockJson(page, "**/api/feed/more", { items: [], exhausted: true });
  await mockJson(page, "**/api/feed/genres", { genres: [] });
  await mockJson(page, "**/api/digest/current", { weekStart: null, generatedAt: null, items: [] });
  await mockJson(page, "**/api/settings/filters", SAVED_FILTERS);
  await mockJson(page, "**/api/feed", {
    generatedAt: new Date().toISOString(),
    hero: [],
    rows: [],
    lockedRowCount: 0,
  });

  await page.goto("/home");
  await page.getByTestId("open-conditions").click();
  await expect(page.getByTestId("conditions-editor")).toBeVisible();
}

test.describe("filter cost meter", () => {
  test("shows what the current rules leave, badges each excluded genre, and notes the other rules", async ({ page }) => {
    await mockCost(page);
    await openConditions(page);

    // 5,319 − (180 + 34 + 640 + 97 + 210) − 4,000 = 158 under the stub's arithmetic.
    await expect(page.getByTestId("filter-cost-line")).toHaveText("These rules leave 158 of 5,319 titles");

    // Per-chip badges, on excluded genres only.
    await expect(page.getByTestId("genre-marginal-comedy")).toHaveText("+180");
    await expect(page.getByTestId("genre-marginal-documentary")).toHaveText("+34");
    await expect(page.getByTestId("genre-marginal-horror")).toHaveCount(0);

    // The rules that are not chips get a sentence instead of a badge.
    await expect(page.getByTestId("language-marginal")).toHaveText("640 more titles if you allow every language");
    await expect(page.getByTestId("crowd-rating-marginal")).toHaveText("97 more titles if you turn this floor off");
    await expect(page.getByTestId("min-year-marginal")).toHaveText("210 more titles if you allow titles before 2000");

    // The "for you" floor is honestly marked as uncounted rather than left conspicuously blank.
    await expect(page.getByTestId("min-rating-note")).toContainText("applied per title by the model");
  });

  test("the counts follow the draft as chips move, before anything is saved", async ({ page }) => {
    const cost = await mockCost(page);
    await openConditions(page);
    await expect(page.getByTestId("filter-cost-line")).toHaveText("These rules leave 158 of 5,319 titles");

    // Excluding horror (neutral → include → exclude) adds a rule worth 12.
    await page.getByTestId("genre-horror").click();
    await page.getByTestId("genre-horror").click();
    await expect(page.getByTestId("genre-horror")).toHaveAttribute("data-state", "exclude");

    await expect(page.getByTestId("genre-marginal-horror")).toHaveText("+12");
    await expect(page.getByTestId("filter-cost-line")).toHaveText("These rules leave 146 of 5,319 titles");

    // It costed the DRAFT: the last body posted carries horror, which no save ever shipped.
    const last = cost.bodies().at(-1) as typeof SAVED_FILTERS;
    expect(last.excludeGenres).toContain("horror");

    // Dropping comedy from the draft takes the rule — and its badge — away again.
    await page.getByTestId("genre-comedy").click();
    await expect(page.getByTestId("genre-comedy")).toHaveAttribute("data-state", "neutral");
    await expect(page.getByTestId("genre-marginal-comedy")).toHaveCount(0);
    await expect(page.getByTestId("filter-cost-line")).toHaveText("These rules leave 326 of 5,319 titles");
  });

  test("a suggestion chip applies its rule to the draft and saves nothing on its own", async ({ page }) => {
    await mockCost(page);
    await openConditions(page);

    // Registered AFTER the boot mocks so it wins the match — Playwright resolves the most recently
    // registered route first, and the boot mock answers this same path.
    const saves: unknown[] = [];
    await page.route("**/api/settings/filters", (route) => {
      if (route.request().method() === "PUT") {
        saves.push(route.request().postDataJSON());
      }
      return route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify(SAVED_FILTERS) });
    });

    const suggestions = page.getByTestId("cost-suggestion");
    // Top three by gain, biggest first: language 640, comedy 180, crowd floor 97.
    await expect(suggestions).toHaveCount(3);
    await expect(suggestions.first()).toContainText("Allow every language");
    await expect(suggestions.first()).toContainText("+640");

    await suggestions.filter({ hasText: "Allow comedy" }).click();

    // The chip it named is now neutral in the draft, its badge is gone, and the count has moved.
    await expect(page.getByTestId("genre-comedy")).toHaveAttribute("data-state", "neutral");
    await expect(page.getByTestId("genre-marginal-comedy")).toHaveCount(0);
    await expect(page.getByTestId("filter-cost-line")).toHaveText("These rules leave 338 of 5,319 titles");

    // Nothing was persisted — the founder decision is that the meter names the trade and the user
    // makes it, so a suggestion is a draft edit and never a save.
    expect(saves).toHaveLength(0);
  });
});
