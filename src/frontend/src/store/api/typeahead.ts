import { api } from "./base";

/** How well a row matches what was typed: the whole name, its start, a word's start, or a look-alike. */
export type MatchTier = "exact" | "prefix" | "word" | "fuzzy";

export interface TypeaheadTitle {
  titleId: string;
  mediaType: "Movie" | "Show";
  tmdbId: number | null;
  name: string;
  year: number | null;
  posterPath: string | null;
  isFullyWatched: boolean;
  /** The account's own rating of the whole movie or show. Absent from servers that predate it. */
  userRating?: number | null;
  /** Watched in part. Absent from servers that predate it. */
  inProgress?: boolean;
  /**
   * The account has watched any of it, rated any of it, saved or dropped it: not a discovery title,
   * so it never carries a "for you" number. Absent from servers that predate it.
   */
  footprint?: boolean;
  /** Always null from the lookup itself: the badge arrives after the rows (`useTypeaheadScoresQuery`). */
  predictedRating: number | null;
  crowdRating?: number | null;
  crowdVotes?: number | null;
  /** Absent from servers that predate the tiered lookup. */
  tier?: MatchTier;
}

export interface TypeaheadPerson {
  personId: string;
  name: string;
  knownFor: string | null;
  profilePath: string | null;
  tier?: MatchTier;
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
  people: TypeaheadPerson[];
  /** Title rows matching at a word start or better — fuzzy look-alikes never count. */
  strongMatches?: number;
  /** Strong matches the account's conditions keep out of the list (FD-5: hidden, and said so). */
  hiddenByConditions?: number;
  /** Present only when the old `&global=1` form was used. */
  global?: GlobalTypeaheadRow[];
}

export interface TypeaheadGlobalPayload {
  global: GlobalTypeaheadRow[];
  hiddenByConditions: number;
}

export interface TypeaheadScoresPayload {
  /** Predicted rating by title id; titles the model cannot score are absent. */
  scores: Record<string, number>;
}

export const typeaheadApi = api.injectEndpoints({
  // Vite HMR re-runs this module; let the re-injection replace the old definitions.
  overrideExisting: Boolean(import.meta.hot),
  endpoints: (b) => ({
    searchTypeahead: b.query<TypeaheadPayload, string>({
      query: (q) => `search/typeahead?q=${encodeURIComponent(q)}`,
      keepUnusedDataFor: 30,
    }),
    // The global leg alone, fired only when the local strong matches are sparse (shouldQueryGlobal).
    searchTypeaheadGlobal: b.query<TypeaheadGlobalPayload, string>({
      query: (q) => `search/typeahead/global?q=${encodeURIComponent(q)}`,
      keepUnusedDataFor: 30,
    }),
    // The personal badge for the rows already on screen, fetched after them.
    typeaheadScores: b.query<TypeaheadScoresPayload, string>({
      query: (ids) => `search/typeahead/scores?ids=${encodeURIComponent(ids)}`,
      keepUnusedDataFor: 60,
    }),
  }),
});

export const {
  useSearchTypeaheadQuery,
  useLazySearchTypeaheadQuery,
  useSearchTypeaheadGlobalQuery,
  useTypeaheadScoresQuery,
} = typeaheadApi;
