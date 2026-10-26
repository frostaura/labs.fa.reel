import type { DoneData } from "../askReelStream";
import type { ChatStatus } from "./reducer";
import type { ConversationPayload } from "./transcript";

/** Paging: whether "Show me more" may be offered, what it names, and the thread menu's listing. */
/**
 * Has this page run out? The server's answer, not an id diff.
 *
 * The old rule counted how many of a page's ids were not already in `order` and latched
 * `exhausted` on zero — but a page's `done.results` is always a subset of what already streamed as
 * `candidate` frames, so that count was usually zero on a page that had just added thirteen cards
 * (R1-03). It was not ALWAYS zero, and the difference was invisible: `candidate` early-returns on
 * a title already in `byId`, which still holds every card of every earlier turn including its
 * below-floor tray, so a page overlapping the previous tray reached `done` with genuinely new ids
 * and behaved correctly. One gesture, opposite behaviour, decided by tray overlap.
 *
 * The server is the only party that can answer this: it derives the no-repeat set from its own
 * rows, so a page it returns nothing for is a page with nothing left to return. `kept` is the
 * server's own count of what it kept and equals `results.length` on every path that sends it;
 * both are required to be empty, so a malformed frame fails towards "ask again" rather than
 * towards a latch the user cannot undo.
 */
export function pageIsExhausted(data: Pick<DoneData, "results" | "kept">): boolean {
  return data.results.length === 0 && (data.kept == null || data.kept <= 0);
}

/**
 * How many threads `Recent ▾` asks for on a first open, and after "Show older".
 *
 * The route is `GET /api/search/conversations?limit=N`, clamped server-side to 1..100, and it has
 * **no `offset`** — so 100 is the whole of what this control can reach, and there is no third
 * gesture to offer. Recorded here rather than discovered again: a "Show older" that keeps
 * requesting would silently return the same hundred rows forever.
 */
export const RECENT_LIMIT = 20;

export const OLDER_LIMIT = 100;

/**
 * What the thread menu should request, and what it may offer.
 *
 * SB-05 is the missing half of a control that was otherwise correct: the menu showed 20 of an
 * account's 51 threads, matching the contract's documented `?limit=20`, and said nothing about the
 * other 31 — so a menu framed as "a means of getting back to an answer" read as a complete list
 * and was not one. The first page stays cheap (this is a control almost nobody touches on a given
 * visit); asking for the rest is one deliberate gesture.
 *
 * The offer is made only over a FULL page, because a short one is the whole list and promising
 * older threads over it promises rows that do not exist. At the ceiling the menu says so instead
 * of offering a gesture the route cannot honour.
 */
export function recentListing(
  showOlder: boolean,
  rowCount: number,
): { limit: number; canShowOlder: boolean; atCeiling: boolean } {
  return {
    limit: showOlder ? OLDER_LIMIT : RECENT_LIMIT,
    canShowOlder: !showOlder && rowCount >= RECENT_LIMIT,
    atCeiling: showOlder && rowCount >= OLDER_LIMIT,
  };
}

