import { createApi, fetchBaseQuery, type BaseQueryFn, type FetchArgs, type FetchBaseQueryError } from "@reduxjs/toolkit/query/react";
import { clearHasSession, markHasSession } from "../lib/sessionFlag";
import type {
  BlendPicksPayload,
  BlendPreview,
  ContinueEntry,
  DigestPayload,
  FeedCard,
  FeedPayload,
  GenreRail,
  MoreFeedRequest,
  MoreFeedResponse,
} from "./feedTypes";
import { titleRemoved, titleRestored } from "./removedTitlesSlice";
import type { PersonPayload, ProvidersPayload, SavedEntry, TitleDetailPayload } from "./titleTypes";

export interface TypeaheadTitle {
  titleId: string;
  mediaType: "Movie" | "Show";
  tmdbId: number | null;
  name: string;
  year: number | null;
  posterPath: string | null;
  isFullyWatched: boolean;
  predictedRating: number | null;
}

/** One "beyond your catalogue" row — a raw TMDB lookup hit, never ingested and never scored. */
export interface GlobalTypeaheadRow {
  source: "tmdb";
  mediaType: "movie" | "show";
  tmdbId: number;
  name: string;
  year: number | null;
  posterPath: string | null;
  crowdRating: number | null;
  predictedRating: number | null;
}

export interface TypeaheadPayload {
  titles: TypeaheadTitle[];
  people: { personId: string; name: string; knownFor: string | null; profilePath: string | null }[];
  /** Only present when the request asked for the global leg (`&global=1`). */
  global?: GlobalTypeaheadRow[];
}

export interface FiltersPayload {
  excludeGenres: string[];
  /** "Must have at least one of these" — the positive half of the genre rule. */
  includeGenres: string[];
  excludeKeywords: string[];
  maturityCeiling: string | null;
  minPredictedRating: number | null;
  /** Allowed original languages (ISO-639-1). Defaults to ["en"]; ["*"] = every language. */
  languages: string[];
  /** Minimum community score (0–10). null = off. */
  minCrowdRating: number | null;
  /** Release-year range; either bound may be null for open-ended. */
  minYear: number | null;
  maxYear: number | null;
}

export interface TasteDnaPayload {
  userMean: number;
  contrarianScore: number;
  ratingsCount: number;
  hoursWatched: number;
  titlesWatched: number;
  showCompletionRate: number;
  topGenres: { genre: string; affinity: number; count: number }[];
  eras: { decade: number; affinity: number; count: number }[];
  creators: { name: string; department: string | null; profilePath: string | null; affinity: number; count: number }[];
  topActors: { personId: string; name: string; profilePath: string | null; rating: number }[];
  histogram: { rating: number; count: number }[];
  drift: { year: number; shares: Record<string, number> }[];
}

/** Pipeline stages as reported by the backend (Account.PipelineStage). */
export type PipelineStage =
  | "Linked"
  | "Ingesting"
  | "Extracting"
  | "Training"
  | "Evaluated"
  | "FeedReady"
  | "Degraded";

export type Tier = "Free" | "Paid" | "Founder";

export interface Session {
  accountId: string;
  displayName: string;
  traktSlug: string;
  avatarUrl: string | null;
  region: string;
  tier: Tier;
  pipelineStage: PipelineStage;
  onboarded: boolean;
  pinConfigured: boolean;
}

export interface TraktAuthStart {
  authorizeUrl: string;
  state: string;
}

export interface SyncJobSummary {
  kind: string;
  status: "Pending" | "Running" | "Succeeded" | "Failed" | "Cancelled";
  progressPct: number | null;
  progressMessage: string | null;
  enqueuedAt: string;
  completedAt: string | null;
}

export interface SyncStatus {
  pipelineStage: PipelineStage;
  connectionStatus: "Active" | "RefreshFailed" | "Revoked";
  lastDeltaSyncAt: string | null;
  lastFullReconcileAt: string | null;
  activeJob: SyncJobSummary | null;
  recentJobs: SyncJobSummary[];
  outboxPending: number;
  outboxDeadLetters: number;
  /** When Trakt last accepted a list/write-back (max SentAt across the outbox). */
  lastListSyncAt: string | null;
  /** Most recent write-back error text, for the indicator's hover detail. */
  outboxLastError: string | null;
}

