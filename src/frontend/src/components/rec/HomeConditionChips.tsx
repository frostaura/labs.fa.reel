import { useLayoutEffect, useRef, useState } from "react";
import { Minus, Plus, X } from "lucide-react";
import { useGetFiltersQuery } from "../../store/api";
import { summaryChips } from "../../lib/pickConditions";
import { conditionChipFace } from "../../lib/conditionChipFace";
import type { BrowseFilter } from "../../lib/browseFilter";
import { Button } from "../ui/button";

/**
 * Home's conditions, as chips you can act on (H-20, H-9, O-11): everything shaping tonight's picks,
 * stated under the page title, and each chip a way into the editor that changes it.
 *
 * - **Neutral, with ± icons.** The chips used to be tinted green and red, which made a standing
 *   preference read as an error state at the top of the page and still left the include/exclude
 *   distinction to hue. They are one neutral style now; a genre or keyword rule carries a `+` or `−`
 *   glyph, and its accessible name spells the rule out ("No documentary").
 * - **Tap opens the editor.** A chip is a button that opens the Conditions dialog — the answer to
 *   "why is my feed like this" is one tap from the thing that changes it.
 * - **"+N more" expands** in place instead of being a dead count, and collapses again.
 * - **`strip` variant** for a phone: the chips ARE the conditions control, inside the header's one
 *   scrolling line, at a 44 px target each — the separate "Conditions" button and a whole chip line
 *   above it were two rows of chrome above the first pick for one job.
 *
 * Browse toggles (per-device Movies/TV and availability) render as dashed chips with their own ✕,
 * because they are cleared here, not in the dialog; the strip omits them, since the segmented
 * control that sets them sits in the same line.
 */
