import type { Page } from "@playwright/test";
import type { ConversationPayload } from "../src/lib/askReel/transcript";
import type { AskCard } from "../src/lib/askReelStream";
import type { RateSuggestion } from "../src/store/api/onboarding";
import type { TasteDnaPayload, DislikesPayload } from "../src/store/api/taste";
import type {
  AccuracyPayload,
  CrossReferencePayload,
  InterleaveSummary,
  ModelMetrics,
  ModelRun,
} from "../src/store/api/metrics";
import type { FeedCard, FeedPayload, LensPayload, NarrativePayload } from "../src/store/feedTypes";
import type { SavedQueueItem, SavedQueueResponse } from "../src/store/savedTypes";
import type { PersonPayload, ProvidersPayload, TitleDetailPayload } from "../src/store/titleTypes";
import { mockHomeChrome } from "./home-chrome";
import { mockJson } from "./helpers";

/**
 * POPULATED fixtures for every route, typed against the store's own payload types so a shape the
 * API no longer sends fails `tsc`, not a run.
 *
 * Written for the accessibility gate (PH-03): an axe pass over an empty or error state proves
 * nothing about the page a person actually reads. Each fixture fills every list the page renders —
 * pockets, charts, creators, lab runs and ledger, interleave standings, cast with photos, rate
 * suggestions, a person's credits — and the shapes are the founder's real ones in miniature.
 * Shared with the phone layout specs, so a fixture that makes a page tall enough to scroll is
 * written once.
 */

/** 1×1 PNG: every image.tmdb.org request is answered with it, so photos render with no network. */
const PIXEL = Buffer.from(
  "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==",
  "base64"
);

export async function mockTmdbImages(page: Page): Promise<void> {
  await page.route("https://image.tmdb.org/**", (route) =>
    route.fulfill({ status: 200, contentType: "image/png", body: PIXEL })
  );
}

const id = (prefix: string, n: number) => `00000000-0000-0000-0000-${prefix}${String(n).padStart(12 - prefix.length, "0")}`;

// ── Home ─────────────────────────────────────────────────────────────────────────────────────────

export const feedCard = (n: number, over: Partial<FeedCard> = {}): FeedCard => ({
  titleId: id("1", n),
  mediaType: "Movie",
  tmdbId: 9100 + n,
  name: `Pick ${n}`,
  year: 2018,
  runtimeMinutes: 115,
  posterPath: `/poster${n}.jpg`,
  backdropPath: `/backdrop${n}.jpg`,
  genres: ["thriller", "drama"],
  predictedRating: 7.2,
  crowdRating: 7.1,
  crowdVotes: 120000,
  whyThis: "Close to the thrillers you rate highest.",
  hook: "A heist goes wrong in a small harbour town.",
  lensClause: null,
  isReleased: true,
  confidence: "confident",
  floorUsed: 6,
  ...over,
});

export const FEED: FeedPayload = {
  generatedAt: "2026-10-26T06:00:00Z",
  hero: [1, 2, 3, 4, 5].map((n) => feedCard(n)),
  rows: [
    {
      kind: "because-you-loved",
      anchorTitleId: id("a", 1),
      anchorName: "Heat",
      title: null,
      items: [6, 7, 8, 9, 10, 11].map((n) =>
        feedCard(n, {
          confidence: undefined,
          floorUsed: undefined,
          evidence: [
            { kind: "person", label: "Michael Mann", role: "director" },
            { kind: "theme", label: "heist" },
          ],
        })
      ),
    },
  ],
  lockedRowCount: 0,
  expectedAbsoluteError: 1.9,
};

export async function mockHome(page: Page): Promise<void> {
  await mockHomeChrome(page);
  await mockJson(page, "**/api/feed", FEED);
}

// ── Taste DNA ────────────────────────────────────────────────────────────────────────────────────

