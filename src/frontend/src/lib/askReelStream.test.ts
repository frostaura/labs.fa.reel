import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { AskStreamError, isKnockout, judgeDegraded, streamAskReel, type AskCard, type AskEvent } from "./askReelStream";

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

  it("carries_the_wait_a_429_asks_for_from_the_body_or_the_header", async () => {
    // WS-8's turn limiter answers 429 `ask_rate_*` with `retryAfterSeconds` in the body and the same
    // number in `Retry-After`. The body wins; the header is the fallback when the body is lost.
    vi.stubGlobal("fetch", vi.fn(() => Promise.resolve(new Response(
      JSON.stringify({ error: "…", code: "ask_rate_minute", retryAfterSeconds: 41 }),
      { status: 429, headers: { "Retry-After": "41" } },
    ))));
    const fromBody = await streamAskReel({ query: "q", clientTurnId: "t" }, () => {}, new AbortController().signal)
      .catch((e: unknown) => e);
    expect((fromBody as AskStreamError).status).toBe(429);
    expect((fromBody as AskStreamError).code).toBe("ask_rate_minute");
    expect((fromBody as AskStreamError).retryAfterSeconds).toBe(41);

    vi.stubGlobal("fetch", vi.fn(() => Promise.resolve(new Response("<html>429</html>", { status: 429, headers: { "Retry-After": "3600" } }))));
    const fromHeader = await streamAskReel({ query: "q", clientTurnId: "t" }, () => {}, new AbortController().signal)
      .catch((e: unknown) => e);
    expect((fromHeader as AskStreamError).code).toBeNull();
    expect((fromHeader as AskStreamError).retryAfterSeconds).toBe(3600);

    vi.stubGlobal("fetch", vi.fn(() => Promise.resolve(new Response("{}", { status: 500 }))));
    const none = await streamAskReel({ query: "q", clientTurnId: "t" }, () => {}, new AbortController().signal)
      .catch((e: unknown) => e);
    expect((none as AskStreamError).retryAfterSeconds).toBeNull();
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

/**
 * The two fields FX3 added to the wire, which nothing on the client read.
 *
 * `assistant-message.conflict` is the flag that says THIS reply was qualified before it was sent —
 * the server rewrites the model's sentence when the question collides with a standing exclusion,
 * and the flag is what lets the page mark the bubble and anchor the notice to it. It is always
 * present as a key so the client has one shape to render.
 *
 * `conflict.excludedKeywords` is the other half of the collision: the excluded thing is not always
 * a genre. "anime" and "musical" are avoid-keywords, and a turn asking for one carried an empty
 * `excludedGenres` and nothing else the client could name.
 */
describe("the additive conflict fields", () => {
  it("carries_the_conflict_flag_on_an_assistant_message", async () => {
    // Fixture variation on the one field: qualified, not qualified, and an older server that
    // sends no flag at all. The third must still deliver the reply — losing a bubble over a
    // missing boolean would be the guard doing more damage than the bug.
    expect(await collect([frame("assistant-message", { text: "Three that fit.", conflict: true })]))
      .toEqual([{ event: "assistant-message", data: { text: "Three that fit.", conflict: true } }]);
    expect(await collect([frame("assistant-message", { text: "Three that fit.", conflict: false })]))
      .toEqual([{ event: "assistant-message", data: { text: "Three that fit.", conflict: false } }]);
    expect(await collect([frame("assistant-message", { text: "Three that fit." })])).toHaveLength(1);
    // A non-boolean flag is refused: it decides whether a "under your conditions" tag renders.
    expect(await collect([frame("assistant-message", { text: "Three that fit.", conflict: "yes" })])).toEqual([]);
  });

  it("carries_excluded_keywords_beside_excluded_genres", async () => {
    const full = {
      message: "Your conditions permanently exclude comedy and anime.",
      excludedGenres: ["comedy"],
      excludedKeywords: ["anime"],
      poolSize: 12,
      catalogueSize: 40_000,
    };
    expect(await collect([frame("conflict", full)])).toEqual([{ event: "conflict", data: full }]);

    // The keyword-only shape, which is the one that had nothing to show before: no genre collides.
    const keywordOnly = { message: "Your conditions permanently exclude anime.", excludedGenres: [], excludedKeywords: ["anime"] };
    expect(await collect([frame("conflict", keywordOnly)])).toEqual([{ event: "conflict", data: keywordOnly }]);
  });

  it("refuses_a_conflict_whose_term_lists_are_not_lists_of_strings", async () => {
    // The lists are joined into a sentence the page prints, so an object among them reaches render
    // as a React child — the same class of failure `why` had, one component over.
    for (const bad of [{ excludedGenres: "comedy" }, { excludedKeywords: [{ t: 1 }] }, { excludedGenres: [3] }]) {
      expect(await collect([frame("conflict", { message: "Excluded.", ...bad })])).toEqual([]);
    }
  });
});

describe("streamAskReel — trust-v1 additions (WS-2 P0)", () => {
  it("reports activity for every chunk, a keepalive comment included", async () => {
    vi.stubGlobal("fetch", vi.fn(() => Promise.resolve(streamed([
      ":keepalive\n\n",
      ":keepalive\n\n",
      frame("done", { results: [], reason: "no picks matched what you asked" }),
    ]))));
    const events: AskEvent[] = [];
    let activity = 0;
    await streamAskReel(
      { query: "ninja movies", clientTurnId: TURN_ID },
      (ev) => events.push(ev),
      new AbortController().signal,
      () => { activity += 1; },
    );
    // Two keepalives produce no event, and still prove the stream is alive.
    expect(activity).toBe(3);
    expect(events.map((e) => e.event)).toEqual(["done"]);
  });

  it("accepts a heartbeat phase and the degraded reply flag", async () => {
    const events = await collect([
      frame("phase", { stage: "searching", found: 0, scored: 0, heartbeat: true }),
      frame("assistant-message", { text: "I couldn't think that one through just now.", conflict: false, degraded: true }),
    ]);
    expect(events).toEqual([
      { event: "phase", data: { stage: "searching", found: 0, scored: 0, heartbeat: true } },
      { event: "assistant-message", data: { text: "I couldn't think that one through just now.", conflict: false, degraded: true } },
    ]);
  });

  it("carries the near-miss tray, the mode, the supply's relaxations and the degraded flags on done", async () => {
    const done = {
      results: [],
      reason: "no picks matched what you asked",
      nearMiss: [card(1, { satisfies: "partial" })],
      mode: "NoneFound",
      supply: { knockouts: [{ kind: "Language", value: "en", contentFilterId: null, label: "English only", wouldUnlock: 11 }] },
      degraded: ["judge:1/3"],
    };
    const events = await collect([frame("done", done)]);
    expect(events).toEqual([{ event: "done", data: done }]);
  });

  it.each([
    ["a near-miss card with no id", { nearMiss: [{ name: "x" }] }],
    ["a non-string mode", { mode: 3 }],
    ["a degraded list with a number in it", { degraded: ["plan", 2] }],
    ["a supply whose offers are not a list", { supply: { knockouts: "English only" } }],
    ["an unchecked tray card with no id", { unchecked: [{ name: "x" }] }],
    ["a satisfies verdict that is not a string", { results: [card(1, { satisfies: 1 as unknown as string })] }],
  ])("drops a done frame with %s", async (_label, extra) => {
    const events = await collect([frame("done", { results: [], reason: null, ...extra })]);
    expect(events).toEqual([]);
  });
});

describe("WS-2 review round 1 — the frames the server really sends", () => {
  it("keeps a done frame whose relaxation offer is malformed; the offer alone is filtered later", async () => {
    // Acceptance D1: the server serialised `kind` as a number (`{"kind":5,…}`) and the whole done
    // frame was rejected, leaving the turn spinning. A chip is not worth the answer.
    const done = {
      results: [],
      reason: "no picks matched what you asked",
      mode: "NoneFound",
      unchecked: [card(1)],
      supply: { knockouts: [{ kind: 5, value: "en", contentFilterId: null, label: "English only", wouldUnlock: 4 }] },
    };
    const events = await collect([frame("done", done)]);
    expect(events).toEqual([{ event: "done", data: done }]);
  });

  it("accepts the server's own none-found shape, enums as names", async () => {
    // Shaped as LiveSearchExpansionService.SupplyWire writes it under the endpoint's camelCase options.
    const done = {
      results: [], streamed: 1, kept: 0, droppedByFloor: 1, belowFloor: [card(2)], nearMiss: [], unchecked: [],
      mode: "NoneFound",
      supply: {
        matchingInCatalogue: 62, alreadySeen: 16, savedOrDismissed: 0, excludedByConditions: 3, eligible: 43,
        belowFloor: 1, unscored: 0, clearingFloor: 42, lowVoteMatches: 9, minVotes: 50, predictedFloor: 6, knockoutsMeasured: true,
        knockouts: [{
          kind: "MinYear", value: "2000", contentFilterId: "6b1f2c1e-0000-4000-8000-000000000001", label: "released 2000 or later",
          wouldUnlock: 19, sample: [{ titleId: "t-s", name: "Shinobi", year: 1987, media: "Movie" }],
        }],
      },
      degraded: [], reason: "no picks matched what you asked", timingsMs: null, debug: null,
    };
    const events = await collect([frame("done", done)]);
    expect(events).toEqual([{ event: "done", data: done }]);
  });

  it.each([
    [{ kind: "Language", value: "en", contentFilterId: null, label: "English only", wouldUnlock: 4 }, true],
    [{ kind: 5, value: "en", contentFilterId: null, label: "English only", wouldUnlock: 4 }, false],
    [{ kind: "Language", value: "en", contentFilterId: null, label: "English only" }, false],
    [{ kind: "Language", value: 1, contentFilterId: null, label: "English only", wouldUnlock: 4 }, false],
  ])("isKnockout(%j) is %s", (offer, ok) => {
    expect(isKnockout(offer)).toBe(ok);
  });

  it.each([
    [["judge:1/3"], true],
    [["plan", "judge:3/3"], true],
    [["judge:0/3"], false],
    // R1-01: titles a batch skipped count as unchecked too, and so does a turn with no model at all.
    [["judge-missing:2/16"], true],
    [["judge:0/3", "judge-missing:1/16"], true],
    [["judge-missing:0/16"], false],
    [["judge-missing:"], false],
    [["no-model"], true],
    [["plan"], false],
    [[], false],
    [undefined, false],
  ])("judgeDegraded(%j) is %s", (flags, failed) => {
    expect(judgeDegraded(flags as string[] | undefined)).toBe(failed);
  });
});

/**
 * §4.9's additive v2 frames. The client renders them while `ASK_ENGINE=v1` frames keep working:
 * every one is optional on the wire, and a malformed one is dropped here rather than reaching the
 * reducer as a typed value — the same discipline as the v1 guards above.
 */
describe("streamAskReel — v2 frames (§4.9)", () => {
  it("parses plan, phase labels, picks, trays, answer, clarify, none-found and degraded", async () => {
    const events = await collect([
      frame("plan", { act: "NewRequest", concepts: ["ninja"], references: ["T1.2"] }),
      frame("phase", { stage: "judging", label: "Checking 6 against your request" }),
      frame("picks", { cards: [card(1), card(2)] }),
      frame("tray", { kind: "nearMiss", cards: [card(3)] }),
      frame("tray", { kind: "belowFloor", cards: [card(4)] }),
      frame("answer", { text: "Yes — it opens on a massacre." }),
      frame("clarify", { question: "Which one?", options: ["The first", "The second"] }),
      frame("none-found", { supply: { knockouts: [] } }),
      frame("degraded", { what: "judge:1/3" }),
    ]);
    expect(events.map((e) => e.event)).toEqual([
      "plan", "phase", "picks", "tray", "tray", "answer", "clarify", "none-found", "degraded",
    ]);
    // A v2 phase carries a label and no counts; the counts read as zero so nothing downstream
    // has to know which engine sent the frame.
    expect(events[1].data).toEqual({ stage: "judging", label: "Checking 6 against your request", found: 0, scored: 0 });
    // `what` is one string or a list on the wire, and always a list to the client.
    expect(events[8].data).toEqual({ what: ["judge:1/3"] });
  });

  it("reads an unknown tray kind as a near miss, never as picks", async () => {
    const [ev] = await collect([frame("tray", { kind: "somethingNew", cards: [card(1)] })]);
    expect(ev).toEqual({ event: "tray", data: { kind: "nearMiss", cards: [card(1)] } });
  });

  it("normalises plan references and drops non-string concepts", async () => {
    const [ev] = await collect([
      frame("plan", { act: "Refine", concepts: ["heist", 4, null], references: [{ turn: 2, rank: 3 }, "T1.1", 7] }),
    ]);
    expect(ev).toEqual({ event: "plan", data: { act: "Refine", concepts: ["heist"], references: ["T2.3", "T1.1"] } });
  });

  it("drops malformed v2 frames rather than handing them on", async () => {
    const events = await collect([
      frame("picks", { cards: [{ titleId: "" }] }),
      frame("picks", { cards: "nope" }),
      frame("tray", { kind: "nearMiss" }),
      frame("answer", { text: 3 }),
      frame("clarify", { question: "Which?", options: [1] }),
      frame("none-found", {}),
      frame("degraded", { what: 5 }),
      frame("plan", { act: 1 }),
      frame("phase", { label: "no stage" }),
    ]);
    expect(events).toEqual([]);
  });
});
