import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { components } from "../store/generated/openapi";
import type { JobFailure, LlmPurposeHealth, OperatorAlert, SyncJobSummary, SyncStatus } from "../store/api/sync";
import {
  ACTIVE_POLL_MS,
  AGED_FAILURE_MS,
  IDLE_POLL_MS,
  SUMMARY_FRESH_MS,
  derivePill,
  healthIssues,
  isAgedFailure,
  shownActiveJob,
  statusPollInterval,
} from "./syncPill";

const NOW = new Date("2026-10-24T12:00:00Z");

function job(overrides: Partial<SyncJobSummary> = {}): SyncJobSummary {
  return {
    id: "00000000-0000-0000-0000-0000000000a1",
    kind: "DeltaSync",
    sseKind: "delta",
    visibility: "UserVisible",
    status: "Running",
    progressPct: 40,
    progressMessage: "nothing cross-referenced this run",
    enqueuedAt: "2026-10-24T11:59:00Z",
    startedAt: "2026-10-24T11:59:01Z",
    completedAt: null,
    attempt: 1,
    maxAttempts: 3,
    retrying: false,
    retryReasonCode: null,
    ...overrides,
  };
}

function failure(overrides: Partial<JobFailure> = {}): JobFailure {
  return {
    kind: "CrossReference",
    sseKind: "xref",
    visibility: "NamedPass",
    failedAt: "2026-10-24T11:30:00Z",
    consecutiveFailures: 1,
    attempts: 3,
    reasonCode: "no_answer",
    ...overrides,
  };
}

function status(overrides: Partial<SyncStatus> = {}): SyncStatus {
  return {
    contractVersion: 2,
    pipelineStage: "FeedReady",
    connectionStatus: "Active",
    lastDeltaSyncAt: "2026-10-24T11:57:00Z",
    lastFullReconcileAt: null,
    activeJob: null,
    recentJobs: [],
    outboxPending: 0,
    outboxDeadLetters: 0,
    lastListSyncAt: null,
    outboxLastError: null,
    retrain: { minNewRatings: 10, newRatingsSinceTrain: 0, hasActiveModel: true, lastTrainedAt: null },
    lastFailures: [],
    providerHealth: { state: "Unmonitored", checkedAt: null, breakers: [] },
    degradedReason: null,
    notEnoughRatings: null,
    ...overrides,
  };
}

const pill = (s: SyncStatus | undefined, extra: Partial<Parameters<typeof derivePill>[0]> = {}) =>
  derivePill({ status: s, statusFailed: false, live: null, summary: null, ...extra });

beforeEach(() => {
  vi.useFakeTimers();
  vi.setSystemTime(NOW);
});
afterEach(() => vi.useRealTimers());

describe("health outranks progress", () => {
  it("shows Reconnect Trakt for a refresh-failed link while an Enrich-era job runs", () => {
    const view = pill(status({ connectionStatus: "RefreshFailed", activeJob: job({ kind: "FullIngest", sseKind: "ingest" }) }));
    expect(view).toMatchObject({ mode: "health", tone: "danger", label: "Reconnect Trakt", reconnect: true });
  });

  it.each(["TraktRevoked", "TraktRefreshFailed"] as const)("offers the reconnect for degradedReason %s", (reason) => {
    expect(pill(status({ degradedReason: reason }))).toMatchObject({ label: "Reconnect Trakt", reconnect: true });
  });

  it("shows danger with a reason after 3 failed cross-reference runs", () => {
    const view = pill(status({ lastFailures: [failure({ consecutiveFailures: 3 })] }))!;
    expect(view).toMatchObject({ mode: "health", tone: "danger", label: "Cross-reference failed" });
    expect(view.lines[0]).toBe("The model returned no usable answer. Failed 3 times in a row, last 30m ago.");
  });

  it("a danger failure outranks a sync the user started", () => {
    const view = pill(status({ activeJob: job(), lastFailures: [failure({ consecutiveFailures: 4 })] }));
    expect(view).toMatchObject({ mode: "health", tone: "danger" });
  });

  it("a single payment refusal is danger at once — no retry fixes it", () => {
    expect(pill(status({ lastFailures: [failure({ reasonCode: "payment_required" })] }))?.tone).toBe("danger");
  });

  it("dead-lettered writes are danger with the count", () => {
    const view = pill(status({ outboxDeadLetters: 2 }))!;
    expect(view).toMatchObject({ tone: "danger", label: "Trakt writes failed", reconnect: false });
    expect(view.lines[0]).toBe("2 ratings or list changes couldn't reach Trakt after every retry.");
  });

  it("a provider that is down is danger, explained by its breaker's code", () => {
    const view = pill(
      status({
        providerHealth: {
          state: "Down",
          checkedAt: null,
          breakers: [{ provider: "OpenRouter", purpose: null, state: "Open", reasonCode: "payment_required", since: "", retryAt: null }],
        },
      })
    )!;
    expect(view).toMatchObject({ tone: "danger", label: "Model provider down" });
    expect(view.lines[0]).toBe("The model provider refused payment (402).");
  });

  it("lists every other issue for the tooltip, dangers first", () => {
    const view = pill(
      status({ connectionStatus: "Revoked", outboxDeadLetters: 1, lastFailures: [failure()] })
    )!;
    expect(view.issues.map((i) => [i.tone, i.label])).toEqual([
      ["danger", "Reconnect Trakt"],
      ["danger", "Trakt writes failed"],
      ["warn", "Cross-reference failed"],
    ]);
  });
});

