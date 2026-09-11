import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { AskStreamError, streamAskReel, type AskCard, type AskEvent } from "./askReelStream";

/**
 * The SSE parser, driven the way the network drives it: a chunked ReadableStream, not a string.
 * Frame boundaries are a network artefact — the server writes frames, the transport hands over
 * arbitrary byte runs — so every bug this file pins (a frame split mid-JSON, a last frame with no
 * terminator, a keepalive comment, a malformed payload reaching the reducer as a typed value) is
 * only reachable through the real read loop. Testing `parseFrame` on tidy strings is what let all
 * four of them through.
 *
 * vitest runs in node here: no DOM, no jsdom. `fetch`, `Response`, `ReadableStream` and
 * `TextEncoder` are node globals, so the stub is a real Response over a real stream.
 */

const encoder = new TextEncoder();

/** A Response whose body delivers exactly these byte runs, one per `read()`. */
function streamed(chunks: string[], status = 200): Response {
  const body = new ReadableStream<Uint8Array>({
    start(controller) {
      for (const c of chunks) controller.enqueue(encoder.encode(c));
      controller.close();
    },
  });
  return new Response(body, { status, headers: { "Content-Type": "text/event-stream" } });
}

function frame(event: string, data: unknown): string {
  return `event: ${event}\ndata: ${JSON.stringify(data)}\n\n`;
}

function card(n: number, extra: Partial<AskCard> = {}): AskCard {
  return {
    titleId: `t${n}`, mediaType: "Movie", tmdbId: n, name: `Title ${n}`, year: 2020,
    posterPath: null, genres: [], similarity: 0.5, predictedRating: 7, ...extra,
  };
}

/** Runs the stream over `chunks` and returns every event the client actually surfaced. */
async function collect(chunks: string[], status = 200): Promise<AskEvent[]> {
  vi.stubGlobal("fetch", vi.fn(() => Promise.resolve(streamed(chunks, status))));
  const events: AskEvent[] = [];
  await streamAskReel({ query: "tense thriller" }, (ev) => events.push(ev), new AbortController().signal);
  return events;
}

