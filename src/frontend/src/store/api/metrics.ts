import { api } from "./base";

export interface ModelEvalDetail {
  topRanked?: { titleId: string; rating: number; predicted: number; hit: boolean }[];
  featureImportance?: Record<string, number>;
  caveats?: string[];
}

/** One rolling split origin — the harness reports five (60/65/70/75/80% of the rating history). */
export interface ModelEvalOrigin {
  fraction: number;
  splitAt: string;
  trainRows: number;
  unfilteredHoldout: number;
  eligibleHoldout: number;
  threshold: number;
  lowSample: boolean;
  ndcgModel: number;
  ndcgBaseline: number;
  ndcgRelativeLift: number;
  precisionModel: number;
  precisionBaseline: number;
}

/**
 * Nullable everywhere the 2026-09 instrument repair added a column: ledger rows written before it
 * genuinely have no NDCG, no CI and no calibration, and rendering a zero would claim a measurement
 * that was never taken.
 */
export interface ModelEval {
  /** Quantised to 0.1 by a ten-slot cut — display only; the gate reads NDCG. */
  modelPrecisionAt10: number;
  baselinePrecisionAt10: number;
  /** The legacy precision lift, kept so the twelve historical rows stay comparable. */
  relativeImprovement: number;
  rmse: number;
  mae: number;
  spearmanRho: number;
  spearmanSe: number | null;
  ndcgAt10Model: number | null;
  ndcgAt10Baseline: number | null;
  ndcgLiftCiLow: number | null;
  ndcgLiftCiHigh: number | null;
  calibrationBias: number | null;
  ece: number | null;
  eligibleHoldoutCount: number | null;
  unfilteredHoldoutCount: number | null;
  holdoutPositiveCount: number;
  lowSample: boolean;
  passedGate: boolean;
  passedLegacyGate: boolean;
  origins: ModelEvalOrigin[] | null;
  detail: ModelEvalDetail;
}

export interface ModelRun {
  id: string;
  iteration: number;
  configHash: string;
  splitAt: string;
  trainRowCount: number;
  holdoutRowCount: number;
  positiveThreshold: number;
  status: string;
  startedAt: string;
  eval: ModelEval | null;
}

/** Predicted-vs-actual summary. `pearsonCorrelation` is null when it is genuinely undefined. */
export interface PredictionAccuracy {
  count: number;
  meanPredicted: number;
  meanActual: number;
  meanAbsoluteError: number;
  bias: number;
  withinOnePointRate: number;
  pearsonCorrelation: number | null;
}

export interface RetroMiss {
  titleId: string;
  name: string;
  year: number | null;
  predicted: number;
  actual: number;
  gap: number;
}

/** "Titles you watched because of Reel average X; titles you found yourself average Y." */
export interface CounterfactualWindow {
  window: string;
  days: number | null;
  viaReelCount: number;
  viaReelMeanRating: number | null;
  otherCount: number;
  otherMeanRating: number | null;
  delta: number | null;
  /** Via-Reel ratings with no other rating within `matchDays` of them — left out of both means. */
  viaReelUnmatchedCount: number;
  /** The matching band: each via-Reel rating is compared with the user's own ratings within ± this many days. */
  matchDays: number;
}

/**
 * The retro closed loop: what Reel predicted against what actually happened. Computed live from
 * the managed list, ratings and stored scores — no new schema, and no smoothing.
 */
export interface ModelEfficiency {
  computedAt: string;
  conversions: {
    count: number;
    ratedCount: number;
    scoredCount: number;
    accuracy: PredictionAccuracy;
    ratingDistribution: { rating: number; count: number }[];
    worstMisses: RetroMiss[];
  };
  predictedVsActual: {
    pairs: number;
    accuracy: PredictionAccuracy;
    worstMisses: RetroMiss[];
  };
  counterfactual: CounterfactualWindow[];
}

/** One ranker's record across the interleaving experiment. */
export interface PolicyStanding {
  policyId: string;
  description: string | null;
  /** "A", "B", or null for a ranker that is no longer one of the configured arms. */
  team: string | null;
  slates: number;
  cards: number;
  credits: number;
  slateWins: number;
}

