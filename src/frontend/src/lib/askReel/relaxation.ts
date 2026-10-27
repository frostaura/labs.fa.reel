import type { AskKnockout } from "../askReelStream";

/** The chip's words: what turning the rule off would unlock, in the reader's terms. */
export function relaxationLabel(knockout: Pick<AskKnockout, "label" | "wouldUnlock">): string {
  return `Without “${knockout.label}”: +${knockout.wouldUnlock}`;
}
