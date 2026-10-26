import { test, expect, type Page } from "@playwright/test";
import { SESSION_FOUNDER, mockJson, stubEventSource } from "./helpers";

/**
 * The two social/weekly surfaces the remediation added:
 *  - the Home digest rail ("New & high for you this week") — renders from /digest/current and
 *    hides itself entirely while the week's build is empty;
 *  - the /blend page — create a shareable code, preview someone else's, and get joint picks
 *    showing BOTH predictions ("8.4 you · 7.9 them").
 */

const card = (n: number, name: string) => ({
  titleId: `00000000-0000-0000-0000-0000000000${n}0`,
  mediaType: "Movie",
  tmdbId: 2000 + n,
  name,
  year: 2026,
  runtimeMinutes: 104,
  posterPath: null,
  backdropPath: null,
  genres: ["drama"],
  predictedRating: 8.7,
  crowdRating: 7.6,
  crowdVotes: 21000,
  whyThis: "New this week and high for you.",
  isReleased: true,
});

async function bootAuthed(page: Page) {
  await stubEventSource(page);
  await mockJson(page, "**/api/auth/me", SESSION_FOUNDER);
  await mockJson(page, "**/api/sync/status", {
    pipelineStage: "FeedReady", connectionStatus: "Active", lastDeltaSyncAt: null,
    lastFullReconcileAt: null, activeJob: null, recentJobs: [], outboxPending: 0, outboxDeadLetters: 0,
  });
}

async function bootHome(page: Page, digestItems: unknown[], degradedReason: string | null = null) {
  await bootAuthed(page);
  await mockJson(page, "**/api/feed/continue-watching", []);
  await mockJson(page, "**/api/feed/more", { items: [], exhausted: true });
  await mockJson(page, "**/api/feed/genres", []);
  await mockJson(page, "**/api/feed", {
    generatedAt: new Date().toISOString(), hero: [card(1, "Hero Pick")], rows: [], lockedRowCount: 0,
  });
  await mockJson(page, "**/api/digest/current", {
    weekStart: "2026-08-03", generatedAt: digestItems.length > 0 ? new Date().toISOString() : null, items: digestItems,
    degradedReason,
  });
  await page.goto("/home");
  await expect(page.getByTestId("hero-card").first()).toBeVisible();
}

test.describe("weekly digest rail", () => {
  test("renders 'New & high for you this week' from the digest payload", async ({ page }) => {
    await bootHome(page, [card(2, "Fresh Signal"), card(3, "High Water")]);

    await expect(page.getByText("New & high for you this week")).toBeVisible();
    await expect(page.getByTestId("rec-card").filter({ hasText: "Fresh Signal" })).toHaveCount(1);
    await expect(page.getByTestId("rec-card").filter({ hasText: "High Water" })).toHaveCount(1);
  });

  test("a digest that met the bar shows no relaxed-bar caption (IR1S-03)", async ({ page }) => {
    await bootHome(page, [card(2, "Fresh Signal")]);

    await expect(page.getByText("New & high for you this week")).toBeVisible();
    await expect(page.getByTestId("rail-caption")).toHaveCount(0);
  });

  // IR1S-03: a build that relaxed the "high for you" bar must not keep the claim in its title.
  test("a relaxed digest is retitled and says why, in the build's words", async ({ page }) => {
    const reason =
      "Only 0 of this week's arrivals cleared the 7 “high for you” bar, so it was relaxed to 6 for this digest.";
    await bootHome(page, [card(2, "Fresh Signal"), card(3, "High Water")], reason);

    await expect(page.getByRole("heading", { name: "New this week", exact: true })).toBeVisible();
    await expect(page.getByText("New & high for you this week")).toHaveCount(0);
    await expect(page.getByTestId("rail-caption")).toHaveText(reason);
    await expect(page.getByTestId("rec-card").filter({ hasText: "Fresh Signal" })).toHaveCount(1);
  });

  test("hides itself entirely while the week's digest is empty", async ({ page }) => {
    await bootHome(page, []);

    await expect(page.getByText("New & high for you this week")).toHaveCount(0);
  });
});

test.describe("blend", () => {
  test("create a code, preview theirs, and read joint picks with both scores", async ({ page }) => {
    await bootAuthed(page);
    await mockJson(page, "**/api/blend/code", { code: "AAAA2222" });
    await mockJson(page, "**/api/blend/BBBB3333/preview", { ownerName: "Sam", eligibleBothCount: 12 });
    await mockJson(page, "**/api/blend/BBBB3333/picks", {
      ownerName: "Sam",
      items: [{
        titleId: "00000000-0000-0000-0000-0000000000b1",
        mediaType: "Movie", tmdbId: 3001, name: "Common Ground", year: 2025,
        runtimeMinutes: 98, posterPath: null, backdropPath: null, genres: ["comedy"],
        crowdRating: 7.2, crowdVotes: 8000, isReleased: true,
        forYou: 8.4, forThem: 7.9, joint: 7.9,
      }],
    });

    await page.goto("/blend");

    // Your code: minted on demand, then displayed for sharing.
    await page.getByRole("button", { name: /get my code/i }).click();
    await expect(page.getByTestId("blend-code")).toHaveText("AAAA2222");

    // Their code: preview names the owner and the overlap before committing to picks.
    await page.getByTestId("blend-their-code").getByPlaceholder("8-character code").fill("bbbb3333");
    await page.getByRole("button", { name: /^preview$/i }).click();
    await expect(page.getByText("Sam", { exact: true })).toBeVisible();
    await expect(page.getByText(/12 titles eligible for you both/i)).toBeVisible();

    // Joint picks: the grid captions each card with BOTH predictions.
    await page.getByTestId("blend-get-picks").click();
    const picks = page.getByTestId("blend-picks");
    await expect(picks).toBeVisible();
    await expect(picks.getByText("Common Ground")).toBeVisible();
    await expect(picks).toContainText("8.4 you · 7.9 them");
  });
});
