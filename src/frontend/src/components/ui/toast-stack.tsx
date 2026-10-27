import { useLayoutEffect, useState, type ReactNode } from "react";
import { createPortal } from "react-dom";

/**
 * The one place every toast sits (integrator CR to WS-9: "UndoToast stacking").
 *
 * Reel raises four toasts — the after-rating verdict, the card undo, Ask Reel's save and delete
 * undos, and the "new version" prompt — and each was its own `fixed … bottom-24` box. Two at once
 * sat exactly on top of each other: rate a card on Home and the verdict covered the card undo
 * (CardUndo hand-lifted itself by 7rem to dodge that one pairing), and a save undo on /search
 * hid a delete undo completely. Now each toast portals into one shared column and they stack, the
 * oldest nearest the edge.
 *
 * The column is attached to `<body>` only while a toast is up, for the reason VerdictToast gave
 * when it was the only one: a Radix dialog marks every other child of `<body>` `aria-hidden` when
 * it opens, and a live region attached after that snapshot is still announced. A column that was
 * already up when a dialog opened has been swept too, so each toast that mounts lifts the sweep
 * off it again: a toast exists to be heard.
 *
 * Two more things the dialog does, and the column undoes for its own subtree only:
 *   - Radix's scroll lock sets `pointer-events: none` on `<body>`; the toasts re-enable it, while the
 *     column's empty gutter stays click-through to the page.
 *   - A press anywhere outside the dialog's React tree closes it. A toast is portalled from the
 *     layout, not from the dialog, so tapping "Undo" or "Dismiss" over the title modal used to close
 *     the modal too. The column keeps its pointer presses to itself.
 */
let column: HTMLElement | null = null;
let holders = 0;

const COLUMN_CLASS =
  "pointer-events-none fixed inset-x-4 bottom-24 z-[60] mx-auto flex max-w-md flex-col-reverse gap-2 md:bottom-6 " +
  "[&>*]:pointer-events-auto";

function keepPressesInside(event: Event) {
  event.stopPropagation();
}

/** The one column, created on first use and attached only while a toast holds it. */
function theColumn(): HTMLElement {
  if (!column) {
    column = document.createElement("div");
    column.className = COLUMN_CLASS;
    column.dataset.testid = "toast-stack";
    column.addEventListener("pointerdown", keepPressesInside);
  }
  return column;
}

function hold(host: HTMLElement) {
  holders += 1;
  if (!host.isConnected) document.body.appendChild(host);
  host.removeAttribute("aria-hidden");
  host.removeAttribute("data-aria-hidden");
}

function release(host: HTMLElement) {
  holders = Math.max(0, holders - 1);
  if (holders === 0) host.remove();
}

/** Renders `children` as one toast in the shared column. Mount it only while the toast is shown. */
export function ToastPortal({ children }: { children: ReactNode }) {
  const [host] = useState(theColumn);
  // Before paint, so the toast never draws detached and never flashes.
  useLayoutEffect(() => {
    hold(host);
    return () => release(host);
  }, [host]);
  return createPortal(children, host);
}