/**
 * Was the answer a resume restores made of more than one page?
 *
 * F4-03: this was `turns.some((t) => t.kind === "page")` — true if the thread had EVER been paged,
 * for the rest of its life. A fresh question asked in a thread that was paged three turns earlier
 * came back reading "6 picks so far · last page: 6 picks ranked for you, from 40 pulled": the same
 * count twice in one sentence, and "last page" over an answer with one page. And it read
 * differently before and after the reload, because the LIVE `paged` is per-answer and a new ask
 * clears it — the same answer, two sentences, with a page turn in between flipping it.
 *
 * The grid a resume restores is the last completed ask plus whatever paged onto it, so the
 * question is not "did this thread ever page" but "did anything page onto the answer on screen".
 *
 * R6-01: that word COMPLETED was the whole rule and the anchor here ignored it, walking to the
 * last `ask` row of any status while the server replays from the last one that finished. Stop a
 * question on a paged thread and the cursor moved past the answer actually on screen, found no
 * page after it, and dropped the running count — the same 49-card grid printing "4 picks ranked
 * for you, from 79 pulled" as a description of itself, which is the exact R1-03 sentence `paged`
 * exists to prevent. And it persisted: `paged` is carried across later pages, so the wrong line
 * survived every subsequent one.
 *
 * The anchor is {@link answeredTurnIndex} — the same row {@link answeredQuestion} reads the
 * question off and {@link resumedPageTarget} names as `pageOf`, which is the server's own
 * `ResumeAsync` rule. Within one resume the grid is then read and described by one rule instead of
 * two. The block the server builds off that anchor is COMPLETED assistant rows, so a page counts
 * on the same terms: one that failed added no cards, and counting it would put the composite line
 * over a single turn's work.
 *
 * The fallback is the previous behaviour verbatim, for a server that carries no `status` on its
 * turn rows: no row can be read as the completed ask, so the cursor goes back to the last `ask` of
 * any status. `kind` is optional on the wire there and absent has always meant `ask`, which
 * matters — reading absent as "not an ask" would leave the cursor at -1 and make every page row in
 * the thread count again.
 */
export function wasPaged(rows: ConversationPayload["turns"]): boolean {
  const answered = answeredTurnIndex(rows);
  if (answered < 0) {
    let lastAsk = -1;
    for (let i = 0; i < rows.length; i++) if ((rows[i].kind ?? "ask") === "ask") lastAsk = i;
    return rows.slice(lastAsk + 1).some((t) => t.kind === "page");
  }
  return rows.slice(answered + 1).some((t) => t.kind === "page" && t.status === "Completed");
}

/**
 * The question the restored grid actually answers.
 *
 * The server replays the last COMPLETED `ask` block, so when the newest turn failed the cards on
 * screen belong to an earlier question — and the seam, which says only "picked up where you left
 * off", let them read as the answer to the one directly above them. This is the server's own rule
 * (`ResumeAsync`'s `lastAsk`) applied to the transcript it sent, so the two cannot drift: the last
 * completed assistant `ask` row, and the nearest user row above it.
 */
export function answeredQuestion(rows: ConversationPayload["turns"]): string {
  const answer = answeredTurnIndex(rows);
  for (let i = answer - 1; i >= 0; i--) {
    if (rows[i].role === "user") return rows[i].text;
  }
  return "";
}

/** Where the last completed assistant `ask` row sits, or -1. The row a resumed grid belongs to. */
function answeredTurnIndex(rows: ConversationPayload["turns"]): number {
  return rows.reduce(
    (found, t, i) => (t.role === "assistant" && (t.kind ?? "ask") === "ask" && t.status === "Completed" ? i : found),
    -1,
  );
}

/**
 * The id of the turn the restored grid belongs to — what a page of it must name as `pageOf`.
 *
 * Exactly the row {@link answeredQuestion} reads the question off, so the two halves of a page
 * request can never name different turns: the query is that row's question and this is that row's
 * id. Null where the payload carries no ids (an older server), which is why `pageOf` is optional —
 * the server then falls back to comparing the query, which is right on every screen except the one
 * R3-02 found.
 */
export function answeredTurnId(rows: ConversationPayload["turns"]): string | null {
  const row = rows[answeredTurnIndex(rows)];
  return row ? row.id ?? row.turnId ?? null : null;
}