export const TASTE: TasteDnaPayload = {
  userMean: 6.8,
  contrarianScore: -0.2,
  ratingsCount: 2044,
  hoursWatched: 4210,
  titlesWatched: 3353,
  showCompletionRate: 0.62,
  topGenres: [
    { genre: "drama", affinity: 7.2, count: 980 },
    { genre: "thriller", affinity: 7.0, count: 512 },
    { genre: "crime", affinity: 7.1, count: 403 },
    { genre: "science-fiction", affinity: 6.9, count: 260 },
    { genre: "comedy", affinity: 6.5, count: 455 },
    { genre: "action", affinity: 6.6, count: 391 },
  ],
  eras: [
    { decade: 1980, affinity: 7.1, count: 140 },
    { decade: 1990, affinity: 7.0, count: 210 },
    { decade: 2000, affinity: 6.9, count: 520 },
    { decade: 2010, affinity: 6.7, count: 801 },
    { decade: 2020, affinity: 6.5, count: 373 },
  ],
  creators: [
    { name: "Michael Mann", department: "Directing", profilePath: "/mann.jpg", affinity: 8.1, count: 7 },
    { name: "Denis Villeneuve", department: "Directing", profilePath: "/villeneuve.jpg", affinity: 7.9, count: 9 },
    { name: "Aaron Sorkin", department: "Writing", profilePath: null, affinity: 7.6, count: 6 },
  ],
  topActors: [
    { personId: id("p", 1), name: "Al Pacino", profilePath: "/pacino.jpg", rating: 8 },
    { personId: id("p", 2), name: "Frances McDormand", profilePath: "/mcdormand.jpg", rating: 9 },
  ],
  histogram: [1, 2, 3, 4, 5, 6, 7, 8, 9, 10].map((rating) => ({ rating, count: [4, 9, 20, 61, 140, 310, 590, 520, 290, 100][rating - 1] })),
  drift: [
    { year: 2022, shares: { drama: 0.4, thriller: 0.3, comedy: 0.3 } },
    { year: 2023, shares: { drama: 0.45, thriller: 0.3, comedy: 0.25 } },
    { year: 2024, shares: { drama: 0.5, thriller: 0.3, comedy: 0.2 } },
  ],
  clusters: [1, 2, 3].map((ordinal) => ({
    ordinal,
    share: [0.42, 0.33, 0.25][ordinal - 1],
    titleCount: [61, 48, 36][ordinal - 1],
    exemplars: [1, 2, 3].map((n) => ({ name: `Pocket ${ordinal} film ${n}`, year: 1995 + n, posterPath: `/pocket${ordinal}${n}.jpg` })),
  })),
};

export const DISLIKES: DislikesPayload = {
  total: 3,
  offset: 0,
  limit: 20,
  hasMore: false,
  items: [
    { titleId: id("d", 1), mediaType: "Movie", tmdbId: 7001, name: "Loud Sequel", year: 2023, posterPath: "/d1.jpg", kind: "NotInterested", reason: "Tone", createdAt: "2026-10-20T10:00:00Z" },
    { titleId: id("d", 2), mediaType: "Show", tmdbId: 7002, name: "Long Procedural", year: 2019, posterPath: null, kind: "Dropped", reason: null, createdAt: "2026-10-10T10:00:00Z" },
    { titleId: id("d", 3), mediaType: "Movie", tmdbId: 7003, name: "Slasher Nine", year: 2021, posterPath: "/d3.jpg", kind: "NotInterested", reason: "Genre", createdAt: "2026-09-30T10:00:00Z" },
  ],
  profile: { windowDays: 180, activeCount: 3, genres: [{ genre: "horror", weight: 0.6 }], tones: [{ tone: "loud", weight: 0.4 }] },
};

export const NARRATIVE: NarrativePayload = {
  text: "You reward patience: slow-burn crime and character drama, with a soft spot for 80s and 90s craft.",
  isEdited: false,
  version: 3,
  ratingsCount: 2044,
  generatedAt: "2026-10-20T06:00:00Z",
  isStale: false,
  regenerating: false,
  writerAvailable: true,
};

export async function mockDna(page: Page): Promise<void> {
  await mockJson(page, "**/api/taste", TASTE);
  await mockJson(page, "**/api/taste/dislikes", DISLIKES);
  await mockJson(page, "**/api/taste/narrative", NARRATIVE);
}

