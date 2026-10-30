import type { ChatStatus, ErrorReason, RateLimit } from "./reducer";

/** What the Ask Reel page says, and when — every sentence built on the client, in one place. */
/**
 * Whether the preset mood chips ("Under 90 minutes, feel-good", …) should show: only on a
 * genuinely fresh Ask Reel — no transcript, no cards on screen, nothing streaming. The moment a
 * conversation exists (including one resumed from a previous visit) the presets yield to the
 * refine chips, which are about THIS thread rather than a cold start.
 */
export function showMoodPresets(turnCount: number, cardCount: number, streaming: boolean): boolean {
  return turnCount === 0 && cardCount === 0 && !streaming;
}

/**
 * The plural of `word` for `n` — the noun only, so call sites read `${n} ${plural(n, "pick")}`.
 *
 * One helper rather than a ternary per site: the count copy on this page is written in four
 * places and only some of them agreed, so the live region announced "1 picks ready." to a screen
 * reader while the server's own sentence beside it was correctly singular. Regular -s plurals are
 * all this page has; anything irregular gets written out longhand rather than taught to a helper
 * that would then be trusted with words it cannot handle.
 */
export function plural(n: number, word: string): string {
  return n === 1 ? word : `${word}s`;
}

/**
 * How many the quality floor held back, as the tray must state it.
 *
 * The tray's contents are the truth when it HAS contents. It used to be labelled from
 * `droppedByFloor`, a scalar the server sends beside the cards, so a `done` carrying
 * `droppedByFloor: 2` and one card put "Show the 2 under your 6 bar" over a tray holding one
 * title. The two happened to agree on live turns, which is exactly why it survived: any server
 * path that sets one without the other prints a number the user can count and disprove.
 *
 * The scalar is still the answer when the tray is empty — a turn can report a cut without sending
 * the cards down, and "3 held back" is honest there. `null` means the server said nothing at all,
 * which is not the same as a zero.
 */
export function floorHeldBack(belowFloorCount: number, droppedByFloor: number | null): number {
  if (belowFloorCount > 0) return belowFloorCount;
  return typeof droppedByFloor === "number" && droppedByFloor > 0 ? droppedByFloor : 0;
}

/**
 * What separates clauses in an accounting line. The server's own `AskReelReason.Separator`, so a
 * client-composed clause sits in the same list as the server's without changing punctuation
 * halfway through the sentence.
 */
const REASON_SEPARATOR = " · ";

/**
 * The provenance line under the grid.
 *
 * The server's sentence describes ONE turn — "13 picks ranked for you, from 65 pulled". On an
 * un-paged answer that is the whole truth and it is rendered verbatim. On a paged one it is true
 * of the last page and false of the grid: R1-03 watched "24 picks ranked for you, from 60 pulled"
 * sit under 37 cards, and the alternative (the newest page's sentence) would have claimed 13 of
 * the same 37. Neither is the accumulated set, so the line says both — what is on screen, and
 * where the last of it came from.
 *
 * A count of zero falls back to the sentence alone: "0 picks so far" is a claim about a screen
 * with no grid on it, and the branch that renders this only fires when there are cards anyway.
 *
 * The count is PICKS — see {@link pickSummary}. It folded in the outside-request channel until
 * F4-05, so the accumulated line claimed as matching the very cards the server's own sentence,
 * forty pixels to its right, had just called closest-but-outside.
 */
export function footnote(reason: string | null, pickCount: number, paged: boolean): string | null {
  if (!reason) return null;
  if (!paged || pickCount <= 0) return reason;
  return `${pickCount} ${plural(pickCount, "pick")} so far${REASON_SEPARATOR}last page: ${reason}`;
}

/**
 * What the settled live region announces, and the vocabulary the footnote counts in.
 *
 * F4-05. "N picks so far" folded in the outside-request cards — the ones the sentence beside it has
 * just called not-matching. Turn 10 read
 *
 *   12 picks so far · last page: 3 picks matched everything you asked ·
 *   3 closest-but-outside shown separately · 6 under your 6.0 bar
 *
 * over 9 picks and 3 outside cards: the 12 is 9 + 3, in the same line as "3 picks matched
 * everything you asked". A **pick** is a card that matched, which is the server's own `kept`; a
 * closest-but-outside card is shown precisely because it did not, and is counted where it is named.
 *
 * Zero picks and zero outside is the honest empty answer. Zero picks WITH an outside channel is
 * not — "No matches" over three cards on screen is the same conflation in reverse.
 */
