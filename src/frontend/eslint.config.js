import js from "@eslint/js";
import tseslint from "typescript-eslint";
import reactHooks from "eslint-plugin-react-hooks";
import reactRefresh from "eslint-plugin-react-refresh";

// Design-system guard: ban arbitrary font sizes so typography stays centralised.
// Use a role (fa-overline / fa-caption / fa-body / fa-section-title / fa-metric /
// fa-metric-sm — see index.css), a type-scale step (text-caption / body / card-title / row / page /
// display), or a standard step (text-xs/sm/base/lg/xl/2xl).
const FONT_SIZE_BANS = [
  {
    selector: "Literal[value=/text-\\[\\d+px\\]/]",
    message:
      "Arbitrary font size (text-[..px]) is banned. Use a typography role (fa-overline/fa-caption/fa-body/fa-section-title/fa-metric/fa-metric-sm) or a standard step (text-xs/sm/base/lg/xl/2xl). See index.css.",
  },
  {
    selector: "TemplateElement[value.raw=/text-\\[\\d+px\\]/]",
    message:
      "Arbitrary font size (text-[..px]) is banned. Use a typography role or a standard step (text-xs/sm/base/lg/xl/2xl). See index.css.",
  },
];

// The token bans (WS-9 task 7, I-8/O-16), on in every directory whose migration is complete.
// esquery regexes cannot contain "/", so a slash is written \u002F.
const HEX = "#([0-9a-fA-F]{3,4}|[0-9a-fA-F]{6}|[0-9a-fA-F]{8})(?![\\w-])";
const LOW_TEXT_OPACITY = "(^|[\\s:])text-[a-z][a-z0-9-]*\\u002F[0-7]?[0-9](?![\\d.\\w])";
const TOKEN_BANS = [
  {
    selector: `Literal[value=/(^|[^\\w&-])${HEX}/]`,
    message: "Hex colours live in the token layer (index.css / tailwind.config.js) and src/design/chartPalette.ts only. Use a token class.",
  },
  {
    selector: `TemplateElement[value.raw=/(^|[^\\w&-])${HEX}/]`,
    message: "Hex colours live in the token layer (index.css / tailwind.config.js) and src/design/chartPalette.ts only. Use a token class.",
  },
  {
    selector: `Literal[value=/${LOW_TEXT_OPACITY}/]`,
    message: "Text below 80% opacity fails contrast on glass. Use text-2 or text-3 instead of lowering a text colour's opacity.",
  },
  {
    selector: `TemplateElement[value.raw=/${LOW_TEXT_OPACITY}/]`,
    message: "Text below 80% opacity fails contrast on glass. Use text-2 or text-3 instead of lowering a text colour's opacity.",
  },
  {
    selector: "Literal[value=/(^|\\s)outline-none(\\s|$)/]:not([value=/focus(-visible)?:/])",
    message: "outline-none removes the keyboard focus indicator. Pair it with a focus-visible: style in the same class string.",
  },
  {
    selector: "JSXOpeningElement[name.name='select']",
    message: "Native <select> is banned. Use the Select primitive (components/ui/select).",
  },
];

const RAW_BUTTON_BAN = {
  selector: "JSXOpeningElement[name.name='button']",
  message: "Raw <button> outside components/ui/ is banned. Use Button, Chip or another ui/ primitive.",
};

// Directories whose migration onto the tokens and primitives is complete. Each surface owner adds
// theirs when its migration lands; everything else is held by the design ratchet
// (scripts/designRatchet.ts), which already bans these patterns in any NEW file and any rise.
// src/design/ is the token and chart-palette module itself, where colour literals are the point.
const MIGRATED_UI = ["src/components/ui/**/*.{ts,tsx}"];
const MIGRATED = [
  "src/App.tsx",
  "src/main.tsx",
  "src/components/{Layout,Footer,MobileTabBar,RealtimeSync,RequireAuth,RequireFounder,SyncStatusPill,VerdictToast,FadeScrollRow,ErrorBoundary,OfflineBanner,UpdatePrompt,SignOutCleanup,NavigationGuard}.tsx",
  "src/components/search/UndoToast.tsx",
];

export default tseslint.config(
  { ignores: ["dist", "node_modules"] },
  {
    extends: [js.configs.recommended, ...tseslint.configs.recommended],
    files: ["**/*.{ts,tsx}"],
    plugins: {
      "react-hooks": reactHooks,
      "react-refresh": reactRefresh,
    },
    rules: {
      ...reactHooks.configs.recommended.rules,
      "react-refresh/only-export-components": ["warn", { allowConstantExport: true }],
      // Relax some strict rules that are fine for this codebase
      "@typescript-eslint/no-unused-vars": ["warn", { argsIgnorePattern: "^_", varsIgnorePattern: "^_" }],
      "@typescript-eslint/no-explicit-any": "warn",
      // eslint-plugin-react-hooks v7 promotes the React Compiler advisory rules to errors.
      // These flag patterns that are not compiler-optimal (sync setState in effects, ref/var
      // access ordering, inline component creation) rather than correctness bugs. Keep them
      // visible as warnings and adopt incrementally instead of failing CI on working code.
      "react-hooks/set-state-in-effect": "warn",
      "react-hooks/refs": "warn",
      "react-hooks/immutability": "warn",
      "react-hooks/static-components": "warn",
      "no-restricted-syntax": ["error", ...FONT_SIZE_BANS],
    },
  },
  // A later block's no-restricted-syntax REPLACES the earlier one, so each repeats the font bans.
  {
    files: MIGRATED_UI,
    rules: { "no-restricted-syntax": ["error", ...FONT_SIZE_BANS, ...TOKEN_BANS] },
  },
  {
    files: MIGRATED,
    rules: { "no-restricted-syntax": ["error", ...FONT_SIZE_BANS, ...TOKEN_BANS, RAW_BUTTON_BAN] },
  },
);