describe("progress", () => {
  it("shows work the user started with fixed copy, never the job's free text", () => {
    const view = pill(status({ activeJob: job() }))!;
    expect(view).toMatchObject({ mode: "active", tone: "active", label: "Syncing with Trakt", pct: 40 });
    expect(JSON.stringify(view)).not.toContain("nothing cross-referenced");
  });

  it("never shows background upkeep, even if the server sent it", () => {
    expect(pill(status({ activeJob: job({ visibility: "Background", sseKind: "enrich" }) }))?.mode).toBe("health");
    expect(shownActiveJob(status({ activeJob: job({ visibility: "SomethingNew" as never }) }))).toBeNull();
  });

  it("ignores a terminal activeJob", () => {
    expect(shownActiveJob(status({ activeJob: job({ status: "Failed" }) }))).toBeNull();
  });

  it("names the cross-reference pass with no percentage and no count", () => {
    const view = pill(
      status({ activeJob: job({ kind: "CrossReference", sseKind: "xref", visibility: "NamedPass", progressPct: 100 }) }),
      { live: { kind: "xref", pct: 29, current: 640, total: 2242 } }
    )!;
    expect(view).toMatchObject({ mode: "active", label: "Cross-referencing · first pass", pct: null, count: null });
  });

  it("a warning outranks the named pass nobody started", () => {
    const view = pill(
      status({
        activeJob: job({ kind: "CrossReference", sseKind: "xref", visibility: "NamedPass" }),
        lastFailures: [failure({ kind: "BuildFeed", sseKind: "feed", reasonCode: "timed_out" })],
      })
    );
    expect(view).toMatchObject({ mode: "health", tone: "warn", label: "Picks build failed" });
  });

  it("a warning does not take the pill from a job the user started", () => {
    const view = pill(status({ activeJob: job(), lastFailures: [failure()] }))!;
    expect(view.mode).toBe("active");
    expect(view.issues).toHaveLength(1);
  });

  it("a retrying job is a warning with its reason and attempt, and no percentage", () => {
    const view = pill(status({ activeJob: job({ retrying: true, attempt: 2, retryReasonCode: "rate_limited" }) }))!;
    expect(view).toMatchObject({ mode: "active", tone: "warn", label: "Retrying · Syncing with Trakt", pct: null });
    expect(view.lines[0]).toBe("A provider is rate-limiting Reel (429). Attempt 2 of 3.");
  });

  it("a queued job has no percentage", () => {
    const view = pill(status({ activeJob: job({ status: "Pending", progressPct: 100 }) }))!;
    expect(view.pct).toBeNull();
    expect(view.lines[0]).toBe("Queued — starts shortly.");
  });

  it("applies a live frame only when its kind matches the active job", () => {
    const active = status({ activeJob: job({ progressPct: 10 }) });
    expect(pill(active, { live: { kind: "delta", pct: 55, current: 5, total: 9 } })).toMatchObject({
      pct: 55,
      count: { current: 5, total: 9 },
    });
    expect(pill(active, { live: { kind: "enrich", pct: 90, current: 1, total: 2 } })).toMatchObject({ pct: 10, count: null });
  });

  it("clamps an out-of-range percentage", () => {
    expect(pill(status({ activeJob: job({ progressPct: 140 }) }))?.pct).toBe(100);
  });
});

