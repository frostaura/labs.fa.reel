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
}

/**
 * Response for the infinite-scroll lane. `exhausted` is the server's explicit "this lane is
 * genuinely dry" signal (it already tried to deepen from TMDB) — distinct from a caught network
 * error, which the client must NOT treat as exhaustion.
 */
export interface MoreFeedResponse {
  items: FeedCard[];
  exhausted: boolean;
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
