import { useEffect, useRef, useState } from "react";
import { ChevronDown, Trash2 } from "lucide-react";
import { useDeleteConversationMutation, useListConversationsQuery } from "../../store/api";
import { relTime } from "../../lib/format";
import { plural, recentListing } from "../../lib/useAskReelChat";

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
  const listing = recentListing(showOlder, 0);
  // Nothing is fetched until the menu is opened. This list is a control almost nobody touches on a
  // given visit, and a query on mount would put a request on the critical path of every single
  // /search load for it.
  const { data: conversations, isFetching } = useListConversationsQuery(listing.limit, { skip: !open });
  const [deleteConversation] = useDeleteConversationMutation();

  // A turn starting while the menu is open closes it, rather than leaving a live-looking list of
  // threads above a button that has just gone inert.
  useEffect(() => {
    // eslint-disable-next-line react-hooks/set-state-in-effect -- follows an external prop change
    if (disabled) setOpen(false);
  }, [disabled]);

  // Close on an outside click, same as the search box's dropdown.
  useEffect(() => {
    if (!open) return;
    const onPointerDown = (event: PointerEvent) => {
      if (boxRef.current && !boxRef.current.contains(event.target as Node)) setOpen(false);
    };
    document.addEventListener("pointerdown", onPointerDown);
    return () => document.removeEventListener("pointerdown", onPointerDown);
  }, [open]);

  const rows = conversations ?? [];
  // Recomputed against what actually came back: the offer is only honest over a FULL page, and a
  // short one is the whole list. `listing.limit` above is keyed on the toggle alone, which is what
  // the request needs; this is what the footer needs.
  const { canShowOlder, atCeiling } = recentListing(showOlder, rows.length);

  return (
    <div ref={boxRef} className="relative">
      <button
        type="button"
        data-testid="askreel-recent"
        aria-expanded={open}
        aria-haspopup="menu"
        disabled={disabled}
        title={disabled ? "Wait for the current results to finish" : undefined}
        onClick={() => setOpen((v) => !v)}
        className="fa-caption inline-flex items-center gap-1.5 rounded-full border border-fa-frost/25 px-3 py-1 text-fa-frost-dim hover:text-fa-frost-bright hover:border-fa-frost/50 hover:bg-fa-frost/10 disabled:opacity-40 disabled:hover:text-fa-frost-dim transition-colors"
      >
        Recent <ChevronDown className="h-3.5 w-3.5" />
      </button>

      {open && (
        <div
          role="menu"
          aria-label="Recent conversations"
          data-testid="askreel-recent-menu"
          className="absolute right-0 top-9 z-50 w-72 max-h-[60vh] overflow-y-auto rounded-lg border border-fa-edge bg-fa-ink-2/95 backdrop-blur-md shadow-2xl reel-rise"
        >
          {isFetching && rows.length === 0 ? (
            <p className="px-3 py-3 fa-caption text-fa-frost-dim">Loading…</p>
          ) : rows.length === 0 ? (
            <p className="px-3 py-3 fa-caption text-fa-frost-dim" data-testid="askreel-recent-empty">
              Nothing here yet — ask something.
            </p>
          ) : (
            rows.map((row) => (
              <div
                key={row.id}
                data-testid="askreel-recent-row"
                className={`flex items-center gap-2 px-3 py-2 transition ${row.id === currentId ? "bg-fa-frost/10" : "hover:bg-fa-glass"}`}
              >
                <button
                  type="button"
                  role="menuitem"
                  data-testid="askreel-recent-open"
                  onClick={() => {
                    setOpen(false);
                    onOpen(row.id);
                  }}
                  className="min-w-0 flex-1 text-left"
                >
                  {/* The title is the thread's first question, which is what a person recognises a
                      conversation by — never a generated summary they have to decode. */}
                  <p className="fa-body text-fa-frost-bright truncate" title={row.title ?? undefined}>
                    {row.title?.trim() ? row.title : "Untitled thread"}
                  </p>
                  <p className="fa-caption text-fa-frost-dim">
                    {row.turnCount} {plural(row.turnCount, "turn")} · {relTime(row.lastTurnAt)}
                    {/* Said here as well as on the page, so a thread with no restorable answer is
                        recognisable BEFORE it is opened rather than after. */}
                    {row.lastCardsAt == null && " · transcript only"}
                  </p>
                </button>
                <button
                  type="button"
                  data-testid="askreel-recent-delete"
                  aria-label={`Delete conversation: ${row.title?.trim() ? row.title : "untitled"}`}
                  onClick={async () => {
                    await deleteConversation(row.id);
                    // Only when it was the one on screen: the page is still rendering a thread the
                    // server no longer has, and its remembered id would resume a 404 on the next
                    // visit. Deleting any OTHER thread must leave the current one untouched.
                    if (row.id === currentId) onCurrentDeleted();
                  }}
                  className="shrink-0 rounded-full p-1.5 text-fa-frost-dim hover:text-fa-warning hover:bg-fa-warning/10 transition-colors"
                >
                  <Trash2 className="h-3.5 w-3.5" />
                </button>
              </div>
            ))
          )}
          {/* SB-05. The list is bounded on purpose, but an account with 51 threads saw 20 of them
              and no hint that the other 31 existed — a small control read as a complete list. One
              gesture asks for everything the route can return; there is no second one, because
              `/conversations` takes a `limit` and no `offset`. */}
          {canShowOlder && (
            <button
              type="button"
              data-testid="askreel-recent-older"
              onClick={() => setShowOlder(true)}
              disabled={isFetching}
              className="w-full border-t border-fa-edge px-3 py-2 text-left fa-caption text-fa-frost-dim hover:text-fa-frost-bright hover:bg-fa-glass disabled:opacity-40 transition-colors"
            >
              Show older threads
            </button>
          )}
          {atCeiling && (
            <p className="border-t border-fa-edge px-3 py-2 fa-caption text-fa-frost-dim/70" data-testid="askreel-recent-ceiling">
              Showing your {rows.length} most recent threads.
            </p>
          )}
        </div>
      )}
    </div>
  );
}
