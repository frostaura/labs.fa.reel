import { describe, expect, it } from "vitest";
import type { components } from "../generated/openapi";
import type {
  ConnectionStatus,
  DegradedReason,
  JobFailure,
  JobStatus,
  JobVisibility,
  ProviderBreaker,
  ProviderHealth,
  ProviderHealthState,
  SyncJobSummary,
  SyncStatus,
} from "./sync";
import type { PipelineStage } from "./auth";

// The check is a type: each entry resolves to `true` only while the hand-written shape names
// exactly the generated keys. A field added to or dropped from a backend record turns one into
// `never`, and `tsc` (which type-checks this file with the rest of src) fails.
type Generated = components["schemas"];
type SameKeys<A, B> = [Exclude<keyof A, keyof B>, Exclude<keyof B, keyof A>] extends [never, never] ? true : never;
// The same check for the string enums: the backend appends values without notice, and a member this
// build does not list is one whose copy and fallback nobody has decided. Readers still map unknown
// values at runtime (lib/syncPill.ts); this makes the drift visible at build time as well.
type SameMembers<A, B> = [Exclude<A, B>, Exclude<B, A>] extends [never, never] ? true : never;

type ContractInSync = [
  SameKeys<SyncStatus, Generated["SyncStatusResponse"]>,
  SameKeys<SyncJobSummary, Generated["SyncJobSummary"]>,
  SameKeys<JobFailure, Generated["JobFailure"]>,
  SameKeys<ProviderBreaker, Generated["ProviderBreaker"]>,
  SameKeys<ProviderHealth, Generated["ProviderHealthSnapshot"]>,
  SameKeys<SyncStatus["retrain"], Generated["RetrainProgress"]>,
  SameKeys<NonNullable<SyncStatus["notEnoughRatings"]>, Generated["RatingsFloor"]>,
  SameMembers<JobVisibility, Generated["JobVisibility"]>,
  SameMembers<JobStatus, Generated["JobStatus"]>,
  SameMembers<ConnectionStatus, Generated["ConnectionStatus"]>,
  SameMembers<DegradedReason, NonNullable<Generated["DegradedReason"]>>,
  SameMembers<ProviderHealthState, Generated["ProviderHealthState"]>,
  SameMembers<ProviderBreaker["state"], Generated["ProviderBreakerState"]>,
  SameMembers<PipelineStage, Generated["PipelineStage"]>,
];

describe("sync status contract", () => {
  it("names exactly the keys and enum members of the generated v2 contract", () => {
    const inSync: ContractInSync = [true, true, true, true, true, true, true, true, true, true, true, true, true, true];
    expect(inSync.every(Boolean)).toBe(true);
  });
});
