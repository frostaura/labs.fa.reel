/**
 * Contextual copy for the header sync pill's tooltip — what the background job is actually
 * doing for the user, per job kind. Kinds arrive on `job-progress` / `job-completed` SSE
 * events; unknown kinds fall back to the generic explainer, so new pipeline jobs are safe
 * to ship backend-first.
 */
export interface SyncExplainer {
  title: string;
  lines: string[];
}

const EXPLAINERS: Record<string, SyncExplainer> = {
  ingest: {
    title: "First sync with Trakt",
    lines: [
      "Pulling everything you've ever watched and rated",
      "Movies, shows, episodes — the full history",
    ],
  },
  hydrate: {
    title: "Enriching your library",
    lines: [
      "Artwork and trailers for every title",
      "Global popularity — the baseline your model must beat",
      "Cast & crew graph that powers your affinities",
      "Fetched once, then shared across all of Reel",
    ],
  },
  delta: {
    title: "Syncing with Trakt",
    lines: [
      "Pulling your latest watches and ratings",
      "Runs automatically every few minutes",
    ],
  },
  reconcile: {
    title: "Nightly reconcile",
    lines: [
      "Full sweep against Trakt to repair any drift",
      "Keeps your queue and history exact",
    ],
  },
  train: {
    title: "Training your model",
    lines: [
      "Fitting a fresh model on your ratings",
      "Every prediction stays explainable",
    ],
  },
  enrich: {
    title: "Reading every title",
    lines: [
      "An AI analyst rates tone, pacing, humour and themes",
      "Plot embeddings place each title in your taste space",
      "Done once, then shared across all of Reel",
    ],
  },
  evaluate: {
    title: "Checking the model",
    lines: [
      "Scoring this model against a popularity baseline",
      "Only a model that beats it ships to your feed",
    ],
  },
  feed: {
    title: "Building tonight's picks",
    lines: [
      "Scoring eligible titles with your model",
      "Ranking by predicted rating, freshness and variety",
    ],
  },
};

const FALLBACK: SyncExplainer = {
  title: "Background sync",
  lines: ["Keeping Reel current with your library"],
};

export function syncExplainer(kind: string | null | undefined): SyncExplainer {
  return (kind && EXPLAINERS[kind]) || FALLBACK;
}

/**
 * Backend JobKind (sync/status payload) → the short SSE kind vocabulary above. Lets a page
 * reload seed the pill from the authoritative status endpoint, not just live events.
 */
const JOB_KIND_TO_SSE: Record<string, string> = {
  FullIngest: "ingest",
  DeltaSync: "delta",
  FullReconcile: "reconcile",
  HydrateCatalog: "hydrate",
  EnrichCatalog: "enrich",
  Train: "train",
  Evaluate: "evaluate",
  BuildFeed: "feed",
};

export function jobKindToSseKind(jobKind: string | null | undefined): string | null {
  return (jobKind && JOB_KIND_TO_SSE[jobKind]) || null;
}

/**
 * Background Trakt polls (delta/reconcile) fire every few minutes and must not hijack the pill
 * from a primary pipeline job (onboarding, enrichment, training) that the user is waiting on.
 */
const BACKGROUND_KINDS = new Set(["delta", "reconcile"]);

export function isBackgroundKind(sseKind: string | null | undefined): boolean {
  return sseKind != null && BACKGROUND_KINDS.has(sseKind);
}

/**
 * What the pipeline is doing for you, in words, per `Account.PipelineStage`.
 *
 * The stage used to be interpolated RAW into the waiting copy on Home — "Your picks are being
 * assembled (Extracting)" — which is an internal state-machine token leaking onto the founder's
 * home screen. Each phrase is a verb clause in the second person, so it reads as the tail of a
 * sentence rather than a label.
 *
 * `Degraded` is the hard-failure parking state (revoked token, dead-lettered writes) and is the
 * one stage with a REASON worth quoting: pass the persisted one (`outboxLastError` on
 * `/api/sync/status`) and it is shown verbatim instead of the generic line. Callers that do not
 * hold it get honest generic copy — never a blank, and never the enum.
 *
 * An unknown stage falls back to the generic clause, so a new backend stage ships safely ahead of
 * the frontend that names it.
 */
const PIPELINE_STAGE_COPY: Record<string, string> = {
  Linked: "linking your Trakt",
  Ingesting: "pulling your history",
  Extracting: "reading every title",
  Training: "teaching your model",
  Evaluated: "checking itself",
  FeedReady: "putting tonight's picks together",
};

/** The generic clause for an unknown stage, and for Degraded with no persisted reason. */
export const PIPELINE_STAGE_FALLBACK = "getting your library ready";

/** What Degraded says when nothing was persisted to explain it. */
export const PIPELINE_DEGRADED_FALLBACK = "your Trakt link needs attention";

export function pipelineStageCopy(
  stage: string | null | undefined,
  degradedReason?: string | null
): string {
  if (stage === "Degraded") {
    return degradedReason?.trim() || PIPELINE_DEGRADED_FALLBACK;
  }

  return (stage && PIPELINE_STAGE_COPY[stage]) || PIPELINE_STAGE_FALLBACK;
}
