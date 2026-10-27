import { expect, test, type Page } from "@playwright/test";

/**
 * What an installed Reel is made of, read off the PRODUCTION build (PH-11).
 *
 * The built manifest had no icons and index.html no apple-touch-icon, so installing gave a
 * browser-made letter tile on Android and a page screenshot on the iPhone home screen. And the
 * viewport meta lacked `viewport-fit=cover`, which makes every `env(safe-area-inset-*)` resolve to 0:
 * the tab bar's and the composer's home-indicator padding were written and never applied.
 *
 * Every icon is fetched and decoded, so a manifest entry naming a missing or mis-sized file fails
 * here rather than on a phone. Playwright cannot emulate a notch, so the inset itself is checked on
 * the founder's installed iPhone (WS-9 task 12); this spec pins the switch that turns it on.
 */

interface ManifestIcon {
  src: string;
  sizes: string;
  type?: string;
  purpose?: string;
}

/** Decodes the image at `src` in the page and returns its natural size. */
async function naturalSize(page: Page, src: string): Promise<{ width: number; height: number }> {
  return page.evaluate(async (url) => {
    const img = new Image();
    img.src = url;
    await img.decode();
    return { width: img.naturalWidth, height: img.naturalHeight };
  }, src);
}

test.beforeEach(async ({ page }) => {
  // The landing page only: no /api call is made before the session read, which is answered 401.
  await page.route(
    (u) => u.pathname.startsWith("/api/"),
    (route) => route.fulfill({ status: 401, contentType: "application/json", body: "{}" })
  );
  await page.goto("/");
});

test("the manifest names 192 and 512 icons and a maskable 512, and each one decodes at its size", async ({ page, request }) => {
  const href = await page.locator('link[rel="manifest"]').getAttribute("href");
  expect(href, "index.html links no manifest").toBeTruthy();
  const res = await request.get(new URL(href!, page.url()).toString());
  expect(res.status()).toBe(200);
  const manifest = (await res.json()) as { icons?: ManifestIcon[] };
  const icons = manifest.icons ?? [];

  const find = (size: string, purpose: string) =>
    icons.find((i) => i.sizes.split(" ").includes(size) && (i.purpose ?? "any").split(" ").includes(purpose));
  const wanted = [
    { size: "192x192", purpose: "any" },
    { size: "512x512", purpose: "any" },
    { size: "512x512", purpose: "maskable" },
  ];
  for (const { size, purpose } of wanted) {
    const icon = find(size, purpose);
    expect(icon, `no ${purpose} icon at ${size}`).toBeTruthy();
    const url = new URL(icon!.src, new URL(href!, page.url())).toString();
    const file = await request.get(url);
    expect(file.status(), url).toBe(200);
    expect(file.headers()["content-type"]).toContain("image/png");
    const [w, h] = size.split("x").map(Number);
    expect(await naturalSize(page, url)).toEqual({ width: w, height: h });
  }
});

test("index.html carries a 180 × 180 apple-touch-icon", async ({ page }) => {
  const link = page.locator('link[rel="apple-touch-icon"]');
  await expect(link).toHaveCount(1);
  const src = await link.evaluate((el) => (el as HTMLLinkElement).href);
  expect(await naturalSize(page, src)).toEqual({ width: 180, height: 180 });
});

test("the viewport opts into the safe area, so env(safe-area-inset-*) can apply", async ({ page }) => {
  const content = await page.locator('meta[name="viewport"]').getAttribute("content");
  const parts = (content ?? "").split(",").map((p) => p.trim());
  expect(parts).toContain("viewport-fit=cover");
  // Opting in must not cost pinch-zoom: no maximum-scale or user-scalable=no rides along.
  expect(parts.some((p) => /^maximum-scale|^user-scalable\s*=\s*no/.test(p))).toBe(false);
});
