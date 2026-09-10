import { createApi, fetchBaseQuery, type BaseQueryFn, type FetchArgs, type FetchBaseQueryError } from "@reduxjs/toolkit/query/react";
import { ensureFreshSession } from "../lib/session";
import { clearHasSession, markHasSession } from "../lib/sessionFlag";
import type {
  BlendPicksPayload,
  BlendPreview,
  ContinueEntry,
  DigestPayload,
  LensPayload,
  NarrativePayload,
  FeedCard,
  FeedPayload,
  GenreRail,
  MoreFeedRequest,
  MoreFeedResponse,
} from "./feedTypes";
import { titleRemoved, titleRestored } from "./removedTitlesSlice";
import { verdictShown } from "./verdictSlice";
import type { RatingVerdict } from "../lib/verdictCopy";
import type { PersonPayload, ProvidersPayload, TitleDetailPayload } from "./titleTypes";
import type { RecentlyWatchedResponse, SavedQueueResponse } from "./savedTypes";

/**
 * The "your model just learned" band's payload. Every field is either already in the evaluation
 * ledger or is a diff of two rows that already exist — nothing here was added to the schema.
 */
export interface ModelLearned {
  trainedAt: string;
  newRatingsSinceLast: number;
  calibrationBias: number | null;
  calibrationMae: number | null;
  /** Previous MAE minus current: positive means the model runs closer to the user's own scores. */
  maeImprovement: number | null;
  spearman: number | null;
  picksChanged: number;
}

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

/**
 * What one content rule costs. `marginal` is the eligible count with that ONE rule relaxed minus the
 * eligible count as drafted — "dropping comedy adds ~180". Marginals deliberately do NOT sum: two
 * rules can be holding the same title out, so relaxing both returns less than the two numbers
 * together, which is exactly why the meter reports per rule instead of one grand total.
 */
export interface FilterCostRule {
  /** The server's FilterKind name — "ExcludeGenre", "Language", "MinCrowdRating", "MinYear"… */
  kind: string;
  /** The value the user would tap off: a genre slug, a threshold, or the joined language list. */
  value: string;
  marginal: number;
}

/** A rule worth offering as a one-tap relaxation, because relaxing it actually returns titles. */
export interface FilterCostSuggestion {
  kind: string;
  value: string;
  gain: number;
}

/**
 * The cost of a draft rule set. `total` is the whole catalogue (the denominator in "421 of 5,319");
 * `eligible` is what survives BOTH the rules and the locked zero-footprint rule — the real supply
 * behind tonight's picks, not a filter-only count.
 */
export interface FilterCostReport {
  total: number;
  eligible: number;
  byRule: FilterCostRule[];
  suggestions: FilterCostSuggestion[];
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
  /**
   * Progress toward the next drift retrain, so a screen that asks for ratings can say what they
   * buy. `newRatingsSinceTrain` counts UserRatings whose immutable `FirstRatedAt` is newer than the
   * Active artifact's `TrainedAt` — the same two inputs the nightly reconcile's gate reads. Absent
   * on a response from a backend that predates the field.
   */
  retrain?: {
    minNewRatings: number;
    newRatingsSinceTrain: number;
    /** False before the first model exists: the next train is the FIRST, not a drift retrain. */
    hasActiveModel: boolean;
    lastTrainedAt: string | null;
  };
}

/**
 * One card on the rate grid. `source` says which half it came from: "watched" is a title the user
 * has seen and never scored — a memory, the highest-information label the product can ask for —
 * and "popular" is the cold-start fallback pool behind it.
 */
export interface RateSuggestion {
  titleId: string;
  mediaType: "Movie" | "Show";
  tmdbId: number;
  name: string;
  year: number | null;
  posterPath: string | null;
  crowdRating: number | null;
  crowdVotes: number | null;
  source: "watched" | "popular";
}

/**
 * One live dislike — a NotInterested or Dropped reaction that has not been revoked. `reason` is
 * null on rows recorded before the card overlay offered a picker (and on Dropped, which has no
 * reason), which is itself worth showing: it is why the profile below says less than the total.
 */
export interface DislikeEntry {
  titleId: string;
  mediaType: "Movie" | "Show";
  tmdbId: number | null;
  name: string;
  year: number | null;
  posterPath: string | null;
  kind: "NotInterested" | "Dropped";
  reason: "Genre" | "SeenEnough" | "Tone" | "NotForMe" | null;
  createdAt: string;
}

