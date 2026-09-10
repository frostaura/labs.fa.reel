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
  poolSize?: number;
  catalogueSize?: number;
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

export type AskEvent =
  | { event: "phase"; data: PhaseData }
  | { event: "conversation"; data: { id: string } }
  | { event: "candidate"; data: AskCard }
  | { event: "candidate-scored"; data: { titleId: string; predictedRating: number | null } }
  | { event: "candidate-reranked"; data: { titleId: string; fit: number | null; why: string | null } }
  | { event: "assistant-message"; data: { text: string } }
  | { event: "conflict"; data: ConflictData }
  | { event: "done"; data: DoneData };

/**
 * A non-2xx response from /api/search/ask, carrying the status so callers can tell the three
 * cases apart: 401 (rotate the session and retry), 409 (`turn_in_progress` — the same turn is
 * already streaming elsewhere, so keep the grid), anything else (a real failure).
 */
export class AskStreamError extends Error {
  readonly status: number;

  constructor(status: number, message = `ask failed: ${status}`) {
    super(message);
    this.name = "AskStreamError";
    this.status = status;
  }
}

export interface AskReelRequest {
  query: string;
  history?: { role: string; text: string }[];
  shownTmdbIds?: number[];
  conversationId?: string | null;
  /**
   * The movie/TV toggle, as "movie" / "tv". Sent so the SERVER narrows discovery rather than the
   * client hiding cards after the fact — filtering locally meant a turn that returned only shows
   * rendered an empty screen under "Movies".
   */
  mediaTypes?: string[];
  /**
   * True when this replays a question already in the transcript — an infinite-scroll page or a
   * re-ask from the media toggle. The server writes a user+assistant SearchTurn pair per request,
   * so without this every scroll page and every filter click permanently duplicated the question
   * in the stored conversation, even though the client hides the repeat on screen.
   */
  continuation?: boolean;
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
    throw new AskStreamError(res.status);
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
    // connection open through an idle stretch; they carry no data and must not look like a frame.
    if (line.startsWith(":")) continue;
    if (line.startsWith("event:")) event = line.slice(6).trim();
    else if (line.startsWith("data:")) data += line.slice(5).trim();
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

export function isPhase(data: unknown): data is PhaseData {
  return (
    isRecord(data) &&
    typeof data.stage === "string" &&
    typeof data.found === "number" &&
    typeof data.scored === "number"
  );
}

export function isConversation(data: unknown): data is { id: string } {
  return isRecord(data) && typeof data.id === "string" && data.id.length > 0;
}

/**
 * Only the fields the grid cannot render without are required. Everything else stays tolerant on
 * purpose: a server that adds a field, or omits one an older client thought mandatory, must not
 * cost the user a card.
 */
export function isCard(data: unknown): data is AskCard {
  return (
    isRecord(data) &&
    typeof data.titleId === "string" &&
    data.titleId.length > 0 &&
    (data.mediaType === "Movie" || data.mediaType === "Show") &&
    typeof data.name === "string"
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

export function isAssistantMessage(data: unknown): data is { text: string } {
  return isRecord(data) && typeof data.text === "string";
}

export function isConflict(data: unknown): data is ConflictData {
  return isRecord(data) && typeof data.message === "string" && data.message.length > 0;
}

export function isDone(data: unknown): data is DoneData {
  if (!isRecord(data)) return false;
  if (!Array.isArray(data.results) || !data.results.every(isCard)) return false;
  if (data.reason != null && typeof data.reason !== "string") return false;
  if (data.belowFloor != null && (!Array.isArray(data.belowFloor) || !data.belowFloor.every(isCard))) return false;
  return true;
}