beforeEach(() => {
  vi.unstubAllGlobals();
});

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe("streamAskReel", () => {
  it("parses_a_frame_split_across_two_chunks", async () => {
    // Fixture variation: the same frame is split at two different points — inside the JSON payload,
    // and between the `event:` and `data:` lines. Only the first is what a naive line-buffered
    // parser survives, so a test that split at the newline would have passed against the bug.
    const whole = frame("phase", { stage: "scoring", found: 4, scored: 2 });
    const midJson = [whole.slice(0, whole.indexOf("found") + 3), whole.slice(whole.indexOf("found") + 3)];
    const midHeader = [whole.slice(0, whole.indexOf("\ndata")), whole.slice(whole.indexOf("\ndata"))];

    for (const chunks of [midJson, midHeader]) {
      const events = await collect(chunks);
      expect(events).toEqual([{ event: "phase", data: { stage: "scoring", found: 4, scored: 2 } }]);
    }
  });

  it("flushes_a_trailing_frame_with_no_terminator", async () => {
    // The answer itself, written immediately before the response closes, arrives without its blank
    // line — and used to sit in the buffer forever while the UI kept saying it was thinking.
    // Fixture variation: with and without the terminating blank line. The terminated case is the
    // control, and it passed before this fix; only the unterminated one was red.
    const done = frame("done", { results: [card(1)], reason: "1 pick" });

    const terminated = await collect([done]);
    const unterminated = await collect([done.slice(0, -2)]);

    expect(terminated).toEqual(unterminated);
    expect(unterminated).toHaveLength(1);
    expect(unterminated[0].event).toBe("done");
  });

  it("ignores_a_keepalive_comment_frame", async () => {
    // Fixture variation: a bare `:` keepalive, and a commented one (`: ping`) — proxies emit both,
    // and neither carries data. A comment glued to a real frame must not damage that frame either.
    const events = await collect([":\n\n", ": ping\n\n", `: ping\n${frame("phase", { stage: "discovery", found: 0, scored: 0 })}`]);
    expect(events).toEqual([{ event: "phase", data: { stage: "discovery", found: 0, scored: 0 } }]);
  });

  it("rejects_a_done_frame_whose_results_are_not_an_array", async () => {
    // Fixture variation: `results` as an object, then as a well-formed array holding one malformed
    // card. The bare `as AskEvent` cast let both through as typed values, and the reducer's
    // `for (const c of results)` threw a TypeError three modules away from the frame that caused it.
    const objectResults = await collect([`event: done\ndata: ${JSON.stringify({ results: { "0": card(1) }, reason: "1 pick" })}\n\n`]);
    expect(objectResults).toEqual([]);

    const badCard = await collect([`event: done\ndata: ${JSON.stringify({ results: [{ name: "no id" }], reason: "1 pick" })}\n\n`]);
    expect(badCard).toEqual([]);

    // The control: the identical frame with a real card is still delivered.
    const good = await collect([frame("done", { results: [card(1)], reason: "1 pick" })]);
    expect(good).toHaveLength(1);
  });

  it("rejects_a_card_whose_year_or_tmdbId_is_the_wrong_type", async () => {
    // The grid renders `year` straight into a paragraph and `tmdbId` straight into the href, so a
    // frame that clears the guard with the wrong type for either one throws inside render rather
    // than here. Fixture variation, one field at a time: a stringified year (what a JSON encoder
    // that quotes numbers produces), an OBJECT year (what actually reached render and threw
    // "Objects are not valid as a React child"), and a stringified tmdbId — each on an otherwise
    // perfect card, so the assertion cannot pass for some other reason.
    for (const bad of [{ year: "1999" }, { year: { oops: true } }, { tmdbId: "550" }]) {
      const asCandidate = await collect([frame("candidate", { ...card(1), ...bad })]);
      expect(asCandidate).toEqual([]);
      // The same card inside a `done` takes the frame down with it — one bad card must not be
      // rendered, and `isDone` requires every result to pass.
      const asDone = await collect([frame("done", { results: [{ ...card(1), ...bad }], reason: "1 pick" })]);
      expect(asDone).toEqual([]);
    }

    // The controls. A null year is a real title with no release date, and a null tmdbId is a title
    // Reel knows about that TMDB does not — both must still reach the grid.
    expect(await collect([frame("candidate", card(1, { year: null }))])).toHaveLength(1);
    expect(await collect([frame("candidate", card(1, { tmdbId: null }))])).toHaveLength(1);
  });

  it("throws_an_AskStreamError_carrying_the_status_on_409", async () => {
    // Fixture variation: 409 (the same turn already streaming) and 500 (a real failure). The
    // client has to tell them apart — one keeps the grid, the other offers a retry — which a bare
    // `Error("ask failed: 409")` could only support by parsing its own message.
    for (const status of [409, 500]) {
      vi.stubGlobal("fetch", vi.fn(() => Promise.resolve(new Response("{}", { status }))));
      const err = await streamAskReel({ query: "q" }, () => {}, new AbortController().signal).catch((e: unknown) => e);
      expect(err).toBeInstanceOf(AskStreamError);
      expect((err as AskStreamError).status).toBe(status);
    }
  });

  it("parses_a_conflict_frame", async () => {
    // Fixture variation: the full payload, then the bare `message` an older server would send.
    // The optional counts must not be load-bearing, or a conflict note would silently vanish.
    const full = { message: "Horror is excluded on this account.", excludedGenres: ["Horror"], poolSize: 12, catalogueSize: 40_000 };
    expect(await collect([frame("conflict", full)])).toEqual([{ event: "conflict", data: full }]);

    expect(await collect([frame("conflict", { message: "Horror is excluded on this account." })])).toEqual([
      { event: "conflict", data: { message: "Horror is excluded on this account." } },
    ]);

    // A conflict with no message is not a conflict — it would render an empty note.
    expect(await collect([frame("conflict", { excludedGenres: ["Horror"] })])).toEqual([]);
  });

  it("retries_once_after_a_401_and_a_refreshed_session", async () => {
    // Fixture variation: a refresh that succeeds, then one that fails. The first must produce the
    // stream; the second must surface the 401 rather than retrying forever.
    for (const refreshOk of [true, false]) {
      const fetchMock = vi.fn((input: RequestInfo | URL) => {
        const url = String(input);
        if (url.includes("/auth/refresh")) return Promise.resolve(new Response(null, { status: refreshOk ? 204 : 401 }));
        if (fetchMock.mock.calls.length <= 1) return Promise.resolve(new Response("{}", { status: 401 }));
        return Promise.resolve(streamed([frame("done", { results: [], reason: "no fresh matches" })]));
      });
      vi.stubGlobal("fetch", fetchMock);

      const events: AskEvent[] = [];
      const err = await streamAskReel({ query: "q" }, (ev) => events.push(ev), new AbortController().signal).catch((e: unknown) => e);

      if (refreshOk) {
        expect(err).toBeUndefined();
        expect(events.map((e) => e.event)).toEqual(["done"]);
      } else {
        expect((err as AskStreamError).status).toBe(401);
        expect(events).toEqual([]);
      }
      // Ask, refresh, and at most one more ask — never a second refresh-and-retry loop.
      expect(fetchMock.mock.calls.length).toBeLessThanOrEqual(3);
    }
  });

  it("carries the below-floor tray and the discovery counts through a done frame", async () => {
    // Fixture variation: `belowFloor` present, then absent. Absent must not fail the guard —
    // older servers, and every stored turn written before the floor split existed, send neither.
    const withTray = await collect([
      frame("done", { results: [card(1)], reason: "1 pick", streamed: 40, kept: 1, droppedByFloor: 3, belowFloor: [card(2)] }),
    ]);
    expect(withTray[0].data).toMatchObject({ streamed: 40, kept: 1, droppedByFloor: 3 });
    expect((withTray[0].data as unknown as { belowFloor: AskCard[] }).belowFloor).toHaveLength(1);

    const without = await collect([frame("done", { results: [card(1)], reason: "1 pick" })]);
    expect(without).toHaveLength(1);
  });
});
