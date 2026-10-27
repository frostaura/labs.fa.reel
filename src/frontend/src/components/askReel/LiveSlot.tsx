import type { Ref } from "react";
import { Sparkles, Square } from "lucide-react";
import PillButton from "./PillButton";
import { CARD_GRID, CardSkeleton, ConflictNotice, RetryButton } from "./parts";
import { narrate } from "../../lib/askReel/copy";
import type { AskReelChat } from "../../lib/askReel/useAskReelChat";
import type { AskView } from "./view";

/**
 * The live turn's block, under the newest row: the 409 card, the turn-status slot (banner while it
 * runs, provenance line once it settles — one box so the grid under it never moves, F6-04), the
 * collision fallbacks, the stopped card, and — while the previous answer is held under its own
 * reply — the cells this turn's cards will fill.
 *
 * `settledRef` is where the page scrolls when a Stop leaves the reader with what arrived (F6-02).
 */
export default function LiveSlot({
  chat,
  view,
  settledRef,
}: {
  chat: AskReelChat;
  view: AskView;
  settledRef: Ref<HTMLDivElement>;
}) {
  const { status, phase, stop, retryTurn, reask, canRetry } = chat;
  const streaming = view.streaming;
  return (
    <div className="space-y-5" aria-live="off" data-testid="askreel-live">
      {/* 409 — the same question is already being answered. Nothing failed and nothing is cleared;
          "Check again" re-sends the SAME turn id. */}
      {status === "blocked" && (
        <div className="fa-card p-4 flex flex-wrap items-center justify-center gap-3 text-center" data-testid="askreel-in-progress">
          <p className="fa-caption text-fa-frost-dim">That question is already being answered — give it a moment.</p>
          <RetryButton onRetry={retryTurn} disabled={streaming} label="Check again" />
        </div>
      )}

      {(streaming || (view.showsProvenance && view.footnoteInSlot)) && (
        <div className="flex min-h-[4.875rem] flex-col justify-center" data-testid="askreel-turn-status">
          {streaming ? (
            <div data-testid="askreel-banner" className="fa-card relative overflow-hidden p-4 flex items-center gap-4">
              <span className="relative flex h-8 w-8 items-center justify-center shrink-0" aria-hidden>
                <span className="absolute inline-flex h-full w-full rounded-full bg-fa-frost/20 animate-ping" />
                <Sparkles className="h-4 w-4 text-fa-frost" />
              </span>
              <div className="flex-1 min-w-0">
                {/* The narration line, driven by `phase` frames — the engine's own label where the
                    v2 engine sends one. Live, because it is the only feedback a screen-reader user
                    gets while Reel is working. */}
                <p data-testid="askreel-narration" aria-live="polite" aria-atomic="true" className="fa-caption text-fa-frost-bright truncate">
                  {narrate(phase)}
                </p>
                {/* The sweep only moves inside a positioning context (`fa-progress-slide` animates
                    `left`). */}
                <div className="relative mt-2 h-1 w-full overflow-hidden rounded bg-fa-frost/10" aria-hidden>
                  <div className="absolute inset-y-0 rounded bg-fa-frost/60 fa-progress-slide" />
                </div>
              </div>
              {phase && phase.found > 0 && (
                <span className="fa-caption text-fa-frost-dim shrink-0 tabular-nums">
                  {phase.scored > 0 ? `${phase.scored} scored` : `${phase.found} found`}
                </span>
              )}
              <PillButton tone="quiet" onClick={stop} data-testid="askreel-stop" aria-label="Stop searching" className="shrink-0">
                <Square className="h-3 w-3" aria-hidden /> Stop
              </PillButton>
            </div>
          ) : (
            /* The provenance line, in the box the banner just vacated — at body size (14 px), not
               caption: it is the account of where the answer came from, and it was the smallest
               type on the page (WS-2 task 3). `footnote` reconciles a paged answer (R1-03). */
            <p className="fa-body text-fa-frost-dim text-center" data-testid="askreel-footnote">{view.footnoteText}</p>
          )}
        </div>
      )}

      {/* The collision notice's FALLBACK position — when there is no reply to attach it to. */}
      {view.conflictMessage && view.answerIndex < 0 && (
        <ConflictNotice message={view.conflictMessage} terms={chat.conflictTerms} />
      )}
      {view.showsTurnConflict && view.turnConflictMessage && view.liveReplyIndex < 0 && !(view.conflictMessage && view.answerIndex < 0) && (
        <ConflictNotice message={view.turnConflictMessage} terms={chat.turnConflictTerms} />
      )}

      {/* A stopped turn is a state, not an absence: the way back, above whatever did arrive. */}
      {status === "stopped" && (
        <div ref={settledRef} className="fa-card p-4 flex flex-wrap items-center justify-center gap-3 text-center scroll-mt-20" data-testid="askreel-stopped">
          <p className="fa-caption text-fa-frost-dim">{view.stoppedText}</p>
          {canRetry && <RetryButton onRetry={reask} disabled={streaming} label="Ask again" />}
          {/* The abandoned turn's own collision, with the row it belongs to and nowhere else (R5-01). */}
          {view.turnConflictMessage && (
            <div className="w-full">
              <ConflictNotice message={view.turnConflictMessage} terms={chat.turnConflictTerms} />
            </div>
          )}
        </div>
      )}

      {/* The new turn's cells, reserved while the previous answer is held under its own reply. */}
      {view.reserveSlots > 0 && (
        <div className={CARD_GRID} data-testid="askreel-reserve">
          {Array.from({ length: view.reserveSlots }, (_, i) => (
            <CardSkeleton key={`reserve-${i}`} />
          ))}
        </div>
      )}
    </div>
  );
}