describe("idle", () => {
  it("a late progress frame cannot keep the pill active once the poll says idle (H-17)", () => {
    const view = pill(status({ activeJob: null }), { live: { kind: "delta", pct: 100, current: null, total: null } })!;
    expect(view).toMatchObject({ mode: "health", tone: "ok", label: "Synced 3m ago" });
  });

  const summary = { ratingsAdded: 3, ratingsChanged: 0, watchedAdded: 0, retrainQueued: false, retrainAt: null, picksChanged: null };

  it("says what the last sync did, while that is recent", () => {
    const view = pill(status(), { summary, summaryAt: NOW.getTime() - 2 * 60_000 });
    expect(view?.label).toBe("Pulled 3 new ratings from Trakt");
  });

  it("falls back to the dated stamp once the sentence is stale, or was never dated", () => {
    expect(pill(status(), { summary, summaryAt: NOW.getTime() - SUMMARY_FRESH_MS - 1 })?.label).toBe("Synced 3m ago");
    expect(pill(status(), { summary, summaryAt: null })?.label).toBe("Synced 3m ago");
    expect(pill(status(), { summary })?.label).toBe("Synced 3m ago");
  });

  it("is honest before the first sync — label and tooltip", () => {
    expect(pill(status({ lastDeltaSyncAt: null }))).toMatchObject({ label: "Not synced yet", title: "Not synced yet" });
  });

  it("the idle tooltip heading claims nothing the stamp cannot back", () => {
    const old = pill(status({ lastDeltaSyncAt: new Date(NOW.getTime() - 45 * 86_400_000).toISOString() }))!;
    expect(old).toMatchObject({ label: "Synced 45d ago", title: "Last sync" });
    for (const view of [old, pill(status())!, pill(status({ lastDeltaSyncAt: null }))!]) {
      expect(view.title).not.toMatch(/everything|all synced|up to date/i);
    }
  });

  it("queued write-backs are not a failure", () => {
    expect(pill(status({ outboxPending: 2 }))).toMatchObject({ tone: "ok", label: "Sending to Trakt…" });
  });

  it("renders nothing before the first answer, and a warning when the answer failed", () => {
    expect(pill(undefined)).toBeNull();
    expect(pill(undefined, { statusFailed: true })).toMatchObject({ tone: "warn", label: "Status unavailable" });
  });

  it("unmonitored provider health is not reported as a problem — or as healthy", () => {
    expect(healthIssues(status(), false)).toEqual([]);
  });

  it("a degraded provider and too few ratings are warnings with specifics", () => {
    const issues = healthIssues(
      status({
        pipelineStage: "NotEnoughRatings",
        notEnoughRatings: { usableRatings: 4, required: 10 },
        providerHealth: {
          state: "Degraded",
          checkedAt: null,
          breakers: [{ provider: "OpenRouter", purpose: "rerank-ask", state: "Degraded", reasonCode: "truncation_breaker", since: "", retryAt: null }],
        },
      }),
      false
    );
    expect(issues.map((i) => i.detail)).toEqual([
      "Ask Reel: Model replies keep getting cut off.",
      "4 of 10 usable ratings — rate a few more and Reel trains your model.",
    ]);
  });

  it("tolerates a payload missing the v2 fields", () => {
    const v1 = { ...status() } as Partial<SyncStatus>;
    delete v1.lastFailures;
    delete v1.providerHealth;
    expect(pill(v1 as SyncStatus)?.tone).toBe("ok");
  });
});

describe("poll cadence", () => {
  it("polls every 5 s only while user-visible work is in flight", () => {
    expect(statusPollInterval(status({ activeJob: job() }))).toBe(ACTIVE_POLL_MS);
    expect(statusPollInterval(status({ activeJob: job({ visibility: "NamedPass", sseKind: "xref" }) }))).toBe(IDLE_POLL_MS);
    expect(statusPollInterval(status())).toBe(IDLE_POLL_MS);
    expect(statusPollInterval(undefined)).toBe(IDLE_POLL_MS);
  });
});

describe("unknown values", () => {
  it("a connection status this build does not know raises no alarm", () => {
    const view = pill(status({ connectionStatus: "SomeFutureState" as SyncStatus["connectionStatus"] }))!;
    expect(view).toMatchObject({ tone: "ok", reconnect: false });
    expect(view.issues).toEqual([]);
  });

  it.each(["RefreshFailed", "Revoked"] as const)("%s still asks for a reconnect", (connectionStatus) => {
    expect(pill(status({ connectionStatus }))).toMatchObject({ tone: "danger", label: "Reconnect Trakt", reconnect: true });
  });

  it("a degraded provider never prints a purpose or provider identifier", () => {
    const issues = healthIssues(
      status({
        providerHealth: {
          state: "Degraded",
          checkedAt: null,
          breakers: [
            { provider: "OpenRouter", purpose: "some-future-purpose", state: "Degraded", reasonCode: "rate_limited", since: "", retryAt: null },
          ],
        },
      }),
      false
    );
    expect(issues.map((i) => i.detail)).toEqual(["Some model features: A provider is rate-limiting Reel (429)."]);
    expect(JSON.stringify(issues)).not.toMatch(/OpenRouter|some-future-purpose/);
  });
});

