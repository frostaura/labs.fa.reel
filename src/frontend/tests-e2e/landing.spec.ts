import { test, expect } from "@playwright/test";

/**
 * The signed-out landing page on phones (H-13). The poster row's intrinsic width (~440 px with
 * all five cards) used to widen the whole column past a 375 px viewport: `scrollWidth` 414, the
 * column starting at x = −32, and the pitch sentence clipped to "nk your Trakt profile".
 */
test.describe("landing on phones", () => {
  for (const width of [320, 360, 375, 390]) {
    test(`no horizontal overflow at ${width} px, and the pitch is whole`, async ({ page }) => {
      await page.setViewportSize({ width, height: 800 });
      // A first-time visitor: no session flag, so the page never probes /auth/me.
      await page.goto("/");
      await expect(page.getByRole("button", { name: /sign in with trakt/i })).toBeVisible();

      // Against the layout width, not innerWidth: WebKit draws a classic vertical scrollbar, so its
      // content box is narrower than the window (310 of 320) and scrollWidth matches that, while
      // Chromium's overlay scrollbar leaves the two equal. Overflow is scrollWidth past clientWidth.
      const { scrollWidth, clientWidth, innerWidth } = await page.evaluate(() => ({
        scrollWidth: document.documentElement.scrollWidth,
        clientWidth: document.documentElement.clientWidth,
        innerWidth: window.innerWidth,
      }));
      expect(scrollWidth).toBe(clientWidth);
      expect(clientWidth).toBeLessThanOrEqual(innerWidth);

      // The pitch paragraph sits fully inside the viewport, left edge included.
      const pitch = page.getByText(/Link your Trakt profile; get live, explainable picks/);
      const box = await pitch.boundingBox();
      expect(box).not.toBeNull();
      expect(box!.x).toBeGreaterThanOrEqual(0);
      expect(box!.x + box!.width).toBeLessThanOrEqual(width);
    });
  }

  test("the three steps sit side by side from sm up, without overflow", async ({ page }) => {
    await page.setViewportSize({ width: 1024, height: 800 });
    await page.goto("/");
    const steps = page.getByTestId("landing-step");
    await expect(steps).toHaveCount(3);
    const boxes = await Promise.all([0, 1, 2].map((i) => steps.nth(i).boundingBox()));
    // One row: every step starts at the same top edge.
    expect(new Set(boxes.map((b) => Math.round(b!.y))).size).toBe(1);
    const { scrollWidth, innerWidth } = await page.evaluate(() => ({
      scrollWidth: document.documentElement.scrollWidth,
      innerWidth: window.innerWidth,
    }));
    expect(scrollWidth).toBe(innerWidth);
  });

  test("no made-up pick ships: no invented title, no scores, no 'streaming now' (KILL-10)", async ({ page }) => {
    await page.setViewportSize({ width: 390, height: 800 });
    await page.goto("/");
    await expect(page.getByTestId("landing-steps")).toBeVisible();
    const body = page.locator("body");
    await expect(body).not.toContainText("Neon Static");
    await expect(body).not.toContainText("streaming now");
    await expect(page.getByTestId("landing-illustration")).toHaveCount(0);
    // No score badge of any kind on a page with no real title to score.
    await expect(body).not.toContainText(/\b\d\.\d\b/);
  });

  test("promises a reason for a pick only when there is one worth naming (R1-43)", async ({ page }) => {
    // The explanation templater ships no line when nothing citable moved the prediction, so "why it
    // was picked" on every pick is a promise the product does not keep.
    await page.goto("/");
    const picks = page.getByTestId("landing-step").nth(2);
    await expect(picks).toContainText("Each with a predicted rating for you and where to watch it");
    await expect(picks).toContainText("and why it was picked, when there's a reason worth naming.");
    await expect(page.getByTestId("landing-steps")).not.toContainText("a predicted rating for you, why it was picked");
  });

  test("carries the Reel mark and no studio artwork (O-19)", async ({ page }) => {
    await page.goto("/");
    const mark = page.getByTestId("landing-mark");
    await expect(mark).toBeVisible();
    await expect(mark).toHaveAttribute("src", /reel-mark|^data:image\/svg/);
    // Nothing above the fold is a poster or backdrop from TMDB.
    await expect(page.locator('img[src*="image.tmdb.org"]')).toHaveCount(0);
  });
});
