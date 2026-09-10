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

export const EMPTY_SYNC = {
  pipelineStage: "Ingesting",
  connectionStatus: "Active",
  lastDeltaSyncAt: null,
  lastFullReconcileAt: null,
  activeJob: null,
  recentJobs: [],
  outboxPending: 0,
  outboxDeadLetters: 0,
};

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

    window.fetch = async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = typeof input === "string" ? input : input instanceof Request ? input.url : String(input);
      if (!url.includes("/api/search/ask")) return realFetch(input, init);
      try {
        requests.push(JSON.parse((init?.body as string) ?? "{}"));
      } catch {
        requests.push(null);
      }
      const entry = { controller: null as unknown as ReadableStreamDefaultController<Uint8Array>, open: true };
      const stream = new ReadableStream<Uint8Array>({
        start(controller) {
          entry.controller = controller;
          streams.push(entry);
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
      push(event: string, data: unknown) {
        const live = streams.filter((s) => s.open).at(-1);
        live?.controller.enqueue(encoder.encode(`event: ${event}\ndata: ${JSON.stringify(data)}\n\n`));
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
  push: (event: string, data: unknown) => void;
  end: () => void;
  requests: unknown[];
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
