import type { JobFailure, SyncJobSummary, SyncStatus } from "../store/api/sync";
import type { LiveProgress, SyncSummary } from "../store/syncSlice";
import { relTime } from "./format";
import {
  activeJobLabel,
  failureAftermath,
  failureNoun,
  isTerminalReason,
  purposeCopy,
  reasonCopy,
  syncExplainer,
  syncSummaryCopy,
} from "./syncCopy";

/**
 * The header pill as data: one pure function from the status API (plus the live SSE frame and the
 * last sync's outcome) to what the pill says. The component only renders this, so every rule the
 * pill follows is unit-tested here.
 *
 * The rules, in order (H-1, H-17, I-1, I*1, O+1):
 *   1. Health outranks progress. A danger issue — a Trakt link that needs reconnecting, dead-lettered
 *      writes, a provider refusing every call, a job kind failing again and again — owns the pill
 *      whatever is running.
 *   2. Then work the user started or is waiting on (`visibility: UserVisible`). Background upkeep is
 *      never `activeJob` (the backend guarantees it; this re-checks it).
 *   3. A warning issue (one recent failure, a degraded provider, too few ratings) outranks the named
 *      cross-reference pass, which nobody started.
 *   4. Then the named pass, by name and with no denominator.
 *   5. Otherwise idle: what the last sync did, or how long ago it ran.
 *
 * Nothing here reads a handler's free-text `progressMessage` or an error string: every word comes
 * from the fixed copy in `syncCopy.ts`.
 */

export type PillTone = "ok" | "active" | "warn" | "danger";

export interface PillIssue {
  tone: "warn" | "danger";
  /** Short header label. */
  label: string;
  /** One sentence for the tooltip. */
  detail: string;
  /** True when the fix is to reconnect Trakt — the pill then offers that action. */
  reconnect: boolean;
  /** What still stands despite the failure (earlier results the page keeps showing), if anything. */
  aftermath?: string | null;
}

export interface PillView {
  /** "active" renders the progress layout; "health" the compact chip. */
  mode: "active" | "health";
  tone: PillTone;
  label: string;
  /** Determinate progress, only when an honest percentage exists. */
  pct: number | null;
  /** "x / y" counts from the live frame, never for the named pass. */
  count: { current: number; total: number } | null;
  reconnect: boolean;
  /** Tooltip heading and lines. */
  title: string;
  lines: string[];
  /** Every health issue, most severe first, for the tooltip. */
  issues: PillIssue[];
}

export interface PillInput {
  status: SyncStatus | undefined;
  /** True when the latest status request failed (5xx, offline). */
  statusFailed: boolean;
  live: LiveProgress | null;
  summary: SyncSummary | null;
  /** When `summary` arrived (epoch ms); an undated or stale summary is not shown. */
  summaryAt?: number | null;
  /** The clock, for tests; defaults to now. */
  now?: number;
}

/** Status-poll cadence: 5 s only while work the user started is in flight, otherwise 60 s. */
export const ACTIVE_POLL_MS = 5_000;
export const IDLE_POLL_MS = 60_000;

/**
 * How long the last sync's sentence ("Already up to date", "Pulled 3 new ratings…") stays on the
 * idle pill. The sentence carries no date, so past this the pill falls back to the dated "Synced
 * 12m ago" stamp rather than repeat an old result as if it were current.
 */
export const SUMMARY_FRESH_MS = 10 * 60_000;

/** Connection states that mean Trakt refuses Reel's sign-in. Anything else — including a value this
 *  build does not know — is not reported as a broken link: an appended enum value must not raise an
 *  alarm, and the stable `degradedReason` still does when the link really is broken. */
const BROKEN_CONNECTION = new Set(["RefreshFailed", "Revoked"]);

/** A failure streak this long reads as danger, not a passing blip. */
export const FAILURE_STREAK_DANGER = 3;

/**
 * A failed run of work nobody is waiting on — a background job or the named cross-reference pass —
 * stops reading as an alarm once its latest failure is this old (R1-20). Nothing has retried it
 * since, nothing the user did is blocked on it, and a red header that stays red for days beside a
 * page still showing that work's earlier results is wallpaper, not news. It stays a warning with
 * its reason and its age. Work the user waits on (a Trakt sync, a picks build they started) and
 * terminal refusals (401/402/403) never age: those keep the danger state until they are fixed.
 */
export const AGED_FAILURE_MS = 24 * 60 * 60_000;

const IN_FLIGHT = new Set(["Pending", "Running"]);

