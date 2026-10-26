import type { MatchTier, TypeaheadPayload } from "../store/api/typeahead";

/**
 * Whether the typeahead should reach beyond the ingested catalogue for a term.
 *
 * The global TMDB leg is a fallback, not a firehose: it fires once the LOCAL lookup has resolved
 * with fewer than 3 STRONG matches (a word start or better) for a term of 3+ characters. Counting
 * every row let fuzzy junk ("Cheaters" for "heat") hold the global leg back (C*3).
 */
export function shouldQueryGlobal(term: string, strongMatches: number): boolean {
  return term.trim().length >= 3 && strongMatches < 3;
}

const TIER_RANK: Record<MatchTier, number> = { exact: 0, prefix: 1, word: 2, fuzzy: 3 };

function rank(tier: MatchTier | undefined): number {
  return tier ? TIER_RANK[tier] : TIER_RANK.fuzzy;
}

/**
 * The strong-match count, from the server when it says, else every title row (a server that
 * predates the tiered lookup sends no tiers, and its rows were the whole shortlist).
 */
export function strongMatchCount(payload: TypeaheadPayload | undefined): number {
  if (!payload) return 0;
  return payload.strongMatches ?? payload.titles.length;
}

/** True when the people group answers the term better than the titles do ("nolan", "keanu"). */
export function peopleFirst(payload: TypeaheadPayload | undefined): boolean {
  if (!payload || payload.people.length === 0) return false;
  if (payload.titles.length === 0) return true;
  return rank(payload.people[0].tier) < rank(payload.titles[0].tier);
}

/** Where Enter goes without an active option: an EXACT match opens directly; anything less asks Reel. */
export type EnterTarget = { kind: "title"; index: number } | { kind: "person"; index: number } | { kind: "ask" };

export function enterTarget(payload: TypeaheadPayload | undefined): EnterTarget {
  if (!payload) return { kind: "ask" };
  const titleExact = payload.titles[0]?.tier === "exact";
  const personExact = payload.people[0]?.tier === "exact";
  if (titleExact && !(personExact && peopleFirst(payload))) return { kind: "title", index: 0 };
  if (personExact) return { kind: "person", index: 0 };
  return { kind: "ask" };
}

/** "3 matches hidden by your conditions" — or nothing to say. */
export function hiddenLine(count: number | undefined): string | null {
  if (!count || count <= 0) return null;
  return `${count} ${count === 1 ? "match" : "matches"} hidden by your conditions`;
}