// ── Lab ──────────────────────────────────────────────────────────────────────────────────────────

const run = (iteration: number): ModelRun => ({
  id: id("r", iteration),
  iteration,
  configHash: `cfg${iteration}`,
  splitAt: "2026-09-01T00:00:00Z",
  trainRowCount: 1600,
  holdoutRowCount: 400,
  positiveThreshold: 8,
  status: "Succeeded",
  startedAt: `2026-10-${String(10 + iteration).padStart(2, "0")}T06:00:00Z`,
  eval: {
    modelPrecisionAt10: 0.3,
    baselinePrecisionAt10: 0.4,
    relativeImprovement: -0.25,
    rmse: 1.6,
    mae: 1.2,
    spearmanRho: 0.21,
    spearmanSe: 0.04,
    ndcgAt10Model: 0.21,
    ndcgAt10Baseline: 0.34,
    ndcgLiftCiLow: -0.31,
    ndcgLiftCiHigh: 0.04,
    calibrationBias: 0.37,
    ece: 0.08,
    eligibleHoldoutCount: 380,
    unfilteredHoldoutCount: 400,
    holdoutPositiveCount: 90,
    lowSample: false,
    passedGate: false,
    passedLegacyGate: false,
    origins: [0.6, 0.65, 0.7, 0.75, 0.8].map((fraction) => ({
      fraction,
      splitAt: "2026-08-01T00:00:00Z",
      trainRows: Math.round(2000 * fraction),
      unfilteredHoldout: 200,
      eligibleHoldout: 180,
      threshold: 8,
      lowSample: false,
      ndcgModel: 0.22,
      ndcgBaseline: 0.33,
      ndcgRelativeLift: -0.33,
      precisionModel: 0.3,
      precisionBaseline: 0.4,
    })),
    detail: {
      featureImportance: { genre_affinity: 0.31, crowd_rating: 0.24, director_affinity: 0.18, era: 0.09 },
      caveats: ["Holdout is the most recent 20% of ratings."],
    },
  },
});

export const MODEL_METRICS: ModelMetrics = {
  gate: {
    threshold: 0.2, passed: false, latestLift: -0.38, ndcgModel: 0.21, ndcgBaseline: 0.34,
    ndcgLiftCiLow: -0.31, ndcgLiftCiHigh: 0.04, ciExcludesZero: false, legacyThreshold: 0.2,
    legacyPassed: false, iterationsUsed: 3, killCriterionAt: 3, criterionExceeded: false,
  },
  efficiency: {
    computedAt: "2026-10-26T06:00:00Z",
    conversions: {
      count: 12,
      ratedCount: 9,
      scoredCount: 9,
      accuracy: { count: 9, meanPredicted: 7.1, meanActual: 6.4, meanAbsoluteError: 1.3, bias: 0.7, withinOnePointRate: 0.44, pearsonCorrelation: 0.31 },
      ratingDistribution: [{ rating: 6, count: 3 }, { rating: 7, count: 4 }, { rating: 8, count: 2 }],
      worstMisses: [{ titleId: id("m", 1), name: "Overhyped Epic", year: 2024, predicted: 8.4, actual: 4, gap: 4.4 }],
    },
    predictedVsActual: {
      pairs: 116,
      accuracy: { count: 116, meanPredicted: 6.81, meanActual: 5.78, meanAbsoluteError: 1.94, bias: 0.37, withinOnePointRate: 0.26, pearsonCorrelation: 0.12 },
      worstMisses: [{ titleId: id("m", 2), name: "Cult Comedy", year: 1999, predicted: 5.1, actual: 9, gap: -3.9 }],
    },
    counterfactual: [{ window: "90d", days: 90, viaReelCount: 9, viaReelMeanRating: 6.4, otherCount: 40, otherMeanRating: 6.1, delta: 0.3 }],
  },
  activeArtifact: { version: 3, algo: "lightgbm-lambdarank", trainedAt: "2026-10-13T06:00:00Z" },
  runs: [3, 2, 1].map(run),
};

