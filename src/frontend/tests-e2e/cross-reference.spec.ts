import { test, expect, type Page } from "@playwright/test";
import { SESSION_FOUNDER, mockJson, stubEventSource } from "./helpers";

/**
 * The cross-reference pass, as the header pill shows it.
 *
 * A pass over the whole eligible pool is the one background job that can legitimately run for ten
 * minutes, and a percentage alone reads as a stall for nine of them. So it publishes `current` and
 * `total` and the pill renders the count — and it is deliberately NOT a background kind, so a
 * five-second Trakt poll cannot take the pill away from it mid-run and report the least interesting
 * thing on screen.
 *
 * What is asserted here is only what a browser can show: the counting pill, the hand-back on
 * completion, and the poll that must not interrupt it. The copy itself is unit-tested in
 * `src/lib/syncCopy.test.ts`; the numbers come from the server and are pinned in
 * `CrossReferenceJobTests`.
 */

const GENERATED_AT = "2026-09-10T18:30:00.000Z";

const card = (n: number) => ({
  titleId: `00000000-0000-0000-0000-00000000000${n}`,
  mediaType: "Movie",
  tmdbId: 2000 + n,
  name: `Pick ${n}`,
  year: 2026,
  runtimeMinutes: 110,
  posterPath: null,
  backdropPath: null,
  genres: ["drama"],
  predictedRating: 8.1,
  crowdRating: 7.5,
  crowdVotes: 9000,
  whyThis: "Explores a heist crew coming apart, like Heat.",
  isReleased: true,
});

const SYNC_IDLE = {
  pipelineStage: "FeedReady",
  connectionStatus: "Active",
  lastDeltaSyncAt: "2026-09-10T18:25:00.000Z",
  lastFullReconcileAt: null,
  activeJob: null,
  recentJobs: [],
  outboxPending: 0,
  outboxDeadLetters: 0,
};

/** The status endpoint while the pass is running — the reload-seed path for the same pill. */
const SYNC_RUNNING_XREF = {
  ...SYNC_IDLE,
  activeJob: {
    kind: "CrossReference",
    // Status contract v2 (WS-6): the pass is a named pass, which the pill shows by name only.
    sseKind: "xref",
    visibility: "NamedPass",
    status: "Running",
    progressPct: 12,
    progressMessage: "Cross-referencing 2,242 titles · round 1 of 3 · 0 ruled out so far",
    enqueuedAt: "2026-09-10T18:26:00.000Z",
    completedAt: null,
  },
};

/** One `job-progress` frame exactly as `CrossReferenceJobHandler.ReportProgress` publishes it. */
const XREF_PROGRESS = {
  kind: "xref",
  pct: 29,
  message: "Cross-referencing 2,242 titles · round 1 of 3 · 640 ruled out so far",
  current: 640,
  total: 2242,
};

/**
 * Boots Home with a mutable `/api/sync/status`. Mutable because the poll is authoritative: the
 * pill hands back when the STATUS says the job is over, not when a frame says so, and a fixture
 * that answered "still running" for ever would assert the opposite of the contract.
 */
async function bootHome(
  page: Page,
  initial: unknown = SYNC_IDLE,
  band: unknown = null
): Promise<(next: unknown) => void> {
  let status = initial;
  await stubEventSource(page);
  await mockJson(page, "**/api/auth/me", SESSION_FOUNDER);
  await page.route(
    (url) => url.pathname === "/api/sync/status",
    (route) =>
      route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify(status) })
  );
  await mockJson(page, "**/api/feed/continue-watching", []);
  await mockJson(page, "**/api/feed/more", { items: [], exhausted: true });
  await mockJson(page, "**/api/feed/genres", { genres: [] });
  await mockJson(page, "**/api/digest/current", { weekStart: null, generatedAt: null, items: [] });
  await page.route(
    (url) => url.pathname === "/api/feed/learned",
    (route) => route.fulfill({ status: 204, body: "" })
  );
  await page.route(
    (url) => url.pathname === "/api/feed/cross-reference",
    (route) =>
      band === null
        ? route.fulfill({ status: 204, body: "" })
        : route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify(band) })
  );
  await mockJson(page, "**/api/feed", {
    generatedAt: GENERATED_AT,
    hero: [card(1)],
    rows: [
      {
        kind: "because-you-loved",
        anchorTitleId: card(1).titleId,
        anchorName: "Anchor Film",
        title: null,
        items: [card(2), card(3)],
      },
    ],
    lockedRowCount: 0,
  });

  await page.goto("/home");
  await expect(page.getByTestId("rec-card")).toHaveCount(2);
  return (next: unknown) => {
    status = next;
  };
}

