import { describe, expect, it } from "vitest";
import type { components } from "../generated/openapi";
import type { TraktRecommendationCard, TraktRecommendations, TraktRecommendationsState } from "../feedTypes";
import type { RecCardData } from "../../components/rec/RecCard";

// The check is a type, as in sync.contract.test.ts: each entry resolves to `true` only while the
// hand-written shape names exactly the generated keys (and enum members), so a field added to or
// dropped from TraktRecommendationEndpoints' records turns one into `never` and `tsc` fails.
type Generated = components["schemas"];
type SameKeys<A, B> = [Exclude<keyof A, keyof B>, Exclude<keyof B, keyof A>] extends [never, never] ? true : never;
type SameMembers<A, B> = [Exclude<A, B>, Exclude<B, A>] extends [never, never] ? true : never;
// And the card must render through the shared RecCard, honest null prediction and all.
type Renders<A> = A extends RecCardData ? true : never;

type ContractInSync = [
  SameKeys<TraktRecommendations, Generated["TraktRecommendationsResponse"]>,
  SameKeys<TraktRecommendationCard, Generated["TraktRecommendationCard"]>,
  SameMembers<TraktRecommendationsState, Generated["TraktRecommendationsState"]>,
  Renders<TraktRecommendationCard>,
];

describe("trakt recommendations contract", () => {
  it("names exactly the keys and states of the generated contract, and renders as a RecCard", () => {
    const inSync: ContractInSync = [true, true, true, true];
    expect(inSync.every(Boolean)).toBe(true);
  });
});
