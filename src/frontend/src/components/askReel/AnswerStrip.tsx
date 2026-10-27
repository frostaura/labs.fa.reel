import { useState } from "react";
import { AlertTriangle, Sparkles } from "lucide-react";
import PickConditionsButton from "../rec/PickConditionsButton";
import { Button } from "../ui/button";
import CardTray from "./CardTray";
import PillButton from "./PillButton";
import RelaxationChip from "./RelaxationChip";
import { CARD_GRID, CardSkeleton, ResultCard, RetryButton } from "./parts";
import { failureCopy, nearMissHeading, plural, removedNote } from "../../lib/askReel/copy";
import { relTime } from "../../lib/format";
import type { AskReelChat } from "../../lib/askReel/useAskReelChat";
import type { AskCard } from "../../lib/askReelStream";
import type { AskView } from "./view";

/**
 * The live answer — the grid, its trays, its paging and every terminal state an answer can be in —
 * rendered under the reply that produced it (chat UI v2, WS-2 task 3: "each turn's cards sit under
 * its bubble, the latest expanded"). Earlier answers are collapsed under their own replies by the
 * transcript; this is the one expanded.
 *
 * `aria-live="off"` on purpose: the transcript around it is a polite `log`, and without the opt-out
 * every card streaming in would be read aloud as part of the conversation. What this strip owes a
 * screen reader — the settled count, a none-found answer — is announced by the page's status region
 * and by the none-found card's own polite region.
 */
