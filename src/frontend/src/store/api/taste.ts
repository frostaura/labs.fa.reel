import type { BlendPicksPayload, BlendPreview, NarrativePayload } from "../feedTypes";
import { api } from "./base";

/** One pocket of taste — a cluster over the account's loved and rewatched titles (Ml/TasteClusterer). */
export interface TasteCluster {
  ordinal: number;
  /** Share of the account's loved-title weight this pocket holds, 0–1. */
  share: number;
  titleCount: number;
  /** The account's own titles nearest the pocket's centre — never recommendations. */
  exemplars: { name: string; year: number | null; posterPath: string | null }[];
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
  /** Empty below the clusterer's input floor (30 loved titles) and for accounts with no embeddings. */
  clusters: TasteCluster[];
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

export const tasteApi = api.injectEndpoints({
  endpoints: (b) => ({
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
    getDislikes: b.query<DislikesPayload, { offset?: number; limit?: number } | void>({
      query: (args) => ({
        url: "taste/dislikes",
        params: { offset: args?.offset ?? 0, limit: args?.limit ?? 20 },
      }),
      providesTags: ["Dislikes"],
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
  useGetTasteDnaQuery,
  useGetNarrativeQuery,
  useSaveNarrativeMutation,
  useRegenerateNarrativeMutation,
  useGetDislikesQuery,
  useCreateBlendCodeMutation,
  useLazyGetBlendPreviewQuery,
  useGetBlendPicksMutation,
} = tasteApi;