export const INTERLEAVE: InterleaveSummary = {
  computedAt: "2026-10-26T06:00:00Z",
  enabled: true,
  policyA: "CalibratedModel",
  policyB: "PopularityBaseline",
  tau: 0.15,
  alpha: 0.05,
  slates: 148,
  decidedSlates: 41,
  ties: 107,
  winsA: 24,
  winsB: 17,
  alwaysValidP: 0.412,
  significant: false,
  preferenceRateA: 0.5854,
  ciLow: 0.3902,
  ciHigh: 0.7805,
  signTestP: 0.3497,
  firstSlateAt: "2026-07-14T18:00:00Z",
  lastSlateAt: "2026-10-26T05:00:00Z",
  decidedSlatesPerDay: 0.71,
  slatesToDecisionAt65: 103,
  estimatedDaysToDecision: 87.3,
  standings: [
    { policyId: "CalibratedModel", description: "Calibrated prediction × freshness × preference boost.", team: "A", slates: 148, cards: 1032, credits: 61, slateWins: 24 },
    { policyId: "PopularityBaseline", description: "Crowd popularity within the eligible pool.", team: "B", slates: 148, cards: 1027, credits: 49, slateWins: 17 },
  ],
};

export const CROSS_REFERENCE: CrossReferencePayload = {
  window: "90d",
  population: { name: "Titles served under a cross-reference standing that you have since rated", selectionNote: "Only titles you chose to rate." },
  crossReference: {
    minimumOutcomes: 20,
    online: [
      { verdict: "keep", n: 31, meanLaterRating: 7.1, lovedRate: 0.35, dislikedRate: 0.1, answerable: true },
      { verdict: "rule-out", n: 6, meanLaterRating: 5.2, lovedRate: 0.0, dislikedRate: 0.5, answerable: false },
    ],
    backtests: [
      {
        promptVersion: 4, evaluatedAt: "2026-10-20T06:00:00Z", judged: 400, auc: 0.68, meanLaterRatingKeep: 7.0,
        meanLaterRatingOut: 5.4, keepLovedRate: 0.33, keepDislikedRate: 0.11, falseRuleOutRate: 0.04,
        falseRuleOutLow: 0.02, falseRuleOutHigh: 0.07, trueRuleOutRate: 0.41, unsureRate: 0.2,
        gating: "demote", caveat: "Pooled across accounts.", current: true,
      },
    ],
  },
};

export async function mockLab(page: Page): Promise<void> {
  await mockJson(page, "**/api/metrics/model", MODEL_METRICS);
  await mockJson(page, "**/api/metrics/interleave", INTERLEAVE);
  await mockJson(page, "**/api/metrics/cross-reference", CROSS_REFERENCE);
}

// ── Track record ─────────────────────────────────────────────────────────────────────────────────

export const ACCURACY: AccuracyPayload = {
  window: "90d",
  from: "2026-07-28T00:00:00Z",
  to: "2026-10-26T00:00:00Z",
  source: "exposures",
  headline: { withinOnePoint: 0.26, n: 116, meanAbsoluteError: 1.94, bias: 0.37, correlation: 0.12, meanPredicted: 6.81, meanActual: 5.78 },
  calibration: [
    { bucket: 5, low: 5, high: 6, n: 14, meanPredicted: 5.6, meanActual: 5.4 },
    { bucket: 6, low: 6, high: 7, n: 61, meanPredicted: 6.7, meanActual: 5.9 },
    { bucket: 7, low: 7, high: 8, n: 31, meanPredicted: 7.4, meanActual: 5.2 },
    { bucket: 8, low: 8, high: 9, n: 10, meanPredicted: 8.3, meanActual: 7.0 },
  ],
  moments: [
    { titleId: id("o", 1), name: "Right Call", year: 2021, posterPath: "/o1.jpg", mediaType: "Movie", tmdbId: 8001, predicted: 8.2, actual: 8, surface: "home-hero", shownAt: "2026-09-01T20:00:00Z", ratedAt: "2026-09-04T22:00:00Z", daysToOutcome: 3 },
    { titleId: id("o", 2), name: "Wrong Call", year: 2020, posterPath: null, mediaType: "Show", tmdbId: 8002, predicted: 7.9, actual: 4, surface: null, shownAt: null, ratedAt: "2026-09-10T22:00:00Z", daysToOutcome: null },
  ],
  conversion: { shown: 40, saved: 6, watched: 3, rated: 3, shownToSaved: 0.15, savedToWatched: 0.5, shownToRated: 0.075, medianDaysToWatch: 4 },
  counterfactual: [{ window: "90d", days: 90, viaReelCount: 9, viaReelMeanRating: 6.4, otherCount: 40, otherMeanRating: 6.1, delta: 0.3 }],
  learnedThisWeek: { trainedAt: "2026-10-20T06:00:00Z", newRatings: 14, calibrationBias: 0.3, calibrationMae: 1.4, spearman: 0.22, picksChanged: 5 },
  windows: [
    { key: "30d", pairs: 40, sameAs: null },
    { key: "90d", pairs: 116, sameAs: null },
  ],
};