export function pickSummary(pickCount: number, outsideCount: number): string {
  const outside = outsideCount > 0 ? ` ${outsideCount} closest-but-outside shown separately.` : "";
  if (pickCount === 0 && outsideCount === 0) return "No matches for that one.";
  if (pickCount === 0) return `No picks matched everything you asked.${outside}`;
  return `${pickCount} ${plural(pickCount, "pick")} ready.${outside}`;
}

/**
 * What the live region says when a thread is RESTORED rather than answered. F6-15.
 *
 * `askreel-status` was the empty string on every resumed landing — cold open, thread switch, hard
 * reload, second tab — while a seam, a grid, a tray, a footnote, three refine chips and a conflict
 * notice were all on screen. The same region is correct on a completed turn ("14 picks ready.") and
 * after a Stop, so a screen-reader user was told about every state of this page except the one
 * where the most appeared without them doing anything.
 *
 * It says what the seam says, because the seam IS the sighted version of this: "picked up where you
 * left off", and then how much came back. A thread with nothing stored says so rather than
 * announcing a silent zero — that is the transcript-only state, and it is a fact about the thread
 * rather than an empty answer.
 */
export function resumeAnnouncement(pickCount: number, outsideCount: number, screen: ResumedScreen = NOTHING_STORED): string {
  const head = "Picked up where you left off ·";
  // The seam's own removal line, verbatim — the region says what the seam says (HREV5-PH-01).
  const gone = removedNote(screen.removed, screen.removedEarlier);
  const said = (...parts: (string | null)[]) => [head, ...parts].filter(Boolean).join(" ");
  // With nothing left on screen the region says what the CARD under the seam says, and it reads
  // which card that is from the strip's own branch (HREV5-PH-01-R1). It used to infer it from
  // `lastCardsAt`, a conversation column an earlier Picks turn — or a none-found turn's own tray —
  // leaves set, so a restored "nothing found" was announced as "you've watched or rated everything
  // this thread found" under the none-found card: a search that found nothing, said to have found
  // things that were then removed.
  switch (screen.branch) {
    case "all-removed":
      return said(gone, ALL_REMOVED_LINE);
    case "none-found":
      return said(gone, noneFoundAnnouncement(screen.shown ?? NOTHING_SHOWN));
    case "beside-grid":
      // The restored reply asked back or answered beside an empty grid; the strip renders nothing
      // and the reply is read by the transcript's log. No card, so no claim about one.
      return gone ? said(gone) : "Picked up where you left off.";
  }
  if (pickCount === 0 && outsideCount === 0) return gone ? said(gone) : said("no saved picks.");
  const outside = outsideCount > 0 ? ` ${outsideCount} closest-but-outside shown separately.` : "";
  return `${head} ${pickCount} ${plural(pickCount, "pick")}.${outside}${gone ? ` ${gone}` : ""}`;
}

/**
 * Which one of the answer strip's mutually exclusive bodies is on screen — derived once, in
 * `deriveAskView`, and read by both the strip and the status region so the two cannot disagree
 * about which card the reader is looking at (HREV5-PH-01-R1).
 */
export type StripBranch =
  /** 404: the thread this tab remembered is gone. */
  | "conversation-gone"
  /** A failure with no cards to keep on screen. */
  | "failed"
  /** A settled "nothing left" — the none-found card and its trays. */
  | "none-found"
  /** A settled reply that asked back or answered beside an empty grid: nothing is rendered. */
  | "beside-grid"
  /** A resumed thread that never stored results. */
  | "transcript-only"
  /** A resumed thread that stored picks and has none left to show. */
  | "all-removed"
  /** A finished search that found nothing at all. */
  | "empty"
  /** The grid (possibly still filling), its outside channel and trays. */
  | "grid";

/** What a resume put on screen beside the picks — the facts the seam and the strip print. */
export interface ResumedScreen {
  /** Which body the strip renders under the seam — see {@link StripBranch}. */
  branch: StripBranch;
  /** Stored cards the eligibility re-filter dropped, as the seam counts them. */
  removed: number;
  /** How many of `removed` were already acted on when the answer was given — see {@link removedNote}. */
  removedEarlier: number;
  /** What the none-found card has beside it; read only on that branch. */
  shown?: NoneFoundShown;
}