/**
 * The dislike history plus what it currently teaches the model. `total` counts every live dislike;
 * `profile.activeCount` counts only those inside `profile.windowDays`, which is the set the
 * recency-decayed demotion is actually built from — the two are reported separately rather than
 * letting one stand in for the other.
 */
export interface DislikesPayload {
  total: number;
  offset: number;
  limit: number;
  hasMore: boolean;
  items: DislikeEntry[];
  profile: {
    windowDays: number;
    activeCount: number;
    genres: { genre: string; weight: number }[];
    tones: { tone: string; weight: number }[];
  };
}

export interface AccountSettings {
  region: string;
  onboarded: boolean;
  email?: string | null;
  /** Served RESOLVED (never null): how long an evening is, for the Saved shelf's pinned pick. */
  eveningBudgetMinutes?: number;
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

/** One ranker's record across the interleaving experiment. */
export interface PolicyStanding {
  policyId: string;
  description: string | null;
  /** "A", "B", or null for a ranker that is no longer one of the configured arms. */
  team: string | null;
  slates: number;
  cards: number;
  credits: number;
  slateWins: number;
}

/**
 * The interleaving readout. Every number is computed on read from the exposure log, and the
 * always-valid p-value may be looked at as often as you like without inflating the error rate —
 * which is the entire reason the harness uses an mSPRT rather than a test with a sample-size plan.
 */
export interface InterleaveSummary {
  computedAt: string;
  enabled: boolean;
  policyA: string;
  policyB: string;
  tau: number;
  alpha: number;
  slates: number;
  decidedSlates: number;
  ties: number;
  winsA: number;
  winsB: number;
  alwaysValidP: number;
  significant: boolean;
  preferenceRateA: number | null;
  ciLow: number;
  ciHigh: number;
  signTestP: number;
  firstSlateAt: string | null;
  lastSlateAt: string | null;
  decidedSlatesPerDay: number | null;
  slatesToDecisionAt65: number;
  estimatedDaysToDecision: number | null;
  standings: PolicyStanding[];
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

/** One predicted band on the calibration chart. Null means the band is empty, not that it is zero. */
export interface CalibrationBucket {
  bucket: number;
  low: number;
  high: number;
  n: number;
  meanPredicted: number | null;
  meanActual: number | null;
}

/** One specific thing Reel got right or wrong, with enough identity to render its poster. */
export interface AccuracyMoment {
  titleId: string;
  name: string;
  year: number | null;
  posterPath: string | null;
  mediaType: string;
  tmdbId: number | null;
  predicted: number;
  actual: number;
  /** Null on the retro fallback — a stored score is not proof the card was ever on screen. */
  surface: string | null;
  shownAt: string | null;
  ratedAt: string;
  daysToOutcome: number | null;
}

/**
 * The track record. `source` is part of the payload rather than an implementation detail: the page
 * says out loud whether a number came from the exposure log ("we know this was on your screen") or
 * from the retro join ("a score existed before you rated it"), because the two are different
 * strengths of evidence and smoothing over the difference is the one thing this surface must not do.
 */
export interface AccuracyPayload {
  window: string;
  from: string | null;
  to: string;
  source: "exposures" | "retro" | "none";
  headline: {
    withinOnePoint: number;
    n: number;
    meanAbsoluteError: number;
    bias: number;
    correlation: number | null;
    meanPredicted: number;
    meanActual: number;
  };
  calibration: CalibrationBucket[];
  moments: AccuracyMoment[];
  conversion: {
    shown: number;
    saved: number;
    watched: number;
    rated: number;
    shownToSaved: number | null;
    savedToWatched: number | null;
    shownToRated: number | null;
    medianDaysToWatch: number | null;
  };
  counterfactual: CounterfactualWindow[];
  learnedThisWeek: {
    trainedAt: string;
    newRatings: number;
    calibrationBias: number | null;
    calibrationMae: number | null;
    maeImprovement: number | null;
    spearman: number | null;
    picksChanged: number;
  } | null;
}

/**
 * Auth rides in HttpOnly cookies (reel_at / reel_rt) — no headers to prepare; every request
 * just needs credentials included. The access cookie lives 15 minutes: on a 401 the base
 * query silently rotates the session via `ensureFreshSession` (single-flight, shared with the
 * raw-fetch callers in `lib/session.ts`) and retries once. A refreshed access cookie also heals
 * the SSE EventSource on its next auto-reconnect.
 * Terminal 401s surface through query `error` states; RequireAuth redirects to landing.
 */
const rawBaseQuery = fetchBaseQuery({ baseUrl: "/api", credentials: "include" });

const baseQueryWithReauth: BaseQueryFn<string | FetchArgs, unknown, FetchBaseQueryError> = async (
  args,
  queryApi,
  extraOptions
) => {
  let result = await rawBaseQuery(args, queryApi, extraOptions);

  const url = typeof args === "string" ? args : args.url;
  if (result.error?.status === 401 && !url.startsWith("auth/")) {
    if (await ensureFreshSession()) {
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
  tagTypes: ["Session", "Sync", "Feed", "ContinueWatching", "TasteDna", "Lab", "Title", "Saved", "RecentlyWatched", "Prefs", "Person", "PrefTags", "Dislikes", "Accuracy", "Narrative", "Lens"],
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
    /**
     * What the latest retrain bought the user. Its own endpoint rather than a field on `getFeed`:
     * the feed payload is fetched constantly and this is a retrain-cadence number. The server
     * answers 204 whenever the band would be a claim the data cannot support, which arrives here as
     * an empty body — hence the null.
     *
     * Tagged "Lab" alongside the ledger: both go stale on exactly the same event (a train/evaluate
     * run completing), and RealtimeSync already invalidates that tag when one does.
     */
    getModelLearned: b.query<ModelLearned | null, void>({
      query: () => ({
        url: "feed/learned",
        responseHandler: async (response) => (response.status === 204 ? null : await response.json()),
      }),
      providesTags: ["Lab"],
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
      { userRating: number; watchedLogged?: boolean; verdict?: RatingVerdict | null },
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
          const { data } = await queryFulfilled;
          // Seen+rated → the title has an interaction footprint now; prune it from the live rows.
          // Deliberately does NOT invalidate Feed — see `markNotInterested`.
          dispatch(titleRemoved({ titleId }));
          // A rating is exactly the answer the loop-closing prompt was asking for, so the strip
          // retires that title on the next read rather than continuing to ask a settled question.
          // Accuracy moves with every rating, so the track record is stale the moment one lands.
          dispatch(api.util.invalidateTags(["TasteDna", "Saved", "RecentlyWatched", "Accuracy"]));
          // The after-rating moment, raised here rather than at the three call sites that can rate:
          // one dispatch means the title modal, the rate picker, the post-watch strip and whatever
          // rating surface ships next all get it, and none of them can forget to.
          if (data?.verdict) {
            dispatch(verdictShown({ verdict: data.verdict, actual: rating }));
          }
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
          // downstream surfaces as rateTitle, the track record included: a retracted rating is one
          // fewer verdict against the model.
          dispatch(api.util.invalidateTags(["TasteDna", "Saved", "Accuracy"]));
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
      // The dislike history on Taste DNA is a plain list of these rows — safe to invalidate,
      // unlike Feed (see below), because nothing is scrolled through it.
      invalidatesTags: ["Dislikes"],
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
      invalidatesTags: ["Dislikes"],
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
      invalidatesTags: ["ContinueWatching", "Dislikes"],
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
    // The queue, in the order the user asked for. `sort` is part of the cache key, so switching
    // orders is a fresh request rather than a client-side re-sort — the server owns the comparator
    // (and the pinned pick), and two implementations of it would drift within a release.
    getSaved: b.query<SavedQueueResponse, { sort?: string } | void>({
      query: (args) => (args && args.sort ? `saved?sort=${encodeURIComponent(args.sort)}` : "saved"),
      providesTags: ["Saved"],
    }),
    // Watched-via-Reel titles that are still unrated. Kept under its own tag: rating one of these
    // must retire that prompt without refetching the whole shelf, and saving a title must not
    // disturb the strip the user is halfway through answering.
    getRecentlyWatched: b.query<RecentlyWatchedResponse, void>({
      query: () => "saved/recently-watched",
      providesTags: ["RecentlyWatched"],
    }),
    getTasteDna: b.query<TasteDnaPayload, void>({
      query: () => "taste",
      providesTags: ["TasteDna"],
    }),
    // The taste narrative — what Reel believes about you, in words you can correct.
    getNarrative: b.query<NarrativePayload, void>({
      query: () => "taste/narrative",
      providesTags: ["Narrative"],
    }),
    // Your own rewrite. It wins everywhere and survives regeneration; only the regenerate action
    // clears it. Invalidates Feed too, because the narrative IS the personalisation context every
    // downstream call rides on — an edit changes what the next build is told about you.
    saveNarrative: b.mutation<NarrativePayload, { text: string | null }>({
      query: (body) => ({ url: "taste/narrative", method: "PUT", body }),
      invalidatesTags: ["Narrative"],
    }),
    regenerateNarrative: b.mutation<{ queued: boolean }, void>({
      query: () => ({ url: "taste/narrative/regenerate", method: "POST" }),
      invalidatesTags: ["Narrative"],
    }),
    // On-demand lens for one title. Generates when nothing usable is cached, behind a 5s server
    // ceiling, and ALWAYS answers with a `source` — so the modal renders the ladder (fresh, stale,
    // or the templater fallback it already had) rather than a spinner that resolves to nothing.
    getLens: b.query<LensPayload, { mediaType: string; tmdbId: number }>({
      query: ({ mediaType, tmdbId }) => `titles/${mediaType}/${tmdbId}/lens`,
      providesTags: (_r, _e, arg) => [{ type: "Lens" as const, id: `${arg.mediaType}:${arg.tmdbId}` }],
    }),
    // "This spoiled it." Withdraws every note for the title so the surface falls back at once.
    reportLensSpoiler: b.mutation<{ withdrawn: number }, { mediaType: string; tmdbId: number }>({
      query: ({ mediaType, tmdbId }) => ({
        url: `titles/${mediaType}/${tmdbId}/lens/spoiled`,
        method: "POST",
      }),
      invalidatesTags: (_r, _e, arg) => [
        { type: "Lens" as const, id: `${arg.mediaType}:${arg.tmdbId}` },
        "Title",
        "Feed",
      ],
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
    // What the DRAFT in the editor costs — a POST because it carries the unsaved rule set, but a
    // query and not a mutation because it changes nothing: it needs the arg-keyed cache, since the
    // editor re-costs on every debounced chip tap and slider drag. `keepUnusedDataFor` matches the
    // server's own 60s window, so a user toggling a chip back and forth pays for it once.
    filterCost: b.query<FilterCostReport, FiltersPayload>({
      query: (filters) => ({ url: "settings/filters/cost", method: "POST", body: filters }),
      keepUnusedDataFor: 60,
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
    /**
     * The track record. Free for every tier — a paid accuracy report reads as hiding the scorecard.
     * Polled by nothing: it changes when a rating lands, and the rating mutation invalidates it.
     */
    getAccuracy: b.query<AccuracyPayload, { window?: string } | void>({
      query: (args) => `accuracy?window=${args && args.window ? args.window : "90d"}`,
      providesTags: ["Accuracy"],
    }),
    getModelMetrics: b.query<ModelMetrics, void>({
      query: () => "metrics/model",
      providesTags: ["Lab"],
    }),
    /**
     * The interleaving instrument. Founder-only server-side; polled slowly because the underlying
     * number moves on the scale of days, and a fast poll would suggest otherwise.
     */
    getInterleave: b.query<InterleaveSummary, void>({
      query: () => "metrics/interleave",
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
    getDislikes: b.query<DislikesPayload, { offset?: number; limit?: number } | void>({
      query: (args) => ({
        url: "taste/dislikes",
        params: { offset: args?.offset ?? 0, limit: args?.limit ?? 20 },
      }),
      providesTags: ["Dislikes"],
    }),
    getRateSuggestions: b.query<RateSuggestion[], void>({
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
  useGetInterleaveQuery,
  useTrainModelMutation,
  useGetFeedQuery,
  useGetContinueWatchingQuery,
  useRebuildFeedMutation,
  useGetFeedGenresQuery,
  useGetModelLearnedQuery,
  useGetAccuracyQuery,
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
  useGetRecentlyWatchedQuery,
  useGetTasteDnaQuery,
  useGetNarrativeQuery,
  useSaveNarrativeMutation,
  useRegenerateNarrativeMutation,
  useGetLensQuery,
  useReportLensSpoilerMutation,
  useSearchTypeaheadQuery,
  useSearchTypeaheadGlobalQuery,
  useGetFiltersQuery,
  useUpdateFiltersMutation,
  useGetPreferenceTagsQuery,
  useAddPreferenceTagMutation,
  useRemovePreferenceTagMutation,
  useGetBillingStatusQuery,
  useCreateCheckoutMutation,
  useGetDislikesQuery,
  useGetRateSuggestionsQuery,
  useSetPinMutation,
  useFilterCostQuery,
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
