import { useEffect, useId, useRef, useState } from "react";
import { Check, ChevronDown, Trash2, X } from "lucide-react";
import { useDeleteConversationMutation, useListConversationsQuery } from "../../store/api";
import { relTime } from "../../lib/format";
import { plural } from "../../lib/askReel/copy";
import { recentListing } from "../../lib/askReel/paging";
import UndoToast from "./UndoToast";
import PillButton from "../askReel/PillButton";
import { Button } from "../ui/button";
import LoadFailed from "../ui/load-failed";

/**
 * `Recent ▾` — the way back into a thread you left.
 *
 * Ask Reel kept conversations on the server long before it could show you that it had any: one
 * thread was reachable (the newest, and not necessarily this tab's), and every other one existed
 * only in the database. This is the whole of the fix — a list, a way to open one, and a way to
 * delete one — and it is deliberately small: threads are a means of getting back to an answer, not
 * a filing system.
 *
 * Deleting removes the transcript and its cards. It never touches the exposure log, which is what
 * every accuracy number on the product is computed from; that promise lives in the endpoint's own
 * doc-comment, and this button is only its front door.
 *
 * That front door has two locks on it now, and they do different jobs (F6-07 — the delete was one
 * 26 px tap, with no dialog, no inline confirm and no undo, sitting eight pixels from the target
 * that OPENS the thread). The inline "Delete? Yes / No" catches the misfire, which on a phone is
 * the likely one. The undo window catches the change of mind — and it is real rather than
 * decorative because the DELETE is not sent until the window closes: the server cannot restore a
 * transcript and its cards, so an undo that fires after the request is a button that lies. The
 * cost is six seconds of deferral on the only irreversible control in Ask Reel, which is the right
 * trade in the only place it is worth paying.
 */