const NOTHING_STORED: ResumedScreen = { branch: "transcript-only", removed: 0, removedEarlier: 0 };
const NOTHING_SHOWN: NoneFoundShown = { unchecked: 0, nearMiss: 0, heldBack: 0, floorLabel: null };

/** What the all-removed card says — shared with the status region so the two cannot drift. */
export const ALL_REMOVED_LINE = "You’ve watched or rated everything this thread found.";

/** What the none-found card is headed with — and what the status region opens on — in each case. */
export const NONE_FOUND_HEADING = {
  /** An honest, empty "nothing left": nothing matched, and nothing is shown in its place. */
  empty: "Nothing left that matches",
  /** Titles were found but nothing could check them against the request — the keyless path. */
  unchecked: "Reel couldn’t check what it found",
} as const;

/** What a none-found answer has on screen beside its card, as the trays count it. */
export interface NoneFoundShown {
  unchecked: number;
  nearMiss: number;
  heldBack: number;
  /** The account's bar, as `formatFloor` writes it; null when none is set. */
  floorLabel: string | null;
}

/** Which heading the none-found card carries: unchecked titles on screen change what it can claim. */
export function noneFoundHeading(shown: Pick<NoneFoundShown, "unchecked">): string {
  return shown.unchecked > 0 ? NONE_FOUND_HEADING.unchecked : NONE_FOUND_HEADING.empty;
}

/**
 * What the status region says over a settled none-found answer. HREV4-PH-01.
 *
 * It follows the card's own branch, because the card sits in a polite region of its own and a
 * screen reader hears both. It used to say "Nothing left that matches" whenever no pick survived —
 * including the keyless turn, where the card read "Reel couldn’t check what it found" over a tray
 * of 11 unchecked titles and 13 more under the bar: two announcements, one of them false, on the
 * path every user gets when the model provider is down. When titles ARE on screen it says what
 * they are, counted as their trays count them; "nothing left" stays for an answer that has nothing.
 */
export function noneFoundAnnouncement(shown: NoneFoundShown): string {
  if (shown.unchecked === 0) return `${NONE_FOUND_HEADING.empty} — Reel’s reply says why.`;
  const parts = [`${shown.unchecked} unchecked ${plural(shown.unchecked, "title")} shown`];
  if (shown.nearMiss > 0) parts.push(`${shown.nearMiss} close but not quite`);
  if (shown.heldBack > 0) parts.push(`${shown.heldBack} held back by your ${shown.floorLabel ? `${shown.floorLabel} ` : ""}bar`);
  return `${NONE_FOUND_HEADING.unchecked} — ${parts.join(", ")}.`;
}

/**
 * Whether the provenance line belongs on screen.
 *
 * `resumed` is in, and SB-06 is what happened when it was not. The resume payload carries the
 * turn's `reason` verbatim — "24 picks ranked for you, from 81 pulled · widened to find enough ·
 * only 5 picks cleared your 6.0 bar, so it was relaxed" — the reducer stores it, and the page then
 * gated the render on `status === "done"`. A resume is deliberately NOT `done` (see the status
 * doc-comment), so a restored answer lost the only on-screen account of how it was assembled,
 * including the fact that the quality floor had been relaxed to fill it.
 *
 * The other two conditions are unchanged and are about honesty of a different kind: mid-page the
 * sentence describes a turn that has not finished, and with nothing on screen it is a claim about
 * a grid that is not there.
 */
export function showsFootnote(
  status: ChatStatus,
  appending: boolean,
  shownCount: number,
  settledAnswer = false,
): boolean {
  if (appending || shownCount <= 0) return false;
  if (status === "done" || status === "resumed") return true;
  // F6-06's third casualty. A Stop over a held answer withdrew the provenance line along with the
  // tray and the paging, leaving a grid with no account of where it came from — for an answer
  // nothing had replaced and whose sentence was still perfectly true. Offered only over a settled
  // answer: a stopped turn's own partial has no finished sentence to print, and `reason` is null
  // there in any case.
  return status === "stopped" && settledAnswer;
}

/**
 * The floor, formatted for the tray label — always one decimal, always the same string.
 *
 * `toLocaleString(undefined, { maximumFractionDigits: 1 })` printed "6" in the tray while the
 * server's own sentence forty pixels below it read "6.0 bar", and on a comma-decimal locale it
 * printed "6,5" beside the server's "6.5". Both halves of that are fixed by pinning the locale
 * and the fraction digits: this is chrome, but it is chrome quoting the SAME number the server
 * quotes, so it renders as an invariant one-decimal string. Display only — it is never sent
 * anywhere, never a cache key, never a filter row.
 */
