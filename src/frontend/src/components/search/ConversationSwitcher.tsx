import { useEffect, useRef, useState } from "react";
import { ChevronDown, Trash2 } from "lucide-react";
import { useDeleteConversationMutation, useListConversationsQuery } from "../../store/api";
import { relTime } from "../../lib/format";
import { plural } from "../../lib/useAskReelChat";

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
}: {
  /** The thread on screen, so the list can mark it and the delete knows when to reset the page. */
  currentId: string | null;
  onOpen: (id: string) => void;
  onCurrentDeleted: () => void;
}) {
  const [open, setOpen] = useState(false);
  const boxRef = useRef<HTMLDivElement>(null);
  // Nothing is fetched until the menu is opened. This list is a control almost nobody touches on a
  // given visit, and a query on mount would put a request on the critical path of every single
  // /search load for it.
  const { data: conversations, isFetching } = useListConversationsQuery(undefined, { skip: !open });
  const [deleteConversation] = useDeleteConversationMutation();

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

  return (
    <div ref={boxRef} className="relative">
      <button
        type="button"
        data-testid="askreel-recent"
        aria-expanded={open}
        aria-haspopup="menu"
        onClick={() => setOpen((v) => !v)}
        className="fa-caption inline-flex items-center gap-1.5 rounded-full border border-fa-frost/25 px-3 py-1 text-fa-frost-dim hover:text-fa-frost-bright hover:border-fa-frost/50 hover:bg-fa-frost/10 transition-colors"
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
        </div>
      )}
    </div>
  );
}
