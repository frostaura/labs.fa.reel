import { readdirSync, readFileSync, writeFileSync } from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

/**
 * The design-system ratchet (WS-9 task 7, I-7/I-8). Counts, per source file, the patterns the token
 * and primitive layer replaces, and fails when any file's count rises above the committed baseline.
 * A file that is not in the baseline has a baseline of 0, so every new file starts clean: these bans
 * apply to new code at once, while existing files come down as their owners migrate them.
 *
 * Counts that fall are fine; rerun with `--write` to lock the improvement in.
 *
 *   node scripts/designRatchet.ts          # check
 *   node scripts/designRatchet.ts --write  # rewrite the baseline from the tree
 */

export type Rule = "rawButton" | "nativeSelect" | "hexColour" | "lowTextOpacity" | "outlineWithoutFocus";

export const RULES: Record<Rule, string> = {
  rawButton: "a raw <button> outside components/ui/ — use the Button primitive",
  nativeSelect: "a native <select> — use the Select primitive",
  hexColour: "a hex colour outside the token and chart modules — use a token class or src/design/chartPalette",
  lowTextOpacity: "text below 80% opacity (text-x/NN) — use text-2 or text-3",
  outlineWithoutFocus: "outline-none with no focus or focus-visible style beside it",
};

const HERE = path.dirname(fileURLToPath(import.meta.url));
export const FRONTEND = path.resolve(HERE, "..");
const SRC = path.join(FRONTEND, "src");
export const BASELINE_PATH = path.join(HERE, "design-ratchet-baseline.json");

/** Where colour literals are the point: the token and chart modules. */
const HEX_ALLOWED = new Set(["src/design/chartPalette.ts", "src/design/contrast.ts"]);

const HEX = /(?<![\w&-])#(?:[0-9a-fA-F]{8}|[0-9a-fA-F]{6}|[0-9a-fA-F]{3,4})(?![\w-])/g;
const LOW_TEXT_OPACITY = /(?<![\w-])text-[a-z][a-z0-9-]*\/(?:[0-7]?[0-9])(?![\d.\w])/g;
const STRING_LITERAL = /"[^"\n]*"|'[^'\n]*'|`[^`]*`/g;

function count(text: string, pattern: RegExp): number {
  return text.match(pattern)?.length ?? 0;
}

/** The ratchet's counts for one file's text. `file` is relative to the frontend root, with `/`. */
export function scan(file: string, text: string): Partial<Record<Rule, number>> {
  const counts: Partial<Record<Rule, number>> = {};
  const set = (rule: Rule, n: number) => {
    if (n > 0) counts[rule] = n;
  };
  const isTsx = file.endsWith(".tsx");
  if (isTsx && !file.startsWith("src/components/ui/")) set("rawButton", count(text, /<button\b/g));
  if (isTsx) set("nativeSelect", count(text, /<select\b/g));
  if (!HEX_ALLOWED.has(file)) set("hexColour", count(text, HEX));
  set("lowTextOpacity", count(text, LOW_TEXT_OPACITY));
  const literals = text.match(STRING_LITERAL) ?? [];
  set(
    "outlineWithoutFocus",
    literals.filter((s) => /(?<![\w:-])outline-none\b/.test(s) && !/\bfocus(?:-visible)?:/.test(s)).length,
  );
  return counts;
}

function sourceFiles(dir: string): string[] {
  return readdirSync(dir, { withFileTypes: true }).flatMap((entry) => {
    const full = path.join(dir, entry.name);
    if (entry.isDirectory()) return entry.name === "generated" ? [] : sourceFiles(full);
    if (!/\.(ts|tsx)$/.test(entry.name) || /\.(test|spec)\.(ts|tsx)$/.test(entry.name)) return [];
    return [full];
  });
}

export type Counts = Record<string, Partial<Record<Rule, number>>>;

/** Every source file's counts, files with nothing to count left out. */
export function scanTree(): Counts {
  const result: Counts = {};
  for (const full of sourceFiles(SRC).sort()) {
    const rel = path.relative(FRONTEND, full).split(path.sep).join("/");
    const counts = scan(rel, readFileSync(full, "utf8"));
    if (Object.keys(counts).length > 0) result[rel] = counts;
  }
  return result;
}

export function readBaseline(): Counts {
  return JSON.parse(readFileSync(BASELINE_PATH, "utf8")) as Counts;
}

/** One line per file and rule whose count rose above its baseline. */
export function regressions(current: Counts, baseline: Counts): string[] {
  const out: string[] = [];
  for (const [file, counts] of Object.entries(current)) {
    for (const [rule, n] of Object.entries(counts) as [Rule, number][]) {
      const allowed = baseline[file]?.[rule] ?? 0;
      if (n > allowed) out.push(`${file}: ${n} × ${RULES[rule]} (baseline ${allowed})`);
    }
  }
  return out;
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const current = scanTree();
  if (process.argv.includes("--write")) {
    writeFileSync(BASELINE_PATH, `${JSON.stringify(current, null, 2)}\n`);
    console.log(`Wrote ${Object.keys(current).length} files to ${path.relative(FRONTEND, BASELINE_PATH)}`);
  } else {
    const found = regressions(current, readBaseline());
    for (const line of found) console.error(line);
    process.exit(found.length > 0 ? 1 : 0);
  }
}
