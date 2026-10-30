import type { TasteDnaPayload } from "../store/api";
import { contrarianTile, genreBarGeometry, meanMarkerPct } from "./tasteDna";

/**
 * The Taste DNA share card's content, computed pure so the export template stays a dumb
 * projection. The old export screenshotted the live dashboard, and its Recharts sections —
 * which measure themselves from the live DOM — rasterised as blank boxes in the offscreen
 * clone; that is the half-blank card this model replaces. Everything here is a number the
 * template renders as plain divs, so nothing in the export can depend on layout it never had.
 */

export interface ShareGenreRow {
  label: string;
  /** Bar length as % of the 0–10 track (shared geometry with the on-page fingerprint). */
  fillPct: number;
  aboveMean: boolean;
  /** Printed affinity, one decimal — same precision the geometry's direction is decided on. */
  affinity: string;
}

export interface ShareCardModel {
  stats: { label: string; value: string }[];
  genres: ShareGenreRow[];
  /** Where the user-mean tick sits on the genre bars' track, as %. */
  meanPct: number;
  meanLabel: string;
  creators: { name: string; affinity: string }[];
  dateLabel: string;
}

export const SHARE_GENRE_COUNT = 6;
export const SHARE_CREATOR_COUNT = 3;

export function shareCardModel(data: TasteDnaPayload, now: Date = new Date()): ShareCardModel {
  // The page tile's value, not a second rounding: raw toFixed printed -0.35 as -0.3 beside the
  // page's -0.4, and -0.04 as "-0.0" (HREV5-DESK-07).
  const contrarian = contrarianTile(data.contrarianScore).value;
  return {
    stats: [
      { label: "hours watched", value: Math.round(data.hoursWatched).toLocaleString("en-US") },
      { label: "titles", value: data.titlesWatched.toLocaleString("en-US") },
      { label: "show completion", value: `${Math.round(data.showCompletionRate * 100)}%` },
      { label: "contrarian score", value: contrarian },
    ],
    genres: data.topGenres.slice(0, SHARE_GENRE_COUNT).map((g) => {
      const bar = genreBarGeometry(g.affinity, data.userMean);
      return {
        label: g.genre.replace("-", " "),
        fillPct: bar.fillPct,
        aboveMean: bar.aboveMean,
        affinity: g.affinity.toFixed(1),
      };
    }),
    meanPct: meanMarkerPct(data.userMean),
    meanLabel: data.userMean.toFixed(1),
    creators: data.creators.slice(0, SHARE_CREATOR_COUNT).map((c) => ({
      name: c.name,
      affinity: c.affinity.toFixed(1),
    })),
    dateLabel: now.toLocaleDateString("en-GB", { day: "numeric", month: "short", year: "numeric" }),
  };
}
