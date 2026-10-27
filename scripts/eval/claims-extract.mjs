// User-facing claims audit — the extraction half (program WS-7 task 9). Pure: reads source files only.
//
// It walks the frontend (src/frontend/src, tests and generated types excluded) with the TypeScript
// compiler and the backend's user-facing C# (Api and Application, logging and exception messages
// excluded) with a literal scanner, and returns every string a user can read that is CLAIM-SHAPED: it
// asserts what the product does ("Watch something and it leaves by itself", "Everything here syncs",
// "Only a model that beats it ships") rather than labelling a control. claims.mjs then holds each one to
// the register in eval/claims/claims.json.
import { readFileSync, readdirSync, statSync } from "node:fs";
import { join, relative, sep } from "node:path";
import { createRequire } from "node:module";

// Behavioural promises: absolutes, automatic behaviour, sync and privacy, money, timing, freshness and
// the product's own vocabulary for "this suits you". Calibrated on the 2026-10-26 tree: every example
// claim the program names is caught, and a label ("Save", "Sign in with Trakt") is not.
export const CLAIM_TRIGGER = new RegExp(
  [
    String.raw`\b(?:only|never|always|every(?:thing|one|body)?|all (?:of )?(?:your|the|it)|no (?:ads|one|longer)|nothing (?:else|here))\b`,
    String.raw`\b(?:automatic(?:ally)?|by itself|on (?:its|their) own|instantly|immediately|in seconds|seconds?-long|real[- ]time|live)\b`,
    String.raw`\b(?:sync(?:s|ed|ing)?|unlock(?:s|ed)?|guarantee[sd]?|private(?:ly)?|forever|permanently|undo(?:ne)?)\b`,
    String.raw`\b(?:we (?:do not|don't|never|won't|will|keep|store|send|share)|you(?:'ll| will) (?:never|always|see|get|be))\b`,
    String.raw`\b(?:leaves?|removes?|disappears?|drops? off|won't (?:show|see|appear)|hidden from|stays? (?:out|hidden|off))\b`,
    String.raw`\b(?:just (?:landed|arrived|added|released)|new (?:&|and) high|high for you|for you|your (?:services|taste|model|ratings)|streaming (?:on|now)|on your services)\b`,
    String.raw`\b(?:free|paid|trial|cancel|refund|subscription|billing|charged?)\b`,
    String.raw`\b(?:ships?|beats?|retrain(?:s|ed)?|trains?|learns?|predict(?:s|ed|ion)?|accura(?:te|cy)|calibrat(?:ed|ion))\b`,
    String.raw`\b(?:tonight|today|weekly|daily|hourly|on fridays|every (?:few|week|day|night|hour)|at the latest|within|shortly|soon)\b`,
    String.raw`\b(?:delete[sd]?|erase[sd]?|export(?:s|ed)?|notif(?:y|ies|ication)s?|email(?:s|ed)?)\b`,
  ].join("|"),
  "iu",
);

