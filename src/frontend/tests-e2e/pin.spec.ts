import { test, expect } from "@playwright/test";
import { SESSION_FOUNDER, mockJson, stubEventSource } from "./helpers";

const FILTERS = {
  excludeGenres: [], includeGenres: [], excludeKeywords: [], maturityCeiling: null, minPredictedRating: null,
};

/**
 * The settings PIN keeps content preferences out of reach on shared screens (kids' profiles).
 * Locked by default for a PIN-configured account until the right PIN is entered; a wrong PIN
 * is rejected and the controls stay hidden.
 */
test.describe("settings PIN", () => {
  async function boot(page: import("@playwright/test").Page) {
    await stubEventSource(page);
    await mockJson(page, "**/api/auth/me", { ...SESSION_FOUNDER, pinConfigured: true });
    await mockJson(page, "**/api/sync/status", {
      pipelineStage: "FeedReady", connectionStatus: "Active", lastDeltaSyncAt: null,
      lastFullReconcileAt: null, activeJob: null, recentJobs: [], outboxPending: 0, outboxDeadLetters: 0,
    });
    await mockJson(page, "**/api/settings/filters", FILTERS);
    await page.goto("/settings");
  }

  test("a PIN-configured account is locked until the correct PIN unlocks it", async ({ page }) => {
    await boot(page);
    // Wrong PIN → 423 Locked; correct PIN → 200 with a short-lived unlock token. The raw PIN is
    // never cached client-side — the token (plus its expiry) is all the frontend keeps.
    await page.route("**/api/settings/pin/verify", async (route) => {
      const body = route.request().postDataJSON() as { pin: string };
      if (body.pin === "2468") {
        await route.fulfill({
          status: 200,
          contentType: "application/json",
          body: JSON.stringify({
            valid: true,
            unlockToken: "e2e-unlock-token",
            expiresAt: new Date(Date.now() + 15 * 60 * 1000).toISOString(),
          }),
        });
      } else {
        await route.fulfill({ status: 423, contentType: "application/json", body: "{}" });
      }
    });

    await expect(page.getByTestId("prefs-locked")).toBeVisible();
    await expect(page.getByTestId("prefs-section")).toHaveCount(0);
    await expect(page.getByTestId("pin-status")).toHaveText("Locked");

    // Wrong PIN stays locked with feedback.
    await page.getByTestId("pin-input").fill("9999");
    await page.getByRole("button", { name: "Unlock" }).click();
    await expect(page.getByText(/wrong pin/i)).toBeVisible();
    await expect(page.getByTestId("prefs-locked")).toBeVisible();

    // Correct PIN reveals the controls, and the badge shows the unlock counting down.
    await page.getByTestId("pin-input").fill("2468");
    await page.getByRole("button", { name: "Unlock" }).click();
    await expect(page.getByTestId("prefs-section")).toBeVisible();
    await expect(page.getByTestId("min-rating-slider")).toBeVisible();
    await expect(page.getByTestId("pin-status")).toHaveText(/Unlocked · \d+m/);

    // What landed in sessionStorage is the opaque token — never the PIN that was typed.
    const stored = await page.evaluate(() => sessionStorage.getItem("reel.pinUnlock"));
    expect(stored).toContain("e2e-unlock-token");
    expect(stored).not.toContain("2468");
  });

  test("removing the PIN demands a fresh PIN entry in a confirm dialog", async ({ page }) => {
    await boot(page);
    await page.route("**/api/settings/pin/verify", async (route) => {
      await route.fulfill({
        status: 200,
        contentType: "application/json",
        body: JSON.stringify({
          valid: true,
          unlockToken: "e2e-unlock-token",
          expiresAt: new Date(Date.now() + 15 * 60 * 1000).toISOString(),
        }),
      });
    });
    // The session mock must flip after the DELETE — the flash lives in the PIN-management block,
    // which only renders while the account is not locked, and a stale `pinConfigured: true` with
    // no unlock token would read as locked again.
    let pinRemoved = false;
    await page.route("**/api/auth/me", (route) =>
      route.fulfill({
        status: 200,
        contentType: "application/json",
        body: JSON.stringify({ ...SESSION_FOUNDER, pinConfigured: !pinRemoved }),
      })
    );
    let deleteHeaders: Record<string, string> | null = null;
    await page.route("**/api/settings/pin", async (route) => {
      if (route.request().method() !== "DELETE") return route.fallback();
      deleteHeaders = route.request().headers();
      pinRemoved = true;
      await route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify({ pinConfigured: false }) });
    });

    // Unlock first so PIN management is visible.
    await page.getByTestId("pin-input").fill("2468");
    await page.getByRole("button", { name: "Unlock" }).click();
    await expect(page.getByTestId("pin-management")).toBeVisible();

    // Remove opens a confirm dialog instead of firing immediately, and the confirm button stays
    // disabled until a plausible PIN is re-entered — no cached state can stand in for it.
    await page.getByRole("button", { name: "Remove", exact: true }).click();
    await expect(page.getByText("Remove the settings PIN?")).toBeVisible();
    await expect(page.getByText(/anyone using this device/i)).toBeVisible();
    await expect(page.getByTestId("remove-pin-confirm")).toBeDisabled();

    await page.getByTestId("remove-pin-input").fill("2468");
    await page.getByTestId("remove-pin-confirm").click();

    // The DELETE carried the freshly-typed raw PIN header (the endpoint refuses unlock tokens).
    await expect(page.getByText("PIN removed.")).toBeVisible();
    expect(deleteHeaders?.["x-settings-pin"]).toBe("2468");
  });
});