/** The active job, if it is one the header may show at all. */
export function shownActiveJob(status: SyncStatus | undefined): SyncJobSummary | null {
  const job = status?.activeJob;
  if (!job || !IN_FLIGHT.has(job.status)) return null;
  // Only the two shown visibilities; Background — or a value this build does not know — is upkeep.
  return job.visibility === "UserVisible" || job.visibility === "NamedPass" ? job : null;
}

export function isUserVisibleWorkInFlight(status: SyncStatus | undefined): boolean {
  return shownActiveJob(status)?.visibility === "UserVisible";
}

export function statusPollInterval(status: SyncStatus | undefined): number {
  return isUserVisibleWorkInFlight(status) ? ACTIVE_POLL_MS : IDLE_POLL_MS;
}

const plural = (n: number, one: string, many = `${one}s`) => (n === 1 ? one : many);

/** True when this failure has aged out of the danger state (see {@link AGED_FAILURE_MS}). */
export function isAgedFailure(failure: JobFailure, now: number = Date.now()): boolean {
  if (failure.visibility === "UserVisible" || isTerminalReason(failure.reasonCode)) return false;
  const failedAt = Date.parse(failure.failedAt);
  return Number.isFinite(failedAt) && now - failedAt > AGED_FAILURE_MS;
}

function failureIssue(failure: JobFailure, now: number): PillIssue {
  const streak = failure.consecutiveFailures;
  const danger =
    isTerminalReason(failure.reasonCode) || (streak >= FAILURE_STREAK_DANGER && !isAgedFailure(failure, now));
  const times = streak > 1 ? `Failed ${streak} times in a row` : "Failed";
  return {
    tone: danger ? "danger" : "warn",
    label: `${failureNoun(failure.sseKind)} failed`,
    detail: `${reasonCopy(failure.reasonCode)} ${times}, last ${relTime(failure.failedAt)}.`,
    reconnect: false,
    aftermath: failureAftermath(failure.sseKind),
  };
}

/** Every health issue the status reports, most severe first (stable within a tone). */
export function healthIssues(status: SyncStatus | undefined, statusFailed: boolean, now: number = Date.now()): PillIssue[] {
  const issues: PillIssue[] = [];

  if (statusFailed) {
    issues.push({
      tone: "warn",
      label: "Status unavailable",
      detail: "Reel couldn't reach the server to check on your sync. It will try again shortly.",
      reconnect: false,
    });
  }
  if (!status) return issues;

  const reason = status.degradedReason;
  if (
    BROKEN_CONNECTION.has(status.connectionStatus) ||
    reason === "TraktRevoked" ||
    reason === "TraktRefreshFailed"
  ) {
    issues.push({
      tone: "danger",
      label: "Reconnect Trakt",
      detail: "Trakt stopped accepting Reel's sign-in, so nothing syncs until you reconnect.",
      reconnect: true,
    });
  }

  if (status.outboxDeadLetters > 0 || reason === "TraktWritesDeadLettered") {
    const n = status.outboxDeadLetters;
    issues.push({
      tone: "danger",
      label: "Trakt writes failed",
      detail:
        n > 0
          ? `${n.toLocaleString("en-US")} ${plural(n, "rating or list change", "ratings or list changes")} couldn't reach Trakt after every retry.`
          : "Ratings or list changes couldn't reach Trakt after every retry.",
      reconnect: false,
    });
  }

  const provider = status.providerHealth;
  const firstBreaker = provider?.breakers?.[0];
  if (provider?.state === "Down" || reason === "ProviderDown") {
    issues.push({
      tone: "danger",
      label: "Model provider down",
      detail: firstBreaker ? reasonCopy(firstBreaker.reasonCode) : "Every model call is being refused.",
      reconnect: false,
    });
  }

  const failures = [...(status.lastFailures ?? [])]
    .sort((a, b) => b.failedAt.localeCompare(a.failedAt))
    .map((failure) => failureIssue(failure, now));
  issues.push(...failures);

  if (reason === "PipelineFailed") {
    issues.push({
      tone: "danger",
      label: "Picks stopped updating",
      detail: "Your pipeline stopped after a failure.",
      reconnect: false,
    });
  }

  if (provider?.state === "Degraded") {
    issues.push({
      tone: "warn",
      label: "Model provider degraded",
      detail: firstBreaker
        ? `${purposeCopy(firstBreaker.purpose)}: ${reasonCopy(firstBreaker.reasonCode)}`
        : "Some model calls are failing.",
      reconnect: false,
    });
  }

  const floor = status.notEnoughRatings;
  if (reason === "NotEnoughRatings" || status.pipelineStage === "NotEnoughRatings") {
    issues.push({
      tone: "warn",
      label: "More ratings needed",
      detail: floor
        ? `${floor.usableRatings.toLocaleString("en-US")} of ${floor.required.toLocaleString("en-US")} usable ratings — rate a few more and Reel trains your model.`
        : "Rate a few more titles and Reel trains your model.",
      reconnect: false,
    });
  }

  // Dangers first; Array.prototype.sort is stable, so the order above holds within a tone.
  return issues.sort((a, b) => (a.tone === b.tone ? 0 : a.tone === "danger" ? -1 : 1));
}

