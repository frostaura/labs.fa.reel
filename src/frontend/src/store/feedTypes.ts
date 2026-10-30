/** Shared feed payload types (mirror FeedEndpoints DTOs). */
export interface FeedCard {
  titleId: string;
  mediaType: "Movie" | "Show";
  tmdbId: number | null;
  name: string;
  year: number | null;
  runtimeMinutes: number | null;
  posterPath: string | null;
  backdropPath: string | null;
  genres: string[];
  predictedRating: number;
  /** The community score (0–10), for comparison against the personal prediction. null = unrated. */
  crowdRating: number | null;
  /** How many people that score is drawn from — 9.1 from 12 votes is not 9.1 from 40,000. */
  crowdVotes: number | null;
  whyThis: string;
  isReleased: boolean;
  providers?: { name: string; logoPath: string | null }[];
  /**
   * Hero cards only: "confident" when the pick cleared the hero's confidence bar on its own merits,
   * "reaching" when the never-blank backfill put it there because nothing did. Absent on every other
   * surface, and absent on hero cards paged in after the snapshot — those are selected against the
   * account's own floor, not the hero bar, so they have no bar to be measured against.
   */
  confidence?: "confident" | "reaching";
  /** The bar `confidence` was measured against (7.5, or the account's higher "for you" minimum). */
  floorUsed?: number;
  /**
   * "Through your lens" copy, where any has been written for this account and title. Every field
   * is optional and every surface must render correctly without them: the daily budget, the
   * provider and the output guards can each withhold copy, and a card that only looks right when
   * the LLM answered is a card that looks broken on a bad day.
   */
  hook?: string | null;
  /** The first clause of the lens — the card's two-line why when it is present. */
  lensClause?: string | null;
  /** The lens's closed-vocabulary mood; the strongest signal the tonight-mood chips sort on. */
  mood?: string | null;
  /** The extracted scalars the chips fall back to. Absent on an unenriched title. */
  texture?: { darkness: number; pacing: number; complexity: number; humor: number } | null;
  /**
   * Anchored rows only (because-you-loved on Home, the title page's "More like this"): what this
   * card verifiably shares with the anchor, strongest first, at most three — the evidence the
   * server's anchor gate admitted it on. Null or absent on every other row.
   */
  evidence?: SharedEvidence[] | null;
}

/** One checkable thing a card shares with its row's anchor (WS-5 `SharedEvidence`). */
export interface SharedEvidence {
  /** "person", "keyword" or "theme"; an unknown kind is shown by its label alone. */
  kind: string;
  /** A person's name, a keyword or a theme. */
  label: string;
  /** For a person: "director", "writer" or "cast". */
  role?: string | null;
}

/** The lens as the API returns it, with the honesty about where it came from. */
export interface LensPayload {
  /** "fresh" (written against your current taste), "stale" (older, still shown), or "none". */
  source: "fresh" | "stale" | "none";
  lens: string | null;
  hook: string | null;
  caution: string | null;
  anchors: { title: string; relation: string }[];
  mood: string | null;
  whenToWatch: string | null;
  generatedAt: string | null;
  /**
   * True when a lens pass that writes this title is queued (WS-1 task 7: the GET only reads, and
   * queues the pass when the note is not current). Optional: an older server never sends it.
   */
  pending?: boolean;
}

/** The account's taste narrative — what Reel believes about you, and your correction to it. */
export interface NarrativePayload {
  /** Already the effective text: your edit if you made one, otherwise the generated version. */
  text: string | null;
  isEdited: boolean;
  version: number | null;
  ratingsCount: number;
  generatedAt: string | null;
  /**
   * True when this is owed a rewrite: your taste has moved since it was written, or it was written by
   * an earlier version of Reel's prompt. Your own edit is never owed a rewrite for the prompt — the
   * words are yours, not the prompt's. It is still shown either way.
   */
  isStale: boolean;
  /**
   * Why it is stale (HREV6-DESK-01): `taste-moved` wins when both apply. Optional and nullable: an older
   * server never sends it, and its `isStale` only ever meant the taste had moved.
   */
  staleReason?: "taste-moved" | "outdated-prompt" | null;
  regenerating: boolean;
  writerAvailable: boolean;
}

export interface FeedRow {
  kind: string;
  anchorTitleId: string | null;
  anchorName: string | null;
  title: string | null;
  items: FeedCard[];
}

export interface FeedPayload {
  generatedAt: string | null;
  hero: FeedCard[];
  rows: FeedRow[];
  lockedRowCount: number;
  /**
   * The calibrator's band behind every "for you" number (WS-5; the same number Saved shows), 1dp.
   * Null until the account's closed loop has pairs; absent from an older server.
   */
  expectedAbsoluteError?: number | null;
}

/** A genre rail the home page renders beneath the personalised rows; filled via /feed/more. */
export interface GenreRail {
  slug: string;
  label: string;
}

