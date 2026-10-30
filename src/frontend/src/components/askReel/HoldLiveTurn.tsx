import { Component, type ReactNode, type RefObject } from "react";
import { foldScroll, isSettling } from "../../lib/askReel/foldScroll";
import { prefersReducedMotion } from "../../lib/askReel/useAskReelChat";

/** Input that means the reader is steering the scroll themselves. */
const READER_INPUT = ["wheel", "touchstart", "touchmove", "keydown", "mousedown"] as const;

/**
 * Keeps the live turn where the reader sees it while the transcript ABOVE it changes shape
 * (HREV6-PH-01).
 *
 * When a follow-up's first content arrives, the previous answer leaves the grid and folds into its
 * "Earlier answer" tray — on a phone, about a screen of posters above the live turn disappears in
 * one commit. Chromium's CSS scroll anchoring absorbs that; WebKit has none, so Safari left the new
 * reply and its first poster row above the viewport and showed only the tail of the grid, breaking
 * WS-2 task 3's "the first screen shows the reply plus at least one row of posters".
 *
 * This is scroll anchoring for that one commit, done by hand: the anchor's viewport top is read
 * BEFORE React mutates the DOM (`getSnapshotBeforeUpdate` — the one hook that runs there, and why
 * this is a class) and compared after it; {@link foldScroll} decides what to do with the shift. A
 * browser that anchors on its own has already moved by the time the after-read runs, sees no shift
 * and does nothing, so the two never stack. Only while a turn streams, and only on the commit
 * where the grid changes rows.
 */
export default class HoldLiveTurn extends Component<{
  anchor: RefObject<HTMLElement | null>;
  /** The transcript row the live grid renders under; a change is the earlier answer folding. */
  gridRow: number;
  /** A turn is streaming — the only time the fold happens under a reader. */
  active: boolean;
  children: ReactNode;
}> {
  private turnStart: number | null = null;
  private lastScroll = -Infinity;
  private lastInput = -Infinity;
  private readonly onScroll = () => { this.lastScroll = performance.now(); };
  private readonly onInput = () => { this.lastInput = performance.now(); };

  componentDidMount(): void {
    if (this.props.active) this.turnStart = performance.now();
    window.addEventListener("scroll", this.onScroll, { passive: true });
    for (const type of READER_INPUT) window.addEventListener(type, this.onInput, { passive: true, capture: true });
  }

  componentWillUnmount(): void {
    window.removeEventListener("scroll", this.onScroll);
    for (const type of READER_INPUT) window.removeEventListener(type, this.onInput, { capture: true });
  }

  getSnapshotBeforeUpdate(prev: Readonly<HoldLiveTurn["props"]>): { top: number; settling: boolean } | null {
    if (!this.props.active || prev.gridRow === this.props.gridRow) return null;
    const el = this.props.anchor.current;
    if (!el) return null;
    const settling = isSettling({
      now: performance.now(), turnStart: this.turnStart, lastScroll: this.lastScroll, lastInput: this.lastInput,
    });
    return { top: el.getBoundingClientRect().top, settling };
  }

  componentDidUpdate(prev: Readonly<HoldLiveTurn["props"]>, _state: unknown, snap: { top: number; settling: boolean } | null): void {
    if (!prev.active && this.props.active) this.turnStart = performance.now();
    if (!this.props.active) this.turnStart = null;
    const el = this.props.anchor.current;
    if (!snap || !el) return;
    const move = foldScroll({
      before: snap.top, after: el.getBoundingClientRect().top, viewportHeight: window.innerHeight, settling: snap.settling,
    });
    if (move.kind === "hold") {
      window.scrollBy({ top: move.by, behavior: "instant" as ScrollBehavior });
    } else if (move.kind === "retarget") {
      // An in-flight smooth scroll is cancelled by an INSTANT one, not by another smooth one —
      // WebKit lets the first animation run on to its old target over a second smooth request.
      window.scrollTo({ top: window.scrollY, behavior: "instant" as ScrollBehavior });
      el.scrollIntoView({ behavior: prefersReducedMotion() ? "auto" : "smooth", block: "start" });
    }
  }

  render(): ReactNode {
    return this.props.children;
  }
}
