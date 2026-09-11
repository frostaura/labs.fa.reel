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

/** One exchange's idempotency key. Required on every request now — the server refuses without it. */
const TURN_ID = "44444444-4444-4444-4444-444444444444";

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
  await streamAskReel({ query: "tense thriller", clientTurnId: TURN_ID }, (ev) => events.push(ev), new AbortController().signal);
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
      const err = await streamAskReel({ query: "q", clientTurnId: TURN_ID }, () => {}, new AbortController().signal).catch((e: unknown) => e);
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
      const err = await streamAskReel({ query: "q", clientTurnId: TURN_ID }, (ev) => events.push(ev), new AbortController().signal).catch((e: unknown) => e);

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

/**
 * The turn record's wire (Phase 1). The server now owns what a conversation is: the client sends
 * one id for the exchange and nothing else about the past, and the stream says which turn it is
 * answering. Every test here is about a field the parser previously had no type for, so the
 * failure mode they close is silent — a field arrives, nothing reads it, and the UI carries on
 * describing the world it used to live in.
 */
describe("streamAskReel — the turn record", () => {
  /** Captures what the client actually PUT ON THE WIRE, which is the half `collect` throws away. */
  async function sentBody(request: Parameters<typeof streamAskReel>[0]): Promise<Record<string, unknown>> {
    let body: Record<string, unknown> = {};
    vi.stubGlobal("fetch", vi.fn((_input: RequestInfo | URL, init?: RequestInit) => {
      body = JSON.parse((init?.body as string) ?? "{}") as Record<string, unknown>;
      return Promise.resolve(streamed([frame("done", { results: [], reason: null })]));
    }));
    await streamAskReel(request, () => {}, new AbortController().signal);
    return body;
  }

  it("sends_the_turn_id_and_never_the_history_or_the_shown_set", async () => {
    // Fixture variation: the full request, then the minimum one. `history`, `shownTmdbIds` and
    // `continuation` were REMOVED, not renamed — a client that still sends them is sending a
    // transcript the server will not read, and the server's own record is the only one that counts.
    const full = await sentBody({
      query: "tense thriller",
      conversationId: "11111111-1111-1111-1111-111111111111",
      clientTurnId: "22222222-2222-2222-2222-222222222222",
      mediaTypes: ["movie"],
    });
    expect(full.clientTurnId).toBe("22222222-2222-2222-2222-222222222222");
    expect(full.conversationId).toBe("11111111-1111-1111-1111-111111111111");
    expect(Object.keys(full)).not.toContain("history");
    expect(Object.keys(full)).not.toContain("shownTmdbIds");
    expect(Object.keys(full)).not.toContain("continuation");

    const minimal = await sentBody({ query: "q", clientTurnId: "33333333-3333-3333-3333-333333333333" });
    expect(minimal.clientTurnId).toBe("33333333-3333-3333-3333-333333333333");
    expect(Object.keys(minimal)).not.toContain("history");
  });

  it("reads_the_turn_id_and_sequence_off_the_conversation_frame", async () => {
    // Fixture variation: the full frame the server sends now, and the bare `{id}` an older one
    // sent. Both must survive — the id is what the tab remembers, and losing the frame entirely
    // because two new fields are missing would cost the conversation, not just the reconnect path.
    const full = await collect([
      frame("conversation", { id: "c-1", turnId: "t-9", sequence: 3 }),
    ]);
    expect(full).toEqual([{ event: "conversation", data: { id: "c-1", turnId: "t-9", sequence: 3 } }]);

    const bare = await collect([frame("conversation", { id: "c-1" })]);
    expect(bare).toEqual([{ event: "conversation", data: { id: "c-1" } }]);

    // An id-less frame is not a conversation: remembering "" would send an empty id on the next turn.
    expect(await collect([frame("conversation", { turnId: "t-9", sequence: 3 })])).toEqual([]);
  });

  it("carries_replayed_removed_and_the_failure_code_through_a_done_frame", async () => {
    // The three things only a REPLAY can say. `removed` in particular is a number the user is owed:
    // cards that silently disappear between two views of the same answer are indistinguishable
    // from a bug. Fixture variation: a replayed success, then a replayed failure.
    const ok = await collect([
      frame("done", { results: [card(1)], reason: "1 pick", replayed: true, removed: 2 }),
    ]);
    expect(ok[0].data).toMatchObject({ replayed: true, removed: 2 });

    const failed = await collect([
      frame("done", { results: [], reason: null, replayed: true, removed: 0, failed: true, failureCode: "client_aborted" }),
    ]);
    expect(failed[0].data).toMatchObject({ failed: true, failureCode: "client_aborted" });
  });

  it("skips_id_lines_and_keepalive_comments_without_losing_the_frame", async () => {
    // The server writes `id:` first on every frame and a `:keepalive` comment every 10 seconds.
    // Fixture variation: the id line alone, and an id line whose frame is preceded by a keepalive
    // glued to it — a parser that treats an unknown field line as data drops the whole frame.
    const idOnly = await collect([`id: 7\n${frame("phase", { stage: "ranking", found: 3, scored: 3 })}`]);
    expect(idOnly).toEqual([{ event: "phase", data: { stage: "ranking", found: 3, scored: 3 } }]);

    const withKeepalive = await collect([
      ":keepalive\n\n",
      `id: 8\n${frame("done", { results: [card(1)], reason: "1 pick" })}`,
    ]);
    expect(withKeepalive.map((e) => e.event)).toEqual(["done"]);
  });

  it("carries_the_servers_stable_code_on_a_400_404_and_409", async () => {
    // The prose in `error` is the user's and may change; `code` is the client's and does not.
    // Fixture variation: the three codes the client acts on differently, plus a body that is not
    // JSON at all (a proxy's HTML error page) — which must still produce the status, not a throw.
    for (const [status, code] of [[400, "query_too_long"], [404, "conversation_not_found"], [409, "turn_in_progress"]] as const) {
      vi.stubGlobal("fetch", vi.fn(() => Promise.resolve(
        new Response(JSON.stringify({ error: "…", code }), { status }),
      )));
      const err = await streamAskReel({ query: "q", clientTurnId: "t" }, () => {}, new AbortController().signal)
        .catch((e: unknown) => e);
      expect(err).toBeInstanceOf(AskStreamError);
      expect((err as AskStreamError).status).toBe(status);
      expect((err as AskStreamError).code).toBe(code);
    }

    vi.stubGlobal("fetch", vi.fn(() => Promise.resolve(new Response("<html>502</html>", { status: 502 }))));
    const err = await streamAskReel({ query: "q", clientTurnId: "t" }, () => {}, new AbortController().signal)
      .catch((e: unknown) => e);
    expect((err as AskStreamError).status).toBe(502);
    expect((err as AskStreamError).code).toBeNull();
  });
});

