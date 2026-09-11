/**
 * POST-SSE client for live "Ask Reel". EventSource is GET-only, so we read the streamed
 * `text/event-stream` body off a fetch ReadableStream and parse the frames ourselves. Each
 * frame is `event: <name>\ndata: <json>\n\n`. Events: phase → candidate → candidate-scored → done.
 */

import { ensureFreshSession } from "./session";

export interface AskCard {
  titleId: string;
  mediaType: "Movie" | "Show";
  tmdbId: number | null;
  name: string;
  year: number | null;
  posterPath: string | null;
  genres: string[];
  similarity: number | null;
  predictedRating: number | null;
  /** Community score (0–10) and its vote count; both absent on older servers. */
  crowdRating?: number | null;
  crowdVotes?: number | null;
  /** Phase B: hyper-personal LLM fit + reason; absent until reranked. */
  fit?: number | null;
  why?: string | null;
  /**
   * The server's own blend score for this card. Server order is authoritative — when this is
   * present the client sorts by it rather than re-deriving a second, subtly different blend from
   * the parts. Absent on older servers, where the client formula still applies.
   */
  blend?: number | null;
  /** Person/filmography queries surface already-watched titles, badged. */
  seen?: boolean;
  /**
   * True for a closest-but-outside card: it trips one of the user's exclusions and is shown,
   * flagged, only because the clean picks could not fill the floor. Rendered in its own section
   * under the main grid, never interleaved. Absent on older servers (= false).
   */
  outsideRequest?: boolean;
}

export interface PhaseData {
  stage: string;
  found: number;
  scored: number;
}

export interface DoneData {
  results: AskCard[];
  reason: string | null;
  /**
   * True when this `done` is the STORED answer to a `clientTurnId` the server has already
   * answered — a dropped connection, a StrictMode double-invoke, an impatient second tap. It makes
   * zero model calls and writes zero rows, and the client uses it to tell "we ran your question
   * again" apart from "you are looking at the same serve twice".
   */
  replayed?: boolean;
  /**
   * How many stored cards the eligibility re-filter dropped on the way out — titles the account
   * has rated, watched, saved or dismissed since the turn ran. Owed to the user as a number: a
   * card that silently disappears between two views of the same answer is indistinguishable from
   * a bug.
   */
  removed?: number;
  /** The stored failure's code when a failed turn is replayed (`client_aborted`, an exception type). */
  failureCode?: string | null;
  /**
   * Server-side failure marker. Set by the expansion service on a caught exception, which
   * otherwise looks identical on the wire to an honest empty answer (`done`, no results). It stays
   * optional because a replayed turn stored before the field existed will not carry it, and
   * `isFailedDone` keeps the wording fallback for exactly those rows.
   */
  failed?: boolean;
  /** Discovery accounting: candidates streamed, cards kept, cards cut by the quality floor. */
  streamed?: number;
  kept?: number;
  droppedByFloor?: number;
  /**
   * The cards the quality floor cut. Shown, if at all, in their own collapsed tray under the grid
   * — never mixed into `results`, which is the answer.
   */
  belowFloor?: AskCard[];
}

/** A conflict between the question and the account's standing exclusions. Informational. */
export interface ConflictData {
  message: string;
  excludedGenres?: string[];
  /**
   * The other half of the collision: the excluded thing is not always a genre. "anime" and
   * "musical" are avoid-keywords, and a turn asking for one carried an empty `excludedGenres` and
   * nothing else the client could name. Absent on a server that predates the field.
   */
  excludedKeywords?: string[];
  poolSize?: number;
  catalogueSize?: number;
}

/**
 * Reel's own sentence for this turn.
 *
 * `conflict` says the server REWROTE it before sending: the reply is written by a model that has
 * seen no candidates, so a question colliding with a standing exclusion got "here are gritty 90s
 * action picks" above seven post-2000 titles. The server qualifies the sentence at the source and
 * flags it here, which is what lets the page mark that bubble and put the full notice under it
 * rather than floating above the grid, detached from what it is about. Optional because an older
 * server does not send it — and losing a reply over a missing boolean would be the guard doing
 * more damage than the bug.
 */
export interface AssistantMessageData {
  text: string;
  conflict?: boolean;
}

/** The phrase the server uses for a caught exception. See `LiveSearchExpansionService`. */
const FAILURE_REASON = /snag/i;

/**
 * Was this `done` frame a failure rather than an honest "nothing matched"? The two look identical
 * on the wire (empty results either way), and conflating them is what put "No matches for that one"
 * and "search hit a snag" on screen together.
 */
export function isFailedDone(data: Pick<DoneData, "results" | "reason" | "failed">): boolean {
  if (data.failed != null) return data.failed;
  return data.results.length === 0 && data.reason != null && FAILURE_REASON.test(data.reason);
}

