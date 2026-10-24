import { expect, test, type Page } from "@playwright/test";
import { SESSION_FOUNDER, mockJson, stubEventSource, syncJob, syncStatus } from "./helpers";

/**
 * The header pill (WS-9 P0 task 1; H-1, H-17, I-1, I*1, H-16, I-12). Every fixture is a full v2
 * status payload, and every assertion is about what the pill SAYS — fixed copy, health before
 * progress, no denominator for the named pass, and no polling from a hidden tab.
 */

/** Boots Home with a mutable status answer; returns a setter and a poll counter. */
async function bootShell(page: Page, initial: Record<string, unknown>) {
  let status = initial;
  let polls = 0;
  let others = 0;
  await stubEventSource(page);
  await mockJson(page, "**/api/auth/me", SESSION_FOUNDER);
  await page.route(
    (url) => url.pathname === "/api/sync/status",
    (route) => {
      polls += 1;
      return route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify(status) });
    }
  );
  // Everything else the page asks for answers empty; this spec is about the header.
  await page.route(
    (url) => url.pathname.startsWith("/api/") && url.pathname !== "/api/sync/status" && url.pathname !== "/api/auth/me",
    (route) => {
      others += 1;
      return route.fulfill({ status: 204, body: "" });
    }
  );
  return {
    set(next: Record<string, unknown>) {
      status = next;
    },
    polls: () => polls,
    /** Every other /api request (feed, taste DNA, …) — a hidden tab should send none of these either. */
    others: () => others,
  };
}

const emit = (page: Page, type: string, data: Record<string, unknown>) =>
  page.evaluate(([t, d]) => (window as unknown as { __sse: { emit: (t: string, d: unknown) => void } }).__sse.emit(t, d), [
    type,
    data,
  ] as const);

test.describe("sync pill", () => {
  // The acceptance fixture is a running Enrich (background upkeep); the import the user started is
  // the harder case, since user-visible work otherwise owns the pill.
  for (const running of [
    syncJob({ kind: "Enrich", sseKind: "enrich", visibility: "Background", progressPct: 40 }),
    syncJob({ kind: "FullIngest", sseKind: "ingest", progressPct: 40 }),
  ]) {
    test(`a refresh-failed Trakt link outranks a running ${String(running.kind)} job and offers the reconnect`, async ({ page }) => {
      await bootShell(
        page,
        syncStatus({
          connectionStatus: "RefreshFailed",
          degradedReason: "TraktRefreshFailed",
          activeJob: running,
        })
      );
      let started = 0;
      await page.route("**/api/auth/trakt/start", (route) => {
        started += 1;
        return route.fulfill({
          status: 200,
          contentType: "application/json",
          body: JSON.stringify({ authorizeUrl: "/__trakt-authorize-stub", state: "s-1" }),
        });
      });
      await page.route("**/__trakt-authorize-stub", (route) =>
        route.fulfill({ status: 200, contentType: "text/html", body: "<title>Trakt</title>authorize" })
      );

      await page.goto("/home");
      const reconnect = page.getByTestId("sync-reconnect");
      await expect(reconnect).toHaveText("Reconnect Trakt");
      await expect(page.getByTestId("sync-pill")).toHaveCount(0);
      await expect(page.getByTestId("sync-announce")).toHaveText("Sync status: Reconnect Trakt");

      await reconnect.click();
      await expect(page).toHaveURL(/__trakt-authorize-stub/);
      expect(started).toBe(1);
    });
  }

  test("three failed cross-reference runs show the danger state with the reason", async ({ page }) => {
    await bootShell(
      page,
      syncStatus({
        lastFailures: [
          {
            kind: "CrossReference",
            sseKind: "xref",
            visibility: "NamedPass",
            failedAt: new Date(Date.now() - 20 * 60_000).toISOString(),
            consecutiveFailures: 3,
            attempts: 3,
            reasonCode: "no_answer",
          },
        ],
      })
    );
    await page.goto("/home");

    const chip = page.getByTestId("sync-health");
    await expect(chip).toHaveAttribute("data-health", "danger");
    await expect(page.getByTestId("sync-health-label")).toHaveText("Cross-reference failed");

    await chip.focus();
    const tooltip = page.getByRole("tooltip");
    await expect(tooltip).toContainText("The model returned no usable answer. Failed 3 times in a row, last 20m ago.");
  });

  test("the cross-reference pass is named, with no percentage and no count, and never quotes the job's text", async ({ page }) => {
    await bootShell(
      page,
      syncStatus({
        activeJob: syncJob({
          kind: "CrossReference",
          sseKind: "xref",
          visibility: "NamedPass",
          progressPct: null,
          progressMessage: "nothing cross-referenced this run",
        }),
      })
    );
    await page.goto("/home");
    await emit(page, "job-progress", { kind: "xref", pct: 29, current: 640, total: 2242, message: "round 1 of 3" });

    const pill = page.getByTestId("sync-pill");
    await expect(page.getByTestId("sync-pill-label")).toHaveText("Cross-referencing · first pass");
    await expect(pill).not.toContainText("%");
    await expect(pill).not.toContainText("nothing cross-referenced");
    await pill.focus();
    await expect(page.getByRole("tooltip")).not.toContainText("640 / 2,242");
  });

  test("a retrying job is a warning with its reason, not a stuck 100%", async ({ page }) => {
    await bootShell(
      page,
      syncStatus({
        activeJob: syncJob({ status: "Pending", progressPct: null, retrying: true, attempt: 2, retryReasonCode: "timed_out" }),
      })
    );
    await page.goto("/home");

    const pill = page.getByTestId("sync-pill");
    await expect(pill).toHaveAttribute("data-tone", "warn");
    await expect(page.getByTestId("sync-pill-label")).toHaveText("Retrying · Syncing with Trakt");
    await expect(pill).not.toContainText("%");
    await pill.focus();
    await expect(page.getByRole("tooltip")).toContainText("It timed out. Attempt 2 of 3.");
  });

  test("a late progress frame followed by idle polls clears within one poll (H-17)", async ({ page }) => {
    const shell = await bootShell(page, syncStatus({ activeJob: syncJob({ progressPct: 30 }) }));
    await page.goto("/home");
    await expect(page.getByTestId("sync-pill-label")).toHaveText("Syncing with Trakt");

    // The job ends on the server; one more frame arrives late, after the job is over.
    shell.set(syncStatus({ lastDeltaSyncAt: new Date().toISOString() }));
    await emit(page, "job-progress", { kind: "delta", pct: 100 });
    const pollsAtEnd = shell.polls();

    // User-visible work polls every 5 s; the next answer says idle and the pill must follow it.
    await expect(page.getByTestId("sync-pill")).toHaveCount(0, { timeout: 7_000 });
    await expect(page.getByTestId("sync-health-label")).toHaveText(/^Synced/);
    expect(shell.polls() - pollsAtEnd).toBeLessThanOrEqual(1);
  });

  test("a `failed` frame refetches status so the failure shows without waiting for the idle poll", async ({ page }) => {
    const shell = await bootShell(page, syncStatus({ lastDeltaSyncAt: new Date().toISOString() }));
    await page.goto("/home");
    await expect(page.getByTestId("sync-health")).toHaveAttribute("data-health", "ok");

    shell.set(
      syncStatus({
        lastFailures: [
          {
            kind: "BuildFeed",
            sseKind: "feed",
            visibility: "UserVisible",
            failedAt: new Date().toISOString(),
            consecutiveFailures: 1,
            attempts: 3,
            reasonCode: "error",
          },
        ],
      })
    );
    await emit(page, "failed", { error: "BuildFeed failed after 3 attempts." });

    await expect(page.getByTestId("sync-health")).toHaveAttribute("data-health", "warn");
    await expect(page.getByTestId("sync-health-label")).toHaveText("Picks build failed");
    await expect(page.getByTestId("sync-health")).not.toContainText("after 3 attempts");
  });
});

