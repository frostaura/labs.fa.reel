/**
 * View-level browse filters — transient UI state (not persisted ContentFilters). Media type
 * and release-availability buckets applied at read time to already-fetched cards.
 *
 * Note on "available": true streaming-availability is per-title and sparse (see ProviderEndpoints
 * 24h cache), so it can't filter the whole feed honestly. "Available now" here means *released*
 * (out, findable); "Coming soon" means not yet released. The where-to-watch panel on each title
 * shows the real streaming options.
 */
export type MediaFilter = "all" | "movie" | "show";
export type AvailabilityFilter = "all" | "available" | "coming";

export interface BrowseFilter {
  media: MediaFilter;
  availability: AvailabilityFilter;
}

export const DEFAULT_BROWSE_FILTER: BrowseFilter = { media: "all", availability: "all" };

export interface BrowseCard {
  mediaType: "Movie" | "Show";
  isReleased: boolean;
}

/** True when the card passes the active filter. */
export function matchesBrowseFilter(card: BrowseCard, f: BrowseFilter): boolean {
  if (f.media === "movie" && card.mediaType !== "Movie") return false;
  if (f.media === "show" && card.mediaType !== "Show") return false;
  if (f.availability === "available" && !card.isReleased) return false;
  if (f.availability === "coming" && card.isReleased) return false;
  return true;
}

/** Media-only predicate — for surfaces without release data (e.g. continue-watching). */
export function matchesMedia(mediaType: "Movie" | "Show", f: BrowseFilter): boolean {
  return f.media === "all" || (f.media === "movie" ? mediaType === "Movie" : mediaType === "Show");
}

export const isFilterActive = (f: BrowseFilter): boolean =>
  f.media !== "all" || f.availability !== "all";

/**
 * Where a browse filter is remembered. Home and search are deliberately SEPARATE keys: "I am
 * browsing movies tonight" and "narrow this one search to TV" are different intents, and leaking
 * one into the other would silently re-scope a search the user never touched.
 */
export type BrowseScope = "home" | "search";

const STORAGE_PREFIX = "reel.browseFilter.";

const MEDIA_VALUES: MediaFilter[] = ["all", "movie", "show"];
const AVAILABILITY_VALUES: AvailabilityFilter[] = ["all", "available", "coming"];

/**
 * Reads a remembered browse filter, or the default.
 *
 * Every stored value is re-validated rather than trusted: this is localStorage, so the content
 * survives deploys and can be hand-edited, and a stale enum member from an older build would
 * otherwise filter the feed against a value no predicate recognises — a silently empty screen with
 * no way to tell why. Anything unrecognised degrades to the default. Never throws: Safari private
 * mode makes localStorage access itself throw, and a filter preference is not worth a blank page.
 */
export function readBrowseFilter(scope: BrowseScope): BrowseFilter {
  try {
    const raw = localStorage.getItem(STORAGE_PREFIX + scope);
    if (!raw) return DEFAULT_BROWSE_FILTER;
    const parsed: unknown = JSON.parse(raw);
    if (typeof parsed !== "object" || parsed === null) return DEFAULT_BROWSE_FILTER;
    const { media, availability } = parsed as Partial<BrowseFilter>;
    return {
      media: MEDIA_VALUES.includes(media as MediaFilter) ? (media as MediaFilter) : "all",
      availability: AVAILABILITY_VALUES.includes(availability as AvailabilityFilter)
        ? (availability as AvailabilityFilter)
        : "all",
    };
  } catch {
    return DEFAULT_BROWSE_FILTER;
  }
}

/** Remembers a browse filter. A storage failure is swallowed — losing the preference beats crashing. */
export function writeBrowseFilter(scope: BrowseScope, filter: BrowseFilter): void {
  try {
    localStorage.setItem(STORAGE_PREFIX + scope, JSON.stringify(filter));
  } catch {
    /* private mode / quota — the filter still applies for this session */
  }
}