const FLOOR_FORMAT = new Intl.NumberFormat("en-US", { minimumFractionDigits: 1, maximumFractionDigits: 1 });

export function formatFloor(floor: number | null | undefined): string | null {
  return typeof floor === "number" && Number.isFinite(floor) ? FLOOR_FORMAT.format(floor) : null;
}

/**
 * What a failed turn says for itself, in one line.
 *
 * Three cases, not two, and the first of them is narrower than it looks. `client_aborted` is what
 * the server writes when the client's connection went away — and it goes away identically for a
 * Stop, a reload, a slept phone and a discarded tab. The server cannot tell those apart, so
 * neither can this sentence.
 *
 * F6-09 is what saying otherwise cost: a finder hard-reloaded a running turn and the resumed
 * transcript told them "you stopped it", permanently, across two further reloads. The wording was
 * ACCURATE for a real Stop — verified in the same session — which is exactly what makes the class
 * dangerous: copy that is right most of the time and asserts a cause the client cannot know. "You
 * left" is true of all four, and it is still specific enough to be worth reading.
 *
 * Any other code is a thing that happened TO them; no code at all is neither, so it claims
 * neither. The silent case is not hypothetical: the resume payload projects `sequence`, `role`,
 * `text`, `kind` and `status` and no `failureCode`, so today every resumed failure arrives
 * unattributed. The wording lights up the moment the field does.
 */
export function failedTurnMessage(failureCode: string | null): string {
  if (failureCode === "client_aborted") return "That one didn’t finish before you left.";
  if (failureCode) return "That one hit a snag.";
  return "That one didn’t finish.";
}

/**
 * The excluded terms a conflict is about — the account's excluded genres and its avoid-keywords,
 * as one list for one sentence.
 *
 * They are two lists server-side and one fact to a reader: "your conditions rule out comedy and
 * anime" does not care which list a term came from. A term can legitimately be in both (a genre
 * the account excludes AND an avoid-keyword it lists), and naming it twice reads as a bug in the
 * very place the product is explaining itself — so the merge is case-insensitively deduplicated,
 * keeping the first spelling. Blanks are dropped: "rules out  and anime" is worse than saying less.
 */
export function excludedTerms(genres: string[] | undefined, keywords: string[] | undefined): string[] {
  const seen = new Set<string>();
  const out: string[] = [];
  for (const raw of [...(genres ?? []), ...(keywords ?? [])]) {
    const term = raw.trim();
    const key = term.toLowerCase();
    if (!term || seen.has(key)) continue;
    seen.add(key);
    out.push(term);
  }
  return out;
}

/**
 * The clause the server writes into a reply it has qualified — and the whole reason the pill is
 * conditional.
 *
 * `AskReelStandingFilters` rewrites the model's sentence as "… — under your conditions that means
 * no comedy and titles from 2000 on." and sets `conflict` on the same frame. The pill renders off
 * that flag, so the bubble read, verbatim:
 *
 *   … under your conditions that means no comedy and titles from 2000 on.**under your conditions**
 *
 * — the annotation repeating the sentence it annotates, with no space between them.
 */
const CONFLICT_CLAUSE = /under your conditions/i;

/**
 * Should the qualified-reply pill render on THIS bubble? F4-06's first half.
 *
 * The flag says the server rewrote the sentence; it does not say what the rewrite looks like. When
 * the sentence already carries the clause, the pill is a second copy of it and the reader gets the
 * same three words twice in a row — so the sentence keeps it and the pill stands down. The pill
 * survives for the case it was actually useful in: a reply that is marked as qualified without
 * saying so in its own words, which is what an older server and a stored row from before the
 * rewrite both look like. Scanning the reply rather than dropping the pill outright is what keeps
 * THAT bubble marked.
 *
 * Case-insensitive and position-free on purpose: the clause is mid-sentence today ("— under your
 * conditions that means …") and was sentence-final before, and a rule that only recognised one of
 * those would put the duplicate back the next time the copy moves.
 */
export function showsConflictPill(text: string): boolean {
  return !CONFLICT_CLAUSE.test(text);
}

/** "comedy" · "comedy and anime" · "comedy, anime and musical". A list a person can read. */
export function listPhrase(items: string[]): string {
  if (items.length === 0) return "";
  if (items.length === 1) return items[0];
  return `${items.slice(0, -1).join(", ")} and ${items[items.length - 1]}`;
}

