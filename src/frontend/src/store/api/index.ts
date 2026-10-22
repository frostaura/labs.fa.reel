import type { Api } from "@reduxjs/toolkit/query/react";
import { api as baseApi } from "./base";
import type { authApi } from "./auth";
import type { feedApi } from "./feed";
import type { metricsApi } from "./metrics";
import type { onboardingApi } from "./onboarding";
import type { savedApi } from "./saved";
import type { searchApi } from "./search";
import type { settingsApi } from "./settings";
import type { syncApi } from "./sync";
import type { tasteApi } from "./taste";
import type { titlesApi } from "./titles";
import type { typeaheadApi } from "./typeahead";

export * from "./auth";
export * from "./feed";
export * from "./metrics";
export * from "./onboarding";
export * from "./saved";
export * from "./search";
export * from "./settings";
export * from "./sync";
export * from "./taste";
export * from "./titles";
export * from "./typeahead";

type DefinitionsOf<A> = A extends Api<infer _BaseQuery, infer Definitions, infer _ReducerPath, infer _TagTypes, infer _Enhancers>
  ? Definitions
  : never;

type ReelEndpoints = DefinitionsOf<typeof authApi> &
  DefinitionsOf<typeof feedApi> &
  DefinitionsOf<typeof metricsApi> &
  DefinitionsOf<typeof onboardingApi> &
  DefinitionsOf<typeof savedApi> &
  DefinitionsOf<typeof searchApi> &
  DefinitionsOf<typeof settingsApi> &
  DefinitionsOf<typeof syncApi> &
  DefinitionsOf<typeof tasteApi> &
  DefinitionsOf<typeof titlesApi> &
  DefinitionsOf<typeof typeaheadApi>;

/**
 * The slice every domain file injected into, typed with all of their endpoints. It is the same
 * object as `./base`'s `api` — `injectEndpoints` mutates and returns its receiver — so the store
 * mounts one reducer and one middleware, and `api.util` / `api.endpoints` reach every endpoint.
 */
export const api = baseApi as ReturnType<typeof baseApi.injectEndpoints<ReelEndpoints>>;