export default function AnswerStrip({
  chat,
  view,
  onSaveTapped,
}: {
  chat: AskReelChat;
  view: AskView;
  onSaveTapped: (card: AskCard) => void;
}) {
  const { status, holdingPrevious, canRetry, reask, mode } = chat;
  const { streaming, visible, outsideVisible } = view;
  // The held previous answer is labelled and kept readable — never under 0.9 (WS-2 task 3). It used
  // to drop to 40 %, which read as "disabled" for an answer the reader was still entitled to use.
  const held = holdingPrevious ? "opacity-90" : "";
  // What a failure says depends on its cause — a turn limit is waited out, a lapsed session is not
  // retried (see `failureCopy`).
  const failure = failureCopy(chat.errorReason, chat.rateLimit);

  const floorTray = view.heldBack > 0 && (
    // The floor, shown rather than assumed. Collapsed: these picks are not being recommended, they
    // are being accounted for. The count is what the tray actually holds, falling back to the
    // server's own scalar only on a turn where the cards were not sent down (`floorHeldBack`); the
    // bar is the account's own, formatted to the one decimal the server's sentence uses.
    <div className={held}>
      <CardTray
        testId="askreel-below-floor"
        toggleTestId="askreel-below-floor-toggle"
        summary={view.floorLabel
          ? `Show the ${view.heldBack} held back by your ${view.floorLabel} bar`
          : `Show the ${view.heldBack} ${plural(view.heldBack, "pick")} Reel held back`}
        heading={view.floorLabel ? `Under your ${view.floorLabel} bar` : "Held back by your bar"}
        // Since R1-01 the tray holds judged matches only, so it says they matched — unless a card
        // here carries no verdict (an older answer, or one the client moved here on a stopped
        // turn), and then the tray claims nothing it cannot back.
        caption={view.belowFloorVisible.length > 0 && view.belowFloorVisible.every((c) => c.satisfies === "yes")
          ? "Reel judged these a match for what you asked, but they score under your bar or have no score from your model yet, so they are not in your picks."
          : "These scored under your bar, or have no score from your model yet, so they are not in your picks."}
        cards={view.belowFloorVisible}
        onSaveTapped={onSaveTapped}
      />
    </div>
  );
  // The judge's partial matches (FD-2b): collapsed, never the grid, never counted as picks.
  const nearMissTray = view.nearMissVisible.length > 0 && (
    <div className={held}>
      <CardTray
        testId="askreel-near-miss"
        toggleTestId="askreel-near-miss-toggle"
        summary={`Close, but not quite — ${view.nearMissVisible.length} ${plural(view.nearMissVisible.length, "title")}`}
        heading={nearMissHeading(chat.plan?.concepts ?? [])}
        caption="Reel judged these a partial match for what you asked, so they are not in your picks."
        cards={view.nearMissVisible}
        onSaveTapped={onSaveTapped}
      />
    </div>
  );
  // The cards nobody could check: labelled for exactly what they are (§4.8, D1).
  const uncheckedTray = view.uncheckedVisible.length > 0 && (
    <div className={held}>
      <CardTray
        testId="askreel-unchecked"
        toggleTestId="askreel-unchecked-toggle"
        summary={`Not checked against your request — ${view.uncheckedVisible.length} ${plural(view.uncheckedVisible.length, "title")}`}
        heading="Not checked against your request"
        caption="Reel couldn’t check these against what you asked this time, so they are not in your picks."
        cards={view.uncheckedVisible}
        onSaveTapped={onSaveTapped}
      />
    </div>
  );
  const trays = <>{floorTray}{nearMissTray}{uncheckedTray}</>;

  return (
    <div className="space-y-5" aria-live="off" data-testid="askreel-answer">
      <ResumeSeam chat={chat} />
      {/* The provenance line, with its grid when that grid is under an earlier reply (see
          `footnoteInSlot`); otherwise it lives in the live slot, in the box the banner vacates. */}
      {view.showsProvenance && !view.footnoteInSlot && (
        <p className="fa-body text-fa-frost-dim text-center" data-testid="askreel-footnote">{view.footnoteText}</p>
      )}
      {body()}
      <Paging chat={chat} view={view} />
      {/* A failure that arrives while cards are already on screen (a page that blew up) must not
          wipe the reel — a quiet inline retry instead of the full-bleed card. Not for a 404: that
          has its own card, and both firing put two "Ask again" buttons under two explanations. */}
      {status === "error" && chat.errorReason !== "conversation" && chat.cards.length > 0 && (
        <div className="flex flex-col items-center gap-2" data-testid="askreel-page-error" data-reason={chat.errorReason ?? "unknown"}>
          <p className="fa-caption text-fa-warning text-center">{failure.inline}</p>
          {canRetry && failure.retryable && <RetryButton onRetry={reask} disabled={streaming} />}
        </div>
      )}
    </div>
  );

  function body() {
    // One message, never two — exactly one of these branches is live.
    if (status === "error" && chat.errorReason === "conversation") {
      // 404. The thread this tab remembered is gone. Not a crash and not a fork.
      return (
        <div className="fa-card p-8 text-center max-w-xl mx-auto space-y-3" data-testid="askreel-conversation-gone">
          <Sparkles className="h-6 w-6 text-fa-frost-dim mx-auto" />
          <p className="fa-body text-fa-frost-bright">That thread isn’t here any more</p>
          <p className="fa-caption text-fa-frost-dim">It was deleted, or it aged out. Asking again starts a new one.</p>
          {canRetry && <RetryButton onRetry={reask} disabled={streaming} label="Ask again" />}
        </div>
      );
    }
    if (status === "error" && chat.cards.length === 0) {
      return (
        <div className="fa-card p-8 text-center max-w-xl mx-auto space-y-3" data-testid="semantic-error" data-reason={chat.errorReason ?? "unknown"}>
          <AlertTriangle className="h-6 w-6 text-fa-warning mx-auto" aria-hidden />
          <p className="fa-body text-fa-frost-bright">{failure.title}</p>
          <p className="fa-caption text-fa-frost-dim">{failure.detail}</p>
          {canRetry && failure.retryable && <RetryButton onRetry={reask} disabled={streaming} />}
        </div>
      );
    }
    const settledAnswer = status === "done" || status === "resumed";
    if (settledAnswer && mode === "NoneFound" && visible.length === 0) {
      // An honest "nothing left". The reply above already says why, with the counts; this card
      // offers what can be done about it — each relaxation opens the editor on its own rule
      // (FD-2c). Already-seen titles are never offered.
      return (
        <div className="space-y-5">
          <div aria-live="polite">
            <div className="fa-card p-6 max-w-xl mx-auto space-y-3" data-testid="askreel-none-found">
              <div className="flex items-center gap-2">
                <Sparkles className="h-5 w-5 text-fa-frost-dim" />
                <p className="fa-body text-fa-frost-bright">
                  {view.uncheckedVisible.length > 0 ? "Reel couldn’t check what it found" : "Nothing left that matches"}
                </p>
              </div>
              {chat.knockouts.length > 0 && (
                <ul className="flex flex-wrap gap-2" data-testid="askreel-none-found-relaxations" aria-label="Conditions you could relax">
                  {chat.knockouts.map((k) => (
                    <li key={`${k.kind}:${k.value}`}>
                      <RelaxationChip knockout={k} />
                    </li>
                  ))}
                </ul>
              )}
              <div className="flex flex-wrap items-center gap-3 [&_[data-testid=open-conditions]]:min-h-11">
                <PickConditionsButton />
                {canRetry && <RetryButton onRetry={reask} disabled={streaming} label="Ask again" />}
              </div>
            </div>
          </div>
          {trays}
        </div>
      );
    }
    if (settledAnswer && (mode === "Clarify" || mode === "Answer") && visible.length === 0) {
      // The reply asked back, or answered a question about a title — with nothing on screen. The
      // reply is the answer; an empty-results card under it would say a search ran (R1-04).
      return null;
    }
    if (status === "resumed" && chat.lastCardsAt == null && chat.resumedTail == null && visible.length === 0 && outsideVisible.length === 0) {
      // A resumed thread is NOT an empty answer — it predates stored results (F4-04: only when the
      // last assistant row completed; a failed or pending tail has its own row in the transcript).
      return (
        <div className="fa-card p-8 text-center max-w-xl mx-auto space-y-2" data-testid="askreel-transcript-only">
          <Sparkles className="h-6 w-6 text-fa-frost-dim mx-auto" />
          <p className="fa-caption text-fa-frost-dim">Transcript only — this thread predates saved results. Ask again to bring it back.</p>
          {canRetry && <RetryButton onRetry={reask} disabled={streaming} label="Ask again" />}
        </div>
      );
    }
    if (status === "resumed" && chat.lastCardsAt != null && visible.length === 0 && outsideVisible.length === 0) {
      // A thread that DID store cards and has none to show: the eligibility re-filter dropped them.
      return (
        <div className="fa-card p-8 text-center max-w-xl mx-auto space-y-2" data-testid="askreel-all-removed">
          <Sparkles className="h-6 w-6 text-fa-frost-dim mx-auto" />
          <p className="fa-caption text-fa-frost-dim">You’ve watched or rated everything this thread found. Ask again for a fresh set.</p>
          {canRetry && <RetryButton onRetry={reask} disabled={streaming} label="Ask again" />}
        </div>
      );
    }
    if (status === "done" && visible.length === 0 && outsideVisible.length === 0) {
      return (
        <div className="fa-card p-8 text-center max-w-xl mx-auto space-y-2" data-testid="semantic-empty">
          <Sparkles className="h-6 w-6 text-fa-frost-dim mx-auto" />
          <p className="fa-body text-fa-frost-bright">No matches for that one</p>
          <p className="fa-caption text-fa-frost-dim">Try a different angle — a mood, a vibe, or “more like &lt;something&gt;”.</p>
          {canRetry && <RetryButton onRetry={reask} disabled={streaming} label="Ask again" />}
        </div>
      );
    }
    return (
      <>
        {/* The seam. While a new turn is working, the previous answer stays up under its own reply
            — labelled, and readable (0.9, never dimmed into looking disabled) — until the first
            content of the new turn replaces it. */}
        {holdingPrevious && (visible.length > 0 || outsideVisible.length > 0) && (
          <p className="fa-caption text-fa-frost-dim" data-testid="askreel-previous-answer">
            {streaming
              ? "Previous answer — held until the new one arrives."
              : "Previous answer — the last question never replaced it."}
          </p>
        )}
        {/* …and the Stop's own seam (F6-06): the grid is the answer, whole, and what it still owes
            the reader is WHICH question it answers. */}
        {view.keptWholeAnswer && chat.pageQuery && (
          <p className="fa-caption text-fa-frost-dim" data-testid="askreel-kept-answer">
            Still your answer to “{chat.pageQuery}” — the question you stopped never replaced it.
          </p>
        )}
        <div className={`${CARD_GRID} ${held}`} data-testid="semantic-results">
          {visible.map((card) => (
            <ResultCard key={card.titleId} card={card} onSaveTapped={onSaveTapped} />
          ))}
          {/* Keyed by the CELL the placeholder occupies, so a card taking cell 0 retires the
              placeholder keyed for cell 0 and the rest keep their keys AND their cells (F6-04). */}
          {Array.from({ length: view.pendingSlots }, (_, i) => (
            <CardSkeleton key={`slot-${visible.length + i}`} />
          ))}
        </div>
        {/* The honest channel: titles that trip something the user excluded, fenced off under their
            own header, never mixed into the grid above. */}
        {outsideVisible.length > 0 && (
          <div className={`space-y-3 border-t border-fa-frost/10 pt-5 ${held}`} data-testid="askreel-outside">
            <div>
              <p className="fa-body text-fa-frost-dim">Outside your request — closest we could get</p>
              <p className="fa-caption text-3">These match the vibe but hit something you excluded.</p>
            </div>
            <div className={CARD_GRID}>
              {outsideVisible.map((card) => (
                <ResultCard key={card.titleId} card={card} onSaveTapped={onSaveTapped} />
              ))}
            </div>
          </div>
        )}
        {trays}
      </>
    );
  }
}

