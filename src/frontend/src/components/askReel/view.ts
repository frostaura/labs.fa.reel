import { floorPartition, type ChatStatus } from "../../lib/askReel/reducer";
import { failureCopy, floorHeldBack, footnote, formatFloor, noneFoundAnnouncement, pickSummary, resumeAnnouncement, showsFootnote, stoppedMessage, type NoneFoundShown, type StripBranch } from "../../lib/askReel/copy";
import { answerRowIndex, leavesGrid } from "../../lib/askReel/transcript";
import { pageRefusalMessage } from "../../lib/askReel/paging";
import { skeletonCount, type AskReelChat } from "../../lib/askReel/useAskReelChat";
import type { AskCard } from "../../lib/askReelStream";

/**
 * Everything the Ask Reel page derives from the hook before it renders — one pure pass, so the
 * page, the transcript and the answer strip read the same numbers instead of each re-deriving
 * their own (WS-2 task 5 split the page into Transcript, AnswerStrip and Composer; this is what
 * keeps the three agreeing).
 */
export interface AskView {
  streaming: boolean;
  stopped: boolean;
  /** The removed-titles set the store holds — saved, dismissed or rated since they were shown. */
  removedSet: ReadonlySet<string>;
  /** Clean picks on screen, after removals and the client floor. */
  visible: AskCard[];
  /** The closest-but-outside channel, on the same terms. */
  outsideVisible: AskCard[];
  /** How much of the grid is still being held open for this turn (F6-04). */
  pendingSlots: number;
  /**
   * Skeletons reserved under the NEW question while the previous answer is held under its own
   * reply — the cells this turn's cards will fill, so nothing below moves when they land.
   */
  reserveSlots: number;
  belowFloorVisible: AskCard[];
  heldBack: number;
  floorLabel: string | null;
  nearMissVisible: AskCard[];
  uncheckedVisible: AskCard[];
  conflictMessage: string | null;
  turnConflictMessage: string | null;
  answerIndex: number;
  liveReplyIndex: number;
  showsTurnConflict: boolean;
  pickCount: number;
  keptWholeAnswer: boolean;
  footnoteText: string | null;
  showsProvenance: boolean;
  /**
   * Whether the provenance line goes in the live slot (the box the banner hands over to, F6-04) —
   * true when the grid is the newest row's. When the grid stays under an EARLIER reply (a kept
   * answer after a Stop, an item question answered beside it), the line goes with the grid it
   * describes rather than under a turn that has nothing to do with it.
   */
  footnoteInSlot: boolean;
  stoppedText: string;
  refusalText: string | null;
  /**
   * Which of the strip's bodies is on screen. The strip renders from it and the status region
   * announces from it — one derivation, so the region never describes a card that is not there
   * (HREV5-PH-01-R1).
   */
  branch: StripBranch;
  /** What the polite status region says once the stream stops. */
  announcement: string;
}

export interface AskViewInputs {
  removedKeys: readonly string[];
  /** The account's For-you bar, from its saved conditions; null when none is set. */
  floorBar: number | null | undefined;
}

/** Acts that answer beside the grid, for which reserving cells under the question would be a lie. */
const BESIDE_GRID_ACTS = new Set(["ItemQuestion", "Chitchat"]);