function healthView(issue: PillIssue, issues: PillIssue[]): PillView {
  return {
    mode: "health",
    tone: issue.tone,
    label: issue.label,
    pct: null,
    count: null,
    reconnect: issues.some((i) => i.reconnect),
    title: issue.tone === "danger" ? "Sync needs attention" : "Worth a look",
    lines: issue.aftermath ? [issue.detail, issue.aftermath] : [issue.detail],
    issues,
  };
}

function activeView(job: SyncJobSummary, live: LiveProgress | null, issues: PillIssue[]): PillView {
  const named = job.visibility === "NamedPass";
  const explainer = syncExplainer(job.sseKind);
  const baseLabel = activeJobLabel(job.sseKind);
  // A live frame describes this job only when its kind matches; anything else is another job's.
  const frame = live && live.kind === job.sseKind ? live : null;

  if (job.retrying) {
    return {
      mode: "active",
      tone: "warn",
      label: `Retrying · ${baseLabel}`,
      pct: null,
      count: null,
      reconnect: issues.some((i) => i.reconnect),
      title: explainer.title,
      lines: [
        `${reasonCopy(job.retryReasonCode)} Attempt ${job.attempt} of ${job.maxAttempts}.`,
        ...explainer.lines,
      ],
      issues,
    };
  }

  const queued = job.status === "Pending";
  const rawPct = queued || named ? null : (frame?.pct ?? job.progressPct);
  const pct = rawPct != null && Number.isFinite(rawPct) ? Math.max(0, Math.min(100, rawPct)) : null;
  const count =
    !named && !queued && frame?.current != null && frame.total != null
      ? { current: frame.current, total: frame.total }
      : null;

  return {
    mode: "active",
    tone: "active",
    label: baseLabel,
    pct,
    count,
    reconnect: issues.some((i) => i.reconnect),
    title: explainer.title,
    lines: queued ? ["Queued — starts shortly.", ...explainer.lines] : explainer.lines,
    issues,
  };
}

export function derivePill({ status, statusFailed, live, summary, summaryAt, now = Date.now() }: PillInput): PillView | null {
  const issues = healthIssues(status, statusFailed, now);
  const danger = issues.find((i) => i.tone === "danger");
  if (danger) return healthView(danger, issues);

  const job = shownActiveJob(status);
  if (job?.visibility === "UserVisible") return activeView(job, live, issues);

  const warn = issues.find((i) => i.tone === "warn");
  if (warn) return healthView(warn, issues);

  if (job) return activeView(job, live, issues);

  if (!status) return null;

  if (status.outboxPending > 0) {
    const n = status.outboxPending;
    return {
      mode: "health",
      tone: "ok",
      label: "Sending to Trakt…",
      pct: null,
      count: null,
      reconnect: false,
      title: "Sending to Trakt",
      lines: [`${n.toLocaleString("en-US")} ${plural(n, "change")} queued for Trakt.`],
      issues,
    };
  }

  const fresh = summaryAt != null && now - summaryAt >= 0 && now - summaryAt <= SUMMARY_FRESH_MS;
  const done = fresh ? syncSummaryCopy(summary) : null;
  const synced = status.lastDeltaSyncAt ? `Synced ${relTime(status.lastDeltaSyncAt)}` : null;
  return {
    mode: "health",
    tone: "ok",
    // A timestamp, not "Up to date": a stamp 45 days old under the words "up to date" is a claim
    // the pill cannot back. The tooltip heading claims nothing either — it names what follows.
    label: done ?? synced ?? "Not synced yet",
    pct: null,
    count: null,
    reconnect: false,
    title: synced ? "Last sync" : "Not synced yet",
    lines: [],
    issues,
  };
}
