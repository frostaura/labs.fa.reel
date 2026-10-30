import AxeBuilder from "@axe-core/playwright";
import type { Page } from "@playwright/test";

export const SESSION_FOUNDER = {
  accountId: "00000000-0000-0000-0000-000000000001",
  displayName: "Test Founder",
  traktSlug: "test-founder",
  avatarUrl: null,
  region: "US",
  tier: "Founder",
  pipelineStage: "FeedReady",
  onboarded: true,
  pinConfigured: false,
};

/**
 * A complete `/api/sync/status` v2 payload (Application/Pipeline/SyncStatusContract.cs), idle and
 * healthy unless overridden. Prefer this over hand-built partial payloads: a fixture missing the v2
 * fields exercises a shape the backend no longer sends.
 */
export function syncStatus(overrides: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    contractVersion: 2,
    pipelineStage: "FeedReady",
    connectionStatus: "Active",
    lastDeltaSyncAt: null,
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

/** One job as `activeJob` / `recentJobs` carry it — a user-started Trakt sync unless overridden. */
export function syncJob(overrides: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    id: "00000000-0000-0000-0000-00000000a001",
    kind: "DeltaSync",
    sseKind: "delta",
    visibility: "UserVisible",
    status: "Running",
    progressPct: 20,
    progressMessage: null,
    enqueuedAt: "2026-10-24T10:00:00Z",
    startedAt: "2026-10-24T10:00:01Z",
    completedAt: null,
    attempt: 1,
    maxAttempts: 3,
    retrying: false,
    retryReasonCode: null,
    ...overrides,
  };
}

/**
 * Mocks a GET-ish endpoint with a JSON body.
 *
 * String patterns of the form "**" + "/api/..." are matched against the URL *pathname*, so
 * requests carrying a query string ("/api/search/typeahead?q=x") still intercept. Playwright's
 * glob matching runs against the full URL including the query, so a bare glob ending at
 * ".../typeahead" silently misses "?q=" requests — the exact trap that broke the mobile
 * typeahead mock. Path matching is exact (no prefix creep): a "/api/feed" pattern never
 * swallows "/api/feed/continue-watching". Pass a RegExp for anything fancier.
 */
export async function mockJson(page: Page, url: string | RegExp, body: unknown, status = 200): Promise<void> {
  const matcher =
    typeof url === "string" && url.startsWith("**/")
      ? (u: URL) => u.pathname === url.slice(2)
      : url;
  await page.route(matcher, (route) =>
    route.fulfill({ status, contentType: "application/json", body: JSON.stringify(body) })
  );
}

/**
 * Replaces EventSource before any app code runs. Tests drive named events through
 * window.__sse.emit(type, data); instances auto-register on a global list.
 */
export async function stubEventSource(page: Page): Promise<void> {
  await page.addInitScript(() => {
    type Listener = (ev: MessageEvent) => void;
    const sources: { listeners: Map<string, Listener[]> }[] = [];

    class FakeEventSource {
      listeners = new Map<string, Listener[]>();
      onerror: ((ev: Event) => void) | null = null;
      constructor(_url: string) {
        sources.push(this);
      }
      addEventListener(type: string, listener: Listener) {
        const list = this.listeners.get(type) ?? [];
        list.push(listener);
        this.listeners.set(type, list);
      }
      close() {
        const index = sources.indexOf(this);
        if (index >= 0) sources.splice(index, 1);
      }
    }

    let seq = 0;
    (window as unknown as Record<string, unknown>).__sse = {
      emit(type: string, data: Record<string, unknown>) {
        seq += 1;
        const payload = JSON.stringify({ seq, ...data });
        for (const source of sources) {
          for (const listener of source.listeners.get(type) ?? []) {
            listener(new MessageEvent(type, { data: payload }));
          }
        }
      },
      count() {
        return sources.length;
      },
    };
    (window as unknown as Record<string, unknown>).EventSource = FakeEventSource;
  });
}

/**
 * Scripted stand-in for the Ask Reel POST-SSE stream (/api/search/ask). route.fulfill can only
 * hand back a complete body, but the specs need frame-by-frame control — "no reorder on a
 * candidate-scored frame" is only observable BETWEEN frames — so this patches window.fetch and
 * serves a ReadableStream the test drives via askPush/askEnd. Request bodies are recorded on
 * window.__ask.requests so specs can assert what each turn actually sent.
 */