/**
 * The interleaving readout. Every number is computed on read from the exposure log, and the
 * always-valid p-value may be looked at as often as you like without inflating the error rate —
 * which is the entire reason the harness uses an mSPRT rather than a test with a sample-size plan.
 */
export interface InterleaveSummary {
  computedAt: string;
  enabled: boolean;
  policyA: string;
  policyB: string;
  tau: number;
  alpha: number;
  slates: number;
  decidedSlates: number;
  ties: number;
  winsA: number;
  winsB: number;
  alwaysValidP: number;
  significant: boolean;
  preferenceRateA: number | null;
  ciLow: number;
  ciHigh: number;
  signTestP: number;
  firstSlateAt: string | null;
  lastSlateAt: string | null;
  decidedSlatesPerDay: number | null;
  slatesToDecisionAt65: number;
  estimatedDaysToDecision: number | null;
  standings: PolicyStanding[];
}

export interface ModelMetrics {
  gate: {
    /** The mean-relative-NDCG-lift clause of the gate (0.20). */
    threshold: number;
    passed: boolean;
    latestLift: number | null;
    ndcgModel: number | null;
    ndcgBaseline: number | null;
    ndcgLiftCiLow: number | null;
    ndcgLiftCiHigh: number | null;
    ciExcludesZero: boolean;
    legacyThreshold: number;
    legacyPassed: boolean;
    iterationsUsed: number;
    killCriterionAt: number;
    criterionExceeded: boolean;
  };
  efficiency: ModelEfficiency;
  activeArtifact: { version: number; algo: string; trainedAt: string } | null;
  runs: ModelRun[];
}

/** One predicted band on the calibration chart. Null means the band is empty, not that it is zero. */
export interface CalibrationBucket {
  bucket: number;
  low: number;
  high: number;
  n: number;
  meanPredicted: number | null;
  meanActual: number | null;
}

/** One specific thing Reel got right or wrong, with enough identity to render its poster. */
export interface AccuracyMoment {
  titleId: string;
  name: string;
  year: number | null;
  posterPath: string | null;
  mediaType: string;
  tmdbId: number | null;
  predicted: number;
  actual: number;
  /** Null on the retro fallback — a stored score is not proof the card was ever on screen. */
  surface: string | null;
  shownAt: string | null;
  ratedAt: string;
  daysToOutcome: number | null;
}

/**
 * The track record. `source` is part of the payload rather than an implementation detail: the page
 * says out loud whether a number came from the exposure log ("we know this was on your screen") or
 * from the retro join ("a score existed before you rated it"), because the two are different
 * strengths of evidence and smoothing over the difference is the one thing this surface must not do.
 */
export interface AccuracyPayload {
  window: string;
  from: string | null;
  to: string;
  source: "exposures" | "retro" | "none";
  headline: {
    withinOnePoint: number;
    n: number;
    meanAbsoluteError: number;
    bias: number;
    correlation: number | null;
    meanPredicted: number;
    meanActual: number;
  };
  calibration: CalibrationBucket[];
  moments: AccuracyMoment[];
  conversion: {
    shown: number;
    saved: number;
    watched: number;
    rated: number;
    shownToSaved: number | null;
    savedToWatched: number | null;
    shownToRated: number | null;
    medianDaysToWatch: number | null;
  };
  counterfactual: CounterfactualWindow[];
  learnedThisWeek: {
    trainedAt: string;
    newRatings: number;
    calibrationBias: number | null;
    calibrationMae: number | null;
    spearman: number | null;
    picksChanged: number;
  } | null;
  /**
   * DEPRECATED here (WS-5 task 11): served at /api/metrics/cross-reference and dropped from this
   * payload once nothing reads it. The track record and the lab read the new endpoint.
   */
  crossReference?: CrossReferenceAccuracy;
  /**
   * Who each block is about and how the account's own choices selected that group (G-8). Optional:
   * a server from before WS-5 task 11 sends none, and the page then shows only its source note.
   */
  populations?: {
    headline: PopulationNote;
    counterfactual: PopulationNote;
    conversion: PopulationNote;
    crossReference: PopulationNote;
  } | null;
  /**
   * Every accepted window with its pair count, narrowest first. A wider window holding exactly a
   * narrower one's pairs names it in `sameAs`, and the page offers only windows that differ (G-9).
   */
  windows?: AccuracyWindow[];
}

