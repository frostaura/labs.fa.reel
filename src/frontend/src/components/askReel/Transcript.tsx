import { Fragment, useLayoutEffect, useRef, useState, type ReactNode, type RefObject } from "react";
import { Sparkles } from "lucide-react";
import CardTray from "./CardTray";
import HoldLiveTurn from "./HoldLiveTurn";
import PillButton from "./PillButton";
import { ConflictNotice, RetryButton } from "./parts";
import { earlierAnswerSummary, failedTurnMessage, showsConflictPill } from "../../lib/askReel/copy";
import { answerHasCards, bubbleOverflows, questionFor, showsInlineFailure, type ChatTurn, type TurnAnswer } from "../../lib/askReel/transcript";
import type { AskReelChat } from "../../lib/askReel/useAskReelChat";
import type { AskCard } from "../../lib/askReelStream";
import type { AskView } from "./view";

/**
 * The conversation, one row per thing said — and, under each reply, the cards that reply produced
 * (chat UI v2, WS-2 task 3). The live answer renders under the row that produced it (`gridRow`);
 * every earlier answer stays under its own reply, collapsed, so a thread keeps everything it found
 * instead of the newest question erasing the rest.
 *
 * A `log` landmark, announced politely: Reel's replies — answers, clarifying questions, the
 * degraded "couldn’t read that fully" — arrive whenever the model finishes, and a screen-reader
 * user is told about them only if the region says it updates. The card blocks inside opt out
 * (`aria-live="off"`), or every streamed card would be read as part of the conversation. Speaker
 * labels are sr-only: sighted users read the bubble alignment and the sparkle for the same thing.
 */
