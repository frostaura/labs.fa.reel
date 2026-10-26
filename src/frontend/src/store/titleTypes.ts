import type { WaterfallPayload } from "../lib/predictionWaterfall";

/** A clickable, rateable person chip — cast or crew. */
export interface CreditPerson {
  personId: string;
  name: string;
  profilePath: string | null;
  userRating: number | null;
  character?: string | null;
}

/** Title detail payload (mirrors TitleEndpoints). */
export interface CastMember extends CreditPerson {
  role: string;
  castOrder: number | null;
}

/** Actor page payload (mirrors PersonEndpoints GET). */
export interface PersonPayload {
  id: string;
  name: string;
  department: string | null;
  profilePath: string | null;
  userRating: number | null;
  derivedAffinity: number | null;
  ratedTitleCount: number;
  filmography: {
    titleId: string;
    mediaType: "Movie" | "Show";
    tmdbId: number | null;
    name: string;
    year: number | null;
    posterPath: string | null;
    userRating: number | null;
    predictedRating: number | null;
    /**
     * What this person did on the title — "Director", "Writer", "Actor" — so a filmography says
     * whether a title is theirs or merely features them (F-24). Optional: until the API sends it
     * (change request to WS-5) the page shows no role rather than guessing one from `department`.
     */
    roles?: string[] | null;
    /** The character played, for an acting credit. */
    character?: string | null;
  }[];
}

/** One verified person behind a person-affinity contribution bar (mirrors ContributionDetails). */
export interface ContributionPerson {
  personId: string;
  name: string;
  yourAverage: number;
  titleCount: number;
}

/** The tap-through behind one contribution bar: person features carry people, era features a note. */
export interface ContributionDetail {
  feature: string;
  people?: ContributionPerson[] | null;
  note?: string | null;
}

export interface TitleDetailPayload {
  titleId: string;
  mediaType: "Movie" | "Show";
  tmdbId: number | null;
  name: string;
  year: number | null;
  overview: string | null;
  /**
   * The personalised why-line for this title — the persisted sentence from the account's Active
   * feed snapshot, or the same deterministic templater's rebuild when the title is outside the
   * current build. Null when nothing verifiable can be cited; never the generic boilerplate.
   */
  whyThis: string | null;
  tagline: string | null;
  runtimeMinutes: number | null;
  certification: string | null;
  genres: string[];
  network: string | null;
  status: string | null;
  traktRating: number | null;
  traktVotes: number;
  /** The card-consistent community score (vote-shrunk), the same number the cards' chip shows. */
  crowdRating?: number | null;
  crowdVotes?: number | null;
  posterPath: string | null;
  backdropPath: string | null;
  trailerUrl: string | null;
  cast: CastMember[];
  directors: CreditPerson[];
  writers: CreditPerson[];
  /**
   * A show's creators (TMDB `created_by`) — for TV the people who made it, where `directors` is
   * per-episode and usually empty (F-24). Optional until the catalogue ingests them (change request
   * to WS-3/WS-5); the page lists them whenever they arrive.
   */
  creators?: CreditPerson[] | null;
  prediction: {
    predictedRating: number;
    contributions: { feature: string; value: number }[];
    scoredAt: string;
    /**
     * The prediction as additive steps from a baseline, in rating points (F-25). Optional until the
     * API sends it (change request to WS-5); `lib/predictionWaterfall.ts` draws it only when it adds
     * up to `predictedRating`, and never derives one from the (normalised) contribution bars.
     */
    waterfall?: WaterfallPayload | null;
    /**
     * The account's measured band behind this number (FD-4), so a deep-linked title carries it
     * without a feed or Saved read in the cache. Null means measured-and-absent (no pairs yet);
     * absent from an older server (change request to WS-5), in which case the cache is read.
     */
    expectedAbsoluteError?: number | null;
  } | null;
  contributionDetails: ContributionDetail[];
  userState: {
    isFullyWatched: boolean;
    plays: number;
    userRating: number | null;
    savedForLater: boolean;
    notInterested: boolean;
    dropped: boolean;
  };
}

/** Where-to-watch payload (mirrors ProviderEndpoints). */
export interface ProviderEntry {
  provider: string;
  logoPath: string | null;
  kinds: string[];
  /**
   * What the link does. Today the API sends "direct" for a maintained provider-side SEARCH pattern
   * and "tmdb" for the TMDB watch page; "search" and "deep" (a real link to the title) are the
   * honest names the client already understands. `lib/whereToWatch.ts` never labels a search
   * "direct" whichever of these arrives.
   */
  linkKind: "direct" | "search" | "deep" | "tmdb";
  url: string;
  displayPriority: number;
}

export interface ProvidersPayload {
  region: string;
  attribution: string;
  tmdbWatchPage: string;
  providers: ProviderEntry[];
}

