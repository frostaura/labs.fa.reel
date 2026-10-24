import type {
  ContinueEntry,
  DigestPayload,
  FeedCard,
  FeedPayload,
  GenreRail,
  MoreFeedRequest,
  MoreFeedResponse,
} from "../feedTypes";
import { api } from "./base";

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

/**
 * The home band's payload. The server answers 204 whenever there is nothing honest to say — no
 * active model, or a pool nothing has judged and nothing is judging — which arrives here as null.
 */
export interface CrossReferenceStatus {
  running: boolean;
  judged: number;
  total: number;
  kept: number;
  ruledOut: number;
  unsure: number;
  noSignal: number;
  gating: string;
}

export const feedApi = api.injectEndpoints({
  // Vite HMR re-runs this module; let the re-injection replace the old definitions.
  overrideExisting: Boolean(import.meta.hot),
  endpoints: (b) => ({
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
    /**
     * The cross-reference band's numbers. Its own endpoint for the same reason `feed/learned` is:
     * the feed payload is refetched constantly and this moves on the cadence of a background pass.
     * A 204 — nothing judged and nothing judging — arrives as null and the band renders nothing.
     */
    getCrossReference: b.query<CrossReferenceStatus | null, void>({
      query: () => ({
        url: "feed/cross-reference",
        responseHandler: async (response) => (response.status === 204 ? null : await response.json()),
      }),
      providesTags: ["CrossReference"],
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
    // Weekly digest — read-only snapshot of this week's "new & high for you" picks.
    getDigest: b.query<DigestPayload, void>({
      query: () => "digest/current",
    }),
  }),
});

export const {
  useGetFeedQuery,
  useGetContinueWatchingQuery,
  useRebuildFeedMutation,
  useGetFeedGenresQuery,
  useGetModelLearnedQuery,
  useGetCrossReferenceQuery,
  useLoadMoreFeedMutation,
  useGetDigestQuery,
} = feedApi;
