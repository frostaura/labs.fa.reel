import { test, expect, type Page } from "@playwright/test";
import { SESSION_FOUNDER, mockJson, stubEventSource, syncStatus } from "./helpers";
import { mockFilterChips } from "./filterChips.fixture";

/**
 * The maturity ceiling is the Select primitive, not a native <select> (HREV1-D08).
 *
 * It was the last native select in the app: an operating-system dropdown in the middle of a form
 * whose Region and Time-zone pickers are the shared token-styled controls, and WS-9's bar is zero
 * native selects. "No ceiling" travels as null in the saved rules and as "none" in the control,
 * because a Radix item cannot carry an empty value. The fixtures vary the saved ceiling (none, R)
 * and the pick (PG-13, back to none).
 */

const FILTERS = (maturityCeiling: string | null) => ({
  excludeGenres: [],
  includeGenres: [],
  excludeKeywords: [],
  maturityCeiling,
  minPredictedRating: null,
  languages: ["en"],
  minCrowdRating: null,
  minYear: null,
  maxYear: null,
});

async function boot(page: Page, saved: string | null): Promise<unknown[]> {
  const puts: unknown[] = [];
  await stubEventSource(page);
  await mockJson(page, "**/api/auth/me", SESSION_FOUNDER);
  await mockJson(page, "**/api/sync/status", syncStatus());
  await mockJson(page, "**/api/billing/status", { tier: "Founder", configured: false });
  await mockJson(page, "**/api/preferences/tags", []);
  await mockJson(page, "**/api/settings", { region: "US", onboarded: true, email: null, eveningBudgetMinutes: 150 });
  await mockJson(page, "**/api/settings/filters/cost", { total: 5000, eligible: 400, byRule: [], suggestions: [] });
  await mockFilterChips(page);
  await page.route(
    (u) => u.pathname === "/api/settings/filters",
    async (route) => {
      if (route.request().method() === "PUT") {
        const body = route.request().postDataJSON() as { filters?: unknown };
        puts.push(body.filters ?? body);
        return route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify(body.filters ?? body) });
      }
      return route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify(FILTERS(saved)) });
    },
  );
  await page.goto("/settings");
  await expect(page.getByTestId("prefs-section")).toBeVisible();
  return puts;
}

test.describe("maturity ceiling picker (HREV1-D08)", () => {
  test("Settings has no native select, and the picker is a labelled combobox", async ({ page }) => {
    await boot(page, null);
    await expect(page.locator("select")).toHaveCount(0);
    const picker = page.getByRole("combobox", { name: "Maturity ceiling" });
    await expect(picker).toBeVisible();
    await expect(picker).toHaveText("none");
  });

  test("a saved ceiling shows its label, and picking one saves it", async ({ page }) => {
    const puts = await boot(page, "R");
    const picker = page.getByTestId("maturity-ceiling");
    await expect(picker).toHaveText("R / TV-MA");

    await picker.click();
    await page.getByRole("option", { name: "PG-13" }).click();
    await expect(picker).toHaveText("PG-13");
    await expect(page.getByTestId("conditions-unsaved")).toBeVisible();
    await page.getByTestId("save-prefs").click();
    await expect(page.getByTestId("conditions-save-status")).toHaveAttribute("data-tone", "success");
    expect(puts).toHaveLength(1);
    expect((puts[0] as { maturityCeiling: string | null }).maturityCeiling).toBe("PG-13");
  });

  test("picking none clears the ceiling to null, not to an empty string or 'none'", async ({ page }) => {
    const puts = await boot(page, "PG");
    await page.getByTestId("maturity-ceiling").click();
    await page.getByRole("option", { name: "none" }).click();
    await page.getByTestId("save-prefs").click();
    await expect(page.getByTestId("conditions-save-status")).toHaveAttribute("data-tone", "success");
    expect((puts[0] as { maturityCeiling: string | null }).maturityCeiling).toBeNull();
  });

  test("the picker works from the keyboard", async ({ page }) => {
    const puts = await boot(page, null);
    const picker = page.getByTestId("maturity-ceiling");
    await picker.focus();
    await page.keyboard.press("Enter");
    await page.getByRole("option", { name: "G", exact: true }).focus();
    await page.keyboard.press("Enter");
    await expect(picker).toHaveText("G");
    await expect(picker).toBeFocused();
    await page.getByTestId("save-prefs").click();
    await expect(page.getByTestId("conditions-save-status")).toHaveAttribute("data-tone", "success");
    expect((puts[0] as { maturityCeiling: string | null }).maturityCeiling).toBe("G");
  });
});
