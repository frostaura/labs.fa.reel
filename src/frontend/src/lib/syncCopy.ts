/**
 * Fixed copy for the header sync pill, per job kind. Kinds are the short SSE names the status API
 * carries on every job (`sseKind`, computed by the backend's `JobVisibilityPolicy`); unknown kinds
 * fall back to generic copy, so new pipeline jobs are safe to ship backend-first.
 *
 * Nothing in this file quotes a handler's free-text `progressMessage` or an exception string: the
 * pill once printed "nothing cross-referenced this run 100%" as live progress for a job that was
 * failing (H-1, I-1). Every word on the header comes from the tables below.
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
      // The old second line promised "only a model that beats it ships to your feed". None of
      // 101 evaluations beat popularity and every model shipped anyway, so the line was a lie.
      "The result lands in the model lab, win or lose",
    ],
  },
  feed: {
    title: "Building tonight's picks",
    lines: [
      "Scoring eligible titles with your model",
      "Ranking by predicted rating, freshness and variety",
    ],
  },
  narrative: {
    title: "Writing your taste profile",
    lines: [
      "Turning your ratings into a description of what you actually like",
      "Rewritten only when your taste has genuinely moved",
    ],
  },
  lens: {
    title: "Writing tonight's notes",
    lines: [
      "A short note on why each headline pick fits you",
      "Written for the hero and the leaders of each row",
    ],
  },
  availability: {
    title: "Refreshing where to watch",
    lines: ["Checking which services carry the titles on your page"],
  },
  digest: {
    title: "Writing your weekly digest",
    lines: ["Collecting the week's picks into one message"],
  },
  seed: {
    title: "Seeding the catalogue",
    lines: ["Adding titles Reel can recommend from"],
  },
  xref: {
    title: "Cross-referencing against your history",
    lines: [
      "Checking every eligible title against the ones you've actually rated",
      "Each candidate is judged beside your nearest rated titles and your own scores",
      "Nothing gets hidden — at most a title is pushed down the page",
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
  NotEnoughRatings: "waiting for a few more ratings",
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

// ── Header pill copy ─────────────────────────────────────────────────────────────────────────

/** The pill's label while a job of this kind is the status API's `activeJob`. */
const ACTIVE_LABELS: Record<string, string> = {
  ingest: "Importing your Trakt history",
  delta: "Syncing with Trakt",
  reconcile: "Reconciling with Trakt",
  hydrate: "Enriching your library",
  enrich: "Reading every title",
  train: "Training your model",
  evaluate: "Checking your model",
  feed: "Building your picks",
  narrative: "Rewriting your taste profile",
  lens: "Writing title notes",
  availability: "Refreshing where to watch",
  digest: "Writing your digest",
  seed: "Seeding the catalogue",
  // The named pass: shown by name, never with a denominator — it has none worth trusting.
  xref: "Cross-referencing · first pass",
};

export const ACTIVE_LABEL_FALLBACK = "Updating Reel";

export function activeJobLabel(sseKind: string | null | undefined): string {
  return (sseKind && ACTIVE_LABELS[sseKind]) || ACTIVE_LABEL_FALLBACK;
}

/** What a failed run of this kind is called: "{noun} failed". */
const FAILURE_NOUNS: Record<string, string> = {
  ingest: "Trakt import",
  delta: "Trakt sync",
  reconcile: "Trakt reconcile",
  hydrate: "Library enrichment",
  enrich: "Title reading",
  train: "Model training",
  evaluate: "Model check",
  feed: "Picks build",
  narrative: "Taste profile",
  lens: "Title notes",
  availability: "Where-to-watch refresh",
  digest: "Weekly digest",
  seed: "Catalogue seed",
  xref: "Cross-reference",
};

export const FAILURE_NOUN_FALLBACK = "A background job";

export function failureNoun(sseKind: string | null | undefined): string {
  return (sseKind && FAILURE_NOUNS[sseKind]) || FAILURE_NOUN_FALLBACK;
}

/**
 * Stable reason codes → a sentence. Covers the backend's `JobFailureReason` codes and the provider
 * breaker codes; an unknown code reads as the generic line, never as the code itself.
 */
const REASON_COPY: Record<string, string> = {
  stalled: "It stopped responding and was abandoned.",
  payment_required: "The model provider refused payment (402).",
  unauthorized: "A provider rejected Reel's credentials (401).",
  forbidden: "A provider refused access (403).",
  rate_limited: "A provider is rate-limiting Reel (429).",
  no_answer: "The model returned no usable answer.",
  budget_spent: "Today's call budget is spent.",
  timed_out: "It timed out.",
  truncation_breaker: "Model replies keep getting cut off.",
  error: "It hit an unexpected error.",
};

export const REASON_FALLBACK = REASON_COPY.error;

export function reasonCopy(code: string | null | undefined): string {
  return (code && REASON_COPY[code]) || REASON_FALLBACK;
}

/** Reason codes that no retry will fix — they need a person, so they read as danger at once. */
const TERMINAL_REASONS = new Set(["payment_required", "unauthorized", "forbidden"]);

export function isTerminalReason(code: string | null | undefined): boolean {
  return code != null && TERMINAL_REASONS.has(code);
}