/**
 * The stream's first frame, and the only one that says where this turn sits. `id` is what the tab
 * remembers; `turnId` is what `GET /api/search/turns/{id}` is asked about after a dropped
 * connection; `sequence` is the position in the transcript. The last two are optional because a
 * frame from an older server carries only the id, and losing the conversation entirely over two
 * missing fields would cost the thread rather than the reconnect path.
 */
export interface ConversationData {
  id: string;
  turnId?: string;
  sequence?: number;
}

export type AskEvent =
  | { event: "phase"; data: PhaseData }
  | { event: "conversation"; data: ConversationData }
  | { event: "candidate"; data: AskCard }
  | { event: "candidate-scored"; data: { titleId: string; predictedRating: number | null } }
  | { event: "candidate-reranked"; data: { titleId: string; fit: number | null; why: string | null } }
  | { event: "assistant-message"; data: AssistantMessageData }
  | { event: "conflict"; data: ConflictData }
  | { event: "done"; data: DoneData };

/**
 * A non-2xx response from /api/search/ask, carrying the status AND the server's stable `code` so
 * callers can tell the cases apart: 401 (rotate the session and retry), 404
 * `conversation_not_found` (the remembered thread is gone — forget it and start fresh), 409
 * `turn_in_progress` (this question is already being answered, so keep the grid and wait), 400
 * (the request itself was wrong), anything else (a real failure).
 *
 * The prose in the body's `error` is the USER's and may change; matching on it is how a UI ends up
 * rendering a crash message for an honest refusal. `code` is the client's and does not change.
 */
export class AskStreamError extends Error {
  readonly status: number;
  /** The server's stable error code, or null when the body was not the house error shape. */
  readonly code: string | null;

  constructor(status: number, code: string | null = null, message = `ask failed: ${status}`) {
    super(message);
    this.name = "AskStreamError";
    this.status = status;
    this.code = code;
  }
}

/**
 * Everything the client is allowed to say about a turn.
 *
 * `history`, `shownTmdbIds` and `continuation` were REMOVED, not renamed. A client-carried
 * transcript is unverifiable, capped, lost on reload and wrong the moment a second tab exists, and
 * every one of those failures showed up as the product forgetting what it had just said. The
 * server derives all three from rows it wrote: the history the model reads, the no-repeat set, and
 * whether this is a new question or another page of the last one.
 */
export interface AskReelRequest {
  query: string;
  /** The thread this belongs to; null or absent mints a new one. An unknown id is a 404, never a fork. */
  conversationId?: string | null;
  /**
   * This exchange's id, minted by the client and resent verbatim on a retry. It is the idempotency
   * key: a dropped connection, a StrictMode double-invoke and an impatient second tap all come
   * back as the SAME answer instead of three billed turns and three copies in the transcript.
   */
  clientTurnId: string;
  /**
   * The movie/TV toggle, as "movie" / "tv". Sent so the SERVER narrows discovery rather than the
   * client hiding cards after the fact — filtering locally meant a turn that returned only shows
   * rendered an empty screen under "Movies".
   */
  mediaTypes?: string[];
  /**
   * The assistant turn this request is another PAGE of — present only on "Show me more", and only
   * when the client knows the id.
   *
   * `kind` is otherwise derived server-side by comparing the query to the conversation's last USER
   * turn, and R3-02 is what that costs the moment the two disagree: settle an answer, ask a
   * follow-up, press Stop, then press "Show me more". The page correctly replays the ANSWERED
   * question — but the last user turn is now the abandoned one, so the server recorded a second
   * `ask`, the transcript grew a question the user never typed, and the next reload replayed one
   * turn's cards instead of the two that were on screen. Forty-two of sixty-six cards vanished.
   *
   * Naming the turn removes the inference: a page extends THAT completed turn whatever was said
   * after it. Optional because a client cannot always name it (an older resume payload projects no
   * turn ids) and because a server that does not know the field ignores it, falling back to the
   * comparison — so sending it is never worse than not.
   */
  pageOf?: string;
}

/**
 * The server's stable `code` off an error body, or null. Deliberately total: a proxy's HTML error
 * page, an empty body and a JSON object with no `code` all mean "no code", and none of them may
 * turn a refusal the client could have handled into a thrown parse error.
 */
async function errorCode(res: Response): Promise<string | null> {
  try {
    const body: unknown = await res.json();
    if (typeof body === "object" && body !== null && "code" in body) {
      const code = (body as { code: unknown }).code;
      return typeof code === "string" && code.length > 0 ? code : null;
    }
  } catch {
    // Not the house error shape — the status is all this response can tell us.
  }
  return null;
}