export interface AccountSettings {
  region: string;
  onboarded: boolean;
  email?: string | null;
}

export interface ModelEvalDetail {
  topRanked?: { titleId: string; rating: number; predicted: number; hit: boolean }[];
  featureImportance?: Record<string, number>;
  caveats?: string[];
}

/** One rolling split origin — the harness reports five (60/65/70/75/80% of the rating history). */
export interface ModelEvalOrigin {
  fraction: number;
  splitAt: string;
  trainRows: number;
  unfilteredHoldout: number;
  eligibleHoldout: number;
  threshold: number;
  lowSample: boolean;
  ndcgModel: number;
  ndcgBaseline: number;
  ndcgRelativeLift: number;
  precisionModel: number;
  precisionBaseline: number;
}

/**
 * Nullable everywhere the 2026-09 instrument repair added a column: ledger rows written before it
 * genuinely have no NDCG, no CI and no calibration, and rendering a zero would claim a measurement
 * that was never taken.
 */
export interface ModelEval {
  /** Quantised to 0.1 by a ten-slot cut — display only; the gate reads NDCG. */
  modelPrecisionAt10: number;
  baselinePrecisionAt10: number;
  /** The legacy precision lift, kept so the twelve historical rows stay comparable. */
  relativeImprovement: number;
  rmse: number;
  mae: number;
  spearmanRho: number;
  spearmanSe: number | null;
  ndcgAt10Model: number | null;
  ndcgAt10Baseline: number | null;
  ndcgLiftCiLow: number | null;
  ndcgLiftCiHigh: number | null;
  calibrationBias: number | null;
  ece: number | null;
  eligibleHoldoutCount: number | null;
  unfilteredHoldoutCount: number | null;
  holdoutPositiveCount: number;
  lowSample: boolean;
  passedGate: boolean;
  passedLegacyGate: boolean;
  origins: ModelEvalOrigin[] | null;
  detail: ModelEvalDetail;
}

export interface ModelRun {
  id: string;
  iteration: number;
  configHash: string;
  splitAt: string;
  trainRowCount: number;
  holdoutRowCount: number;
  positiveThreshold: number;
  status: string;
  startedAt: string;
  eval: ModelEval | null;
}

/** Predicted-vs-actual summary. `pearsonCorrelation` is null when it is genuinely undefined. */
export interface PredictionAccuracy {
  count: number;
  meanPredicted: number;
  meanActual: number;
  meanAbsoluteError: number;
  bias: number;
  withinOnePointRate: number;
  pearsonCorrelation: number | null;
}

export interface RetroMiss {
  titleId: string;
  name: string;
  year: number | null;
  predicted: number;
  actual: number;
  gap: number;
}

/** "Titles you watched because of Reel average X; titles you found yourself average Y." */
export interface CounterfactualWindow {
  window: string;
  days: number | null;
  viaReelCount: number;
  viaReelMeanRating: number | null;
  otherCount: number;
  otherMeanRating: number | null;
  delta: number | null;
}

/**
 * The retro closed loop: what Reel predicted against what actually happened. Computed live from
 * the managed list, ratings and stored scores — no new schema, and no smoothing.
 */
export interface ModelEfficiency {
  computedAt: string;
  conversions: {
    count: number;
    ratedCount: number;
    scoredCount: number;
    accuracy: PredictionAccuracy;
    ratingDistribution: { rating: number; count: number }[];
    worstMisses: RetroMiss[];
  };
  predictedVsActual: {
    pairs: number;
    accuracy: PredictionAccuracy;
    worstMisses: RetroMiss[];
  };
  counterfactual: CounterfactualWindow[];
}