/**
 * The seam a resume lands on — WHEN you left, and which question the grid answers when that is not
 * the last one asked (SB-03). Dated from `lastCardsAt`, falling back to `lastTurnAt` (the only
 * stamp a transcript-only thread has). `removed` rides with it: a card that silently disappears
 * between two views of the same answer is indistinguishable from a bug.
 */
function ResumeSeam({ chat }: { chat: AskReelChat }) {
  const stamp = chat.lastCardsAt ?? chat.lastTurnAt;
  if (chat.status !== "resumed" || !stamp) return null;
  return (
    <div className="flex flex-wrap items-baseline gap-x-2" data-testid="askreel-seam">
      <p className="fa-caption text-fa-frost-dim">Picked up where you left off · {relTime(stamp)}</p>
      {chat.resumedTail && chat.answeredQuery && <SeamQuestion question={chat.answeredQuery} />}
      {chat.removed > 0 && (
        <p className="fa-caption text-3" data-testid="askreel-removed">
          {/* R1-11: "since" only of cards acted on after the answer — see removedNote. */}
          {removedNote(chat.removed, chat.removedEarlier)}
        </p>
      )}
    </div>
  );
}

/**
 * Which question the restored grid answers — one line of it, clamped rather than truncated, so the
 * full question stays in the DOM and on the `title`, and one tap opens it in place (F6-08).
 */
