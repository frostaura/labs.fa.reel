import { api } from "./base";

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

export const onboardingApi = api.injectEndpoints({
  endpoints: (b) => ({
    getRateSuggestions: b.query<RateSuggestion[], void>({
      query: () => "titles/rate-suggestions",
    }),
  }),
});

export const {
  useGetRateSuggestionsQuery,
} = onboardingApi;
