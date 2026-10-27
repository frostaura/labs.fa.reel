import AxeBuilder from "@axe-core/playwright";
import { test, expect, type Page } from "@playwright/test";
import { SESSION_FOUNDER, mockJson, stubEventSource } from "./helpers";
import { mockFilterChips } from "./filterChips.fixture";

/**
 * The condition summary is legible wherever the editor opens it (R1-44).
 *
 * Its muted text — the neutral chip ("English only"), the group labels and "+N more" — was
 * `frost-dim`, which is 4.18:1 at 11 px once the chip's glass sits on the dialog's ink-2 surface:
 * under AA, so Ask Reel's quality spec had to exclude the summary from its axe run by name. These
 * run the full axe rule set on the summary, with no exclusion, on the two surfaces that host it in
 * a dialog or a card: the Home conditions dialog and the Settings conditions card.
 *
 * Fixtures vary the chips on screen, because a contrast rule only judges text that renders: a
 * neutral chip alone, and a neutral chip beside include and exclude chips and a year range.
 */

const SYNC = {
  pipelineStage: "FeedReady", connectionStatus: "Active", lastDeltaSyncAt: null,
  lastFullReconcileAt: null, activeJob: null, recentJobs: [], outboxPending: 0, outboxDeadLetters: 0,
};

const BASE = {
  excludeGenres: [] as string[], includeGenres: [] as string[], excludeKeywords: [], maturityCeiling: null,
  minPredictedRating: null, languages: ["en"], minCrowdRating: null, minYear: null as number | null, maxYear: null,
};

const FIXTURES: Record<string, typeof BASE> = {
  "only the default language rule (one neutral chip)": BASE,
  "neutral beside include, exclude and a year range": {
    ...BASE, includeGenres: ["crime"], excludeGenres: ["horror", "documentary"], minYear: 1990, languages: ["en", "fr"],
  },
};

async function axeOnSummary(page: Page, scope: string) {
  const summary = page.locator(scope);
  await expect(summary).toBeVisible();
  // Let the dialog's open animation finish: axe samples colours as they are painted.
  await page.waitForTimeout(400);
  const result = await new AxeBuilder({ page }).include(scope).analyze();
  return result.violations.map((v) => ({ rule: v.id, nodes: v.nodes.map((n) => `${n.target.join(" ")} — ${n.failureSummary ?? ""}`) }));
}

async function bootHome(page: Page, filters: typeof BASE) {
  await stubEventSource(page);
  // Registered first, so every specific mock below wins: nothing reaches the dev proxy.
  await page.route(
    (u) => u.pathname.startsWith("/api/"),
    (route) => route.fulfill({ status: 404, contentType: "application/json", body: "{}" })
  );
  await mockJson(page, "**/api/auth/me", SESSION_FOUNDER);
  await mockJson(page, "**/api/sync/status", SYNC);
  await mockJson(page, "**/api/settings/filters", filters);
  await mockJson(page, "**/api/settings/filters/cost", { total: 5000, eligible: 400, byRule: [], suggestions: [] });
  await mockFilterChips(page);
  await mockJson(page, "**/api/feed", {
    generatedAt: new Date().toISOString(),
    hero: [{
      titleId: "00000000-0000-0000-0000-000000000c01", mediaType: "Movie", tmdbId: 7001, name: "Heat", year: 1995,
      runtimeMinutes: 170, posterPath: null, backdropPath: null, genres: ["crime"], predictedRating: 8.2,
      crowdRating: 8.3, crowdVotes: 90000, whyThis: "Because of the films you rate highest.", isReleased: true,
      confidence: "confident", floorUsed: 7.5,
    }],
    rows: [],
    lockedRowCount: 0,
  });
  await mockJson(page, "**/api/feed/more", { items: [], exhausted: true });
  await page.goto("/home");
}

async function bootSettings(page: Page, filters: typeof BASE) {
  await stubEventSource(page);
  await mockJson(page, "**/api/auth/me", SESSION_FOUNDER);
  await mockJson(page, "**/api/sync/status", SYNC);
  await mockJson(page, "**/api/billing/status", { tier: "Founder", configured: false });
  await mockJson(page, "**/api/preferences/tags", []);
  await mockJson(page, "**/api/settings/filters/cost", { total: 5000, eligible: 400, byRule: [], suggestions: [] });
  await mockFilterChips(page);
  await mockJson(page, "**/api/settings/filters", filters);
  await mockJson(page, "**/api/settings", { region: "US", onboarded: true, email: null, eveningBudgetMinutes: 150, timeZone: null, effectiveTimeZone: "UTC" });
  await page.goto("/settings");
}

for (const [label, filters] of Object.entries(FIXTURES)) {
  test(`the summary in the Home conditions dialog passes axe with no exclusion (${label})`, async ({ page }) => {
    await bootHome(page, filters);
    await page.getByTestId("open-conditions").click();
    await expect(page.getByRole("dialog")).toBeVisible();
    expect(await axeOnSummary(page, '[role="dialog"] [data-testid="conditions-summary"]')).toEqual([]);
  });

  test(`the summary on the Settings conditions card passes axe with no exclusion (${label})`, async ({ page }) => {
    await bootSettings(page, filters);
    expect(await axeOnSummary(page, '[data-testid="settings-conditions"] [data-testid="conditions-summary"]')).toEqual([]);
  });
}