export interface ModelMetrics {
  gate: {
    /** The mean-relative-NDCG-lift clause of the gate (0.20). */
    threshold: number;
    passed: boolean;
    latestLift: number | null;
    ndcgModel: number | null;
    ndcgBaseline: number | null;
    ndcgLiftCiLow: number | null;
    ndcgLiftCiHigh: number | null;
    ciExcludesZero: boolean;
    legacyThreshold: number;
    legacyPassed: boolean;
    iterationsUsed: number;
    killCriterionAt: number;
    criterionExceeded: boolean;
  };
  efficiency: ModelEfficiency;
  activeArtifact: { version: number; algo: string; trainedAt: string } | null;
  runs: ModelRun[];
}

/**
 * Auth rides in HttpOnly cookies (reel_at / reel_rt) — no headers to prepare; every request
 * just needs credentials included. The access cookie lives 15 minutes: on a 401 the base
 * query silently rotates the session via /auth/refresh (single-flight) and retries once.
 * A refreshed access cookie also heals the SSE EventSource on its next auto-reconnect.
 * Terminal 401s surface through query `error` states; RequireAuth redirects to landing.
 */
const rawBaseQuery = fetchBaseQuery({ baseUrl: "/api", credentials: "include" });

let refreshInFlight: Promise<Response> | null = null;

const baseQueryWithReauth: BaseQueryFn<string | FetchArgs, unknown, FetchBaseQueryError> = async (
  args,
  queryApi,
  extraOptions
) => {
  let result = await rawBaseQuery(args, queryApi, extraOptions);

  const url = typeof args === "string" ? args : args.url;
  if (result.error?.status === 401 && !url.startsWith("auth/")) {
    refreshInFlight ??= fetch("/api/auth/refresh", { method: "POST", credentials: "include" });
    const refresh = await refreshInFlight.finally(() => {
      refreshInFlight = null;
    });
    if (refresh.ok) {
      result = await rawBaseQuery(args, queryApi, extraOptions);
    }
  }

  return result;
};