describe("aged failures and what still stands (R1-20)", () => {
  const ago = (ms: number) => new Date(NOW.getTime() - ms).toISOString();
  const DAY = 24 * 60 * 60_000;

  it("a cross-reference streak whose last failure is 2 days old is a warning, not danger, with its age", () => {
    const view = pill(status({ lastFailures: [failure({ consecutiveFailures: 4, failedAt: ago(2 * DAY) })] }))!;
    expect(view).toMatchObject({ mode: "health", tone: "warn", label: "Cross-reference failed" });
    expect(view.lines).toEqual([
      "The model returned no usable answer. Failed 4 times in a row, last 2d ago.",
      "Titles earlier passes judged keep their verdicts.",
    ]);
  });

  it("the same streak inside the window is still danger, and still says what stands", () => {
    const view = pill(status({ lastFailures: [failure({ consecutiveFailures: 4, failedAt: ago(AGED_FAILURE_MS) })] }))!;
    expect(view.tone).toBe("danger");
    expect(view.lines[1]).toBe("Titles earlier passes judged keep their verdicts.");
    const aged = pill(status({ lastFailures: [failure({ consecutiveFailures: 4, failedAt: ago(AGED_FAILURE_MS + 60_000) })] }))!;
    expect(aged.tone).toBe("warn");
  });

  it("work the user waits on never ages: a Trakt sync failing for 3 days stays danger", () => {
    const view = pill(
      status({
        lastFailures: [
          failure({ kind: "DeltaSync", sseKind: "delta", visibility: "UserVisible", consecutiveFailures: 3, failedAt: ago(3 * DAY), reasonCode: "timed_out" }),
        ],
      })
    )!;
    expect(view).toMatchObject({ tone: "danger", label: "Trakt sync failed" });
    // A sync has no earlier result for the page to keep showing.
    expect(view.lines).toEqual(["It timed out. Failed 3 times in a row, last 3d ago."]);
  });

  it("a terminal refusal never ages, even on background work", () => {
    const view = pill(
      status({
        lastFailures: [
          failure({ kind: "EnrichCatalog", sseKind: "enrich", visibility: "Background", failedAt: ago(5 * DAY), reasonCode: "payment_required" }),
        ],
      })
    )!;
    expect(view).toMatchObject({ tone: "danger", label: "Title reading failed" });
  });

  it("an unreadable timestamp is not treated as old", () => {
    expect(isAgedFailure(failure({ consecutiveFailures: 3, failedAt: "not a date" }), NOW.getTime())).toBe(false);
    expect(pill(status({ lastFailures: [failure({ consecutiveFailures: 3, failedAt: "not a date" })] }))!.tone).toBe("danger");
  });

  it("an aged warning does not take the pill from a sync the user started", () => {
    const view = pill(status({ activeJob: job(), lastFailures: [failure({ consecutiveFailures: 5, failedAt: ago(3 * DAY) })] }))!;
    expect(view).toMatchObject({ mode: "active", label: "Syncing with Trakt" });
    expect(view.issues.map((i) => i.tone)).toEqual(["warn"]);
  });

  it("ages against the clock it is given, not only the wall clock", () => {
    const f = failure({ consecutiveFailures: 3, failedAt: "2026-10-20T00:00:00Z" });
    expect(isAgedFailure(f, Date.parse("2026-10-20T12:00:00Z"))).toBe(false);
    expect(isAgedFailure(f, Date.parse("2026-10-21T00:00:01Z"))).toBe(true);
  });
});