export default function Transcript({
  chat,
  view,
  liveSlot,
  answer,
  liveAnchor,
  watchExpired,
  onSaveTapped,
}: {
  chat: AskReelChat;
  view: AskView;
  /** The live turn's status block — banner, stop and blocked cards — after the newest row. */
  liveSlot: ReactNode;
  /** The live answer, rendered under the row that produced it. */
  answer: ReactNode;
  /**
   * Where the page scrolls when a turn starts (F6-02): just above the newest QUESTION, so the first
   * screen of a turn is the question, then Reel's reply, the narration and the cards — in that
   * order, with nothing of the new turn inserted above the viewport (WS-2 task 3, mobile: "the
   * first screen shows the reply plus at least one row of posters"). An object ref, because
   * {@link HoldLiveTurn} reads the same element to keep the live turn still while the earlier
   * answer folds above it (HREV6-PH-01).
   */
  liveAnchor: RefObject<HTMLDivElement | null>;
  watchExpired: boolean;
  onSaveTapped: (card: AskCard) => void;
}) {
  const { turns, status, canRetry, reask, send, gridRow, resumedTail } = chat;
  const lastIndex = turns.length - 1;
  let lastQuestion = -1;
  turns.forEach((t, i) => {
    if (t.role === "user") lastQuestion = i;
  });
  // `scroll-mt` clears the sticky header, which would otherwise sit on top of the question.
  const anchor = <div ref={liveAnchor} className="scroll-mt-20" aria-hidden data-testid="askreel-live-anchor" />;
  const streaming = view.streaming;

  return (
    <HoldLiveTurn anchor={liveAnchor} gridRow={gridRow} active={streaming}>
      <div className="space-y-3" data-testid="askreel-transcript" role="log" aria-live="polite">
        {turns.map((turn, i) => (
          <Fragment key={i}>
            {i === lastQuestion && anchor}
            {row(turn, i)}
            {/* The live turn's block sits under the newest row, and the live answer under the row
                that produced it — after the slot when that is the newest row, so the banner hands
                over to the provenance line ABOVE the grid in one box and the grid never moves
                (F6-04's residual). */}
            {i === lastIndex && liveSlot}
            {i === gridRow && answer}
          </Fragment>
        ))}
        {turns.length === 0 && (
          <>
            {anchor}
            {liveSlot}
            {answer}
          </>
        )}
        {/* A turn that is not finished, it is still running — nothing to retry and nothing to
            apologise for; the page's poll finds out. */}
        {resumedTail?.status === "pending" && (
          <p className="fa-caption text-fa-frost-dim" data-testid="askreel-pending-turn">
            {watchExpired ? "That one is taking a while. Ask again to start over." : "Still working on that one…"}
          </p>
        )}
      </div>
    </HoldLiveTurn>
  );

  function row(turn: ChatTurn, i: number) {
    if (turn.role === "user") return <UserBubble text={turn.text} />;
    if (turn.failed) {
      // R5-02. A turn that did not finish is a row, not an absence — except the one the stopped
      // card is about (`showsInlineFailure`). "Ask again" replays THIS row's question: on the tail
      // that is `lastQuery` and `reask` replaces it; further back it is a new ask with its bubble.
      return (
        <>
          {showsInlineFailure(turn, i === lastIndex, status) && (
            <div className="flex flex-wrap items-center gap-3" data-testid="askreel-failed-turn">
              <p className="fa-caption text-fa-frost-dim">{failedTurnMessage(turn.failureCode ?? null)}</p>
              {(i === lastIndex ? canRetry : questionFor(turns, i).length > 0) && (
                <RetryButton
                  onRetry={i === lastIndex ? reask : () => send(questionFor(turns, i), false)}
                  disabled={streaming}
                  label="Ask again"
                />
              )}
            </div>
          )}
          {i !== gridRow && <EarlierAnswer answer={turn.answer} onSaveTapped={onSaveTapped} />}
        </>
      );
    }
    return (
      <div className="space-y-2">
        <div className="flex items-start gap-3 max-w-[85%]" data-testid="askreel-reply">
          <Sparkles className="h-5 w-5 text-fa-frost shrink-0 mt-1" />
          <p className="fa-body text-fa-frost-bright">
            <span className="sr-only">Reel said</span>
            {turn.text}
            {/* A degraded reply is the server's own "I couldn't read that" — labelled, so it is
                never mistaken for an answer about the cards under it. */}
            {turn.degraded && (
              <span
                data-testid="askreel-reply-degraded"
                className="fa-caption ml-2 inline-flex items-center rounded-full border border-fa-warning/40 px-2 py-0.5 align-middle text-fa-warning"
              >
                couldn’t read that fully
              </span>
            )}
            {/* A judge batch failed on this turn: some of its cards were never checked against the
                request, and the bubble says so rather than vouching for them. */}
            {turn.unchecked && (
              <span
                data-testid="askreel-reply-unchecked"
                className="fa-caption ml-2 inline-flex items-center rounded-full border border-fa-warning/40 px-2 py-0.5 align-middle text-fa-warning"
              >
                some cards unchecked
              </span>
            )}
            {/* The server rewrote this sentence; the pill marks it — unless the sentence already
                says so itself (F4-06, `showsConflictPill`). */}
            {turn.conflict && showsConflictPill(turn.text) && (
              <span
                data-testid="askreel-reply-conflict"
                className="fa-caption ml-2 inline-flex items-center rounded-full border border-fa-warning/40 px-2 py-0.5 align-middle text-fa-warning"
              >
                under your conditions
              </span>
            )}
          </p>
        </div>
        {/* A clarifying question offers its readings as answers — one tap asks it. Only on the
            newest row, and only while nothing is running: an old question's options answer a
            conversation that has moved on. */}
        {turn.clarifyOptions && turn.clarifyOptions.length > 0 && i === lastIndex && (
          <div className="flex flex-wrap gap-2 pl-8" data-testid="askreel-clarify-options">
            {turn.clarifyOptions.map((option) => (
              <PillButton
                key={option}
                tone="accent"
                data-testid="askreel-clarify-option"
                onClick={() => send(option, false)}
                disabled={streaming}
                className="whitespace-normal py-2 text-left"
              >
                {option}
              </PillButton>
            ))}
          </div>
        )}
        {/* The answer's notice goes under the reply the GRID belongs to (R5-01: `answerRowIndex`
            skips a failed row, so live and resumed agree); the running turn's own goes under its
            own reply. On a fresh turn they are the same row, and only one notice goes on it. */}
        {i === view.answerIndex && view.conflictMessage && (
          <ConflictNotice message={view.conflictMessage} terms={chat.conflictTerms} />
        )}
        {i === view.liveReplyIndex && view.showsTurnConflict && view.turnConflictMessage &&
          !(view.conflictMessage && i === view.answerIndex) && (
          <ConflictNotice message={view.turnConflictMessage} terms={chat.turnConflictTerms} />
        )}
        {i !== gridRow && <EarlierAnswer answer={turn.answer} onSaveTapped={onSaveTapped} />}
      </div>
    );
  }
}