export default function ConversationSwitcher({
  currentId,
  onOpen,
  onCurrentDeleted,
  disabled = false,
}: {
  /** The thread on screen, so the list can mark it and the delete knows when to reset the page. */
  currentId: string | null;
  onOpen: (id: string) => void;
  onCurrentDeleted: () => void;
  /**
   * Closed and inert while a turn is streaming, on exactly the terms `+ New` already was.
   *
   * F6-01: `+ New` was disabled and this was not, so the one gesture that could leave a running
   * turn behind was the one still live. Switching mid-stream put the new thread's question and
   * reply above the old thread's progress banner and 34 of its cards, and the in-flight answer
   * then landed in the thread the user had switched TO — durably, with its turn count moved. The
   * client-side guards against a stale frame are the backstop; not offering the gesture while a
   * turn is running is the fix.
   */
  disabled?: boolean;
}) {
  const [open, setOpen] = useState(false);
  // Set once by "Show older" and kept: the user has said they want the longer list, and making
  // them ask again on every open would be the control arguing with them. See `recentListing` for
  // why there are exactly two sizes and no third.
  const [showOlder, setShowOlder] = useState(false);
  const boxRef = useRef<HTMLDivElement>(null);
  const panelId = useId();
  const triggerRef = useRef<HTMLButtonElement>(null);
  /** The row whose trash has been tapped and is now asking. One at a time, by construction. */
  const [confirming, setConfirming] = useState<string | null>(null);
  /**
   * A delete the user has agreed to and the page has not sent. The row is already gone from the
   * list — the answer to "did that work" has to be immediate — and this is what the undo window
   * is holding, plus the title, because a toast that says "thread deleted" without naming which
   * one is useless on an account with fifty of them.
   */
  const [pending, setPending] = useState<{ id: string; title: string } | null>(null);
  const listing = recentListing(showOlder, 0);
  // Nothing is fetched until the menu is opened. This list is a control almost nobody touches on a
  // given visit, and a query on mount would put a request on the critical path of every single
  // /search load for it.
  const { data: conversations, isFetching, isError, refetch } = useListConversationsQuery(listing.limit, { skip: !open });
  const [deleteConversation] = useDeleteConversationMutation();

  // A turn starting while the menu is open closes it, rather than leaving a live-looking list of
  // threads above a button that has just gone inert.
  useEffect(() => {
    // eslint-disable-next-line react-hooks/set-state-in-effect -- follows an external prop change
    if (disabled) setOpen(false);
  }, [disabled]);

  // Close on an outside click, same as the search box's dropdown — and on Escape, which F6-14
  // found this component had never listened for: the menu stayed mounted with `aria-expanded`
  // still true, and the only ways out were a second tap on the button or a tap outside. Escape is
  // the one gesture a keyboard user has for "I did not mean to open this", and on a panel that is
  // a bottom sheet at phone widths (below) it is also the fastest way out for anyone at all.
  useEffect(() => {
    if (!open) return;
    const onPointerDown = (event: PointerEvent) => {
      if (boxRef.current && !boxRef.current.contains(event.target as Node)) setOpen(false);
    };
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key !== "Escape") return;
      setOpen(false);
      // Focus goes back to the control that opened the panel rather than being left on a node
      // that is about to be unmounted, which drops it to <body>.
      triggerRef.current?.focus();
    };
    document.addEventListener("pointerdown", onPointerDown);
    document.addEventListener("keydown", onKeyDown);
    return () => {
      document.removeEventListener("pointerdown", onPointerDown);
      document.removeEventListener("keydown", onKeyDown);
    };
  }, [open]);

  // A half-asked question does not survive the panel closing: reopening the list to find one row
  // already saying "Delete?" is the control remembering something the user walked away from.
  useEffect(() => {
    // eslint-disable-next-line react-hooks/set-state-in-effect -- follows an external state change
    if (!open) setConfirming(null);
  }, [open]);

  /**
   * The window closed — send it.
   *
   * Also the unmount path, which is the half that is easy to miss: leaving `/search` inside the
   * six seconds must still delete the thread the user asked to delete, or the undo window becomes
   * a way to lose a delete rather than to take one back. `commitRef` is what the unmount effect
   * reads, so it always sees the LATEST pending value rather than the one captured when the
   * effect was first run.
   */
  const commitDelete = async (target: { id: string; title: string }) => {
    setPending((p) => (p?.id === target.id ? null : p));
    await deleteConversation(target.id);
    // Only when it was the one on screen: the page is still rendering a thread the server no
    // longer has, and its remembered id would resume a 404 on the next visit. Deleting any OTHER
    // thread must leave the current one untouched.
    if (target.id === currentId) onCurrentDeleted();
  };
  const commitRef = useRef<(() => void) | null>(null);
  // Written in an effect, not during render — a ref assigned in the render body is the pattern
  // that goes stale under a bail-out. Two effects rather than one because the unmount handler must
  // be registered exactly once, and this one has to re-run whenever the pending delete changes.
  useEffect(() => {
    commitRef.current = pending ? () => void commitDelete(pending) : null;
    // `commitDelete` closes over `currentId` and the mutation trigger, both stable enough that
    // re-keying this on the function identity would only make it run on every render.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [pending]);
  useEffect(() => () => commitRef.current?.(), []);

  // The row under an open undo window is already gone as far as the list is concerned. Hiding it
  // here rather than optimistically patching the RTK cache keeps the cache honest — nothing has
  // been deleted yet — and an undo is then one piece of local state going away.
  const rows = (conversations ?? []).filter((row) => row.id !== pending?.id);
  // Recomputed against what actually came back: the offer is only honest over a FULL page, and a
  // short one is the whole list. `listing.limit` above is keyed on the toggle alone, which is what
  // the request needs; this is what the footer needs.
  const { canShowOlder, atCeiling } = recentListing(showOlder, rows.length);
  // REV7-PH-02: a read that failed is a third state, never the empty one. A 500 or 503 rendered
  // "Nothing here yet — ask something." — an account with fifty threads told it had none, and no
  // way to ask again. While the retry is in flight the failure gives way to "Loading…" as any read
  // does. Rows already on screen from a shorter, successful read stay: they are still true, and
  // what failed is the longer list ("Show older").
  const failed = isError && !isFetching;

  return (
    <div ref={boxRef} className="relative">
      <PillButton
        ref={triggerRef}
        tone="quiet"
        data-testid="askreel-recent"
        aria-expanded={open}
        aria-controls={open ? panelId : undefined}
        disabled={disabled}
        title={disabled ? "Wait for the current results to finish" : undefined}
        onClick={() => setOpen((v) => !v)}
      >
        Recent <ChevronDown className="h-3.5 w-3.5" aria-hidden />
      </PillButton>

      {open && (
        <section
          id={panelId}
          aria-label="Recent conversations"
          data-testid="askreel-recent-menu"
          /*
           * Two shapes, and the breakpoint is where the anchor stops being able to hold it.
           *
           * F6-05: below `md` the header wraps, which moves `Recent ▾` inward — and a 288 px panel
           * pinned to that button's RIGHT edge then starts at x: -114. The document does not
           * scroll horizontally, so 40% of the control was unreachable by any gesture; every
           * thread title was cut mid-word on the left. Clamping the WIDTH does not fix it, because
           * the overflow comes from the anchor, not from the size: at 320 the panel is already
           * only as wide as the viewport.
           *
           * So at phone widths it stops being anchored at all and becomes a bottom sheet —
           * `fixed`, inset from both edges, above the composer's `z-30` and the tab bar's `z-40`.
           * That is inside the viewport at ANY width by construction rather than by arithmetic,
           * and it puts a list whose whole purpose is one-handed navigation under the thumb
           * instead of up beside the title. From `md` it is the anchored popover again, where the
           * header does not wrap and the anchor is honest.
           *
           * It stays a DOM child of `boxRef` in both shapes, which is what keeps the
           * tap-outside-to-close handler correct: `fixed` changes where a box paints, not where it
           * sits in the tree.
           *
           * A labelled section of rows, not `role="menu"`: a menu may contain only menu items, and
           * each row here is TWO controls (open, delete) with a confirm that replaces one of them —
           * axe failed the old markup on `aria-required-children` the moment it opened. Rows are a
           * list, every control a plain button, and the trigger's `aria-expanded` and
           * `aria-controls` say what it opens.
           */
          className="fixed inset-x-2 bottom-2 z-50 max-h-[55vh] overflow-y-auto rounded-lg border border-fa-edge bg-fa-ink-2/95 backdrop-blur-md shadow-2xl reel-rise md:absolute md:inset-x-auto md:bottom-auto md:right-0 md:top-12 md:w-72 md:max-h-[60vh]"
        >
          {failed && (
            <div className="p-2">
              <LoadFailed
                what={rows.length > 0 ? "your older threads" : "your conversations"}
                onRetry={() => void refetch()}
                retrying={isFetching}
                variant="inline"
                testId="askreel-recent-error"
              />
            </div>
          )}
          {isFetching && rows.length === 0 ? (
            <p className="px-3 py-3 fa-caption text-fa-frost-dim">Loading…</p>
          ) : failed && rows.length === 0 ? null : rows.length === 0 ? (
            <p className="px-3 py-3 fa-caption text-fa-frost-dim" data-testid="askreel-recent-empty">
              Nothing here yet — ask something.
            </p>
          ) : (
            <ul>
            {rows.map((row) => (
              <li
                key={row.id}
                data-testid="askreel-recent-row"
                className={`flex items-center gap-2 px-3 py-2 transition ${row.id === currentId ? "bg-fa-frost/10" : "hover:bg-fa-glass"}`}
              >
                <Button
                  variant="ghost"
                  data-testid="askreel-recent-open"
                  onClick={() => {
                    setOpen(false);
                    onOpen(row.id);
                  }}
                  // 44 px of vertical target inside a 60 px row: the primary way into a thread was
                  // a 36 px box, eight pixels shy of the touch minimum the row's own delete control
                  // already meets. `justify-center` keeps the two lines optically where they were.
                  className="flex h-auto min-h-11 min-w-0 flex-1 flex-col items-stretch justify-center gap-0 whitespace-normal rounded-none p-0 text-left font-normal hover:bg-transparent"
                >
                  {/* The title is the thread's first question, which is what a person recognises a
                      conversation by — never a generated summary they have to decode. */}
                  <span className="block fa-body text-fa-frost-bright truncate" title={row.title ?? undefined}>
                    {row.title?.trim() ? row.title : "Untitled thread"}
                  </span>
                  <span className="block fa-caption text-fa-frost-dim">
                    {row.turnCount} {plural(row.turnCount, "turn")} · {relTime(row.lastTurnAt)}
                    {/* Said here as well as on the page, so a thread with no restorable answer is
                        recognisable BEFORE it is opened rather than after — and said as what the
                        LIST can actually see (F4-04b). "Transcript only" is defined by the contract
                        as a thread that predates stored results, and the row read that way over a
                        thread whose only answer had failed seven minutes earlier. The list has one
                        field here, `lastCardsAt`, and it means "no picks were ever stored"; why
                        there are none is the page's business, not this row's. */}
                    {row.lastCardsAt == null && " · no saved picks"}
                  </span>
                </Button>
                {confirming === row.id ? (
                  // The confirm takes the trash's place rather than floating over the row: a
                  // dialog on a phone covers the very list the user is deciding about, and a
                  // second control appearing BESIDE the first is how a misfire becomes two.
                  <div className="flex shrink-0 items-center gap-1" data-testid="askreel-recent-confirm">
                    <span className="fa-caption text-fa-warning">Delete?</span>
                    <Button
                      variant="ghost"
                      size="icon"
                      data-testid="askreel-recent-delete-yes"
                      aria-label={`Confirm delete: ${row.title?.trim() ? row.title : "untitled"}`}
                      onClick={() => {
                        setConfirming(null);
                        setPending({ id: row.id, title: row.title?.trim() ? row.title : "Untitled thread" });
                      }}
                      className="min-h-11 min-w-11 rounded-full text-fa-warning hover:bg-fa-warning/10 hover:text-fa-warning"
                    >
                      <Check className="h-4 w-4" aria-hidden />
                    </Button>
                    <Button
                      variant="ghost"
                      size="icon"
                      data-testid="askreel-recent-delete-no"
                      aria-label="Keep this conversation"
                      onClick={() => setConfirming(null)}
                      className="min-h-11 min-w-11 rounded-full text-fa-frost-dim hover:text-fa-frost-bright"
                    >
                      <X className="h-4 w-4" aria-hidden />
                    </Button>
                  </div>
                ) : (
                  <Button
                    variant="ghost"
                    size="icon"
                    data-testid="askreel-recent-delete"
                    aria-label={`Delete conversation: ${row.title?.trim() ? row.title : "untitled"}`}
                    onClick={() => setConfirming(row.id)}
                    className="min-h-11 min-w-11 shrink-0 rounded-full text-fa-frost-dim hover:text-fa-warning hover:bg-fa-warning/10"
                  >
                    <Trash2 className="h-4 w-4" aria-hidden />
                  </Button>
                )}
              </li>
            ))}
            </ul>
          )}
          {/* SB-05. The list is bounded on purpose, but an account with 51 threads saw 20 of them
              and no hint that the other 31 existed — a small control read as a complete list. One
              gesture asks for everything the route can return; there is no second one, because
              `/conversations` takes a `limit` and no `offset`. */}
          {canShowOlder && (
            <Button
              variant="ghost"
              data-testid="askreel-recent-older"
              onClick={() => setShowOlder(true)}
              disabled={isFetching}
              className="flex min-h-11 w-full justify-start rounded-none border-t border-fa-edge px-3 text-xs font-normal text-fa-frost-dim hover:text-fa-frost-bright disabled:opacity-40"
            >
              Show older threads
            </Button>
          )}
          {atCeiling && (
            <p className="border-t border-fa-edge px-3 py-2 fa-caption text-3" data-testid="askreel-recent-ceiling">
              Showing your {rows.length} most recent threads.
            </p>
          )}
        </section>
      )}

      {/* Outside the `open` guard on purpose: closing the menu must not silently cancel the window
          or silently commit inside it. The toast is the whole of what the user can still act on. */}
      {pending && (
        <UndoToast
          id={pending.id}
          testId="askreel-delete-toast"
          message={`Deleted “${pending.title}”.`}
          onUndo={() => setPending(null)}
          onExpire={() => void commitDelete(pending)}
        />
      )}
    </div>
  );
}
