import axe from "axe-core";

/**
 * axe in a component test (WS-2B CR-3): the e2e harness (tests-e2e/a11y.spec.ts) runs the full rule
 * set per route in real browsers; this runs it on one rendered component in jsdom, so a surface
 * owner can pin a component's accessibility next to its behaviour without booting a page.
 *
 * jsdom has no layout and no paint, so the rules that need them — colour contrast, target size,
 * scrollable-region focus — cannot be judged here and are switched off; the browser run owns
 * those. Everything else (names, roles, ARIA attributes and states, labels, landmarks inside the
 * component, list structure, duplicate ids) runs.
 *
 * Usage, in a `// @vitest-environment jsdom` file:
 *   const { container } = render(<Thing />);
 *   expect(await axeViolations(container)).toEqual([]);
 */
const NEEDS_A_BROWSER = ["color-contrast", "color-contrast-enhanced", "target-size", "scrollable-region-focusable"];

/** A violation, flattened so `toEqual([])` prints what broke and where. */
export interface AxeFinding {
  rule: string;
  impact: string | null | undefined;
  help: string;
  targets: string[];
}

/**
 * Runs axe on `node` and returns its violations. `disable` turns off further rules by id, for a
 * component that is legitimately a fragment (e.g. `region` for content meant to sit in a landmark).
 */
export async function axeViolations(node: Element, disable: string[] = []): Promise<AxeFinding[]> {
  const rules = Object.fromEntries([...NEEDS_A_BROWSER, ...disable].map((id) => [id, { enabled: false }]));
  const result = await axe.run(node, { rules, resultTypes: ["violations"] });
  return result.violations.map((v) => ({
    rule: v.id,
    impact: v.impact,
    help: v.help,
    targets: v.nodes.map((n) => n.target.join(" ")),
  }));
}