function SeamQuestion({ question }: { question: string }) {
  const [expanded, setExpanded] = useState(false);
  return (
    <Button
      variant="link"
      data-testid="askreel-seam-answers"
      title={question}
      aria-expanded={expanded}
      onClick={() => setExpanded((v) => !v)}
      className={`fa-caption block h-auto max-w-full p-0 text-left text-xs font-normal text-3 hover:text-fa-frost-dim ${expanded ? "whitespace-normal" : "truncate"}`}
    >
      These answer “{question}”.
    </Button>
  );
}

/**
 * Paging is a decision the user makes, not a scroll position — and it is offered only over an
 * answer a page can legitimately extend (`pageable`, the hook's own rule, so the control and
 * `loadMore` cannot disagree). A refused page is a line UNDER the control, which stays (R5-04).
 */
function Paging({ chat, view }: { chat: AskReelChat; view: AskView }) {
  const { pageable, appending, exhausted, loadMore } = chat;
  if (!(view.visible.length > 0 && (pageable || appending || exhausted))) return null;
  return (
    <div className="flex flex-col items-center gap-2">
      {(pageable || appending) && (
        <PillButton tone="strong" data-testid="askreel-more" onClick={loadMore} disabled={view.streaming || appending}>
          {appending ? "Finding more like these…" : "Show me more"}
        </PillButton>
      )}
      {exhausted && !appending && (
        <p className="fa-caption text-fa-frost-dim text-center">
          That&apos;s everything on this thread — refine it above for a different angle.
        </p>
      )}
      {view.refusalText && (
        <p className="fa-caption text-fa-warning text-center" data-testid="askreel-page-refused">
          {view.refusalText}
        </p>
      )}
    </div>
  );
}
