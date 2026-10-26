/** WCAG 2.x relative luminance and contrast, for the token and chart-palette checks. */

/** `#RRGGBB` or `R G B` channels → [r, g, b] in 0–255. */
export function parseColour(colour: string): [number, number, number] {
  const hex = /^#([0-9a-f]{6})$/i.exec(colour.trim());
  if (hex) {
    const n = Number.parseInt(hex[1], 16);
    return [(n >> 16) & 255, (n >> 8) & 255, n & 255];
  }
  const channels = colour.trim().split(/\s+/).map(Number);
  if (channels.length === 3 && channels.every((c) => Number.isInteger(c) && c >= 0 && c <= 255)) {
    return channels as [number, number, number];
  }
  throw new Error(`Not a #RRGGBB colour or an R G B channel triple: ${colour}`);
}

function luminance([r, g, b]: [number, number, number]): number {
  const linear = (c: number) => {
    const s = c / 255;
    return s <= 0.03928 ? s / 12.92 : ((s + 0.055) / 1.055) ** 2.4;
  };
  return 0.2126 * linear(r) + 0.7152 * linear(g) + 0.0722 * linear(b);
}

/** The WCAG contrast ratio between two opaque colours, 1–21. */
export function contrast(a: string, b: string): number {
  const [hi, lo] = [luminance(parseColour(a)), luminance(parseColour(b))].sort((x, y) => y - x);
  return (hi + 0.05) / (lo + 0.05);
}
