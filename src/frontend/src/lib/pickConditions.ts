import type { FiltersPayload } from "../store/api";

/**
 * The conditions behind tonight's picks — vocabulary, the three-state genre rule, year-range
 * sanitising and the "what is actually on" summary.
 *
 * Kept pure and separate from the editor component for two reasons: it is the half worth unit
 * testing, and the same rules are consumed by two surfaces (the Settings section and the dialog on
 * the home feed). One implementation, two mounts — a second copy would drift within a week.
 */

/**
 * The genre vocabulary. This is the app's existing list, moved here from PreferencesSection rather
 * than re-invented — the slugs are what the backend filters on, so a new list would silently stop
 * matching.
 */
export const GENRES = [
  "action", "adventure", "animation", "anime", "comedy", "crime", "documentary", "drama",
  "family", "fantasy", "history", "holiday", "horror", "music", "musical", "mystery",
  "romance", "science-fiction", "sports", "superhero", "suspense", "thriller", "war", "western",
] as const;

/** Maturity ceilings, "" meaning no ceiling. */
export const MATURITY_LEVELS = ["", "G", "PG", "PG-13", "R"] as const;

export const ALL_LANGUAGES = "*";

/** Common original languages offered in the picker; the catalogue skews heavily English. */
export const LANGUAGES: { code: string; label: string }[] = [
  { code: "en", label: "English" },
  { code: "es", label: "Spanish" },
  { code: "fr", label: "French" },
  { code: "de", label: "German" },
  { code: "it", label: "Italian" },
  { code: "ja", label: "Japanese" },
  { code: "ko", label: "Korean" },
  { code: "zh", label: "Chinese" },
  { code: "hi", label: "Hindi" },
  { code: "pt", label: "Portuguese" },
];

/** Oldest year the range control offers — cinema predates it, the catalogue effectively does not. */
export const YEAR_FLOOR = 1900;

/**
 * Newest year the range offers. Two years ahead of now, because the catalogue carries announced
 * but unreleased titles and a ceiling of "this year" would quietly hide them. Takes `now` so the
 * bound is testable rather than a moving target.
 */
export function yearCeiling(now: Date = new Date()): number {
  return now.getFullYear() + 2;
}

export type GenreState = "neutral" | "include" | "exclude";

/** Human wording for each state — the same words the editor and its legend use. */
export const GENRE_STATE_LABEL: Record<GenreState, string> = {
  neutral: "not considered",
  include: "must have at least one of these",
  exclude: "must have none of these",
};

type GenreLists = Pick<FiltersPayload, "includeGenres" | "excludeGenres">;

export function genreState(filters: GenreLists, genre: string): GenreState {
  // Exclusion wins if a payload somehow carries a genre on both lists: "never show me this" is the
  // safer reading of a contradiction, and it is the one the user notices if it is wrong.
  if (filters.excludeGenres.includes(genre)) return "exclude";
  if (filters.includeGenres.includes(genre)) return "include";
  return "neutral";
}

/**
 * Puts a genre into an explicit state, guaranteeing the invariant the two lists must hold: a genre
 * is on at most ONE of them. "Include action and exclude action" is unsatisfiable and would empty
 * the feed with no visible cause.
 */
export function setGenreState<T extends GenreLists>(filters: T, genre: string, state: GenreState): T {
  const includeGenres = filters.includeGenres.filter((g) => g !== genre);
  const excludeGenres = filters.excludeGenres.filter((g) => g !== genre);
  if (state === "include") includeGenres.push(genre);
  if (state === "exclude") excludeGenres.push(genre);
  return { ...filters, includeGenres, excludeGenres };
}

/** neutral → include → exclude → neutral. Positive intent first: most taps mean "more of this". */
export const NEXT_GENRE_STATE: Record<GenreState, GenreState> = {
  neutral: "include",
  include: "exclude",
  exclude: "neutral",
};

/** One tap on a genre chip. */
export function cycleGenre<T extends GenreLists>(filters: T, genre: string): T {
  return setGenreState(filters, genre, NEXT_GENRE_STATE[genreState(filters, genre)]);
}

export interface YearRange {
  minYear: number | null;
  maxYear: number | null;
}

/**
 * Sanitises a release-year range. Typed input reaches here as anything — an empty box, "19",
 * "20255", or a floor above the ceiling — and an inverted range is the interesting case: sending
 * `min 2010, max 1990` to the server matches nothing at all, so the feed empties with no
 * explanation. The bounds are swapped instead, because someone who typed them the wrong way round
 * plainly meant the span between them.
 */
export function normalizeYearRange(
  range: { minYear?: number | null; maxYear?: number | null },
  bounds: { floor?: number; ceiling?: number } = {},
): YearRange {
  const floor = bounds.floor ?? YEAR_FLOOR;
  const ceiling = bounds.ceiling ?? yearCeiling();

  const clean = (value: number | null | undefined): number | null => {
    if (value == null || !Number.isFinite(value)) return null;
    return Math.min(ceiling, Math.max(floor, Math.round(value)));
  };

  let minYear = clean(range.minYear);
  let maxYear = clean(range.maxYear);
  if (minYear != null && maxYear != null && minYear > maxYear) {
    [minYear, maxYear] = [maxYear, minYear];
  }
  return { minYear, maxYear };
}