export async function stubAskReel(page: Page): Promise<void> {
  await page.addInitScript(() => {
    const streams: { controller: ReadableStreamDefaultController<Uint8Array>; open: boolean }[] = [];
    const requests: unknown[] = [];
    const encoder = new TextEncoder();
    const realFetch = window.fetch.bind(window);
    // Every AbortController the app makes, so a spec can cut the page's OWN request from outside —
    // a dropped connection, a proxy, a stray cleanup — with no Stop pressed and nothing mocked
    // into failing. A transparent subclass: it adds a push and changes no behaviour.
    const controllers: AbortController[] = [];
    const RealAbortController = window.AbortController;
    class RecordingAbortController extends RealAbortController {
      constructor() {
        super();
        controllers.push(this);
      }
    }
    window.AbortController = RecordingAbortController as unknown as typeof AbortController;
    // The signals of the /ask requests, newest last — how `abortLive` finds the right controller
    // among the several dozen RTK Query makes for an ordinary page load.
    const askSignals: AbortSignal[] = [];
    // The next N /ask calls answer with this status and code instead of a stream. Queued rather
    // than latched, because the states worth testing are transitions: a 409 followed by the real
    // answer is the whole of "wait, then check again".
    const failures: { status: number; code: string }[] = [];

    window.fetch = async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = typeof input === "string" ? input : input instanceof Request ? input.url : String(input);
      if (!url.includes("/api/search/ask")) return realFetch(input, init);
      // The signal is honoured, not merely accepted. A stub that ignores it cannot fail when the
      // page aborts its own request — which is precisely the defect the Home → /search hand-off
      // shipped with: one `POST /api/search/ask` killed by a StrictMode cleanup, and every e2e
      // spec passing over it because the stub kept streaming into a request the browser had cut.
      const signal = init?.signal ?? null;
      if (signal) askSignals.push(signal);
      if (signal?.aborted) throw new DOMException("The user aborted a request.", "AbortError");
      try {
        requests.push(JSON.parse((init?.body as string) ?? "{}"));
      } catch {
        requests.push(null);
      }
      const failure = failures.shift();
      if (failure) {
        // The house error shape — `{ error, code }` — because the client switches on `code` and a
        // bare status would let a test pass against a client that only reads the number.
        return new Response(JSON.stringify({ error: "…", code: failure.code }), {
          status: failure.status,
          headers: { "Content-Type": "application/json" },
        });
      }
      const entry = { controller: null as unknown as ReadableStreamDefaultController<Uint8Array>, open: true };
      const stream = new ReadableStream<Uint8Array>({
        start(controller) {
          entry.controller = controller;
          streams.push(entry);
          // An abort errors the body exactly as the browser does, so the pending `reader.read()`
          // rejects with an AbortError and the client's own catch runs.
          signal?.addEventListener("abort", () => {
            entry.open = false;
            try {
              controller.error(new DOMException("The user aborted a request.", "AbortError"));
            } catch {
              // Already closed or errored — nothing left to cut.
            }
          }, { once: true });
        },
        cancel() {
          entry.open = false;
        },
      });
      return new Response(stream, { status: 200, headers: { "Content-Type": "text/event-stream" } });
    };

    (window as unknown as Record<string, unknown>).__ask = {
      requests,
      count() {
        return streams.filter((s) => s.open).length;
      },
      failNext(status: number, code: string) {
        failures.push({ status, code });
      },
      abortLive() {
        const signal = askSignals.at(-1);
        const owner = controllers.find((c) => c.signal === signal);
        if (!owner || owner.signal.aborted) return false;
        owner.abort();
        return true;
      },
      push(event: string, data: unknown) {
        const live = streams.filter((s) => s.open).at(-1);
        // `id:` first on every frame, exactly as the server writes it. A parser that treats an
        // unrecognised field line as data drops the whole frame, and a stub that omits the line
        // would never catch that — which is how a client can pass every test and then see nothing
        // at all against the real endpoint.
        live?.controller.enqueue(
          encoder.encode(`id: ${streams.length}\nevent: ${event}\ndata: ${JSON.stringify(data)}\n\n`),
        );
      },
      keepalive() {
        const live = streams.filter((s) => s.open).at(-1);
        live?.controller.enqueue(encoder.encode(":keepalive\n\n"));
      },
      end() {
        const live = streams.filter((s) => s.open).at(-1);
        if (live) {
          live.open = false;
          try {
            live.controller.close();
          } catch {
            // Reader already cancelled the stream — nothing left to close.
          }
        }
      },
    };
  });
}

interface AskStub {
  count: () => number;
  abortLive: () => boolean;
  push: (event: string, data: unknown) => void;
  keepalive: () => void;
  failNext: (status: number, code: string) => void;
  end: () => void;
  requests: unknown[];
}

