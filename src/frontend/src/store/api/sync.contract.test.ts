import { describe, expect, it } from "vitest";
import type { components } from "../generated/openapi";
import type { JobFailure, ProviderBreaker, ProviderHealth, SyncJobSummary, SyncStatus } from "./sync";

// The check is a type: each entry resolves to `true` only while the hand-written shape names
// exactly the generated keys. A field added to or dropped from a backend record turns one into
// `never`, and `tsc` (which type-checks this file with the rest of src) fails.
type Generated = components["schemas"];
type SameKeys<A, B> = [Exclude<keyof A, keyof B>, Exclude<keyof B, keyof A>] extends [never, never] ? true : never;

type ContractInSync = [
  SameKeys<SyncStatus, Generated["SyncStatusResponse"]>,
  SameKeys<SyncJobSummary, Generated["SyncJobSummary"]>,
  SameKeys<JobFailure, Generated["JobFailure"]>,
  SameKeys<ProviderBreaker, Generated["ProviderBreaker"]>,
  SameKeys<ProviderHealth, Generated["ProviderHealthSnapshot"]>,
  SameKeys<SyncStatus["retrain"], Generated["RetrainProgress"]>,
  SameKeys<NonNullable<SyncStatus["notEnoughRatings"]>, Generated["RatingsFloor"]>,
];

describe("sync status contract", () => {
  it("names exactly the keys of the generated v2 contract", () => {
    const inSync: ContractInSync = [true, true, true, true, true, true, true];
    expect(inSync.every(Boolean)).toBe(true);
  });
});