export default function HomeConditionChips({
  browse,
  onBrowseChange,
  onOpenEditor,
  limit,
  variant = "row",
  className = "",
}: {
  browse?: BrowseFilter | null;
  onBrowseChange?: (next: BrowseFilter) => void;
  onOpenEditor: () => void;
  /** Condition chips shown before "+N more". */
  limit: number;
  variant?: "row" | "strip";
  className?: string;
}) {
  const { data: filters } = useGetFiltersQuery();
  const [expanded, setExpanded] = useState(false);
  const strip = variant === "strip";
  // The row variant holds ONE line until "+N more" is tapped (HREV1-D04). The saved conditions land
  // after the first paint, and a chip line that appeared — or wrapped — above the picks then shoved
  // the whole hero down (73 px at 834, every cold load, CLS 0.133). So the line is drawn at its own
  // height from the first frame, and filled with as many chips as fit on it; the rest sit behind
  // "+N more". `fit` is that count, measured before paint; null means "measure on this render".
  const holdsOneLine = !strip && !expanded;
  const [fit, setFit] = useState<number | null>(null);
  const rowRef = useRef<HTMLDivElement>(null);

  const chips = filters ? summaryChips(filters, strip ? null : browse) : [];
  const conditions = chips.filter((c) => c.kind === "condition");
  const views = chips.filter((c) => c.kind === "browse");
  const chipsKey = chips.map((c) => c.key).join("|");

  // A new set of chips is a new measurement. Adjusted during render (React's alternative to a
  // syncing effect), like Home's seed key: it settles in one extra pass, before anything paints.
  const [measuredFor, setMeasuredFor] = useState(chipsKey);
  if (measuredFor !== chipsKey) {
    setMeasuredFor(chipsKey);
    setFit(null);
  }

  // Before paint: with every chip on the (non-wrapping, clipped) line, count the ones that end
  // inside it, leaving room for "+N more" and the browse group. setState in a layout effect
  // re-renders synchronously, so the over-full line is never painted.
  useLayoutEffect(() => {
    if (!holdsOneLine || fit !== null || !rowRef.current) return;
    setFit(chipsThatFit(rowRef.current));
  }, [holdsOneLine, fit, chipsKey]);

  // A resize re-measures from scratch (all chips, then count).
  useLayoutEffect(() => {
    const row = rowRef.current;
    if (!holdsOneLine || !row || typeof ResizeObserver === "undefined") return;
    let width = row.clientWidth;
    const observer = new ResizeObserver(() => {
      if (row.clientWidth !== width) {
        width = row.clientWidth;
        setFit(null);
      }
    });
    observer.observe(row);
    return () => observer.disconnect();
  }, [holdsOneLine]);

  // Waits for the real payload rather than rendering against the defaults: `normalizeFilters` fills
  // an absent payload out to `languages: ["en"]`, so an in-flight query would briefly assert an
  // "English only" condition the account may not actually have. The strip has no line to hold (it
  // sits inside the phone's control line); the row holds its line with an invisible chip.
  if (!filters) {
    if (strip) return null;
    return (
      <div className={`${ONE_LINE} ${className}`} aria-hidden data-testid="conditions-summary-pending" />
    );
  }
  if (strip && conditions.length === 0 && views.length === 0) return null;

  const cap = holdsOneLine ? Math.min(limit, fit ?? conditions.length) : expanded ? conditions.length : limit;
  const shown = conditions.slice(0, cap);
  const hidden = conditions.length - shown.length;

  const target = strip ? `${BARE} min-h-11 shrink-0` : `${ROW_TARGET} shrink-0`;
  const pill = PILL;

  return (
    <div
      ref={rowRef}
      className={
        strip
          ? `flex items-center gap-1.5 ${className}`
          : holdsOneLine
            ? `${ONE_LINE} gap-x-4 ${className}`
            : `flex flex-wrap items-center gap-x-4 gap-y-0 ${className}`
      }
      data-testid="conditions-summary"
    >
      {/* Nothing saved and nothing toggled: the line still says so, rather than vanishing from a
          height the page has already laid out around (HREV1-D04). */}
      {!strip && conditions.length === 0 && views.length === 0 && (
        <span className={`flex items-center gap-x-1.5 ${ROW_TARGET}`} data-testid="summary-none">
          <span className="fa-overline text-3">Conditions</span>
          <span className="fa-caption text-3">none saved</span>
        </span>
      )}
      {conditions.length > 0 && (
        <div
          className={strip || holdsOneLine ? "flex shrink-0 items-center gap-x-1.5 whitespace-nowrap" : "flex flex-wrap items-center gap-x-1.5"}
          role="group"
          aria-label="Conditions — saved to your account; tap one to change them"
          data-testid="summary-group-conditions"
        >
          {!strip && (
            <span
              className="fa-overline text-3"
              title="Saved to your account — these apply everywhere. Tap a chip to change them."
            >
              Conditions
            </span>
          )}
          {shown.map((chip) => {
            const face = conditionChipFace(chip);
            return (
              <Button
                key={chip.key}
                variant="ghost"
                onClick={onOpenEditor}
                aria-label={`${face.spoken} — change conditions`}
                title={`${face.spoken} — tap to change your conditions`}
                className={`group ${target}`}
                data-testid="condition-chip"
                data-sign={face.sign ?? "none"}
              >
                <span className={pill}>
                  {face.sign === "include" && <Plus className="h-3 w-3 shrink-0" aria-hidden />}
                  {face.sign === "exclude" && <Minus className="h-3 w-3 shrink-0" aria-hidden />}
                  <span className="first-letter:uppercase inline-block">{face.text}</span>
                </span>
              </Button>
            );
          })}
          {hidden > 0 || expanded ? (
            <Button
              variant="ghost"
              onClick={() => setExpanded((v) => !v)}
              aria-expanded={expanded}
              className={`${target} px-1 text-3 underline-offset-2 hover:text-fa-frost hover:underline`}
              data-testid="conditions-more"
            >
              <span className="fa-caption">{expanded ? "Show fewer" : `+${hidden} more`}</span>
            </Button>
          ) : null}
        </div>
      )}

      {views.length > 0 && (
        <div
          className={holdsOneLine ? "flex shrink-0 items-center gap-x-1.5 whitespace-nowrap" : "flex flex-wrap items-center gap-x-1.5"}
          data-testid="summary-group-browse"
        >
          <span
            className="fa-overline text-3"
            title="This browser only — set by the Movies/TV and availability toggles, and not saved to your account."
          >
            Viewing on this device
          </span>
          {views.map((chip) => (
            <span
              key={chip.key}
              // Dashed: a different kind of filter, said by shape as well as by the group label.
              className="inline-flex items-center gap-0.5 rounded-full border border-dashed border-fa-edge bg-fa-glass pl-2 fa-caption text-fa-frost"
              data-testid={chip.key}
            >
              {chip.label}
              {onBrowseChange && browse && chip.clear && (
                <Button
                  variant="ghost"
                  onClick={() => onBrowseChange({ ...browse, ...chip.clear })}
                  aria-label={`Clear “${chip.label}” — show everything again`}
                  title={`Clear “${chip.label}”`}
                  data-testid={`clear-${chip.key}`}
                  className="h-auto min-h-11 min-w-11 rounded-full p-0 text-fa-frost hover:bg-transparent hover:text-fa-frost-bright [@media(pointer:fine)]:min-h-0 [@media(pointer:fine)]:min-w-0 [@media(pointer:fine)]:p-1"
                >
                  <X className="h-3 w-3" aria-hidden />
                </Button>
              )}
            </span>
          ))}
        </div>
      )}
    </div>
  );
}