export const api = createApi({
  reducerPath: "reelApi",
  baseQuery: baseQueryWithReauth,
  // "Feed" is the picks snapshot and is deliberately NOT invalidated by per-title reactions — see
  // the note on `markNotInterested`. "ContinueWatching" is split out so dropping a show can refresh
  // that rail without re-seeding (and so resetting) every infinite row the user is scrolling.
  tagTypes: ["Session", "Sync", "Feed", "ContinueWatching", "TasteDna", "Lab", "Title", "Saved", "Prefs", "Person", "PrefTags"],
  endpoints: (b) => ({
    getSession: b.query<Session, void>({
      query: () => "auth/me",
      providesTags: ["Session"],
      // A successful probe proves this browser holds a session — remember it so the landing
      // page's probe-skip (see Landing.tsx) never applies to a signed-in browser. A failure
      // deliberately does NOT clear the flag: a network blip must not degrade the next visit.
      async onQueryStarted(_arg, { queryFulfilled }) {
        try {
          await queryFulfilled;
          markHasSession();
        } catch {
          // Signed out or offline — leave the flag as-is; only logout clears it.
        }
      },
    }),
    startTraktAuth: b.mutation<TraktAuthStart, void>({
      // POST: generates a fresh signed state server-side on each call.
      query: () => ({ url: "auth/trakt/start", method: "POST" }),
    }),
    exchangeTraktCode: b.mutation<Session, { code: string; state: string }>({
      query: (body) => ({ url: "auth/trakt/callback", method: "POST", body }),
      invalidatesTags: ["Session", "Sync"],
      // The OAuth callback IS account creation — set the flag here so the very first
      // post-sign-in landing visit probes instead of assuming signed-out.
      async onQueryStarted(_arg, { queryFulfilled }) {
        try {
          await queryFulfilled;
          markHasSession();
        } catch {
          // Exchange failed — no session was established, nothing to remember.
        }
      },
    }),
    logout: b.mutation<void, void>({
      query: () => ({ url: "auth/logout", method: "POST" }),
      async onQueryStarted(_arg, { queryFulfilled }) {
        // Clear regardless of outcome: the user asked to sign out, so the landing page should
        // treat this browser as signed-out even if the server call failed.
        clearHasSession();
        await queryFulfilled.catch(() => undefined);
      },
    }),
    getSyncStatus: b.query<SyncStatus, void>({
      query: () => "sync/status",
      providesTags: ["Sync"],
    }),
    triggerSync: b.mutation<void, void>({
      query: () => ({ url: "sync/now", method: "POST" }),
      invalidatesTags: ["Sync"],
    }),
    updateSettings: b.mutation<AccountSettings, Partial<AccountSettings>>({
      query: (body) => ({ url: "settings", method: "PUT", body }),
      invalidatesTags: ["Session"],
    }),
    getFeed: b.query<FeedPayload, void>({
      query: () => "feed",
      providesTags: ["Feed"],
    }),
    getContinueWatching: b.query<ContinueEntry[], void>({
      query: () => "feed/continue-watching",
      providesTags: ["ContinueWatching", "Feed"],
    }),
    rebuildFeed: b.mutation<void, void>({
      query: () => ({ url: "feed/rebuild", method: "POST" }),
      invalidatesTags: ["Sync"],
    }),
    getFeedGenres: b.query<GenreRail[], void>({
      query: () => "feed/genres",
      transformResponse: (r: { genres: GenreRail[] }) => r.genres,
      providesTags: ["Feed"],
    }),
    // Infinite-scroll page fetch. A mutation (not a query) because pagination is driven
    // imperatively — the caller accumulates pages and passes the ids already shown to exclude.
    // Returns the page plus the server's `exhausted` flag so the row can latch on true dryness
    // (never on a transient error). A missing flag defaults to exhausted for safety.
    loadMoreFeed: b.mutation<MoreFeedResponse, MoreFeedRequest>({
      query: (body) => ({ url: "feed/more", method: "POST", body }),
      transformResponse: (r: { items: FeedCard[]; exhausted?: boolean }) => ({ items: r.items, exhausted: r.exhausted ?? true }),
    }),
    getTitle: b.query<TitleDetailPayload, { mediaType: string; tmdbId: number }>({
      query: ({ mediaType, tmdbId }) => `titles/${mediaType}/${tmdbId}`,
      providesTags: (_r, _e, arg) => [{ type: "Title", id: `${arg.mediaType}:${arg.tmdbId}` }],
    }),
    getProviders: b.query<ProvidersPayload, { mediaType: string; tmdbId: number }>({
      query: ({ mediaType, tmdbId }) => `titles/${mediaType}/${tmdbId}/providers`,
      providesTags: (_r, _e, arg) => [{ type: "Title", id: `providers:${arg.mediaType}:${arg.tmdbId}` }],
    }),
    getPerson: b.query<PersonPayload, string>({
      query: (personId) => `people/${personId}`,
      providesTags: (_r, _e, personId) => [{ type: "Person", id: personId }],
    }),
    ratePerson: b.mutation<{ personId: string; rating: number }, { personId: string; rating: number }>({
      query: ({ personId, rating }) => ({ url: `people/${personId}/rating`, method: "POST", body: { rating } }),
      // Refresh the actor page, the DNA Top-Actors section, and the open title's cast badges.
      invalidatesTags: (_r, _e, arg) => [{ type: "Person", id: arg.personId }, "TasteDna", "Title"],
      async onQueryStarted({ personId, rating }, { dispatch, queryFulfilled }) {
        const patch = dispatch(
          api.util.updateQueryData("getPerson", personId, (draft) => {
            draft.userRating = rating;
          })
        );
        try {
          await queryFulfilled;
        } catch {
          patch.undo();
        }
      },
    }),
    unratePerson: b.mutation<{ personId: string }, { personId: string }>({
      query: ({ personId }) => ({ url: `people/${personId}/rating`, method: "DELETE" }),
      invalidatesTags: (_r, _e, arg) => [{ type: "Person", id: arg.personId }, "TasteDna", "Title"],
      async onQueryStarted({ personId }, { dispatch, queryFulfilled }) {
        const patch = dispatch(
          api.util.updateQueryData("getPerson", personId, (draft) => {
            draft.userRating = null;
          })
        );
        try {
          await queryFulfilled;
        } catch {
          patch.undo();
        }
      },
    }),
    rateTitle: b.mutation<
      { userRating: number; watchedLogged?: boolean },
      { titleId: string; mediaType: string; tmdbId: number; rating: number; markWatched?: boolean }
    >({
      // titleId rides along only to key the local removal set; it must not leak into the POST body.
      query: ({ mediaType, tmdbId, titleId: _titleId, ...body }) => ({ url: `titles/${mediaType}/${tmdbId}/rating`, method: "POST", body }),
      async onQueryStarted({ titleId, mediaType, tmdbId, rating }, { dispatch, queryFulfilled }) {
        const patch = dispatch(
          api.util.updateQueryData("getTitle", { mediaType, tmdbId }, (draft) => {
            draft.userState.userRating = rating;
            draft.userState.isFullyWatched = true;
          })
        );
        try {
          await queryFulfilled;
          // Seen+rated → the title has an interaction footprint now; prune it from the live rows.
          // Deliberately does NOT invalidate Feed — see `markNotInterested`.
          dispatch(titleRemoved({ titleId }));
          dispatch(api.util.invalidateTags(["TasteDna", "Saved"]));
        } catch {
          patch.undo();
        }
      },
    }),
    unrateTitle: b.mutation<{ userRating: null }, { mediaType: string; tmdbId: number }>({
      query: ({ mediaType, tmdbId }) => ({ url: `titles/${mediaType}/${tmdbId}/rating`, method: "DELETE" }),
      async onQueryStarted({ mediaType, tmdbId }, { dispatch, queryFulfilled }) {
        const patch = dispatch(
          api.util.updateQueryData("getTitle", { mediaType, tmdbId }, (draft) => {
            draft.userState.userRating = null;
          })
        );
        try {
          await queryFulfilled;
          // Un-rating changes taste and (for a Reel-logged movie) reverses the play — same
          // downstream surfaces as rateTitle.
          dispatch(api.util.invalidateTags(["TasteDna", "Saved"]));
        } catch {
          patch.undo();
        }
      },
    }),
    markNotInterested: b.mutation<void, { titleId: string; mediaType: string; tmdbId: number; reason?: string }>({
      query: ({ mediaType, tmdbId, reason }) => ({
        url: `titles/${mediaType}/${tmdbId}/reactions/not_interested`,
        method: "POST",
        body: { reason },
      }),
      async onQueryStarted({ titleId, mediaType, tmdbId }, { dispatch, queryFulfilled }) {
        const patch = dispatch(
          api.util.updateQueryData("getTitle", { mediaType, tmdbId }, (draft) => {
            draft.userState.notInterested = true;
          })
        );
        try {
          await queryFulfilled;
          // "Not interested" leaves the feed immediately via the local removal set — and ONLY via
          // that. Invalidating Feed here refetches the snapshot, which mints a new generatedAt,
          // which re-keys every row in Home, which throws away the pages the user has scrolled and
          // scrolls them back to the start. Acting on one card must never reset the list around it;
          // the server rebuild that this reaction triggers is adopted at a deliberate boundary
          // instead (Home's "new picks ready" control, or the next mount).
          dispatch(titleRemoved({ titleId }));
        } catch {
          patch.undo();
        }
      },
    }),
    undoNotInterested: b.mutation<void, { titleId: string; mediaType: string; tmdbId: number }>({
      query: ({ mediaType, tmdbId }) => ({ url: `titles/${mediaType}/${tmdbId}/reactions/not_interested`, method: "DELETE" }),
      async onQueryStarted({ titleId, mediaType, tmdbId }, { dispatch, queryFulfilled }) {
        const patch = dispatch(
          api.util.updateQueryData("getTitle", { mediaType, tmdbId }, (draft) => {
            draft.userState.notInterested = false;
          })
        );
        try {
          await queryFulfilled;
          // Re-eligible — restoring the key un-hides the card that is still in the row's local
          // state, so no refetch is needed (and a refetch would reset the row).
          dispatch(titleRestored({ titleId }));
        } catch {
          patch.undo();
        }
      },
    }),
    // `titleId` rides along only to key the local removal set — the URL is built from
    // mediaType/tmdbId and there is no body, so it never reaches the wire.
    markDropped: b.mutation<void, { titleId: string; mediaType: string; tmdbId: number }>({
      query: ({ mediaType, tmdbId }) => ({ url: `titles/${mediaType}/${tmdbId}/reactions/dropped`, method: "POST" }),
      invalidatesTags: ["ContinueWatching"], // that rail only — not the picks snapshot
      async onQueryStarted({ titleId, mediaType, tmdbId }, { dispatch, queryFulfilled }) {
        // Two patches, because the drop button exists on two surfaces. The detail page reads
        // getTitle; the Continue Watching shelf reads getContinueWatching, and it used to have no
        // optimistic patch at all — the card sat there through a POST *and* the invalidation
        // refetch before vanishing. On a fast local stack that is ~50ms and invisible, but there is
        // no spinner and no disabled state, so on a slow or flaky connection the button reads as
        // dead and invites a second click. Removing the entry up front makes the shelf respond to
        // the click itself rather than to the network.
        const patches = [
          dispatch(
            api.util.updateQueryData("getTitle", { mediaType, tmdbId }, (draft) => {
              draft.userState.dropped = true;
            })
          ),
          dispatch(
            api.util.updateQueryData("getContinueWatching", undefined, (draft) =>
              draft.filter((entry) => entry.tmdbId !== tmdbId)
            )
          ),
        ];
        try {
          await queryFulfilled;
          // Dropping is an interaction footprint like any other, so the title must leave the picks
          // rows too — not just the Continue Watching rail. It previously did not, and a dropped
          // show sat in Tonight's picks reading as if the drop had not registered. Via the local
          // removal set and ONLY that: invalidating Feed here would remount every infinite row and
          // discard the pages the user has scrolled — see `markNotInterested`.
          dispatch(titleRemoved({ titleId }));
        } catch {
          patches.forEach((p) => p.undo());
        }
      },
    }),
    undrop: b.mutation<void, { titleId: string; mediaType: string; tmdbId: number }>({
      query: ({ mediaType, tmdbId }) => ({ url: `titles/${mediaType}/${tmdbId}/reactions/dropped`, method: "DELETE" }),
      invalidatesTags: ["ContinueWatching"],
      async onQueryStarted({ titleId, mediaType, tmdbId }, { dispatch, queryFulfilled }) {
        const patch = dispatch(
          api.util.updateQueryData("getTitle", { mediaType, tmdbId }, (draft) => {
            draft.userState.dropped = false;
          })
        );
        try {
          await queryFulfilled;
          // Symmetric with the drop: restore the key so the card comes back to the live rows
          // without a refetch, exactly as `undoNotInterested` does. Without this an undrop leaves
          // the card hidden until a reload.
          dispatch(titleRestored({ titleId }));
        } catch {
          patch.undo();
        }
      },
    }),
    saveForLater: b.mutation<void, { titleId: string; mediaType: string; tmdbId: number }>({
      query: ({ mediaType, tmdbId }) => ({ url: `titles/${mediaType}/${tmdbId}/reactions/save_for_later`, method: "POST" }),
      async onQueryStarted({ titleId, mediaType, tmdbId }, { dispatch, queryFulfilled }) {
        const patch = dispatch(
          api.util.updateQueryData("getTitle", { mediaType, tmdbId }, (draft) => {
            draft.userState.savedForLater = true;
          })
        );
        try {
          await queryFulfilled;
          // A saved title drops out of the live rows now. Saved list refreshes; the picks snapshot
          // deliberately does not — see `markNotInterested`.
          dispatch(titleRemoved({ titleId }));
          dispatch(api.util.invalidateTags(["Saved"]));
        } catch {
          patch.undo();
        }
      },
    }),
    unsaveForLater: b.mutation<void, { titleId: string; mediaType: string; tmdbId: number }>({
      query: ({ mediaType, tmdbId }) => ({ url: `titles/${mediaType}/${tmdbId}/reactions/save_for_later`, method: "DELETE" }),
      async onQueryStarted({ titleId, mediaType, tmdbId }, { dispatch, queryFulfilled }) {
        const patch = dispatch(
          api.util.updateQueryData("getTitle", { mediaType, tmdbId }, (draft) => {
            draft.userState.savedForLater = false;
          })
        );
        try {
          await queryFulfilled;
          // Unsaving makes it eligible again — restore it to the live rows and refresh Saved.
          dispatch(titleRestored({ titleId }));
          dispatch(api.util.invalidateTags(["Saved"]));
        } catch {
          patch.undo();
        }
      },
    }),
    getSaved: b.query<{ managedListUrl: string | null; items: SavedEntry[] }, void>({
      query: () => "saved",
      providesTags: ["Saved"],
    }),
    getTasteDna: b.query<TasteDnaPayload, void>({
      query: () => "taste",
      providesTags: ["TasteDna"],
    }),
    getFilters: b.query<FiltersPayload, void>({
      query: () => "settings/filters",
      providesTags: ["Prefs"],
    }),
    // Saves ride the short-lived unlock token from /pin/verify (X-Settings-Unlock) — the raw PIN
    // is never cached client-side, so it is never replayed per call.
    updateFilters: b.mutation<FiltersPayload, { filters: FiltersPayload; unlockToken?: string }>({
      query: ({ filters, unlockToken }) => ({
        url: "settings/filters",
        method: "PUT",
        body: filters,
        headers: unlockToken ? { "X-Settings-Unlock": unlockToken } : undefined,
      }),
      invalidatesTags: ["Prefs", "Feed"],
    }),
    setPin: b.mutation<{ pinConfigured: boolean }, { pin: string; currentPin?: string }>({
      query: (body) => ({ url: "settings/pin", method: "POST", body }),
      invalidatesTags: ["Session"],
    }),
    verifyPin: b.mutation<{ valid: boolean; unlockToken: string; expiresAt: string }, { pin: string }>({
      query: (body) => ({ url: "settings/pin/verify", method: "POST", body }),
    }),
    // PIN removal is destructive, so it deliberately takes the freshly re-entered raw PIN — the
    // backend refuses unlock tokens on this endpoint and callers must prompt, not read a cache.
    removePin: b.mutation<{ pinConfigured: boolean }, { pin: string }>({
      query: ({ pin }) => ({ url: "settings/pin", method: "DELETE", headers: { "X-Settings-Pin": pin } }),
      invalidatesTags: ["Session"],
    }),
    searchTypeahead: b.query<TypeaheadPayload, string>({
      query: (q) => `search/typeahead?q=${encodeURIComponent(q)}`,
      keepUnusedDataFor: 30,
    }),
    // Fired only when the local shortlist comes back sparse — the backend re-runs the local
    // lookup and adds a `global` array of TMDB-wide matches (see shouldQueryGlobal).
    searchTypeaheadGlobal: b.query<TypeaheadPayload, string>({
      query: (q) => `search/typeahead?q=${encodeURIComponent(q)}&global=1`,
      keepUnusedDataFor: 30,
    }),
    getModelMetrics: b.query<ModelMetrics, void>({
      query: () => "metrics/model",
      providesTags: ["Lab"],
    }),
    trainModel: b.mutation<void, void>({
      query: () => ({ url: "metrics/model/train", method: "POST" }),
      invalidatesTags: ["Lab", "Sync"],
    }),
    getPreferenceTags: b.query<{ id: string; text: string }[], void>({
      query: () => "preferences/tags",
      providesTags: ["PrefTags"],
    }),
    addPreferenceTag: b.mutation<{ id: string; text: string }, string>({
      query: (text) => ({ url: "preferences/tags", method: "POST", body: { text } }),
      invalidatesTags: ["PrefTags", "Feed"],
    }),
    removePreferenceTag: b.mutation<void, string>({
      query: (id) => ({ url: `preferences/tags/${id}`, method: "DELETE" }),
      invalidatesTags: ["PrefTags", "Feed"],
    }),
    getBillingStatus: b.query<{ tier: string; configured: boolean }, void>({
      query: () => "billing/status",
      providesTags: ["Session"],
    }),
    getRateSuggestions: b.query<
      { titleId: string; mediaType: "Movie" | "Show"; tmdbId: number; name: string; year: number | null; posterPath: string | null }[],
      void
    >({
      query: () => "titles/rate-suggestions",
    }),
    createCheckout: b.mutation<{ url?: string; error?: string }, void>({
      query: () => ({ url: "billing/checkout", method: "POST" }),
    }),
    // Weekly digest — read-only snapshot of this week's "new & high for you" picks.
    getDigest: b.query<DigestPayload, void>({
      query: () => "digest/current",
    }),
    // Web push. The VAPID key is fetched lazily at opt-in time, not on page load.
    getVapidPublicKey: b.query<{ key: string }, void>({
      query: () => "push/vapid-public-key",
    }),
    subscribePush: b.mutation<{ ok: boolean }, { endpoint: string; keys: { p256dh: string; auth: string } }>({
      query: (body) => ({ url: "push/subscribe", method: "POST", body }),
    }),
    unsubscribePush: b.mutation<{ ok: boolean }, { endpoint: string }>({
      query: (body) => ({ url: "push/subscribe", method: "DELETE", body }),
    }),
    // Blend — two-person joint picks via a shareable code.
    createBlendCode: b.mutation<{ code: string }, void>({
      query: () => ({ url: "blend/code", method: "POST" }),
    }),
    getBlendPreview: b.query<BlendPreview, string>({
      query: (code) => `blend/${encodeURIComponent(code)}/preview`,
    }),
    getBlendPicks: b.mutation<BlendPicksPayload, string>({
      query: (code) => ({ url: `blend/${encodeURIComponent(code)}/picks`, method: "POST" }),
    }),
  }),
});