/**
 * The resume note for cards that left a stored answer (R1-11). "Since" is said only of the cards
 * the account acted on AFTER the answer was given: a resumed Nolan answer (a person query shows
 * seen titles) lost cards rated in 2020–2025 under a 2026 answer, and "you’ve since rated…" about
 * them was false. Null when nothing left.
 */
export function removedNote(removed: number, removedEarlier: number): string | null {
  if (removed <= 0) return null;
  const earlier = Math.max(0, Math.min(removedEarlier, removed));
  const since = removed - earlier;
  const head = `${removed} ${removed === 1 ? "pick is" : "picks are"} gone from this answer`;
  const acts = "rated, watched, saved or dismissed";
  if (earlier === 0) return `${head} — you’ve since ${acts} ${removed === 1 ? "it" : "them"}.`;
  if (since === 0) return `${head} — you’d already ${acts} ${removed === 1 ? "it" : "them"} when it was given.`;
  return `${head} — you’d already ${acts} ${earlier} of them when it was given, and you’ve acted on ${since} since.`;
}

/**
 * What the refine chips are built from: the planner's reading of the answer on screen (its
 * concepts — the user's own topic words, §4.9 `plan`) and the account's standing conditions.
 */
export interface RefineInputs {
  concepts: string[];
  /** Whether there is a card on screen for "the first one" to point at. */
  hasCards: boolean;
  excludedGenres: string[];
  minYear: number | null;
  maxYear: number | null;
}

/**
 * The refine chips — generated, not a fixed list (WS-2 task 3, A-15).
 *
 * The fixed three ("Something lighter", "More intense", "More like the first one") were the same
 * under a ninja answer and a rom-com one, and two of them could be offered against a standing
 * condition that guaranteed a conflict notice: "lighter" reads as comedy (deterministically, WS-2
 * 1f), so on an account that excludes comedy the chip asked for the one thing the account had ruled
 * out. Chips now name the topic the user asked about, and a direction a condition rules out is not
 * offered at all: lighter against an excluded comedy, newer against a standing latest year, older
 * against a standing earliest one. "More like the first one" needs a first one.
 *
 * Deterministic and capped at four, so the row's height is stable from one answer to the next.
 */
export function refineChips(input: RefineInputs): string[] {
  const excluded = new Set(input.excludedGenres.map((g) => g.trim().toLowerCase()));
  const lighterAllowed = !excluded.has("comedy");
  const topic = input.concepts.map((c) => c.trim()).filter(Boolean).slice(0, 2).join(" ");
  const chips: string[] = [];
  if (topic) {
    if (lighterAllowed) chips.push(`Lighter ${topic} picks`);
    if (input.maxYear == null) chips.push(`Newer ${topic} picks`);
    if (input.minYear == null) chips.push(`Older ${topic} picks`);
  } else {
    if (lighterAllowed) chips.push("Something lighter");
    chips.push("More intense");
  }
  if (input.hasCards) chips.push("More like the first one");
  return chips.slice(0, 4);
}

/**
 * The narration line, driven by `phase` frames (WS-2 task 3).
 *
 * A v2 phase carries the engine's own sentence (`label`) and it is printed verbatim: the server
 * knows what it is doing ("Checking 6 against your request") and the client would only be guessing.
 * A v1 phase carries a stage and counts, narrated here. Every counted noun goes through `plural` —
 * a narrow first turn genuinely does reach "Found 1 titles", and the live region reads it aloud.
 */
export function narrate(phase: { stage: string; found: number; label?: string } | null): string {
  if (phase?.label?.trim()) return phase.label.trim();
  switch (phase?.stage) {
    case "planning":
      return "Reading your request…";
    case "searching":
    case "retrieving":
      return "Scanning the catalogue…";
    case "discovered":
      return `Found ${phase.found} ${plural(phase.found, "title")} — matching them to your taste…`;
    case "embedding":
      return `Reading ${phase.found} ${plural(phase.found, "plot")}…`;
    case "scoring":
      return `Scoring ${phase.found} ${plural(phase.found, "pick")} for you…`;
    case "judging":
      return "Checking each one against your request…";
    case "ranking":
      return "Ranking your picks…";
    case "composing":
      return "Writing it up…";
    default:
      return "Thinking…";
  }
}

