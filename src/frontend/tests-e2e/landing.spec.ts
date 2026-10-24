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

      const { scrollWidth, innerWidth } = await page.evaluate(() => ({
        scrollWidth: document.documentElement.scrollWidth,
        innerWidth: window.innerWidth,
      }));
      expect(scrollWidth).toBe(innerWidth);

      // The pitch paragraph sits fully inside the viewport, left edge included.
      const pitch = page.getByText(/Link your Trakt profile; get live, explainable picks/);
      const box = await pitch.boundingBox();
      expect(box).not.toBeNull();
      expect(box!.x).toBeGreaterThanOrEqual(0);
      expect(box!.x + box!.width).toBeLessThanOrEqual(width);
    });
  }

  test("the full poster fan still shows from sm up", async ({ page }) => {
    await page.setViewportSize({ width: 1024, height: 800 });
    await page.goto("/");
    await expect(page.getByText("Sci-fi", { exact: true })).toBeVisible();
    await expect(page.getByText("Comedy", { exact: true })).toBeVisible();
    const { scrollWidth, innerWidth } = await page.evaluate(() => ({
      scrollWidth: document.documentElement.scrollWidth,
      innerWidth: window.innerWidth,
    }));
    expect(scrollWidth).toBe(innerWidth);
  });
});
