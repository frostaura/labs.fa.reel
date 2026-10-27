import type { ChipTone } from "../../lib/pickConditions";

/**
 * The condition summary's colours, in one place so their contrast can be held to AA (R1-44).
 *
 * The summary renders at 11 px (`fa-caption`) and 10 px (`fa-overline`) on every surface that hosts
 * the editor: the Settings card (glass on ink), and the Home and Ask Reel dialogs (ink-2). Small
 * text needs 4.5:1. The muted text used to be `frost-dim`, which is 4.18:1 once the neutral chip's
 * glass sits on the dialog's ink-2 — so the neutral chip ("English only"), the group labels and
 * "+N more" failed axe there. `text-3` is the tertiary text token, AA on every ink surface by
 * construction (design/tokens.test.ts), and stays AA under a chip's glass on all of them
 * (conditionTone.test.ts).
 */

/** Group labels, the overflow count and the empty line. */
export const SUMMARY_MUTED_TEXT = "text-3";

/** One chip's border, fill and text, by tone. Include and exclude carry their meaning in words too. */
export const CHIP_TONE: Record<ChipTone, string> = {
  include: "border-fa-success/50 bg-fa-success/15 text-fa-success",
  exclude: "border-fa-danger/50 bg-fa-danger/15 text-fa-danger",
  neutral: `border-fa-edge bg-fa-glass ${SUMMARY_MUTED_TEXT}`,
};