/**
 * R1-05 — the malformed-frame guard closed `year` and `tmdbId` and left `why`.
 *
 * The commit that added the guard named the right rule — "the grid renders both directly, so those
 * are exactly the two the tolerant-on-purpose rule must not cover" — and then applied it to two of
 * the three fields that meet it. `ResultCard` renders `{card.why}` raw, one line below the `year`
 * it fixed, so `why: {t:1}` cleared validation and threw the identical "Objects are not valid as a
 * React child" in the identical component. `isReranked` DID type-check `why`, so the
 * `candidate-reranked` path was guarded and the `candidate` / `done` path was not.
 *
 * `outsideRequest` and `seen` are here for a different reason: they are not printed, they ROUTE.
 * A truthy non-boolean `outsideRequest` silently moves a card out of the recommended grid into the
 * "outside your request" fence, and `seen` decides whether a card gets quick actions or a pill. A
 * wrong type there produces a wrong screen rather than a crash, which is the harder one to notice.
 */
describe("isCard — the fields the grid renders and routes on", () => {
  it("rejects_an_object_why_on_a_candidate_and_inside_a_done", async () => {
    // The exact frame R1-05 probed, and the one that reached render.
    expect(await collect([frame("candidate", card(1, { why: { t: 1 } as unknown as string }))])).toEqual([]);
    expect(await collect([
      frame("done", { results: [{ ...card(1), why: { t: 1 } }], reason: "1 pick" }),
    ])).toEqual([]);
  });

  it("rejects_a_bad_card_arriving_in_the_below_floor_tray", async () => {
    // R2's note: the guard protects the frame, not the title — the same title came back inside
    // `done` and rendered in the tray. The tray's cards go through the same `isCard`, so a `done`
    // whose `belowFloor` carries one is refused whole rather than half-rendered.
    expect(await collect([
      frame("done", { results: [card(1)], reason: "1 pick", belowFloor: [{ ...card(2), why: { t: 1 } }] }),
    ])).toEqual([]);
  });

  it("rejects_a_non_boolean_outsideRequest_or_seen", async () => {
    // Fixture variation, one field at a time. A string "false" is the dangerous one: it is truthy,
    // so the card would be fenced off under "Outside your request" by a value that says otherwise.
    for (const bad of [{ outsideRequest: "false" }, { outsideRequest: 1 }, { seen: "yes" }, { seen: {} }]) {
      expect(await collect([frame("candidate", { ...card(1), ...bad })])).toEqual([]);
    }
  });

  it("stays_tolerant_of_absence_which_is_what_an_older_server_sends", async () => {
    // The controls. None of the three is required: a null why (not reranked yet), an absent
    // outsideRequest and an absent seen are every ordinary card on the wire today.
    expect(await collect([frame("candidate", card(1, { why: null }))])).toHaveLength(1);
    expect(await collect([frame("candidate", card(1, { why: "Because it is tense." }))])).toHaveLength(1);
    expect(await collect([frame("candidate", card(1))])).toHaveLength(1);
    expect(await collect([frame("candidate", card(1, { outsideRequest: true, seen: false }))])).toHaveLength(1);
    // …and a field the client has never heard of still costs the card nothing.
    expect(await collect([frame("candidate", { ...card(1), somethingNew: { a: 1 } })])).toHaveLength(1);
  });
});
