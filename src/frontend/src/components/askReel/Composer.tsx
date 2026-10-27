import { useLayoutEffect, useRef, useState } from "react";
import { Mic, Send } from "lucide-react";
import { Button } from "../ui/button";
import { COMPOSER_MIN_PX, composerHeight, composerSends } from "../../lib/askReel/useAskReelChat";

/**
 * The composer — the question field, the mic and Send.
 *
 * It sits ABOVE the mobile tab bar, not under it. Both are `fixed` at `bottom-0` and the tab bar is
 * `z-40`, so on every phone width Send and the input were behind it and a tap on Send navigated to
 * /dna — invisible to any visibility assertion, since the composer renders perfectly, just covered.
 * The offset is the bar's MEASURED height (see `useTabBarHeight`), safe-area padding included; the
 * `3.5rem` class is only the first paint's guess before the layout effect runs. From `md` up the
 * tab bar is gone, so the composer returns to the very bottom. `askreel-mobile.spec.ts` hit-tests
 * this with `elementFromPoint` at three widths and asserts the seam is flush.
 */
export default function Composer({
  draft,
  onDraftChange,
  onSubmit,
  streaming,
  wide,
  speech,
}: {
  draft: string;
  onDraftChange: (text: string) => void;
  onSubmit: (text: string) => void;
  streaming: boolean;
  /** Desktop gets the example-laden placeholder; on a phone it wraps to two clipped lines. */
  wide: boolean;
  speech: { supported: boolean; listening: boolean; start: () => void; stop: () => void };
}) {
  /**
   * The composer's height, measured rather than guessed: the same 400 characters are four lines
   * wide and eight narrow, so a character count cannot decide this. Released to `auto` first
   * because a textarea reports `scrollHeight` against its CURRENT height and would otherwise never
   * shrink back after a deletion. A layout effect, so both writes land in one paint.
   */
  const composerRef = useRef<HTMLTextAreaElement>(null);
  const barPx = useTabBarHeight();
  useLayoutEffect(() => {
    const el = composerRef.current;
    if (!el) return;
    // An EMPTY textarea measures its placeholder, not its value: Chromium reports
    // `scrollHeight: 92` for a resting composer because the placeholder sentence wraps to three
    // lines at 375 px. Measured rather than assumed (the probe is in the spec), and the empty case
    // is the one height this component already knows without asking.
    if (!draft) {
      el.style.height = `${COMPOSER_MIN_PX}px`;
      return;
    }
    el.style.height = "auto";
    el.style.height = `${composerHeight(el.scrollHeight)}px`;
  }, [draft]);

  return (
    <form
      onSubmit={(e) => { e.preventDefault(); onSubmit(draft); }}
      className="fixed bottom-[calc(3.5rem+env(safe-area-inset-bottom))] md:bottom-0 inset-x-0 z-30 border-t border-fa-frost/10 bg-background/95 backdrop-blur px-4 py-3"
      style={barPx == null ? undefined : { bottom: barPx }}
    >
      {/* `items-end` so the mic and Send stay on the composer's last line as it grows, rather
          than drifting to the middle of a four-line field. */}
      <div className="max-w-3xl mx-auto flex items-end gap-2">
        {/* F6-18 + the 44 px audit. A single-line `<input>` 37 px tall: under the touch minimum
            at every width, and about 36 characters wide on a phone for a question the server
            takes up to 1 000 of — a 1 000-character one measured `scrollWidth 6101` in it. The
            bubble has been readable back since F6-02; the field it is typed into was not.

            A textarea wraps, grows to four lines and then scrolls (see `composerHeight`), and it
            is the only shape in which Enter-sends / Shift+Enter-newline is a real distinction:
            as an `<input>` Shift+Enter "submitted" because there was no second line to make and
            the form's default ran (F3-12). `resize-none` because the grip would fight the
            measured height, and the height is set from content on every keystroke. */}
        <textarea
          ref={composerRef}
          data-testid="askreel-composer"
          rows={1}
          value={draft}
          onChange={(e) => onDraftChange(e.target.value)}
          onKeyDown={(e) => {
            if (!composerSends(e.key, e.shiftKey, e.nativeEvent.isComposing)) return;
            e.preventDefault();
            onSubmit(draft);
          }}
          aria-label="Ask Reel"
          placeholder={
            wide
              ? "Ask for more, or refine — “something funnier”, “more like the second one”…"
              : "Ask for more, or refine…"
          }
          style={{ height: COMPOSER_MIN_PX }}
          className="flex-1 min-h-11 resize-none overflow-y-auto rounded-3xl bg-fa-frost/10 px-4 py-2.5 fa-body leading-6 text-fa-frost-bright placeholder:text-3 focus:outline-none focus:ring-1 focus:ring-fa-frost/40"
        />
        {speech.supported && (
          <Button
            variant="ghost"
            size="icon"
            onClick={() => (speech.listening ? speech.stop() : speech.start())}
            disabled={streaming}
            aria-label="Voice search"
            aria-pressed={speech.listening}
            data-testid="askreel-mic"
            className={`min-h-11 min-w-11 rounded-full disabled:opacity-40 ${
              speech.listening ? "bg-fa-frost/40 text-fa-frost-bright animate-pulse hover:bg-fa-frost/40" : "bg-fa-frost/10 text-fa-frost-dim hover:text-fa-frost-bright"
            }`}
          >
            <Mic className="h-4 w-4" aria-hidden />
          </Button>
        )}
        <Button
          type="submit"
          variant="ghost"
          size="icon"
          disabled={!draft.trim() || streaming}
          data-testid="askreel-send"
          className="min-h-11 min-w-11 rounded-full bg-fa-frost/20 text-fa-frost-bright hover:bg-fa-frost/30 hover:text-fa-frost-bright disabled:opacity-40"
          aria-label="Send"
        >
          <Send className="h-4 w-4" aria-hidden />
        </Button>
      </div>
    </form>
  );
}

/**
 * The mobile tab bar's rendered height in CSS pixels — its safe-area padding included, and 0 from
 * `md` up, where the bar is `display: none` — or null before it has been measured, or where there
 * is no bar to measure (a page rendered outside the app shell).
 *
 * PH-06: the composer used to sit `3.5rem` (56 px) up while the bar is as tall as its icon and
 * label make it, 49 px in Chromium and WebKit alike, and the 7 px between them showed the page —
 * the footer's "Reel by FrostAura" — through the gap. A constant restated here is a second copy of
 * a fact the bar owns, so it is read off the bar and followed with a ResizeObserver: a font, a
 * rotation's safe-area change or the breakpoint all move the seam, and the composer moves with it.
 */
function useTabBarHeight(): number | null {
  const [height, setHeight] = useState<number | null>(null);
  useLayoutEffect(() => {
    const bar = document.querySelector<HTMLElement>("[data-testid='mobile-tabbar']");
    if (!bar) return;
    const measure = () => setHeight(bar.getBoundingClientRect().height);
    measure();
    if (typeof ResizeObserver === "undefined") return;
    const observer = new ResizeObserver(measure);
    // The border box: the safe-area inset is the bar's PADDING, and the default content box never
    // reports a padding change — a rotation would have moved the seam without a callback.
    observer.observe(bar, { box: "border-box" });
    return () => observer.disconnect();
  }, []);
  return height;
}