/** One population on the track record, in plain words, with the selection effect behind it. */
export interface PopulationNote {
  name: string;
  selectionNote: string;
}

export interface AccuracyWindow {
  key: string;
  pairs: number;
  sameAs: string | null;
}

/** GET /api/metrics/cross-reference — the cross-reference block, moved off /api/accuracy (G-10). */
export interface CrossReferencePayload {
  window: string;
  population: PopulationNote;
  crossReference: CrossReferenceAccuracy;
}

/**
 * How the titles served under one cross-reference standing actually turned out. `answerable` is the
 * server's own judgement about whether `n` is large enough to read anything into — the panel shows
 * the count and refuses to draw a conclusion below it, because this measurement can never reach
 * significance (about 2% of ratings are made in the app) and the offline backtest is the gate.
 */
export interface CrossReferenceVerdictOutcome {
  verdict: string;
  n: number;
  meanLaterRating: number | null;
  lovedRate: number | null;
  dislikedRate: number | null;
  answerable: boolean;
}

/** The newest pooled backtest at one prompt generation — the evidence that decided what a verdict may do. */
export interface CrossReferenceBacktest {
  promptVersion: number;
  evaluatedAt: string;
  judged: number;
  auc: number | null;
  meanLaterRatingKeep: number | null;
  meanLaterRatingOut: number | null;
  keepLovedRate: number | null;
  keepDislikedRate: number | null;
  falseRuleOutRate: number;
  falseRuleOutLow: number;
  falseRuleOutHigh: number;
  trueRuleOutRate: number;
  unsureRate: number;
  gating: string;
  caveat: string;
  /** Whether this is the generation the job is writing today. */
  current: boolean;
}

export interface CrossReferenceAccuracy {
  online: CrossReferenceVerdictOutcome[];
  backtests: CrossReferenceBacktest[];
  minimumOutcomes: number;
}

export const metricsApi = api.injectEndpoints({
  // Vite HMR re-runs this module; let the re-injection replace the old definitions.
  overrideExisting: Boolean(import.meta.hot),
  endpoints: (b) => ({
    /**
     * The track record. Free for every tier — a paid accuracy report reads as hiding the scorecard.
     * Polled by nothing: it changes when a rating lands, and the rating mutation invalidates it.
     */
    getAccuracy: b.query<AccuracyPayload, { window?: string } | void>({
      query: (args) => `accuracy?window=${args && args.window ? args.window : "90d"}`,
      providesTags: ["Accuracy"],
    }),
    /**
     * The cross-reference outcomes and backtests (WS-5 task 11). The track record reads it for the
     * plain-words half; the lab reads it for the statistics (AUC, intervals, prompt generations).
     */
    getCrossReferenceStats: b.query<CrossReferencePayload, { window?: string } | void>({
      query: (args) => `metrics/cross-reference?window=${args && args.window ? args.window : "90d"}`,
      providesTags: ["Accuracy"],
    }),
    getModelMetrics: b.query<ModelMetrics, void>({
      query: () => "metrics/model",
      providesTags: ["Lab"],
    }),
    /**
     * The interleaving instrument. Founder-only server-side; polled slowly because the underlying
     * number moves on the scale of days, and a fast poll would suggest otherwise.
     */
    getInterleave: b.query<InterleaveSummary, void>({
      query: () => "metrics/interleave",
      providesTags: ["Lab"],
    }),
    trainModel: b.mutation<void, void>({
      query: () => ({ url: "metrics/model/train", method: "POST" }),
      invalidatesTags: ["Lab", "Sync"],
    }),
  }),
});

export const {
  useGetModelMetricsQuery,
  useGetInterleaveQuery,
  useTrainModelMutation,
  useGetAccuracyQuery,
  useGetCrossReferenceStatsQuery,
} = metricsApi;