describe("the server decides what is shown (WS-6 CR: no kind list copied on the frontend)", () => {
  // Every job kind the backend can send, checked against the generated contract so it cannot drift.
  // It is the universe the property below quantifies over, not a classification: the header must
  // treat every kind alike and read only `visibility`.
  const ALL_KINDS = [
    "FullIngest", "DeltaSync", "FullReconcile", "HydrateCatalog", "EnrichCatalog", "Train", "Evaluate", "BuildFeed",
    "RefreshAvailability", "BuildDigest", "BuildTasteNarrative", "PersonaliseTitles", "SeedCatalog", "CrossReference",
  ] as const;
  type SameMembers<A, B> = [Exclude<A, B>, Exclude<B, A>] extends [never, never] ? true : never;
  const covered: SameMembers<(typeof ALL_KINDS)[number], components["schemas"]["JobKind"]> = true;

  it.each(ALL_KINDS)("%s is shown or hidden by its visibility alone", (kind) => {
    expect(covered).toBe(true);
    const as = (visibility: SyncJobSummary["visibility"]) =>
      status({ activeJob: job({ kind, sseKind: "some-kind", visibility }) });
    expect(shownActiveJob(as("Background"))).toBeNull();
    expect(shownActiveJob(as("UserVisible"))?.kind).toBe(kind);
    expect(shownActiveJob(as("NamedPass"))?.kind).toBe(kind);
    expect(statusPollInterval(as("UserVisible"))).toBe(ACTIVE_POLL_MS);
    expect(statusPollInterval(as("Background"))).toBe(IDLE_POLL_MS);
  });
});

describe("UnrecognisedState (status v2)", () => {
  it("raises no issue, no reconnect and no failure copy on a Degraded account", () => {
    const s = status({ pipelineStage: "Degraded", degradedReason: "UnrecognisedState" });
    expect(healthIssues(s, false)).toEqual([]);
    expect(pill(s)).toMatchObject({ mode: "health", tone: "ok", reconnect: false, label: "Synced 3m ago" });
  });
});

