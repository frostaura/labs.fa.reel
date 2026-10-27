// @vitest-environment jsdom
import { cleanup, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import SyncStatusPill from "./SyncStatusPill";
import { idleStatus, makeStore, renderWithStore, seedSyncStatus } from "../test/renderWithStore";
import { axeViolations } from "../test/axe";

/**
 * The pill as rendered (WS-9 task 11). The rules themselves are pinned in lib/syncPill.test.ts;
 * these pin what reaches the screen and the screen reader.
 */
beforeEach(() => {
  // Nothing in these tests may reach the network: the status is seeded into the cache.
  vi.stubGlobal("fetch", vi.fn(() => new Promise(() => {})));
});
afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

async function renderPill(status = idleStatus()) {
  const store = makeStore();
  await seedSyncStatus(store, status);
  return renderWithStore(<SyncStatusPill />, store);
}

describe("SyncStatusPill", () => {
  it("idle: a factual stamp, announced politely", async () => {
    await renderPill();
    expect(screen.getByRole("button", { name: /Synced 3m ago/ })).toBeTruthy();
    const region = screen.getByRole("status");
    expect(region.getAttribute("aria-live")).toBe("polite");
    expect(region.textContent).toBe("Sync status: Synced 3m ago");
  });

  it("a refresh-failed Trakt link outranks a running job and offers the reconnect", async () => {
    await renderPill(
      idleStatus({
        connectionStatus: "RefreshFailed",
        activeJob: {
          id: "00000000-0000-0000-0000-0000000000a1",
          kind: "FullIngest",
          sseKind: "ingest",
          visibility: "UserVisible",
          status: "Running",
          progressPct: 40,
          progressMessage: "free text that must never reach the header",
          enqueuedAt: new Date().toISOString(),
          startedAt: new Date().toISOString(),
          completedAt: null,
          attempt: 1,
          maxAttempts: 3,
          retrying: false,
          retryReasonCode: null,
        },
      })
    );
    expect(screen.getByRole("status").textContent).toBe("Sync status: Reconnect Trakt");
    expect(screen.getByTestId("sync-reconnect")).toBeTruthy();
    expect(document.body.textContent).not.toContain("free text that must never reach the header");
  });

  it("three failed cross-reference runs show the danger state", async () => {
    await renderPill(
      idleStatus({
        lastFailures: [
          {
            kind: "CrossReference",
            sseKind: "xref",
            visibility: "NamedPass",
            failedAt: new Date(Date.now() - 30 * 60_000).toISOString(),
            consecutiveFailures: 3,
            attempts: 3,
            reasonCode: "no_answer",
          },
        ],
      })
    );
    expect(screen.getByRole("status").textContent).toBe("Sync status: Cross-reference failed");
  });

  it("breaks no axe rule in its idle, reconnect and operator-alert states", async () => {
    for (const status of [
      idleStatus(),
      idleStatus({ connectionStatus: "RefreshFailed" }),
      idleStatus({
        alerts: [
          { code: "job_failures", since: new Date().toISOString(), raisedAt: new Date().toISOString(), reasonCode: null, provider: null, count: 6, threshold: 5, kinds: [{ kind: "DeltaSync", count: 6 }] },
        ],
      }),
    ]) {
      const { container, unmount } = await renderPill(status);
      expect(await axeViolations(container)).toEqual([]);
      unmount();
    }
  });

  it("shows an operator alert by its fixed label", async () => {
    await renderPill(
      idleStatus({
        alerts: [
          { code: "provider_refused", since: new Date().toISOString(), raisedAt: new Date().toISOString(), reasonCode: "payment_required", provider: "OpenRouter", count: null, threshold: null, kinds: null },
        ],
      })
    );
    expect(screen.getByRole("status").textContent).toBe("Sync status: Model provider refusing Reel");
  });
});