export function deriveAskView(chat: AskReelChat, inputs: AskViewInputs): AskView {
  const { status, turns, holdingPrevious } = chat;
  const removedSet = new Set(inputs.removedKeys);
  const streaming = status === "streaming";
  const stopped = status === "stopped";

  // The floor, on a turn that never reached `done` (F4-08) — bounded to the ids neither the server
  // finished (`settled`) nor already gated (v2 `pinned`): see `clientFloorable`.
  const floorable = new Set(chat.clientFloorable);
  const clientFloor = streaming || stopped ? inputs.floorBar : null;
  const cleanPartition = floorPartition(chat.cards.filter((c) => !removedSet.has(c.titleId)), clientFloor, floorable);
  const outsidePartition = floorPartition(chat.outsideCards.filter((c) => !removedSet.has(c.titleId)), clientFloor, floorable);
  const visible = cleanPartition.kept;
  const outsideVisible = outsidePartition.kept;
  const clientHeldBack = [...cleanPartition.held, ...outsidePartition.held];

  const pendingSlots = streaming && !holdingPrevious ? Math.max(0, skeletonCount(chat.phase) - visible.length) : 0;
  // While the previous answer is held, the new turn's cells are reserved under its question — once
  // the turn has said it is searching (a phase), and never for an act that answers beside the grid.
  const besideGrid = chat.plan != null && BESIDE_GRID_ACTS.has(chat.plan.act);
  const reserveSlots = streaming && holdingPrevious && chat.phase != null && !besideGrid ? skeletonCount(chat.phase) : 0;

  const belowFloorVisible = [...chat.belowFloor.filter((c) => !removedSet.has(c.titleId)), ...clientHeldBack];
  const heldBack = floorHeldBack(belowFloorVisible.length, stopped ? null : chat.droppedByFloor);

  const conflictMessage = chat.conflict?.trim() ? chat.conflict : null;
  const turnConflictMessage = chat.turnConflict?.trim() ? chat.turnConflict : null;
  const last = turns.at(-1);
  const liveReplyIndex = last?.role === "assistant" && !last.failed ? turns.length - 1 : -1;

  const pickCount = visible.length;
  const settledCount = pickCount + outsideVisible.length;
  const keptWholeAnswer = stopped && chat.arrived === 0 && chat.settledAnswer && settledCount > 0;
  const footnoteText = footnote(chat.reason, pickCount, chat.paged);
  const showsProvenance = !!footnoteText && showsFootnote(status, chat.appending, settledCount, chat.settledAnswer);
  const stoppedText = stoppedMessage(chat.arrived);
  const refusalText = pageRefusalMessage(chat.pageRefusal);

  const floorLabel = formatFloor(inputs.floorBar);
  const nearMissVisible = chat.nearMiss.filter((c) => !removedSet.has(c.titleId));
  const uncheckedVisible = chat.unchecked.filter((c) => !removedSet.has(c.titleId));
  // What the none-found card has beside it — the same arrays its heading and trays are built from.
  const shown: NoneFoundShown = { unchecked: uncheckedVisible.length, nearMiss: nearMissVisible.length, heldBack, floorLabel };
  const branch = stripBranch(chat, visible, outsideVisible);

  return {
    streaming, stopped, removedSet, visible, outsideVisible, pendingSlots, reserveSlots,
    belowFloorVisible, heldBack, floorLabel, nearMissVisible, uncheckedVisible,
    conflictMessage, turnConflictMessage, answerIndex: answerRowIndex(turns), liveReplyIndex,
    showsTurnConflict: !!turnConflictMessage && streaming && !chat.appending,
    pickCount, keptWholeAnswer, footnoteText, showsProvenance, stoppedText, refusalText,
    footnoteInSlot: chat.gridRow >= turns.length - 1,
    branch,
    announcement: announce(status, branch, refusalText, stoppedText, pickCount, outsideVisible.length, failureCopy(chat.errorReason, chat.rateLimit).title, shown, {
      removed: chat.removed, removedEarlier: chat.removedEarlier,
    }),
  };
}

/**
 * Which body the answer strip renders — exactly one is live, in this order. The strip switches on
 * it and `announce` reads it, so the status region follows the card on screen rather than
 * re-deriving it from a field that only usually agrees (HREV5-PH-01-R1: `lastCardsAt` alone
 * announced the all-removed card over a restored none-found one).
 */
export function stripBranch(chat: AskReelChat, visible: readonly AskCard[], outsideVisible: readonly AskCard[]): StripBranch {
  const { status, mode } = chat;
  if (status === "error" && chat.errorReason === "conversation") return "conversation-gone";
  if (status === "error" && chat.cards.length === 0) return "failed";
  const settledAnswer = status === "done" || status === "resumed";
  if (settledAnswer && mode === "NoneFound" && visible.length === 0) return "none-found";
  if (settledAnswer && leavesGrid(mode) && visible.length === 0) return "beside-grid";
  const nothingShown = visible.length === 0 && outsideVisible.length === 0;
  if (status === "resumed" && nothingShown && chat.lastCardsAt == null && chat.resumedTail == null) return "transcript-only";
  if (status === "resumed" && nothingShown && chat.lastCardsAt != null) return "all-removed";
  if (status === "done" && nothingShown) return "empty";
  return "grid";
}

/**
 * The settled outcome, announced once the stream stops — separate from the streaming narration so
 * the two live regions never talk over each other. Two counts, because they mean two different
 * things and folding them together is F4-05.
 */
function announce(
  status: ChatStatus, branch: StripBranch, refusal: string | null, stopped: string, picks: number, outside: number, failure: string,
  shown: NoneFoundShown, removal: { removed: number; removedEarlier: number },
): string {
  if (status === "streaming") return "";
  // The refusal is what the region says while it stands (R5-04): the state underneath is the one
  // the screen was already in, and announcing THAT again says nothing about the gesture that failed.
  if (refusal) return refusal;
  // The failure's own title — a turn limit is announced as one, not as a crash.
  if (status === "error") return `${failure}.`;
  if (status === "blocked") return "That question is still being answered.";
  if (status === "stopped") return stopped;
  // F6-15: a resumed landing is announced as the seam shows it — and, with nothing left on screen,
  // as the card the strip renders under it does (HREV5-PH-01, HREV5-PH-01-R1).
  if (status === "resumed") return resumeAnnouncement(picks, outside, { branch, ...removal, shown });
  if (status !== "done") return "";
  // An honest "nothing left" is announced as one, not as "No matches": the reply beside it gives
  // the counts, and the none-found card the relaxations (WS-2 task 3). It follows the card's own
  // branch — unchecked titles on screen are not "nothing left" (HREV4-PH-01).
  if (branch === "none-found") return noneFoundAnnouncement(shown);
  // A reply that answered beside an EMPTY grid — small talk, a title asked about by name, a question
  // back — is not a search, and "No matches for that one." would announce one that never ran (the
  // R1-04 screen, said aloud). The reply itself is already read out by the transcript's polite log.
  if (branch === "beside-grid") return "";
  return pickSummary(picks, outside);
}
