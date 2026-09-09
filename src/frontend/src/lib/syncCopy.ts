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

/** The `job-completed` payload for a delta/reconcile. Mirrors the backend's `SyncSummary`. */
export interface SyncSummaryPayload {
  ratingsAdded: number;
  ratingsChanged: number;
  watchedAdded: number;
  retrainQueued: boolean;
  retrainAt: string | null;
  picksChanged: number | null;
}

/** The idle sentence when a sync found nothing at all. */
export const UP_TO_DATE = "Already up to date";

const plural = (count: number, singular: string, many = `${singular}s`) => (count === 1 ? singular : many);

/** Grouped the same way the server groups it — invariant, not locale-dependent. */
const count = (value: number) => value.toLocaleString("en-US");

/**
 * The post-sync sentence: what arrived from Trakt, then the one consequence worth naming.
 *
 * Mirrors `SyncSummaryCopy.Describe` in
 * `src/backend/FrostAura.Reel.Application/Sync/SyncSummary.cs`, which renders the same shape into
 * the job's persisted `ProgressMessage`. Both exist because the pill has two sources — this live
 * SSE payload and the 5-second poll of that message — and a single renderer on either side would
 * leave the other one guessing. Change one, change the other; both are covered by tests asserting
 * the same example sentences.
 *
 * The numbers themselves are the fix. The count the sync used to have in hand was every rating row
 * walked from Trakt on a full paginated refetch — ~3,400 for the founder's account, seasons and
 * episodes included — so this sentence would have read "Pulled 3,412 new ratings" after every
 * single sync, forever.
 */
export function syncSummaryCopy(summary: SyncSummaryPayload | null | undefined): string | null {
  if (!summary) return null;

  const lead =
    summary.ratingsAdded > 0
      ? `Pulled ${count(summary.ratingsAdded)} new ${plural(summary.ratingsAdded, "rating")} from Trakt`
      : summary.ratingsChanged > 0
        ? `Updated ${count(summary.ratingsChanged)} ${plural(summary.ratingsChanged, "rating")} from Trakt`
        : summary.watchedAdded > 0
          ? `Pulled ${count(summary.watchedAdded)} new ${plural(summary.watchedAdded, "watch", "watches")} from Trakt`
          : UP_TO_DATE;

  // A concrete change to the page outranks a promise about tonight: "3 picks changed" is something
  // the user can go and look at. picksChanged === 0 is knowledge, not a clause.
  const tail =
    summary.picksChanged != null && summary.picksChanged > 0
      ? `${count(summary.picksChanged)} ${plural(summary.picksChanged, "pick")} changed`
      : summary.retrainQueued
        ? summary.retrainAt
          ? `retraining tonight at ${utcHourMinute(summary.retrainAt)} UTC`
          : "retraining now"
        : null;

  return tail ? `${lead} — ${tail}` : lead;
}

/**
 * HH:mm of an ISO instant, in UTC. Read off the string rather than through `Date`, so the hour the
 * user is promised is the hour the server scheduled — a browser in Johannesburg rendering "05:00
 * UTC" for an 03:00 job is a lie the local timezone would tell for free.
 */
function utcHourMinute(iso: string): string {
  const parsed = new Date(iso);
  if (Number.isNaN(parsed.getTime())) return "03:00";
  return `${String(parsed.getUTCHours()).padStart(2, "0")}:${String(parsed.getUTCMinutes()).padStart(2, "0")}`;
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