/**
 * The Button primitive's own padding, fill and size cleared, so only its focus ring is kept: the
 * button is the hit area and the pill inside it is what you see (the card quick actions' pattern).
 */
const BARE = "h-auto gap-0 rounded-full p-0 font-normal hover:bg-transparent";
/** A 44 px target on any coarse pointer, around a pill that stays caption-sized. */
const ROW_TARGET = `${BARE} min-h-11 [@media(pointer:fine)]:min-h-0`;
const PILL =
  "inline-flex items-center gap-1 whitespace-nowrap rounded-full border border-fa-edge bg-fa-glass px-2 py-0.5 fa-caption text-fa-frost transition group-hover:border-fa-frost/40 group-hover:text-fa-frost-bright";

/**
 * The row's one line: a FIXED height, the chips' own (a 44 px target on a coarse pointer, the
 * caption pill under a mouse), so the line is the same height before the conditions arrive, with
 * none saved, and with a dozen — nothing below it can move (HREV1-D04). Clipped sideways only, with
 * a margin for the focus ring, so a measuring pass never widens the page.
 */
const ONE_LINE =
  "flex flex-nowrap items-center h-11 [@media(pointer:fine)]:h-6 overflow-x-clip [overflow-clip-margin:4px]";

/** Room kept at the line's end for "+N more" (it reads "+13 more" at its widest). */
const MORE_RESERVE_PX = 72;

/**
 * How many condition chips end inside the row's one line, with room for "+N more" and the browse
 * group after them. Read from the laid-out row (every chip rendered, no wrapping), so it is exact
 * for the fonts and pointer in force. At least one chip always shows.
 */
function chipsThatFit(row: HTMLElement): number {
  const chips = Array.from(row.querySelectorAll<HTMLElement>('[data-testid="condition-chip"]'));
  if (chips.length === 0) return 0;
  const rowBox = row.getBoundingClientRect();
  const browse = row.querySelector<HTMLElement>('[data-testid="summary-group-browse"]');
  const gap = parseFloat(getComputedStyle(row).columnGap) || 0;
  const browseWidth = browse ? browse.getBoundingClientRect().width + gap : 0;
  const ends = chips.map((chip) => chip.getBoundingClientRect().right - rowBox.left);
  // Everything fits, and nothing beyond the chips on the line needs a "+N more".
  const needsMore = row.querySelector('[data-testid="conditions-more"]') !== null;
  if (!needsMore && ends[ends.length - 1] <= rowBox.width - browseWidth) return chips.length;
  const room = rowBox.width - browseWidth - MORE_RESERVE_PX;
  const fitting = ends.filter((end) => end <= room).length;
  return Math.max(1, fitting);
}
