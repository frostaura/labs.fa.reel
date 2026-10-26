import type { ConditionChip } from "./pickConditions";

/** How one condition chip presents on Home: its glyph, its visible text and its spoken name. */
export interface ConditionChipFace {
  /** A genre or keyword rule: "+" for one the picks must have, "−" for one they must not. */
  sign: "include" | "exclude" | null;
  /** What the chip shows beside the glyph — the rule's subject alone when the glyph says the rest. */
  text: string;
  /** The whole rule in words, for the accessible name and the tooltip. Never relies on the glyph. */
  spoken: string;
}

/**
 * The chip's face. Genre and keyword rules lose their "has " / "no " lead-in on screen because the
 * ± glyph says it — "− documentary" rather than a red "no documentary" — while the spoken name keeps
 * the words, so a screen reader and a colour-blind reader get the rule without the glyph.
 *
 * Keyed on `summaryChips`' own key prefixes (`inc:`, `exc:`, `kw:`), which are the derivation's
 * stable identity for these rules; every other rule (rating floors, years, languages, maturity)
 * has no sign and keeps its label verbatim.
 */
export function conditionChipFace(chip: Pick<ConditionChip, "key" | "label">): ConditionChipFace {
  const spoken = chip.label.charAt(0).toUpperCase() + chip.label.slice(1);
  if (chip.key.startsWith("inc:")) {
    return { sign: "include", text: chip.label.replace(/^has /, ""), spoken };
  }
  if (chip.key.startsWith("exc:") || chip.key.startsWith("kw:")) {
    return { sign: "exclude", text: chip.label.replace(/^no /, ""), spoken };
  }
  return { sign: null, text: chip.label, spoken };
}
