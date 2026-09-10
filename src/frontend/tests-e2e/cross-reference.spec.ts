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
  whyThis: "Strong fit with your taste profile.",
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
async function bootHome(page: Page, initial: unknown = SYNC_IDLE): Promise<(next: unknown) => void> {
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

test.describe("cross-reference pass", () => {
  test("the pill counts current / total for the whole pass and hands back on completion", async ({ page }) => {
    // Seeded from the status poll first — a reload mid-pass must show the pill, and the JobKind →
    // SSE-kind mapping is what makes that render as the cross-reference explainer rather than the
    // generic "Background sync" fallback.
    const setStatus = await bootHome(page, SYNC_RUNNING_XREF);

    const pill = page.getByTestId("sync-pill");
    await expect(pill).toContainText("0 ruled out so far");

    await emit(page, "job-progress", XREF_PROGRESS);

    await expect(pill).toContainText("Cross-referencing 2,242 titles · round 1 of 3 · 640 ruled out so far");
    await expect(pill).toContainText("29%");

    // The count lives in the tooltip, which is what makes a ten-minute job legible: a percentage
    // says "still going", a count says how far through the pool it is.
    await pill.focus();
    const tooltip = page.getByRole("tooltip");
    await expect(tooltip).toContainText("Cross-referencing against your history");
    await expect(tooltip).toContainText("640 / 2,242");

    // The pass finishes. The status poll is authoritative and now reports no active job, so the
    // pill hands back to the idle health chip rather than sticking at 29% for ever.
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
    await bootHome(page);
    await emit(page, "job-progress", XREF_PROGRESS);

    const pill = page.getByTestId("sync-pill");
    await expect(pill).toContainText("Cross-referencing 2,242 titles");

    // A delta sync fires every few minutes and would otherwise overwrite a ten-minute pass with a
    // five-second one — the founder's stated preference, asserted rather than left as an accident.
    await emit(page, "job-progress", {
      kind: "delta",
      pct: 50,
      message: "Syncing with Trakt",
    });

    await expect(pill).toContainText("Cross-referencing 2,242 titles · round 1 of 3 · 640 ruled out so far");
  });
});