/**
 * What a stopped turn says, in one place — the live region and the on-screen card must not be able
 * to disagree, and the count is the turn's OWN: cards held over from the previous answer are on
 * screen and are explicitly not what arrived before the user stopped. Picks from a turn that never
 * reached `done` are unranked, and the sentence says so.
 */
export function stoppedMessage(arrived: number): string {
  return arrived === 0
    ? "Stopped before any picks arrived."
    : `Stopped — ${arrived} ${plural(arrived, "pick")} arrived before you stopped. Unranked — stopped before Reel finished.`;
}

/**
 * The near-miss tray's heading — FD-2's "Close, but not X", with X the topic the user asked for
 * when the planner named one, and the plain wording when it did not.
 */
export function nearMissHeading(concepts: readonly string[]): string {
  const topic = concepts.map((c) => c.trim()).filter(Boolean).slice(0, 2).join(" ");
  return topic ? `Close, but not ${topic}` : "Close, but not quite";
}

/** The summary of an earlier answer kept under its own reply: what is inside, before it is opened. */
export function earlierAnswerSummary(picks: number, trayed: number): string {
  const head = picks > 0 ? `${picks} ${plural(picks, "pick")}` : "No picks";
  return trayed > 0 ? `Earlier answer · ${head} · ${trayed} in its trays` : `Earlier answer · ${head}`;
}

/**
 * What a failed `/ask` says, by cause — the full card's title and line, the one-liner shown under
 * an answer that is still on screen, and whether "Try again" can work.
 *
 * The causes are the ones `failureFor` can name, and each is a different thing to do: a limit is
 * waited out (and says for how long), a lapsed session is signed back into (a retry cannot fix it),
 * a timeout and an unnamed failure are retried, a malformed request is reworded. One "Search hit a
 * snag — Reel couldn’t reach the search engine" for all of them told a reader who had simply asked
 * too often that the engine was down (WS-8 CR-5), and told a signed-out one to keep retrying.
 */
export interface FailureCopy {
  title: string;
  detail: string;
  inline: string;
  retryable: boolean;
}

const RATE_WINDOW: Record<NonNullable<RateLimit["window"]>, string> = { minute: "a minute", hour: "an hour", day: "a day" };

export function failureCopy(reason: ErrorReason, rateLimit: RateLimit | null = null): FailureCopy {
  switch (reason) {
    case "rate": {
      const per = rateLimit?.window ? RATE_WINDOW[rateLimit.window] : null;
      const when = waitPhrase(rateLimit?.retryAfterSeconds ?? null);
      const title = per ? `That’s the most questions Reel answers in ${per}` : "Reel is answering too many of your questions at once";
      const detail = when ? `You can ask again ${when}. Nothing was lost — your conversation is as you left it.` : "Give it a moment, then ask again. Nothing was lost — your conversation is as you left it.";
      return { title, detail, inline: when ? `${title} — you can ask again ${when}.` : `${title} — give it a moment.`, retryable: true };
    }
    case "session":
      return {
        title: "You’re signed out",
        detail: "Your session ended before Reel could answer. Sign in again to keep asking — your conversations are saved.",
        inline: "You’re signed out — sign in again to keep asking.",
        retryable: false,
      };
    case "timeout":
      return {
        title: "That took too long",
        detail: "Reel stopped waiting for an answer. Asking again usually works.",
        inline: "That took too long — asking again usually works.",
        retryable: true,
      };
    case "request":
      return {
        title: "Reel couldn’t take that question",
        detail: "Something about the request was off. Try asking it another way.",
        inline: "Reel couldn’t take that request — try asking it another way.",
        retryable: false,
      };
    default:
      return {
        title: "Search hit a snag",
        detail: "Reel couldn’t finish that answer. It’s usually momentary.",
        inline: "That last request hit a snag.",
        retryable: true,
      };
  }
}

/** "in 40 seconds", "in about 3 minutes", "in about 5 hours" — or null when no wait was named. */
export function waitPhrase(seconds: number | null): string | null {
  if (seconds == null || !Number.isFinite(seconds) || seconds <= 0) return null;
  if (seconds < 60) {
    const s = Math.ceil(seconds);
    return `in ${s} ${plural(s, "second")}`;
  }
  if (seconds < 3600) {
    const m = Math.ceil(seconds / 60);
    return m === 1 ? "in about a minute" : `in about ${m} minutes`;
  }
  const h = Math.ceil(seconds / 3600);
  return h === 1 ? "in about an hour" : `in about ${h} hours`;
}