test.describe("status polling", () => {
  test("idle polls once a minute, and a hidden tab sends no requests for 10 minutes however busy the stream is", async ({ page }) => {
    await page.clock.install();
    const shell = await bootShell(page, syncStatus({ lastDeltaSyncAt: new Date().toISOString() }));
    await page.goto("/home");
    await expect(page.getByTestId("sync-health")).toBeVisible();

    // 60 s cadence while nothing the user started is running: two polls in two minutes, not 24.
    const afterLoad = shell.polls();
    for (let i = 0; i < 24; i += 1) {
      await page.clock.runFor(5_000);
      await page.waitForTimeout(25);
    }
    expect(shell.polls() - afterLoad).toBeGreaterThanOrEqual(1);
    expect(shell.polls() - afterLoad).toBeLessThanOrEqual(2);

    await page.evaluate(() => {
      Object.defineProperty(document, "visibilityState", { configurable: true, get: () => "hidden" });
      Object.defineProperty(document, "hidden", { configurable: true, get: () => true });
      document.dispatchEvent(new Event("visibilitychange"));
    });
    const hiddenAt = shell.polls();
    const othersAt = shell.others();
    // The live stream stays open while hidden and real handlers keep finishing: a frame a minute —
    // completions, a failure, a stream reconnect — must not turn into a single request.
    await emit(page, "open", {});
    for (let minute = 0; minute < 10; minute += 1) {
      await emit(page, minute % 3 === 2 ? "failed" : "job-completed", { kind: minute % 2 ? "feed" : "delta" });
      if (minute === 5) await emit(page, "open", {});
      await page.clock.runFor(60_000);
      await page.waitForTimeout(25);
    }
    expect(shell.polls() - hiddenAt).toBe(0);
    expect(shell.others() - othersAt).toBe(0);

    // Coming back with an answer older than the cadence asks at once.
    await page.evaluate(() => {
      Object.defineProperty(document, "visibilityState", { configurable: true, get: () => "visible" });
      Object.defineProperty(document, "hidden", { configurable: true, get: () => false });
      document.dispatchEvent(new Event("visibilitychange"));
    });
    await expect.poll(() => shell.polls()).toBe(hiddenAt + 1);
    // The held invalidations and the stale-answer refetch land as ONE status request, not two.
    await page.waitForTimeout(500);
    expect(shell.polls()).toBe(hiddenAt + 1);
  });

  test("work the user started polls every 5 s", async ({ page }) => {
    await page.clock.install();
    const shell = await bootShell(page, syncStatus({ activeJob: syncJob() }));
    await page.goto("/home");
    await expect(page.getByTestId("sync-pill")).toBeVisible();

    // Step the clock one interval at a time so each poll's request can complete before the next is
    // due (RTK skips a poll while the previous request is still in flight).
    const start = shell.polls();
    for (let i = 0; i < 6; i += 1) {
      await page.clock.runFor(5_000);
      await expect.poll(() => shell.polls()).toBeGreaterThanOrEqual(start + i + 1);
    }
  });
});