/** Streams /api/search/ask, invoking onEvent per parsed SSE frame. Resolves when the stream ends. */
export async function streamAskReel(
  request: AskReelRequest,
  onEvent: (ev: AskEvent) => void,
  signal: AbortSignal,
): Promise<void> {
  const init: RequestInit = {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(request),
    credentials: "include",
    signal,
  };

  let res = await fetch("/api/search/ask", init);
  // The access cookie lives 15 minutes and a conversation outlives it, so a 401 here is routine
  // rather than terminal. Rotate the session on the shared single-flight latch and retry exactly
  // once; a second 401 is a genuinely dead session and reaches the hook as `reason: "session"`.
  if (res.status === 401 && (await ensureFreshSession())) {
    res = await fetch("/api/search/ask", init);
  }
  if (!res.ok || !res.body) {
    throw new AskStreamError(res.status, await errorCode(res));
  }

  const reader = res.body.getReader();
  const decoder = new TextDecoder();
  let buffer = "";

  const drain = () => {
    let sep: number;
    while ((sep = buffer.indexOf("\n\n")) !== -1) {
      const frame = buffer.slice(0, sep);
      buffer = buffer.slice(sep + 2);
      const ev = parseFrame(frame);
      if (ev) onEvent(ev);
    }
  };

  for (;;) {
    const { done, value } = await reader.read();
    if (done) break;
    buffer += decoder.decode(value, { stream: true });
    drain();
  }

  // Flush whatever the server left unterminated. A `done` frame written immediately before the
  // response closes can arrive without its blank line — nginx and Kestrel both end the body on
  // the last write, not on a tidy frame boundary — and dropping it left the UI streaming forever
  // with the answer already in the buffer. Decoding with `stream: false` also releases any
  // trailing multi-byte character the decoder is still holding.
  buffer += decoder.decode();
  drain();
  const tail = parseFrame(buffer);
  if (tail) onEvent(tail);
}

function parseFrame(frame: string): AskEvent | null {
  let event = "message";
  let data = "";
  for (const line of frame.split("\n")) {
    // `:` starts a comment. Servers and proxies emit bare `:` lines as keepalives to hold the
    // connection open through an idle stretch; ours writes `:keepalive` every 10 seconds. They
    // carry no data and must not look like a frame.
    if (line.startsWith(":")) continue;
    if (line.startsWith("event:")) event = line.slice(6).trim();
    else if (line.startsWith("data:")) data += line.slice(5).trim();
    // `id:` is written first on every frame and is deliberately dropped: it exists so a client can
    // echo it back as Last-Event-ID, and this client reconnects through GET /api/search/turns/{id}
    // instead. Every other field line is ignored on the same terms — an unknown one must cost the
    // frame nothing.
  }
  if (!data) return null;
  let parsed: unknown;
  try {
    parsed = JSON.parse(data);
  } catch {
    return null;
  }
  return validate(event, parsed);
}

/**
 * Per-event runtime validation, replacing the bare `as AskEvent` cast this parser used to end on.
 * The cast asserted the server's shape rather than checking it, so one malformed frame — a `done`
 * whose `results` came back as an object, a card with no id — reached the reducer as a typed value
 * and threw somewhere far away from the frame that caused it. A frame that fails its guard is
 * dropped here, where the failure is local; the hook's watchdog is what stops a stream that
 * dropped its `done` from hanging forever.
 */
function validate(event: string, data: unknown): AskEvent | null {
  switch (event) {
    case "phase":
      return isPhase(data) ? { event, data } : null;
    case "conversation":
      return isConversation(data) ? { event, data } : null;
    case "candidate":
      return isCard(data) ? { event, data } : null;
    case "candidate-scored":
      return isScored(data) ? { event, data } : null;
    case "candidate-reranked":
      return isReranked(data) ? { event, data } : null;
    case "assistant-message":
      return isAssistantMessage(data) ? { event, data } : null;
    case "conflict":
      return isConflict(data) ? { event, data } : null;
    case "done":
      return isDone(data) ? { event, data } : null;
    default:
      return null;
  }
}

function isRecord(v: unknown): v is Record<string, unknown> {
  return typeof v === "object" && v !== null && !Array.isArray(v);
}

function isNumberOrNull(v: unknown): boolean {
  return v == null || (typeof v === "number" && Number.isFinite(v));
}

/** A flag the client acts on, or nothing at all. Absent is a real answer; a truthy string is not. */
function isBooleanOrAbsent(v: unknown): boolean {
  return v == null || typeof v === "boolean";
}

export function isPhase(data: unknown): data is PhaseData {
  return (
    isRecord(data) &&
    typeof data.stage === "string" &&
    typeof data.found === "number" &&
    typeof data.scored === "number"
  );
}

/**
 * The id is required and the other two are not, on purpose. A tab that remembers "" sends an empty
 * conversation id on its next turn; a tab that cannot name the turn merely loses the reconnect
 * path, which is a smaller loss than dropping the frame that carries the thread.
 */