/** Request body for the infinite-scroll lane (POST /feed/more). */
export interface MoreFeedRequest {
  row: string;
  anchorTitleId?: string | null;
  genre?: string | null;
  excludeTitleIds: string[];
  limit?: number;
  /** Active browse-filter medium ("movie" | "show"); omit for "all". Makes paging media-aware
   * server-side so a Movies lane fills with movies (deepening from TMDB) instead of dead-ending. */
  mediaType?: string;
  /**
   * Titles already on the page in OTHER rails or the hero — excluded from this slice but not
   * counted as this row's depth (WS-5's `/feed/more` contract). Omitted where there is no page.
   */
  pageExcludeTitleIds?: string[];
  /** One opaque nonce (≤ 64 chars) per page view, so concurrent rails cannot serve the same title. */
  pageToken?: string;
}

/**
 * Response for the infinite-scroll lane. `exhausted` is the server's explicit "this lane is
 * genuinely dry" signal (it already tried to deepen from TMDB) — distinct from a caught network
 * error, which the client must NOT treat as exhaustion.
 */
/**
 * What a short rail's supply looks like (FeedEndpoints' `RailSupply`, WS-5 task 9). Sent only when
 * the page came up short of the asked limit, and today only for genre rails; null elsewhere.
 */
export interface RailSupply {
  /** Titles a standing condition holds out of this rail — the N in "your conditions hide N". */
  hiddenByConditions: number;
  /** Titles no condition touches that the account has already seen. */
  alreadySeen: number;
  /** Unseen titles that pass and clear the floor (some may be on screen elsewhere). */
  clearingFloor: number;
}

/**
 * What a short anchored rail's window lost (FeedEndpoints' `AnchoredSupply`, HREV2-RET-05): sent for
 * "more like this" and "because you loved" pages that came up short, null elsewhere. Neither count is
 * the account's conditions, which the window had already applied.
 */
export interface AnchoredSupply {
  /** Unseen titles above the for-you bar that the anchored gate refused: too little shared with the anchor. */
  gateHeldBack: number;
  /** Titles in the anchor's window under the account's for-you bar. */
  belowBar: number;
}

export interface MoreFeedResponse {
  items: FeedCard[];
  exhausted: boolean;
  /** Present when the page came up short and the server counted why (see {@link RailSupply}). */
  supply?: RailSupply | null;
  /** Present when an anchored page came up short (see {@link AnchoredSupply}). */
  anchoredSupply?: AnchoredSupply | null;
}

export interface ContinueEntry {
  titleId: string;
  mediaType: "Movie" | "Show";
  tmdbId: number | null;
  name: string;
  posterPath: string | null;
  backdropPath: string | null;
  nextEpisodeSeason: number | null;
  nextEpisodeNumber: number | null;
  watchedEpisodes: number;
  totalAired: number;
  completionPct: number;
}

/** The current ISO week's digest — feed-shaped cards; empty items until the Friday build runs. */
export interface DigestPayload {
  weekStart: string | null;
  generatedAt: string | null;
  items: FeedCard[];
  /** Why the build relaxed the "high for you" bar to fill the rail; null when it met the bar (IR1S-03). */
  degradedReason: string | null;
}

/** A Blend joint pick: the shared card fields plus both personal predictions (1dp). */
export interface BlendCard {
  titleId: string;
  mediaType: "Movie" | "Show";
  tmdbId: number | null;
  name: string;
  year: number | null;
  runtimeMinutes: number | null;
  posterPath: string | null;
  backdropPath: string | null;
  genres: string[];
  crowdRating: number | null;
  crowdVotes: number | null;
  isReleased: boolean;
  providers?: { name: string; logoPath: string | null }[];
  forYou: number;
  forThem: number;
  /** min(forYou, forThem) — the ordering key. */
  joint: number;
}

export interface BlendPreview {
  ownerName: string;
  eligibleBothCount: number;
}

export interface BlendPicksPayload {
  ownerName: string;
  items: BlendCard[];
}

/**
 * One card on Home's "Trakt recommends" rail (`GET /api/feed/trakt-recommendations`). The list is
 * Trakt's, so `predictedRating` is honestly nullable — Reel's "for you" number where its model has
 * one, nothing where it does not — and `whyThis` is empty: Reel did not choose these titles.
 * `traktRank` is the card's position in Trakt's list for its media type.
 */
export interface TraktRecommendationCard {
  titleId: string;
  mediaType: "Movie" | "Show";
  tmdbId: number | null;
  name: string;
  year: number | null;
  runtimeMinutes: number | null;
  posterPath: string | null;
  backdropPath: string | null;
  genres: string[];
  predictedRating: number | null;
  crowdRating: number | null;
  crowdVotes: number | null;
  whyThis: string;
  isReleased: boolean;
  providers: { name: string; logoPath: string | null }[];
  traktRank: number;
}

/** What the rail can honestly claim: Trakt's list now, its last list, or why there is none. */
export type TraktRecommendationsState = "Ok" | "Stale" | "NotLinked" | "RelinkRequired" | "Unavailable";

/** The rail's payload: the state, what Trakt sent, and what Reel took out and why. */
export interface TraktRecommendations {
  state: TraktRecommendationsState;
  fetchedAt: string | null;
  returnedByTrakt: number;
  unresolved: number;
  hiddenAsSeen: number;
  hiddenAsSetAside: number;
  hiddenByConditions: number;
  heldBelowBar: number;
  bar: number | null;
  unscoredRemaining: number;
  items: TraktRecommendationCard[];
}