/**
 * Makes the next Ask Reel request answer with a status instead of a stream — a 409
 * `turn_in_progress`, a 404 `conversation_not_found`, a 400. Queued, so a test can script "refused,
 * then answered" and watch the page move between the two.
 */
export async function askFailNext(page: Page, status: number, code: string): Promise<void> {
  await page.evaluate(
    ([s, c]) => (window as unknown as { __ask: AskStub }).__ask.failNext(s as number, c as string),
    [status, code] as const,
  );
}

/**
 * Cuts the newest live Ask Reel request at its own AbortController — the page's request dying for
 * a reason the page did not choose. Returns false when there was nothing live to cut, so a spec
 * cannot pass by aborting nothing.
 */
export async function askAbortLive(page: Page): Promise<boolean> {
  return page.evaluate(() => (window as unknown as { __ask: AskStub }).__ask.abortLive());
}

/** Emits a `:keepalive` comment frame — the thing a parser must skip without losing its place. */
export async function askKeepalive(page: Page): Promise<void> {
  await page.evaluate(() => (window as unknown as { __ask: AskStub }).__ask.keepalive());
}

/** Emits one SSE frame on the most recent live Ask Reel stream opened by {@link stubAskReel}. */
export async function askPush(page: Page, event: string, data: Record<string, unknown>): Promise<void> {
  await page.evaluate(
    ([e, d]) => (window as unknown as { __ask: AskStub }).__ask.push(e as string, d),
    [event, data] as const
  );
}

/**
 * Pushes a `conflict` frame — the server naming a collision between the question and the
 * account's own exclusions. Its own helper because the payload has four fields of which only
 * `message` is required, and a spec that spells the object out inline tends to send all four and
 * so never exercises the shape an older server actually sends.
 */
export async function askPushConflict(page: Page, message: string, extra: Record<string, unknown> = {}): Promise<void> {
  await askPush(page, "conflict", { message, ...extra });
}

/** Closes the most recent live Ask Reel stream (the server ending the response). */
export async function askEnd(page: Page): Promise<void> {
  await page.evaluate(() => (window as unknown as { __ask: AskStub }).__ask.end());
}

/** Waits until the app has opened an Ask Reel stream, then returns the recorded request bodies. */
export async function askRequests(page: Page): Promise<unknown[]> {
  return page.evaluate(() => (window as unknown as { __ask: AskStub }).__ask.requests);
}

/** Waits for at least `n` Ask Reel requests to have been issued. */
export async function waitForAsk(page: Page, n = 1): Promise<void> {
  await page.waitForFunction(
    (min) => ((window as unknown as { __ask?: AskStub }).__ask?.requests.length ?? 0) >= (min as number),
    n
  );
}

export async function emit(page: Page, type: string, data: Record<string, unknown>): Promise<void> {
  await page.evaluate(
    ([t, d]) => (window as unknown as { __sse: { emit: (a: string, b: unknown) => void } }).__sse.emit(t as string, d),
    [type, data] as const
  );
}

export async function waitForStream(page: Page): Promise<void> {
  await page.waitForFunction(
    () => (window as unknown as { __sse?: { count: () => number } }).__sse?.count() ?? 0 > 0
  );
}

/**
 * Asserts that something STAYS true for `ms`: `check` runs every 50 ms across the window and once at
 * its end, so a state that flips and flips back fails here, where a sleep followed by one check would
 * pass. Use it only for "nothing happens" assertions; a wait for something to happen is
 * `expect.poll` or a web-first assertion.
 */
export async function expectHolds(check: () => Promise<unknown> | unknown, ms: number): Promise<void> {
  const end = Date.now() + ms;
  do {
    await check();
    await new Promise((resolve) => setTimeout(resolve, 50));
  } while (Date.now() < end);
  await check();
}

/**
 * An axe scan that opens no page of its own (HREV5-PH-02). By default `AxeBuilder.analyze()` runs
 * `axe.runPartial` in each frame and then `axe.finishRun` in a NEW blank page, so every scan costs a
 * second page — and one WebKit browser can create only 63 (scripts/e2e/webkitShards.ts). The a11y
 * file's 32 axe cases created 64 pages, and the 65th — the keyboard-path test after them — hung in
 * `page.goto` every time. Legacy mode runs `axe.run` in the page itself; the only thing it gives up
 * is cross-origin frame contents (the YouTube trailer, which is not ours to fix), and the frame's
 * own title is still checked from the top document.
 */
export function axeScan(page: Page): AxeBuilder {
  return new AxeBuilder({ page }).setLegacyMode();
}
