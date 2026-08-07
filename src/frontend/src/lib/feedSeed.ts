/**
 * Decides which feed snapshot the home rows are seeded from.
 *
 * Every row is keyed on the returned value, so changing it remounts the rows and discards the pages
 * the user has scrolled. That makes this a UX rule, not a caching detail: acting on a card (rate /
 * save / not interested) queues a server rebuild, and seeding directly from the newest snapshot
 * meant a single rating reset the list out from under someone working through it. Queueing three
 * things from one row was impossible.
 *
 * So a fresher snapshot is *held*, not applied, and adopted only at a boundary the user chose.
 */
export interface SeedDecision {
  /** generatedAt of the newest snapshot in the RTK cache ("" when none has loaded yet). */
  latest: string;
  /** generatedAt the rows are currently seeded from ("" before the first load). */
  adopted: string;
  /** Set when the user pressed refresh — the next snapshot is theirs to be reset by. */
  adoptOnNext: boolean;
}

/**
 * Returns the key to seed from, or null to keep the current seed. Null is the common case: it is
 * what protects an in-progress scroll.
 */
export function resolveSeedKey({ latest, adopted, adoptOnNext }: SeedDecision): string | null {
  if (!latest) return null; // nothing loaded yet
  if (!adopted) return latest; // first snapshot — nothing to disturb
  if (latest === adopted) return null; // unchanged
  return adoptOnNext ? latest : null; // fresher: adopt only if the user asked
}