// A string is user-readable prose when it carries at least three words and starts like a sentence or a
// fragment a component renders; identifiers, CSS classes, URLs, test ids and keys are not.
const PROSE = /^(?=.*\p{L}{2,}.*\s\S+\s\S+)[^\s{}<>]/u;
const NOT_PROSE = /^(?:[a-z0-9_-]+(?:\s+[a-z0-9_:/[\]().!#-]+)*|https?:\/\/\S+|\/api\/\S*|[A-Z_]{3,}|\S+\.\S+\(.*)$/u;
const TAILWIND = /\b(?:flex|grid|text-|bg-|px-|py-|mt-|mb-|rounded|border|items-|justify-|gap-|w-|h-|min-|max-|hover:|focus|sm:|md:|lg:|aria-)\S*/u;

export function isProse(text) {
  const t = text.trim();
  if (t.length < 12 || !PROSE.test(t)) return false;
  if (NOT_PROSE.test(t)) return false;
  // Tailwind class lists: mostly utility tokens.
  const tokens = t.split(/\s+/u);
  if (tokens.filter((x) => TAILWIND.test(x)).length >= Math.max(2, tokens.length / 2)) return false;
  return true;
}

export const normalise = (text) => text.replace(/\s+/gu, " ").replace(/&(?:ldquo|rdquo);/gu, '"').replace(/&amp;/gu, "&").replace(/&apos;|&rsquo;/gu, "'").trim();

const FRONTEND_EXCLUDE = /(?:\.test\.|\.spec\.|__tests__|\.gen\.|\.fixtures?\.|\.d\.ts$|\/test\/|\/mocks?\/|\.stories\.)/u;

function walk(dir, keep) {
  const out = [];
  for (const name of readdirSync(dir)) {
    const path = join(dir, name);
    if (name === "node_modules" || name === "bin" || name === "obj" || name.startsWith(".")) continue;
    const st = statSync(path);
    if (st.isDirectory()) out.push(...walk(path, keep));
    else if (keep(path)) out.push(path);
  }
  return out;
}

/** Every prose string in the frontend, with the file and line it is on. */
export function frontendStrings(repo) {
  const root = join(repo, "src", "frontend", "src");
  const ts = createRequire(join(repo, "src", "frontend", "package.json"))("typescript");
  const files = walk(root, (p) => /\.(tsx?|mts)$/u.test(p) && !FRONTEND_EXCLUDE.test(p.split(sep).join("/")));
  const found = [];
  for (const file of files) {
    const text = readFileSync(file, "utf8");
    const sf = ts.createSourceFile(file, text, ts.ScriptTarget.Latest, true, file.endsWith("x") ? ts.ScriptKind.TSX : ts.ScriptKind.TS);
    const add = (node, value) => {
      const v = normalise(value);
      if (!isProse(v)) return;
      found.push({ file: relative(repo, file).split(sep).join("/"), line: sf.getLineAndCharacterOfPosition(node.getStart()).line + 1, text: v });
    };
    const visit = (node) => {
      if (ts.isJsxElement(node) || ts.isJsxFragment(node)) {
        // One element's text, with each embedded expression as "…", so a sentence split by {count} reads whole.
        const parts = node.children.map((c) => (ts.isJsxText(c) ? c.text : ts.isJsxExpression(c) && c.expression && (ts.isStringLiteral(c.expression) || ts.isNoSubstitutionTemplateLiteral(c.expression)) ? c.expression.text : ts.isJsxExpression(c) ? "…" : "\u0000"));
        for (const chunk of parts.join("").split("\u0000")) if (chunk.replace(/…/gu, "").trim()) add(node, chunk);
      } else if (ts.isStringLiteral(node) || ts.isNoSubstitutionTemplateLiteral(node)) {
        // Skip import specifiers, JSX attribute names that are not copy, and object keys.
        const p = node.parent;
        const attr = p && ts.isJsxAttribute(p) ? p.name.getText() : null;
        if (!(p && (ts.isImportDeclaration(p) || ts.isExportDeclaration(p) || ts.isPropertyAssignment(p) && p.name === node)) && !(attr && /^(?:className|data-|href|to|id|key|type|role|src|variant|size)/u.test(attr))) add(node, node.text);
      } else if (ts.isTemplateExpression(node)) {
        add(node, [node.head.text, ...node.templateSpans.map((s) => `…${s.literal.text}`)].join(""));
      }
      ts.forEachChild(node, visit);
    };
    visit(sf);
  }
  return found;
}

const CS_LITERAL = /\$?@?"((?:[^"\\\n]|\\.|"")*)"/gu;
// Statements whose literals never reach a user: logging, exceptions, regexes, tracing, SQL, routing.
// A statement that opens on such a line keeps its continuation lines out too, until its closing ";".
const CS_NOT_COPY = /\b(?:Log(?:Information|Warning|Error|Debug|Trace|Critical)|logger\.|_logger\.|throw new|nameof|Exception\(|\[(?:Fact|Theory|InlineData|GeneratedRegex|Description)|Regex|Activity|Meter|Counter|Histogram|SetTag|AddTag|ConnectionString|Sql|FromSql|ExecuteSql|HasColumnName|ToTable|Route|Map(?:Get|Post|Put|Delete|Group)|WithName|WithTags|RequireAuthorization|Header|ContentType|application\/|InvalidOperation|ArgumentException)/u;

/** Every prose string literal in the backend's user-facing projects, with the file and line it is on. */
export function backendStrings(repo) {
  const roots = ["FrostAura.Reel.Api", "FrostAura.Reel.Application"].map((p) => join(repo, "src", "backend", p));
  const found = [];
  for (const root of roots) {
    for (const file of walk(root, (p) => p.endsWith(".cs") && !/[/\\](?:Migrations|Persistence)[/\\]/u.test(p))) {
      const lines = readFileSync(file, "utf8").split("\n");
      let inComment = false;
      let inNoise = false;
      lines.forEach((line, i) => {
        const trimmed = line.trim();
        if (trimmed.startsWith("/*")) inComment = true;
        if (inComment) { if (trimmed.includes("*/")) inComment = false; return; }
        if (trimmed.startsWith("//") || trimmed.startsWith("///")) return;
        if (CS_NOT_COPY.test(line)) inNoise = true;
        if (inNoise) { if (/;\s*(?:\/\/.*)?$/u.test(trimmed)) inNoise = false; return; }
        for (const m of line.matchAll(CS_LITERAL)) {
          const v = normalise(m[1].replace(/\{[^}]*\}/gu, "…").replace(/\\"/gu, '"').replace(/""/gu, '"'));
          if (isProse(v)) found.push({ file: relative(repo, file).split(sep).join("/"), line: i + 1, text: v });
        }
      });
    }
  }
  return found;
}

/** The claim-shaped user-facing strings in the tree: prose that matches CLAIM_TRIGGER, deduplicated by text and file. */
export function extractClaims(repo) {
  const seen = new Set();
  const out = [];
  for (const s of [...frontendStrings(repo), ...backendStrings(repo)]) {
    if (!CLAIM_TRIGGER.test(s.text)) continue;
    const key = `${s.file}\u0000${s.text}`;
    if (seen.has(key)) continue;
    seen.add(key);
    out.push(s);
  }
  return out;
}