/**
 * An earlier answer, kept under the reply that produced it — collapsed, so the newest answer is the
 * one expanded, and openable from the keyboard like every tray. Its cards are the answer as it stood
 * when the thread moved past it.
 */
function EarlierAnswer({ answer, onSaveTapped }: { answer: TurnAnswer | undefined; onSaveTapped: (card: AskCard) => void }) {
  if (!answerHasCards(answer)) return null;
  const trayed = answer.nearMiss.length + answer.belowFloor.length + answer.unchecked.length;
  const cards = [...answer.cards, ...answer.belowFloor, ...answer.nearMiss, ...answer.unchecked];
  return (
    <div aria-live="off" className="pl-8" data-testid="askreel-earlier-answer">
      <CardTray
        testId="askreel-earlier"
        toggleTestId="askreel-earlier-toggle"
        summary={earlierAnswerSummary(answer.cards.length, trayed)}
        heading="Earlier answer"
        caption={trayed > 0
          ? "What this reply showed, its picks first and then what it kept in its trays."
          : "What this reply showed. Your newest answer is the one below."}
        cards={cards}
        onSaveTapped={onSaveTapped}
      />
    </div>
  );
}

/**
 * One question the user asked, capped at six lines (F6-02's other half): the rest is one tap away,
 * and the toggle is offered only when there IS a rest, measured rather than inferred from the
 * character count. The latch matters: expanding removes the clamp, so it is measured only while
 * collapsed.
 */
function UserBubble({ text }: { text: string }) {
  const textRef = useRef<HTMLSpanElement>(null);
  const [expanded, setExpanded] = useState(false);
  const [clampable, setClampable] = useState(false);

  useLayoutEffect(() => {
    const el = textRef.current;
    if (!el || expanded) return;
    setClampable(bubbleOverflows(el.scrollHeight, el.clientHeight));
  }, [text, expanded]);

  return (
    <div className="flex flex-col items-end gap-1">
      <p
        className="fa-body bg-fa-frost/15 text-fa-frost-bright rounded-2xl rounded-br-sm px-4 py-2 max-w-[80%]"
        data-testid="askreel-user-bubble"
      >
        <span className="sr-only">You said</span>
        {/* Exclusive classes: `line-clamp-6` sets `display: -webkit-box`, and a `block` beside it
            can win on stylesheet order and silently un-clamp the bubble. */}
        <span ref={textRef} className={expanded ? "block whitespace-pre-wrap" : "whitespace-pre-wrap line-clamp-6"}>
          {text}
        </span>
      </p>
      {clampable && (
        <PillButton
          tone="quiet"
          data-testid="askreel-bubble-toggle"
          aria-expanded={expanded}
          onClick={() => setExpanded((v) => !v)}
        >
          {expanded ? "Show less" : "Show more"}
        </PillButton>
      )}
    </div>
  );
}
