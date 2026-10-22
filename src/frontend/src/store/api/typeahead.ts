import { api } from "./base";

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

export const typeaheadApi = api.injectEndpoints({
  endpoints: (b) => ({
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
  }),
});

export const {
  useSearchTypeaheadQuery,
  useSearchTypeaheadGlobalQuery,
} = typeaheadApi;