function emit(page: Page, type: string, data: Record<string, unknown>) {
  return page.evaluate(
    ([eventType, payload]) => {
      (window as unknown as { __sse: { emit: (t: string, d: Record<string, unknown>) => void } }).__sse.emit(
        eventType as string,
        payload as Record<string, unknown>
      );
    },
    [type, data] as const
  );
}

/** The band's payload while a pass is working through the pool. */
const BAND_RUNNING = {
  running: true,
  judged: 640,
  total: 2242,
  kept: 210,
  ruledOut: 0,
  unsure: 430,
  noSignal: 402,
  gating: "demote-only",
};

/** …and after it finishes, with the founder's real demote-only gating decision. */
const BAND_LANDED = { ...BAND_RUNNING, running: false, judged: 2242, ruledOut: 640, unsure: 1392 };

/** The prompt-v2 backtest on the founder's own holdout, 2026-09-11, five split origins pooled. */
const ACCURACY_WITH_XREF = {
  window: "90d",
  from: "2026-06-12T00:00:00Z",
  to: "2026-09-11T00:00:00Z",
  source: "exposures",
  headline: {
    withinOnePoint: 0.26,
    n: 116,
    meanAbsoluteError: 1.94,
    bias: 0.37,
    correlation: -0.004,
    meanPredicted: 6.81,
    meanActual: 5.78,
  },
  calibration: [],
  moments: [],
  conversion: {
    shown: 470,
    saved: 104,
    watched: 73,
    rated: 73,
    shownToSaved: 0.2213,
    savedToWatched: 0.7019,
    shownToRated: 0.1553,
    medianDaysToWatch: 11,
  },
  counterfactual: [],
  learnedThisWeek: null,
  crossReference: {
    minimumOutcomes: 20,
    online: [
      { verdict: "ProvisionalKeep", n: 31, meanLaterRating: 7.1, lovedRate: 0.35, dislikedRate: 0.1, answerable: true },
      { verdict: "Out", n: 4, meanLaterRating: 5.5, lovedRate: 0.25, dislikedRate: 0.5, answerable: false },
    ],
    backtests: [
      {
        promptVersion: 2,
        evaluatedAt: "2026-09-11T02:14:00Z",
        judged: 1194,
        auc: 0.655,
        meanLaterRatingKeep: 6.83,
        meanLaterRatingOut: 6.16,
        keepLovedRate: 0.332,
        keepDislikedRate: 0.081,
        falseRuleOutRate: 0.162,
        falseRuleOutLow: 0.123,
        falseRuleOutHigh: 0.21,
        trueRuleOutRate: 0.228,
        unsureRate: 0.46,
        gating: "demote-only",
        caveat: "",
        current: true,
      },
    ],
  },
};