export const {
  useGetSessionQuery,
  useStartTraktAuthMutation,
  useExchangeTraktCodeMutation,
  useLogoutMutation,
  useGetSyncStatusQuery,
  useTriggerSyncMutation,
  useUpdateSettingsMutation,
  useGetModelMetricsQuery,
  useTrainModelMutation,
  useGetFeedQuery,
  useGetContinueWatchingQuery,
  useRebuildFeedMutation,
  useGetFeedGenresQuery,
  useLoadMoreFeedMutation,
  useGetTitleQuery,
  useGetProvidersQuery,
  useGetPersonQuery,
  useRatePersonMutation,
  useUnratePersonMutation,
  useRateTitleMutation,
  useUnrateTitleMutation,
  useMarkNotInterestedMutation,
  useUndoNotInterestedMutation,
  useMarkDroppedMutation,
  useUndropMutation,
  useSaveForLaterMutation,
  useUnsaveForLaterMutation,
  useGetSavedQuery,
  useGetTasteDnaQuery,
  useSearchTypeaheadQuery,
  useSearchTypeaheadGlobalQuery,
  useGetFiltersQuery,
  useUpdateFiltersMutation,
  useGetPreferenceTagsQuery,
  useAddPreferenceTagMutation,
  useRemovePreferenceTagMutation,
  useGetBillingStatusQuery,
  useCreateCheckoutMutation,
  useGetRateSuggestionsQuery,
  useSetPinMutation,
  useVerifyPinMutation,
  useRemovePinMutation,
  useGetDigestQuery,
  useLazyGetVapidPublicKeyQuery,
  useSubscribePushMutation,
  useUnsubscribePushMutation,
  useCreateBlendCodeMutation,
  useLazyGetBlendPreviewQuery,
  useGetBlendPicksMutation,
} = api;