export async function mockTrackRecord(page: Page): Promise<void> {
  // The account's cross-reference standing; 204 is the server's "no pass has run" answer.
  await page.route(
    (url) => url.pathname === "/api/feed/cross-reference",
    (route) => route.fulfill({ status: 204, body: "" })
  );
  await mockJson(page, "**/api/accuracy", ACCURACY);
  await mockJson(page, "**/api/metrics/cross-reference", CROSS_REFERENCE);
}

// ── Title and person ─────────────────────────────────────────────────────────────────────────────

export const PERSON_ID = id("aa", 1);

export const TITLE: TitleDetailPayload = {
  titleId: id("t", 1),
  mediaType: "Movie",
  tmdbId: 1001,
  name: "Quiet Harbor",
  year: 2024,
  overview: "A harbormaster keeps one last secret while the town's fishing fleet fails around her.",
  whyThis: "Shares the slow-burn dread you love.",
  tagline: "Some tides never turn.",
  runtimeMinutes: 112,
  certification: "PG-13",
  genres: ["drama", "thriller"],
  network: null,
  status: "Released",
  traktRating: 7.8,
  traktVotes: 12000,
  crowdRating: 7.6,
  crowdVotes: 12000,
  posterPath: "/harbor.jpg",
  backdropPath: "/harbor-bd.jpg",
  trailerUrl: null,
  cast: [1, 2, 3, 4, 5, 6].map((n) => ({
    personId: n === 1 ? PERSON_ID : id("c", n),
    name: `Cast Member ${n}`,
    profilePath: n === 6 ? null : `/cast${n}.jpg`,
    userRating: n === 2 ? 8 : null,
    character: `Role ${n}`,
    role: "Actor",
    castOrder: n,
  })),
  directors: [{ personId: id("dd", 1), name: "Ana Director", profilePath: "/dir.jpg", userRating: null }],
  writers: [{ personId: id("ww", 1), name: "Wen Writer", profilePath: null, userRating: null }],
  prediction: {
    predictedRating: 7.4,
    contributions: [
      { feature: "genre_affinity", value: 0.4 },
      { feature: "director_affinity", value: 0.25 },
      { feature: "crowd_rating", value: -0.1 },
    ],
    scoredAt: "2026-10-26T06:00:00Z",
    expectedAbsoluteError: 1.9,
  },
  contributionDetails: [
    { feature: "director_affinity", people: [{ personId: id("dd", 1), name: "Ana Director", yourAverage: 8.1, titleCount: 3 }] },
  ],
  userState: { isFullyWatched: false, plays: 0, userRating: null, savedForLater: false, notInterested: false, dropped: false },
};

