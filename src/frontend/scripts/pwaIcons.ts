/**
 * Renders the PWA install icons from the brand mark (PH-11): `npm run pwa:icons`.
 *
 * The manifest had no icons at all and index.html no apple-touch-icon, so an installed Reel got a
 * browser-made letter tile on Android and a page screenshot on the iPhone home screen. The PNGs are
 * committed (a build needs no browser), and this script is how they are remade when the mark
 * changes — rasterised by the repo's own Playwright Chromium, so there is no image tool to install.
 *
 * - `pwa-192.png`, `pwa-512.png` — purpose "any": the favicon as drawn, rounded ink tile and all.
 * - `pwa-maskable-512.png` — purpose "maskable": full-bleed ink with the mark at 70%, because a
 *   launcher crops a maskable icon to its own shape and only the central circle of radius 40% is
 *   safe. At 80% the spark's tip sat at 43% of the edge from the centre and a circle mask cut it;
 *   at 70% it is at 37.6%.
 * - `apple-touch-icon.png` — 180 × 180, full-bleed like the maskable one: iOS rounds the corners
 *   itself, and a pre-rounded tile shows its transparent corners as a black rim.
 */
import { readFileSync, writeFileSync } from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { chromium } from "@playwright/test";

const ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const PUBLIC = path.join(ROOT, "public");
/** The shell's ink (index.html theme-color), the tile the mark sits on. */
const INK = "#0B1B30";

const favicon = readFileSync(path.join(PUBLIC, "favicon.svg"), "utf8");
const mark = readFileSync(path.join(ROOT, "src/assets/brand/reel-mark.svg"), "utf8");

/**
 * The bare mark centred on a full-bleed ink square, scaled to `inset` of the edge. The mark's root
 * `fill="none"` is carried onto the wrapper: dropped with the `<svg>` tag, the stroke-only reel arc
 * fills black.
 */
function fullBleed(inset: number): string {
  const pad = (40 * (1 - inset)) / 2;
  const inner = mark.replace(/^<svg[^>]*>/, "").replace(/<\/svg>\s*$/, "");
  return `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 40 40"><rect width="40" height="40" fill="${INK}"/><g fill="none" transform="translate(${pad} ${pad}) scale(${inset})">${inner}</g></svg>`;
}

const ICONS: { file: string; size: number; svg: string }[] = [
  { file: "pwa-192.png", size: 192, svg: favicon },
  { file: "pwa-512.png", size: 512, svg: favicon },
  { file: "pwa-maskable-512.png", size: 512, svg: fullBleed(0.7) },
  { file: "apple-touch-icon.png", size: 180, svg: fullBleed(0.86) },
];

const browser = await chromium.launch();
try {
  for (const { file, size, svg } of ICONS) {
    const page = await browser.newPage({ viewport: { width: size, height: size }, deviceScaleFactor: 1 });
    const sized = svg.replace(/<svg /, `<svg width="${size}" height="${size}" `);
    await page.setContent(
      `<!doctype html><html><body style="margin:0;background:transparent">${sized}</body></html>`
    );
    const png = await page.locator("svg").screenshot({ omitBackground: true });
    writeFileSync(path.join(PUBLIC, file), png);
    await page.close();
    console.log(`public/${file} ${size}x${size}`);
  }
} finally {
  await browser.close();
}
