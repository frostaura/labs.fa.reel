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
  posterPath: string | null;
  backdropPath: string | null;
  trailerUrl: string | null;
  cast: CastMember[];
  directors: CreditPerson[];
  writers: CreditPerson[];
  prediction: {
    predictedRating: number;
    contributions: { feature: string; value: number }[];
    scoredAt: string;
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