export const PROVIDERS: ProvidersPayload = {
  region: "US",
  attribution: "JustWatch",
  tmdbWatchPage: "https://www.themoviedb.org/movie/1001/watch",
  providers: [
    { provider: "Netflix", logoPath: "/netflix.jpg", kinds: ["flatrate"], linkKind: "search", url: "https://www.netflix.com/search?q=Quiet%20Harbor", displayPriority: 1 },
    { provider: "Apple TV", logoPath: "/apple.jpg", kinds: ["rent", "buy"], linkKind: "tmdb", url: "https://www.themoviedb.org/movie/1001/watch", displayPriority: 2 },
  ],
};

export const LENS: LensPayload = {
  source: "fresh",
  lens: "A patient coastal noir: the dread is in what the town will not say.",
  hook: "A harbormaster keeps one last secret.",
  caution: "Slow first act.",
  anchors: [{ title: "Mare of Easttown", relation: "same small-town grief" }],
  mood: "brooding",
  whenToWatch: "A quiet weeknight.",
  generatedAt: "2026-10-25T06:00:00Z",
};

/**
 * Where-to-watch and the lens for ANY title: Saved's tonight card, Ask Reel's answer cards and the
 * title page all read them for whichever title they show. Registered before a page's own mocks.
 */
export async function mockTitleSideReads(page: Page): Promise<void> {
  await page.route(/\/api\/titles\/(movie|show)\/\d+\/providers$/, (route) =>
    route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify(PROVIDERS) })
  );
  await page.route(/\/api\/titles\/(movie|show)\/\d+\/lens$/, (route) =>
    route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify(LENS) })
  );
}

export async function mockTitle(page: Page, title: TitleDetailPayload = TITLE): Promise<void> {
  const base = `**/api/titles/${title.mediaType.toLowerCase()}/${title.tmdbId}`;
  await mockJson(page, base, title);
  await mockJson(page, `${base}/providers`, PROVIDERS);
  await mockJson(page, `${base}/lens`, LENS);
  // "More like this" — only for THIS title's anchor, so a title opened over Home leaves Home's own
  // rails to Home's mocks.
  await page.route(
    (url) => url.pathname === "/api/feed/more",
    (route) => {
      const body = route.request().postDataJSON() as { anchorTitleId?: string | null } | null;
      if (body?.anchorTitleId !== title.titleId) return route.fallback();
      return route.fulfill({
        status: 200,
        contentType: "application/json",
        body: JSON.stringify({ items: [2, 3, 4].map((n) => feedCard(40 + n)), exhausted: true }),
      });
    }
  );
}

export const PERSON: PersonPayload = {
  id: PERSON_ID,
  name: "Cast Member 1",
  department: "Acting",
  profilePath: "/cast1.jpg",
  userRating: 8,
  derivedAffinity: 7.6,
  ratedTitleCount: 4,
  hiddenByConditions: 1,
  filmography: [1, 2, 3, 4, 5].map((n) => ({
    titleId: id("f", n),
    mediaType: n % 2 ? "Movie" : "Show",
    tmdbId: 6000 + n,
    name: `Credit ${n}`,
    year: 2000 + n * 3,
    posterPath: n === 5 ? null : `/credit${n}.jpg`,
    userRating: n <= 2 ? 7 + n : null,
    predictedRating: n > 2 ? 6.5 + n / 10 : null,
    roles: n === 3 ? ["Actor", "Writer"] : ["Actor"],
    character: `Character ${n}`,
  })),
};

export async function mockPerson(page: Page): Promise<void> {
  await mockJson(page, `**/api/people/${PERSON_ID}`, PERSON);
}

// ── Saved ────────────────────────────────────────────────────────────────────────────────────────

export const savedItem = (n: number): SavedQueueItem => ({
  titleId: id("2", n),
  mediaType: n % 3 ? "Movie" : "Show",
  tmdbId: 9200 + n,
  name: `Saved ${n}`,
  year: 2021,
  posterPath: n % 4 ? `/saved${n}.jpg` : null,
  runtimeMinutes: 100,
  predictedRating: 7.0,
  crowdRating: 7.4,
  crowdVotes: 12000,
  providers: n % 2 ? [{ name: "Netflix", logoPath: "/netflix.jpg" }] : [],
  availableNow: n % 2 === 1,
  savedAt: "2026-08-01T00:00:00Z",
  daysSaved: 39,
  fitsEveningBudget: true,
  onManagedList: true,
  listSync: "onList",
  watchTonight: n === 1,
  watchTonightReason: n === 1 ? "Fits your evening and it is streaming now." : null,
});

