/**
 * Attribution and ratchet logic for the unmocked-API gate (trust-v1 R1-21). Pure, so it is unit
 * tested; the Playwright reporter in `unmockedApiReporter.ts` only feeds it.
 *
 * The suite runs one worker with no parallelism, so tests run strictly one after another and a
 * request belongs to the test that most recently began before it arrived (each start is the
 * worker's own clock, so a test's first request never lands before its start is known). A request that lands
 * after a test ended but before the next began (a late fetch from a closing page) still belongs
 * to the test whose page sent it, which is that same most-recent test.
 */

export interface TripwireHit {
  at: number;
  method: string;
  path: string;
}

export interface TestStart {
  key: string;
  at: number;
}

/** testKey → the sorted, de-duplicated "METHOD /path" calls it made without a mock. */
export type UnmockedByTest = Record<string, string[]>;

/** Ids and numbers in a path change between fixtures; the route shape is what a mock covers. */
export function routeShape(path: string): string {
  return path
    .split("/")
    .map((segment) =>
      /^\d+$/.test(segment) || /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(segment)
        ? ":id"
        : segment
    )
    .join("/");
}

export function attribute(hits: readonly TripwireHit[], starts: readonly TestStart[]): {
  byTest: UnmockedByTest;
  beforeAnyTest: string[];
} {
  const ordered = [...starts].sort((a, b) => a.at - b.at);
  const sets = new Map<string, Set<string>>();
  const orphans = new Set<string>();

  for (const hit of hits) {
    const call = `${hit.method} ${routeShape(hit.path)}`;
    let owner: TestStart | undefined;
    for (const start of ordered) {
      if (start.at <= hit.at) owner = start;
      else break;
    }
    if (!owner) {
      orphans.add(call);
      continue;
    }
    if (!sets.has(owner.key)) sets.set(owner.key, new Set());
    sets.get(owner.key)!.add(call);
  }

  const byTest: UnmockedByTest = {};
  for (const key of [...sets.keys()].sort()) byTest[key] = [...sets.get(key)!].sort();
  return { byTest, beforeAnyTest: [...orphans].sort() };
}

export interface Verdict {
  /** Calls not covered by the baseline: these fail the run. */
  unexpected: UnmockedByTest;
  /** Baseline calls not seen in this run (only meaningful for a full run). */
  unseen: UnmockedByTest;
}

/**
 * The baseline is a shrinking list of known debts, each an open change request to the spec's
 * owner. Anything outside it fails. Nothing a change introduces may be added to make a run pass;
 * the one addition allowed is an older debt in someone else's spec that timing had hidden (see
 * unmockedApiReporter.ts).
 */
export function judge(found: UnmockedByTest, baseline: UnmockedByTest, ranKeys: ReadonlySet<string>): Verdict {
  const unexpected: UnmockedByTest = {};
  for (const [key, calls] of Object.entries(found)) {
    const allowed = new Set(baseline[key] ?? []);
    const extra = calls.filter((call) => !allowed.has(call));
    if (extra.length) unexpected[key] = extra;
  }
  const unseen: UnmockedByTest = {};
  for (const [key, calls] of Object.entries(baseline)) {
    if (!ranKeys.has(key)) continue;
    const seen = new Set(found[key] ?? []);
    const missing = calls.filter((call) => !seen.has(call));
    if (missing.length) unseen[key] = missing;
  }
  return { unexpected, unseen };
}

export function parseHits(log: string): TripwireHit[] {
  return log
    .split("\n")
    .filter((line) => line.trim() !== "")
    .map((line) => JSON.parse(line) as TripwireHit);
}

/**
 * Where WebKit shard `shard` writes a harvest (UNMOCKED_API_FOUND): `found.json` →
 * `found.json.webkit-3.json`. Each shard is its own Playwright run with its own reporter, so one
 * shared path keeps only the last shard's findings.
 */
export function shardHarvestFile(file: string, shard: number): string {
  return `${file}.webkit-${shard}.json`;
}