test.describe("cross-reference pass", () => {
  test("the pill names the pass from the status poll and hands back on completion", async ({ page }) => {
    // Seeded from the status poll — a reload mid-pass must show the pill. Since pill v2 (WS-9,
    // H-1/O+1) the named pass reads by name only: no percentage, no count and never the handler's
    // free text, because nobody started it and a ten-minute bar reads as a stall. The count lives on
    // the band below (next test). shell-status.spec.ts pins the no-number rule itself.
    const setStatus = await bootHome(page, SYNC_RUNNING_XREF);

    const pill = page.getByTestId("sync-pill");
    await expect(page.getByTestId("sync-pill-label")).toHaveText("Cross-referencing · first pass");

    await emit(page, "job-progress", XREF_PROGRESS);
    await expect(page.getByTestId("sync-pill-label")).toHaveText("Cross-referencing · first pass");
    await expect(pill).not.toContainText("ruled out so far");

    // The pass finishes. The status poll is authoritative and now reports no active job, so the
    // pill hands back to the idle health chip rather than sticking for ever.
    setStatus(SYNC_IDLE);
    await emit(page, "job-completed", {
      kind: "xref",
      judged: 2242,
      ruledOut: 640,
      kept: 210,
      unsure: 1392,
      noEmbedding: 402,
      picksChanged: 0,
      gating: "demote-only",
    });

    await expect(page.getByTestId("sync-pill")).toHaveCount(0);
    await expect(page.getByTestId("sync-health-label")).toHaveText(/^Synced/);
  });

  test("a Trakt poll cannot steal the pill from a run in progress", async ({ page }) => {
    await bootHome(page, SYNC_RUNNING_XREF);
    await emit(page, "job-progress", XREF_PROGRESS);

    await expect(page.getByTestId("sync-pill-label")).toHaveText("Cross-referencing · first pass");

    // A delta sync fires every few minutes and would otherwise overwrite a ten-minute pass with a
    // five-second one — the founder's stated preference, asserted rather than left as an accident.
    await emit(page, "job-progress", {
      kind: "delta",
      pct: 50,
      message: "Syncing with Trakt",
    });

    await expect(page.getByTestId("sync-pill-label")).toHaveText("Cross-referencing · first pass");
  });

  test("the band counts through the pool while the pass runs", async ({ page }) => {
    await bootHome(page, SYNC_RUNNING_XREF, BAND_RUNNING);

    // A count, not a percentage: a pass over the whole eligible pool can legitimately take ten
    // minutes, and a bar reads as a stall for nine of them.
    await expect(page.getByTestId("cross-reference-sentence")).toHaveText(
      "Deep pass running · 640 of 2,242 cross-referenced"
    );
  });

  test("the band reports what it found and never implies a demotion", async ({ page }) => {
    await bootHome(page, SYNC_IDLE, BAND_LANDED);

    const band = page.getByTestId("cross-reference-band");
    await expect(band).toContainText("Cross-referenced 2,242 titles against your history");
    await expect(band).toContainText("210 kept, nudged up a little");
    // The whole point of the band's licence to sit on the front page: it says, in words, that the
    // 640 rule-outs did nothing — because on the founder's own history the out verdict caught 16%
    // of the titles he went on to love.
    await expect(band).toContainText("rule-outs change nothing yet");
    await expect(band).not.toContainText("hidden");
    // And the coverage it could not reach, rather than a headline that quietly implies the pool
    // was whole.
    await expect(page.getByTestId("cross-reference-coverage")).toContainText("402 titles had nothing to reason from");
  });

  test("the band renders nothing at all when the server has nothing honest to say", async ({ page }) => {
    // 204 → null. A band reading "cross-referenced 0 titles" is the product padding.
    await bootHome(page, SYNC_IDLE, null);

    await expect(page.getByTestId("cross-reference-band")).toHaveCount(0);
  });

  test("the track record shows the measurement that decided what a verdict may do", async ({ page }) => {
    await stubEventSource(page);
    await mockJson(page, "**/api/auth/me", SESSION_FOUNDER);
    await mockJson(page, "**/api/sync/status", SYNC_IDLE);
    await mockJson(page, "**/api/accuracy", ACCURACY_WITH_XREF);
    await page.goto("/track-record");

    const panel = page.getByTestId("cross-reference");
    await expect(panel).toBeVisible();

    // Not one figure on this panel is typed into the page — every one of them is the account's own
    // measurement, arriving from the API.
    await expect(page.getByTestId("cross-reference-backtest")).toContainText(
      "titles it kept went on to average 6.83; titles it ruled out averaged 6.16"
    );
    await expect(page.getByTestId("cross-reference-backtest")).toContainText(
      "caught 16% of the titles you went on to love"
    );
    await expect(panel).toContainText("95% confidence 12.3–21.0%");
    await expect(panel).toContainText("this is the version running now");

    // The online half answers where it can and refuses where it cannot — with the count, so the
    // refusal is checkable rather than mysterious.
    const rows = page.getByTestId("cross-reference-online-row");
    await expect(rows.filter({ hasText: "kept:" })).toContainText("31 rated since · averaging 7.10");
    await expect(rows.filter({ hasText: "ruled out:" })).toContainText("not yet answerable — 4 outcomes so far");
  });
});