/** True when the two bounds, as typed, are the wrong way round — worth telling the user about. */
export function isInvertedYearRange(range: { minYear?: number | null; maxYear?: number | null }): boolean {
  const { minYear, maxYear } = range;
  return minYear != null && maxYear != null && Number.isFinite(minYear) && Number.isFinite(maxYear) && minYear > maxYear;
}

/**
 * A year box's text as a year, or null. Deliberately strict about four digits: parsing every
 * keystroke means "1" and "19" arrive here on the way to "1990", and clamping those into the
 * offered span would rewrite the box under the user's cursor. Anything not yet a year is simply
 * "not set", and {@link normalizeYearRange} does the clamping once, on blur and on save.
 */
export function parseYearInput(text: string): number | null {
  const trimmed = text.trim();
  return /^\d{4}$/.test(trimmed) ? Number(trimmed) : null;
}

export const DEFAULT_FILTERS: FiltersPayload = {
  excludeGenres: [],
  includeGenres: [],
  excludeKeywords: [],
  maturityCeiling: null,
  minPredictedRating: null,
  languages: ["en"],
  minCrowdRating: null,
  minYear: null,
  maxYear: null,
};

/**
 * Fills a payload out to the full shape. The crowd-rating and year fields are newer than some of
 * the deployments that serve this bundle, so they arrive absent rather than null — reading them
 * straight through would put `undefined` into a slider's `value` and flip it to uncontrolled.
 */
export function normalizeFilters(raw: FiltersLike): FiltersPayload {
  const languages = raw?.languages?.length ? raw.languages : DEFAULT_FILTERS.languages;
  const years = normalizeYearRange({ minYear: raw?.minYear, maxYear: raw?.maxYear });
  return {
    excludeGenres: raw?.excludeGenres ?? [],
    includeGenres: raw?.includeGenres ?? [],
    excludeKeywords: raw?.excludeKeywords ?? [],
    maturityCeiling: raw?.maturityCeiling ?? null,
    minPredictedRating: raw?.minPredictedRating ?? null,
    languages,
    minCrowdRating: raw?.minCrowdRating ?? null,
    minYear: years.minYear,
    maxYear: years.maxYear,
  };
}

/**
 * A filters payload as it may actually arrive: complete, partial (a server that predates the
 * crowd-rating and year fields), or absent entirely while the query is in flight.
 */
export type FiltersLike = Partial<FiltersPayload> | null | undefined;

export type ChipTone = "include" | "exclude" | "neutral";

export interface ConditionChip {
  /** Stable React key. */
  key: string;
  /** Self-describing on its own — these chips also render on the feed with no legend beside them. */
  label: string;
  tone: ChipTone;
}

const LANGUAGE_LABEL = new Map(LANGUAGES.map((l) => [l.code, l.label]));

function titleCase(slug: string): string {
  return slug.replace(/-/g, " ");
}

/**
 * Everything currently narrowing the feed, as chips. This is the answer to "why does my feed look
 * like this" — the question a filter set buried two screens away can never answer. An empty array
 * means genuinely nothing is on; the caller says so in words.
 *
 * Deliberately silent about conditions that restrict nothing: "all languages", a rating floor of
 * zero, a year range spanning the whole catalogue. A chip for a non-condition trains the eye to
 * ignore the row.
 */
export function summaryChips(raw: FiltersLike): ConditionChip[] {
  const f = normalizeFilters(raw);
  const chips: ConditionChip[] = [];

  for (const genre of f.includeGenres) {
    chips.push({ key: `inc:${genre}`, label: `has ${titleCase(genre)}`, tone: "include" });
  }
  for (const genre of f.excludeGenres) {
    chips.push({ key: `exc:${genre}`, label: `no ${titleCase(genre)}`, tone: "exclude" });
  }
  if (f.minCrowdRating != null && f.minCrowdRating > 0) {
    chips.push({ key: "crowd", label: `crowd ≥ ${f.minCrowdRating.toFixed(1)}`, tone: "include" });
  }
  if (f.minPredictedRating != null && f.minPredictedRating > 0) {
    chips.push({ key: "predicted", label: `for you ≥ ${f.minPredictedRating.toFixed(1)}`, tone: "include" });
  }
  if (f.minYear != null && f.maxYear != null) {
    chips.push({ key: "years", label: `${f.minYear}–${f.maxYear}`, tone: "include" });
  } else if (f.minYear != null) {
    chips.push({ key: "years", label: `${f.minYear} onwards`, tone: "include" });
  } else if (f.maxYear != null) {
    chips.push({ key: "years", label: `up to ${f.maxYear}`, tone: "include" });
  }
  if (!f.languages.includes(ALL_LANGUAGES)) {
    const named = f.languages.map((code) => LANGUAGE_LABEL.get(code) ?? code.toUpperCase());
    chips.push({
      key: "languages",
      label: named.length === 1 ? `${named[0]} only` : named.join(", "),
      tone: "neutral",
    });
  }
  if (f.maturityCeiling) {
    chips.push({ key: "maturity", label: `up to ${f.maturityCeiling}`, tone: "exclude" });
  }
  for (const keyword of f.excludeKeywords) {
    chips.push({ key: `kw:${keyword}`, label: `no “${keyword}”`, tone: "exclude" });
  }

  return chips;
}