export function isConversation(data: unknown): data is ConversationData {
  return (
    isRecord(data) &&
    typeof data.id === "string" &&
    data.id.length > 0 &&
    (data.turnId == null || typeof data.turnId === "string") &&
    (data.sequence == null || typeof data.sequence === "number")
  );
}

/**
 * Three classes of field, and the differences matter.
 *
 * **Required:** the ones the grid cannot render without — `titleId`, `mediaType`, `name`.
 *
 * **Type-checked but optional — RENDERED directly as a value:** `year` into a paragraph, `tmdbId`
 * into the card's href, `why` into a paragraph and a `title` attribute. Absent is fine for all
 * three (an older server, a title with no release year, a card the rerank has not reached); the
 * wrong TYPE is not, because `{year: {oops: true}}` cleared this guard, reached `<p>{card.year}</p>`
 * and threw "Objects are not valid as a React child" three modules away from the frame that caused
 * it. `why` is the same field in the same component one line below, and it was left open until
 * R1-05 probed it — `isReranked` type-checked it, so the `candidate-reranked` path was guarded and
 * this one was not.
 *
 * **Type-checked but optional — ROUTING:** `outsideRequest` and `seen`. Neither is printed; each
 * decides WHERE a card goes. A truthy non-boolean `outsideRequest` (a string `"false"`, say)
 * silently moves a card out of the recommended grid into the fenced "outside your request"
 * channel, and `seen` chooses between the quick actions and a "Seen" pill. A wrong type there
 * produces a wrong screen rather than a crash, which is the harder failure to notice.
 *
 * Everything else stays tolerant on purpose: a server that adds a field, or omits one an older
 * client thought mandatory, must not cost the user a card.
 *
 * The tray gets this for free — `isDone` runs every `belowFloor` card through here too, so a
 * malformed card cannot arrive by the side door the grid's own guard closed.
 */
export function isCard(data: unknown): data is AskCard {
  return (
    isRecord(data) &&
    typeof data.titleId === "string" &&
    data.titleId.length > 0 &&
    (data.mediaType === "Movie" || data.mediaType === "Show") &&
    typeof data.name === "string" &&
    isNumberOrNull(data.year) &&
    isNumberOrNull(data.tmdbId) &&
    (data.why == null || typeof data.why === "string") &&
    isBooleanOrAbsent(data.outsideRequest) &&
    isBooleanOrAbsent(data.seen)
  );
}

export function isScored(data: unknown): data is { titleId: string; predictedRating: number | null } {
  return isRecord(data) && typeof data.titleId === "string" && isNumberOrNull(data.predictedRating);
}

export function isReranked(data: unknown): data is { titleId: string; fit: number | null; why: string | null } {
  return (
    isRecord(data) &&
    typeof data.titleId === "string" &&
    isNumberOrNull(data.fit) &&
    (data.why == null || typeof data.why === "string")
  );
}

export function isAssistantMessage(data: unknown): data is AssistantMessageData {
  // `conflict` decides whether the reply renders an "under your conditions" tag, so a truthy
  // string would badge an unqualified answer. Absent stays fine — see AssistantMessageData.
  return isRecord(data) && typeof data.text === "string" && isBooleanOrAbsent(data.conflict);
}

/** A list of terms the page joins into a sentence, or nothing. Absent is every older server. */
function isStringListOrAbsent(v: unknown): boolean {
  return v == null || (Array.isArray(v) && v.every((x) => typeof x === "string"));
}

export function isConflict(data: unknown): data is ConflictData {
  return (
    isRecord(data) &&
    typeof data.message === "string" &&
    data.message.length > 0 &&
    // Both lists are joined into a sentence the page prints, so a non-string among them reaches
    // render as a React child — `why`'s failure, one component over.
    isStringListOrAbsent(data.excludedGenres) &&
    isStringListOrAbsent(data.excludedKeywords)
  );
}

export function isDone(data: unknown): data is DoneData {
  if (!isRecord(data)) return false;
  if (!Array.isArray(data.results) || !data.results.every(isCard)) return false;
  if (data.reason != null && typeof data.reason !== "string") return false;
  if (data.belowFloor != null && (!Array.isArray(data.belowFloor) || !data.belowFloor.every(isCard))) return false;
  // `removed` is rendered DIRECTLY as a count ("2 picks are gone"), so its type is checked for the
  // same reason `year` is on a card: the wrong one reaches the page as a typed value and prints
  // there. Absent stays fine — no server before Phase 1 sent it.
  if (data.removed != null && typeof data.removed !== "number") return false;
  if (data.failureCode != null && typeof data.failureCode !== "string") return false;
  return true;
}
