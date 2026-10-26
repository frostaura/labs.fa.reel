import type { SharedEvidence } from "../store/feedTypes";

/**
 * The chips an anchored card shows: what it verifiably shares with the title its row is anchored
 * on. The server admits a card to "Because you loved X" or "More like this" only on this evidence,
 * so showing it makes the row's claim checkable on its face instead of a sentence to take on trust.
 */

export interface EvidenceChip {
  /** The chip text — short enough for a 176px card. */
  text: string;
  /** The full statement, for the tooltip and screen readers. */
  description: string;
}

/** At most three, like the server; a card is a poster, not a dossier. */
export const MAX_EVIDENCE_CHIPS = 3;

const ROLE_SHORT: Record<string, string> = { director: "dir.", writer: "writer" };
const ROLE_NOUN: Record<string, string> = { director: "director", writer: "writer", cast: "cast member" };

/** One evidence item as a chip. A blank label is no evidence at all, and yields null. */
export function evidenceChip(e: SharedEvidence): EvidenceChip | null {
  const label = e.label?.trim();
  if (!label) return null;
  if (e.kind === "person") {
    const short = e.role ? ROLE_SHORT[e.role] : undefined;
    const noun = (e.role && ROLE_NOUN[e.role]) || "person";
    return { text: short ? `${label} · ${short}` : label, description: `Shared ${noun}: ${label}` };
  }
  if (e.kind === "keyword") return { text: label, description: `Shared story keyword: ${label}` };
  if (e.kind === "theme") return { text: label, description: `Shared theme: ${label}` };
  return { text: label, description: `Shared: ${label}` };
}

/** The chips for a card, strongest first as the server ordered them, blanks dropped, capped. */
export function evidenceChips(evidence: SharedEvidence[] | null | undefined): EvidenceChip[] {
  return (evidence ?? [])
    .map(evidenceChip)
    .filter((c): c is EvidenceChip => c !== null)
    .slice(0, MAX_EVIDENCE_CHIPS);
}