/**
 * The question and the turn a resumed "Show me more" must name — read off ONE row, deliberately.
 *
 * R5-03. The id came from {@link answeredTurnId} (the last completed ask, which is right) and the
 * query fell back to the payload's `lastQuery`, which the contract defines as the last USER row.
 * On any thread whose newest question was stopped those are two different turns, so the request
 * went out carrying the kept answer's id and the abandoned question — and the server refused it
 * `409 page_query_mismatch`, exactly as it should. Paging was then permanently dead on that
 * thread: the state is stored, so every reload reproduced it, and there was no gesture that could
 * clear it.
 *
 * Both halves come off `answeredTurnIndex` here, so a request that names one turn's row and
 * another turn's question is not merely absent — it is unrepresentable. `lastQuery` keeps its own
 * job untouched: it is the question "Ask again" replays, and after a Stop that is the one nobody
 * answered.
 *
 * The fallback is the previous behaviour verbatim, for the server that carries no `status` on its
 * turn rows: no row can be read as the completed ask, so the query is the payload's and the id is
 * null — and the server derives `kind` by comparing the query, which is right on every screen but
 * this one.
 */
export function resumedPageTarget(
  rows: ConversationPayload["turns"],
  fallbackQuery: string,
): { query: string; turnId: string | null } {
  const index = answeredTurnIndex(rows);
  if (index < 0) return { query: fallbackQuery, turnId: null };
  return { query: answeredQuestion(rows) || fallbackQuery, turnId: answeredTurnId(rows) };
}

/**
 * Why the page of the answer on screen was refused, where the server named something the reader
 * can act on. `null` = nothing was refused.
 *
 * `mismatch` is `409 page_query_mismatch`: the question this page claims to extend is not the
 * question that turn answered. It is a refusal, not a failure — nothing on screen is wrong and
 * nothing was lost, which is why it has a state of its own rather than sharing `blocked`'s.
 */
export type PageRefusal = "mismatch" | null;

/**
 * What a refused page says for itself — one line, beside the control that took the refusal.
 *
 * R5-04: every `409` mapped to `turn_in_progress`, so a refused page announced "That question is
 * still being answered." into a live region while nothing at all was running, and the `blocked`
 * state it landed on withdrew the footnote and the paging control with it. The contract is
 * explicit that `code` is what a client switches on, and that matching on anything else "is how a
 * UI ends up rendering a crash message for an honest refusal".
 */
export function pageRefusalMessage(refusal: PageRefusal): string | null {
  if (refusal === "mismatch") return "That page no longer matches its answer — ask again.";
  return null;
}

/**
 * Whether "Show me more" may be offered at all — the question being, whose answer is on screen?
 *
 * A page EXTENDS an answer, so it only makes sense over one: a completed turn of this visit, or a
 * thread the server handed back whole. Every other state fails for a reason of its own, and R1-04
 * is what happens when none of them are checked. Get a 37-card answer, ask something new, press
 * Stop before the first card lands: the previous answer is correctly held and dimmed, and the page
 * control rendered underneath it with no idea the cards above it belonged to an earlier question.
 * Pressing it replayed the STOPPED question, the dim came off, and 37 held cards silently stopped
 * being "the previous answer" and became page one of a different one.
 *
 * So: `holdingPrevious` is disqualifying whatever the status — those cards are not this question's
 * answer. `stopped` is disqualifying because an abandoned turn never reached `done`, which is where
 * ranking and the quality floor run: extending an unranked, unfloored partial compounds it. `error`
 * and `blocked` have their own controls ("Ask again", "Check again") saying what to do instead, and
 * a page from either would be a second, contradictory offer beside them.
 */
export function canPage(
  status: ChatStatus,
  holdingPrevious: boolean,
  exhausted: boolean,
  cardCount: number,
  settled = 0,
): boolean {
  if (cardCount === 0 || exhausted || holdingPrevious) return false;
  if (status === "done" || status === "resumed") return true;
  // …and the one stopped screen where paging is honest: a Stop that gave a held previous answer
  // back whole (F6-06). Every card on it is settled — finished, server-ordered, server-floored work
  // — and `pageQuery` names the question it answers, so the page extends that answer rather than
  // the abandoned one. A stopped turn's OWN partial is what the rule above exists for and has
  // `settled === 0` against a non-zero grid, so it is still refused. R1-04 was not "a stopped state
  // offered paging"; it was paging sending the question that had never been answered.
  return status === "stopped" && settled === cardCount;
}