describe("operator alerts (WS-6 `alerts`, Founder tier only)", () => {
  const alert = (overrides: Partial<OperatorAlert> = {}): OperatorAlert => ({
    code: "job_failures",
    since: "2026-10-24T11:00:00Z",
    raisedAt: "2026-10-24T11:20:00Z",
    reasonCode: null,
    provider: null,
    count: 7,
    threshold: 5,
    kinds: [
      { kind: "DeltaSync", count: 4 },
      { kind: "CrossReference", count: 2 },
      { kind: "BuildFeed", count: 1 },
      { kind: "SeedCatalog", count: 1 },
    ],
    ...overrides,
  });

  it("job failures across Reel are a warning naming the worst kinds in fixed copy", () => {
    const view = pill(status({ alerts: [alert()] }))!;
    expect(view).toMatchObject({ mode: "health", tone: "warn", label: "Jobs failing across Reel", reconnect: false });
    expect(view.lines[0]).toBe(
      "7 job attempts failed in the last hour, across every account (the alert fires at 5). Most: Trakt sync 4, Cross-reference 2, Picks build 1, and more."
    );
  });

  it("a refused provider is danger, says which and since when, and outranks a sync the user started", () => {
    const view = pill(
      status({
        activeJob: job(),
        alerts: [alert({ code: "provider_refused", provider: "Tmdb", reasonCode: "unauthorized", since: "2026-10-24T11:50:00Z", count: null, threshold: null, kinds: null })],
      })
    )!;
    expect(view).toMatchObject({ mode: "health", tone: "danger", label: "TMDB refusing Reel" });
    expect(view.lines[0]).toBe(
      "A provider rejected Reel's credentials (401). Reel stopped calling TMDB 10m ago for every account. It tries one call every 15 minutes and resumes when the key or the account is fixed."
    );
  });

  it("is not repeated when the provider-health breaker already reports that provider down", () => {
    const s = status({
      providerHealth: {
        state: "Down",
        checkedAt: null,
        breakers: [{ provider: "OpenRouter", purpose: null, state: "Open", reasonCode: "payment_required", since: "", retryAt: null }],
      },
      alerts: [alert({ code: "provider_refused", provider: "OpenRouter", reasonCode: "payment_required", count: null, threshold: null, kinds: null })],
    });
    expect(healthIssues(s, false).map((i) => i.label)).toEqual(["Model provider down"]);
  });

  it("names the model provider without its brand when no breaker covers it", () => {
    const s = status({ alerts: [alert({ code: "provider_refused", provider: "OpenRouter", reasonCode: "payment_required", kinds: null })] });
    expect(pill(s)?.label).toBe("Model provider refusing Reel");
  });

  it("an unknown code, an unknown job kind and missing counts read as generic copy, never as identifiers", () => {
    const issues = healthIssues(
      status({
        alerts: [
          alert({ code: "disk_full" }),
          alert({ count: null, threshold: null, kinds: [{ kind: "QuantumDefrag", count: 3 }] }),
        ],
      }),
      false
    );
    expect(issues.map((i) => [i.tone, i.label])).toEqual([
      ["warn", "Operator alert"],
      ["warn", "Jobs failing across Reel"],
    ]);
    expect(issues[1].detail).toBe("Job attempts are failing across every account. Most: A background job 3.");
    expect(JSON.stringify(issues)).not.toMatch(/disk_full|QuantumDefrag/);
  });

  describe("llm_alarm, by purpose (R1-10)", () => {
    const llm = (overrides: Partial<OperatorAlert> = {}) =>
      alert({ code: "llm_alarm", reasonCode: "length", provider: "OpenRouter", count: 50, threshold: null, kinds: null, purpose: "rerank-feed", ...overrides });
    const health = (purpose: string, windowCalls: number, lengthRate: number, fallbackRate: number): LlmPurposeHealth => ({
      purpose,
      calls: 400,
      answered: 390,
      lengthCalls: 9,
      truncated: 1,
      fallbacks: 3,
      retries: 12,
      stops: 0,
      outcomes: { answered: 390 },
      windowCalls,
      windowLengthRate: lengthRate,
      windowFallbackRate: fallbackRate,
      windowRetryRate: 0.1,
      alarming: true,
    });

    it("names the feature and the exact count from llmHealth, as a warning", () => {
      const view = pill(status({ alerts: [llm()], llmHealth: [health("lens", 50, 0.5, 0.5), health("rerank-feed", 50, 0.06, 0)] }))!;
      expect(view).toMatchObject({ mode: "health", tone: "warn", label: "Model replies cut off", reconnect: false });
      expect(view.lines[0]).toBe(
        "Ranking your picks: 3 of its last 50 model calls ran into the reply-length limit, across every account (the alarm line is 2%)."
      );
    });

    it("reads the fallback rate for a fallback alarm, not the length rate", () => {
      const issues = healthIssues(
        status({ alerts: [llm({ reasonCode: "fallback", purpose: "ask-plan" })], llmHealth: [health("ask-plan", 50, 0.5, 0.04)] }),
        false
      );
      expect(issues.map((i) => [i.label, i.detail])).toEqual([
        ["Model calls falling back", "Ask Reel: 2 of its last 50 model calls returned no usable answer and fell back, across every account (the alarm line is 2%)."],
      ]);
    });

    it("without llmHealth for the purpose it states the line crossed, never a made-up count", () => {
      for (const llmHealth of [undefined, [], [health("rerank-feed", 0, 0, 0)], [health("lens", 50, 0.1, 0)]]) {
        const [issue] = healthIssues(status({ alerts: [llm()], llmHealth }), false);
        expect(issue.detail).toBe(
          "Ranking your picks: more than 2% of its last 50 model calls ran into the reply-length limit, across every account (the alarm line is 2%)."
        );
      }
    });

    it("an unknown purpose, an unknown rate and a missing count read as generic copy, never as identifiers", () => {
      const [issue] = healthIssues(status({ alerts: [llm({ purpose: "probe-quantum", reasonCode: "latency", count: null })] }), false);
      expect(issue).toMatchObject({ tone: "warn", label: "Model calls misbehaving" });
      expect(issue.detail).toBe("Some model features: its recent model calls are above the 2% alarm line, across every account.");
      expect(JSON.stringify(issue)).not.toMatch(/probe-quantum|latency/);
    });

    it("is never the 'can't name' alert, and two purposes the user calls by one name show once", () => {
      const issues = healthIssues(
        status({ alerts: [llm({ purpose: "search-agent" }), llm({ purpose: "rerank-ask" }), llm({ purpose: "lens" })] }),
        false
      );
      expect(issues.map((i) => i.label)).toEqual(["Model replies cut off", "Model replies cut off"]);
      expect(issues.map((i) => i.detail.split(":")[0])).toEqual(["Ask Reel", "Lenses"]);
      expect(JSON.stringify(issues)).not.toMatch(/can't name/);
    });

    it("never outranks a sync the user started", () => {
      expect(pill(status({ activeJob: job(), alerts: [llm()] }))).toMatchObject({ mode: "active", tone: "active" });
    });
  });

  it("no alerts, an empty list or a v1 payload without the field raise nothing", () => {
    expect(healthIssues(status({ alerts: [] }), false)).toEqual([]);
    expect(healthIssues(status(), false)).toEqual([]);
  });
});