export const SAVED: SavedQueueResponse = {
  managedListUrl: "https://trakt.tv/lists/42",
  sort: "tonight",
  eveningBudgetMinutes: 150,
  expectedAbsoluteError: 1.9,
  unscoredRemaining: 0,
  items: [1, 2, 3, 4, 5, 6, 7, 8].map(savedItem),
};

export async function mockSaved(page: Page): Promise<void> {
  await mockTitleSideReads(page);
  await mockJson(page, "**/api/saved", SAVED);
  await mockJson(page, "**/api/saved/recently-watched", {
    expectedAbsoluteError: 1.9,
    items: [1, 2].map((n) => ({
      titleId: id("w", n), mediaType: "Movie", tmdbId: 9300 + n, name: `Watched ${n}`, year: 2020,
      posterPath: `/w${n}.jpg`, predictedRating: 7.1, watchedAt: "2026-10-20T21:00:00Z",
    })),
  });
}

// ── Rate ─────────────────────────────────────────────────────────────────────────────────────────

export const RATE_SUGGESTIONS: RateSuggestion[] = [1, 2, 3, 4, 5, 6, 7, 8].map((n) => ({
  titleId: id("s", n),
  mediaType: n % 3 ? "Movie" : "Show",
  tmdbId: 5000 + n,
  name: `Suggestion ${n}`,
  year: 1990 + n,
  posterPath: n === 8 ? null : `/s${n}.jpg`,
  crowdRating: 7.2,
  crowdVotes: 40000,
  source: n <= 5 ? "watched" : "popular",
}));

export async function mockRate(page: Page): Promise<void> {
  await mockJson(page, "**/api/titles/rate-suggestions", RATE_SUGGESTIONS);
}

// ── Search (Ask Reel, resumed) ───────────────────────────────────────────────────────────────────

export const CONVERSATION_ID = "7f3a1b2c-0000-4000-8000-0000000000a1";

const askCard = (n: number): AskCard => ({
  titleId: id("k", n),
  mediaType: n === 2 ? "Show" : "Movie",
  tmdbId: 8100 + n,
  name: `Answer ${n}`,
  year: 2010 + n,
  posterPath: n === 3 ? null : `/answer${n}.jpg`,
  genres: ["crime", "thriller"],
  similarity: 0.6,
  predictedRating: 7.4,
  crowdRating: 7.2,
  crowdVotes: 30000,
  fit: 0.8,
  why: `A tense crew film like the heists you rate highest (${n}).`,
  blend: 0.5,
  seen: false,
});

export const CONVERSATION: ConversationPayload = {
  id: CONVERSATION_ID,
  title: "a tense heist",
  lastCardsAt: "2026-10-26T18:00:00Z",
  lastTurnAt: "2026-10-26T18:00:00Z",
  turns: [
    { sequence: 0, role: "user", text: "a tense heist", kind: "ask", status: "Completed" },
    { sequence: 1, role: "assistant", text: "Three that fit.", kind: "ask", status: "Completed" },
  ],
  cards: [1, 2, 3].map(askCard),
  reason: "3 picks ranked for you, from 40 pulled.",
  removed: 0,
  lastQuery: "a tense heist",
  mediaTypes: [],
  shown: [8101, 8102, 8103],
};

export async function mockSearch(page: Page): Promise<void> {
  await mockTitleSideReads(page);
  await mockJson(page, "**/api/search/conversations", [
    { id: CONVERSATION_ID, title: "a tense heist", turnCount: 2, lastTurnAt: CONVERSATION.lastTurnAt, lastCardsAt: CONVERSATION.lastCardsAt },
  ]);
  await mockJson(page, "**/api/search/conversations/latest", CONVERSATION);
  await mockJson(page, `**/api/search/conversations/${CONVERSATION_ID}`, CONVERSATION);
}
